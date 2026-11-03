using ArenaMode.Shared;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArenaMode.Kits;

/// <summary>
/// The server's armory (/arenamode/armory): every scored item that fits a gear slot, with its slot, bar, metascore,
/// class access and short name; and the preset editor (/arenamode/presets/edit) that copies, edits and deletes a
/// profile's custom presets. An edit hands back the merged kit catalogue, which replaces the client's.
/// </summary>
internal static class ArenaArmory
{
    internal sealed class Entry
    {
        internal string Tpl, Slot, Bar, Name, Unlock, Caliber;
        internal double Score;
        internal bool Live;
        internal HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Weapons: the game's default preset for the item (root parented to "root"), for a full icon.</summary>
        internal List<KitItemNode> Items;

        internal bool AllowedFor(string classId)
            => Classes.Count == 0 || Classes.Contains(classId);
    }

    private static List<Entry> _entries;

    internal static IReadOnlyList<Entry> Entries
    {
        get
        {
            if (_entries != null) return _entries;
            var list = new List<Entry>();
            try
            {
                foreach (var node in JArray.Parse(RequestHandler.GetJson(Protocol.ArmoryRoute)).OfType<JObject>())
                {
                    var entry = new Entry
                    {
                        Tpl = node.Value<string>("tpl"), Slot = node.Value<string>("slot"), Bar = node.Value<string>("bar") ?? "firepower",
                        Name = node.Value<string>("name"), Unlock = node.Value<string>("unlock") ?? "",
                        Score = node.Value<double?>("score") ?? 0, Live = node.Value<bool?>("live") ?? false,
                        Caliber = node.Value<string>("caliber"),
                    };
                    if (node["classes"] is JArray classes)
                        foreach (var c in classes) entry.Classes.Add(c.ToString());
                    if (node["items"] is JArray items)
                        try { entry.Items = items.ToObject<List<KitItemNode>>(); } catch { /* bare icon */ }
                    list.Add(entry);
                }
                Plugin.ModLogger.LogInfo($"Arena armory: {list.Count} items.");
            }
            catch (Exception ex) { Plugin.ModLogger.LogWarning($"Arena armory unavailable: {ex.Message}"); }
            return _entries = list;
        }
    }

    /// <summary>Armory items for a slot; a class limits them to its own, null lists all. Live-ranked first.</summary>
    internal static IEnumerable<Entry> ForSlot(string slot, string classId)
        => Entries.Where(e => e.Slot == slot && (classId == null || e.AllowedFor(classId)))
            .OrderByDescending(e => e.Live).ThenBy(e => e.Score).ThenBy(e => e.Name);

    /// <summary>create (KitId = the preset to copy), swap (KitId, Slot, Tpl) or delete (KitId). Null on success.</summary>
    internal static string Edit(string action, string kitId, string slot = null, string tpl = null)
    {
        try
        {
            var request = new JObject { ["Action"] = action, ["KitId"] = kitId, ["Slot"] = slot, ["Tpl"] = tpl };
            var response = JObject.Parse(RequestHandler.PostJson(Protocol.PresetEditRoute, request.ToString()));
            string error = response.Value<string>("error");
            KitCatalog.Preload();
            if (error != null) Plugin.ModLogger.LogWarning($"Arena preset {action} failed: {error}");
            return error;
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena preset {action} failed: {ex.Message}");
            return ex.Message;
        }
    }
}
