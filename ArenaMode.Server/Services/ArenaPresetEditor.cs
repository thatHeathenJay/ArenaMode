using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace ArenaMode.Server.Services;

/// <summary>
/// Custom presets per profile (config/custom-presets/&lt;profile&gt;.json): a copy of a stock preset the player edits
/// one gear slot at a time. A swapped item arrives as the game's own default preset for it (attachments, magazine),
/// a weapon gets a magazine, rounds and a chambered round, armour gets its empty plate slots filled, and an
/// armoured rig that blocks body armour removes it. Custom kits are merged into the kit catalogue for the session.
/// </summary>
public static class ArenaPresetEditor
{
    private static readonly MongoId EquipmentRoot = new("55d7217a4bdc2d86028b456d");
    private static readonly MongoId AmmoBase = new("5485a8684bdc2da71d8b4567");
    private static readonly object Lock = new();
    private static TemplateTable? _templates;
    private static GlobalTable? _globals;
    private static LocaleTable? _locales;
    private static Dictionary<string, JsonObject>? _metascore;

    internal static void Bind(TemplateTable templates, GlobalTable globals, LocaleTable locales)
    {
        _templates = templates;
        _globals = globals;
        _locales = locales;
        _metascore = null;
    }

    /// <summary>The metascore table's items (tpl -> entry), parsed once.</summary>
    internal static Dictionary<string, JsonObject> Metascore
    {
        get
        {
            if (_metascore != null) return _metascore;
            var table = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            try
            {
                if (JsonNode.Parse(ArenaContext.MetascoreJson)?["items"] is JsonObject items)
                    foreach (var (tpl, node) in items)
                        if (node is JsonObject entry) table[tpl] = entry;
            }
            catch { /* empty table */ }
            return _metascore = table;
        }
    }

    // ------------------------------------------------------------------ store

    private static string FileFor(string profileId)
        => System.IO.Path.Combine(ConfigStore.ConfigDir, "custom-presets", profileId + ".json");

    private static JsonArray Load(string profileId)
    {
        try
        {
            string file = FileFor(profileId);
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonArray array) return array;
        }
        catch { /* corrupt file: start over */ }
        return new JsonArray();
    }

    private static void Save(string profileId, JsonArray kits)
    {
        string file = FileFor(profileId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, kits.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Stock catalogue plus this profile's custom presets.</summary>
    public static string MergeKits(MongoId sessionId, string baseJson)
    {
        lock (Lock)
        {
            var customs = Load(sessionId.ToString());
            if (customs.Count == 0) return baseJson;
            try
            {
                var all = JsonNode.Parse(baseJson) as JsonArray ?? new JsonArray();
                foreach (var kit in customs) all.Add(kit?.DeepClone());
                return all.ToJsonString();
            }
            catch { return baseJson; }
        }
    }

    // ----------------------------------------------------------------- actions

    public static string Handle(MongoId sessionId, string action, string? kitId, string? slot, string? tpl, out string? error)
    {
        error = null;
        lock (Lock)
        {
            string profileId = sessionId.ToString();
            var customs = Load(profileId);
            try
            {
                switch (action)
                {
                    case "create": Create(customs, kitId ?? ""); break;
                    case "swap": Swap(customs, kitId ?? "", slot ?? "", tpl ?? ""); break;
                    case "delete":
                        for (int i = customs.Count - 1; i >= 0; i--)
                            if (customs[i]?["id"]?.GetValue<string>() == kitId) customs.RemoveAt(i);
                        break;
                    default: error = "unknown action"; break;
                }
                if (error == null) Save(profileId, customs);
            }
            catch (Exception ex) { error = ex.Message; }
        }
        return MergeKits(sessionId, ArenaContext.KitsJson);
    }

    private static void Create(JsonArray customs, string baseId)
    {
        var stock = JsonNode.Parse(ArenaContext.KitsJson) as JsonArray;
        var source = stock?.FirstOrDefault(k => k?["id"]?.GetValue<string>() == baseId) as JsonObject
                     ?? customs.FirstOrDefault(k => k?["id"]?.GetValue<string>() == baseId) as JsonObject
                     ?? throw new InvalidOperationException("base preset not found");
        string cls = ClassOf(baseId);
        int n = 1;
        while (customs.Any(k => k?["id"]?.GetValue<string>() == $"custom-{cls}-{n}")) n++;
        var copy = (JsonObject)source.DeepClone();
        copy["id"] = $"custom-{cls}-{n}";
        copy["name"] = $"Custom {n}";
        copy["order"] = 100 + n;
        copy["base"] = baseId;
        customs.Add(copy);
    }

    internal static string ClassOf(string kitId)
    {
        string id = kitId.StartsWith("custom-", StringComparison.Ordinal) ? kitId.Substring(7) : kitId;
        int dash = id.IndexOf('-');
        return dash > 0 ? id.Substring(0, dash) : id;
    }

    private static void Swap(JsonArray customs, string kitId, string slot, string tpl)
    {
        var kit = customs.FirstOrDefault(k => k?["id"]?.GetValue<string>() == kitId) as JsonObject
                  ?? throw new InvalidOperationException("custom preset not found");
        var templates = _templates ?? throw new InvalidOperationException("templates unavailable");
        if (!templates.Items.TryGetValue(new MongoId(tpl), out var template))
            throw new InvalidOperationException("unknown item");
        if (SlotOf(template.Id) != slot)
            throw new InvalidOperationException("item does not fit that slot");
        var nodes = kit["items"] as JsonArray ?? throw new InvalidOperationException("preset has no items");
        var rootId = nodes.FirstOrDefault(n => n?["parentId"] == null)?["_id"]?.GetValue<string>()
                     ?? throw new InvalidOperationException("preset has no root");

        // Remember the old weapon's round for the new weapon when the calibre matches.
        string? oldAmmo = null;
        var oldSlotNode = nodes.FirstOrDefault(n => n?["parentId"]?.GetValue<string>() == rootId && n?["slotId"]?.GetValue<string>() == slot) as JsonObject;
        if (oldSlotNode != null)
        {
            var subtree = Subtree(nodes, oldSlotNode["_id"]!.GetValue<string>());
            oldAmmo = subtree.FirstOrDefault(n => n["slotId"]?.GetValue<string>() == "cartridges")?["_tpl"]?.GetValue<string>();
            foreach (var n in subtree) nodes.Remove(n);
        }
        if (slot == "FirstPrimaryWeapon") RemoveSlot(nodes, rootId, "SecondPrimaryWeapon");
        if (slot == "TacticalVest" && Prop(template, "BlocksArmorVest")?.GetValue<bool>() == true)
            RemoveSlot(nodes, rootId, "ArmorVest");
        if (slot == "ArmorVest")
        {
            var rig = nodes.FirstOrDefault(n => n?["parentId"]?.GetValue<string>() == rootId && n?["slotId"]?.GetValue<string>() == "TacticalVest") as JsonObject;
            if (rig != null && templates.Items.TryGetValue(new MongoId(rig["_tpl"]!.GetValue<string>()), out var rigTemplate)
                && Prop(rigTemplate, "BlocksArmorVest")?.GetValue<bool>() == true)
                RemoveSlot(nodes, rootId, "TacticalVest");
        }

        string newRootId = Graft(nodes, template.Id, rootId, slot);
        if (slot is "FirstPrimaryWeapon" or "Holster")
            ArmWeapon(nodes, template, newRootId, oldAmmo, ClassOf(kitId));
        FillPlates(nodes);
    }

    private static List<JsonObject> Subtree(JsonArray nodes, string rootId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { rootId };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var n in nodes)
                if (n is JsonObject o && o["parentId"]?.GetValue<string>() is { } p && ids.Contains(p) && ids.Add(o["_id"]!.GetValue<string>()))
                    grew = true;
        }
        return nodes.OfType<JsonObject>().Where(o => ids.Contains(o["_id"]!.GetValue<string>())).ToList();
    }

    private static void RemoveSlot(JsonArray nodes, string rootId, string slot)
    {
        var node = nodes.FirstOrDefault(n => n?["parentId"]?.GetValue<string>() == rootId && n?["slotId"]?.GetValue<string>() == slot) as JsonObject;
        if (node == null) return;
        foreach (var n in Subtree(nodes, node["_id"]!.GetValue<string>())) nodes.Remove(n);
    }

    /// <summary>The item's own default preset (attachments and all) under the equipment root; a bare item otherwise.</summary>
    private static string Graft(JsonArray nodes, MongoId tpl, string rootId, string slot)
    {
        var preset = _globals?.ItemPresets.Values.FirstOrDefault(p => p.Encyclopedia == tpl && p.Items is { Count: > 0 });
        if (preset == null)
        {
            string id = NewId();
            nodes.Add(Node(id, tpl.ToString(), rootId, slot));
            return id;
        }
        var presetRoot = preset.Items!.First(i => i.ParentId == null);
        var remap = preset.Items!.ToDictionary(i => i.Id, _ => NewId());
        foreach (var item in preset.Items!)
        {
            bool isRoot = ReferenceEquals(item, presetRoot) || item.ParentId == null;
            var node = Node(remap[item.Id], item.Template.ToString(), isRoot ? rootId : remap[item.ParentId!], isRoot ? slot : item.SlotId);
            if (item.Upd != null)
            {
                var upd = JsonSerializer.SerializeToNode(item.Upd, JsonUtil.JsonSerializerOptionsNoIndent);
                if (upd is JsonObject u && u.Count > 0) node["upd"] = u;
            }
            nodes.Add(node);
        }
        return remap[presetRoot.Id];
    }

    private static void ArmWeapon(JsonArray nodes, TemplateItem weapon, string weaponId, string? oldAmmo, string cls)
    {
        var templates = _templates!;
        nodes.OfType<JsonObject>().First(n => n["_id"]!.GetValue<string>() == weaponId)["upd"] =
            new JsonObject { ["Repairable"] = new JsonObject { ["Durability"] = 100, ["MaxDurability"] = 100 }, ["FireMode"] = new JsonObject { ["FireMode"] = "single" } };
        var magazine = nodes.OfType<JsonObject>().FirstOrDefault(n => n["parentId"]?.GetValue<string>() == weaponId && n["slotId"]?.GetValue<string>() == "mod_magazine");
        if (magazine == null)
        {
            var magSlot = weapon.Properties?.Slots?.FirstOrDefault(s => s.Name == "mod_magazine");
            var first = magSlot?.Properties?.Filters?.FirstOrDefault()?.Filter?.FirstOrDefault();
            if (first != null)
            {
                magazine = Node(NewId(), first.Value.ToString(), weaponId, "mod_magazine");
                nodes.Add(magazine);
            }
        }
        string? calibre = weapon.Properties?.Caliber;
        string? ammo = oldAmmo != null && templates.Items.TryGetValue(new MongoId(oldAmmo), out var oldRound) && oldRound.Properties?.Caliber == calibre
            ? oldAmmo : PickAmmo(calibre, cls, magazine);
        if (ammo == null) return;
        if (magazine != null)
        {
            string magId = magazine["_id"]!.GetValue<string>();
            foreach (var n in Subtree(nodes, magId).Where(n => n["slotId"]?.GetValue<string>() == "cartridges").ToList()) nodes.Remove(n);
            int capacity = 1;
            if (templates.Items.TryGetValue(new MongoId(magazine["_tpl"]!.GetValue<string>()), out var magTemplate))
                capacity = (int)(magTemplate.Properties?.Cartridges?.FirstOrDefault()?.MaxCount ?? 1);
            var rounds = Node(NewId(), ammo, magId, "cartridges");
            rounds["upd"] = new JsonObject { ["StackObjectsCount"] = capacity };
            nodes.Add(rounds);
        }
        if (weapon.Properties?.Chambers?.Any() == true)
        {
            foreach (var n in nodes.OfType<JsonObject>().Where(n => n["parentId"]?.GetValue<string>() == weaponId && n["slotId"]?.GetValue<string>() == "patron_in_weapon").ToList()) nodes.Remove(n);
            var chambered = Node(NewId(), ammo, weaponId, weapon.Properties.Chambers.First().Name ?? "patron_in_weapon");
            chambered["upd"] = new JsonObject { ["StackObjectsCount"] = 1 };
            nodes.Add(chambered);
        }
    }

    /// <summary>A live-scored round of the calibre the class may use (lowest MP), else the magazine's first filter.</summary>
    private static string? PickAmmo(string? calibre, string cls, JsonObject? magazine)
    {
        var templates = _templates!;
        string? best = null;
        double bestScore = double.MaxValue;
        string className = char.ToUpperInvariant(cls[0]) + cls.Substring(1);
        foreach (var (tpl, entry) in Metascore)
        {
            if (entry["perUnit"]?.GetValue<bool>() != true || entry["source"]?.GetValue<string>() != "live") continue;
            if (!templates.Items.TryGetValue(new MongoId(tpl), out var round) || round.Properties?.Caliber != calibre) continue;
            if ((entry["live"] as JsonObject)?["classAccess"] is JsonObject access
                && access[className]?.GetValue<string>() is { } state && state != "allowed") continue;
            double score = entry["score"]?.GetValue<double>() ?? 0;
            if (score < bestScore) { bestScore = score; best = tpl; }
        }
        if (best != null) return best;
        if (magazine != null && templates.Items.TryGetValue(new MongoId(magazine["_tpl"]!.GetValue<string>()), out var magTemplate))
            return magTemplate.Properties?.Cartridges?.FirstOrDefault()?.Properties?.Filters?.FirstOrDefault()?.Filter?.FirstOrDefault().ToString();
        return null;
    }

    /// <summary>Armour with open plate slots gets the best plate its own filter accepts.</summary>
    private static void FillPlates(JsonArray nodes)
    {
        var templates = _templates!;
        foreach (var node in nodes.OfType<JsonObject>().ToList())
        {
            if (!templates.Items.TryGetValue(new MongoId(node["_tpl"]!.GetValue<string>()), out var template)) continue;
            foreach (var slot in template.Properties?.Slots ?? [])
            {
                if (slot.Name == null || !slot.Name.Contains("plate", StringComparison.OrdinalIgnoreCase)) continue;
                string id = node["_id"]!.GetValue<string>();
                if (nodes.Any(n => n?["parentId"]?.GetValue<string>() == id && n?["slotId"]?.GetValue<string>() == slot.Name)) continue;
                var candidates = slot.Properties?.Filters?.FirstOrDefault()?.Filter ?? [];
                MongoId? best = null;
                double bestClass = 0;
                foreach (var candidate in candidates)
                {
                    if (!templates.Items.TryGetValue(candidate, out var plate)) continue;
                    double armorClass = 0;
                    double.TryParse(Prop(plate, "armorClass")?.ToString(), out armorClass);
                    if (armorClass > bestClass) { bestClass = armorClass; best = candidate; }
                }
                if (best != null) nodes.Add(Node(NewId(), best.Value.ToString(), id, slot.Name));
            }
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Which gear slot a template belongs in, by its parent chain; null when it is not gear.</summary>
    internal static string? SlotOf(MongoId tpl)
    {
        var templates = _templates;
        if (templates == null) return null;
        foreach (var (slot, baseId) in ArenaArmory.SlotBases)
        {
            var node = tpl;
            for (int depth = 0; depth < 12; depth++)
            {
                if (!templates.Items.TryGetValue(node, out var item) || item.Parent == default) break;
                if (item.Parent == baseId) return slot;
                node = item.Parent;
            }
        }
        return null;
    }

    private static JsonNode? Prop(TemplateItem template, string name)
    {
        try { return JsonSerializer.SerializeToNode(template.Properties, JsonUtil.JsonSerializerOptionsNoIndent)?[name]; }
        catch { return null; }
    }

    private static JsonObject Node(string id, string tpl, string? parentId, string? slotId)
    {
        var node = new JsonObject { ["_id"] = id, ["_tpl"] = tpl };
        if (parentId != null) node["parentId"] = parentId;
        if (slotId != null) node["slotId"] = slotId;
        return node;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..24];
}
