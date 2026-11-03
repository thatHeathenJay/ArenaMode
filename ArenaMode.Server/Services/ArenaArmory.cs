using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace ArenaMode.Server.Services;

/// <summary>
/// The armory the preset editor draws from: every scored item (config/metascore.json) that fits a gear slot,
/// with its slot, bar, score, class access (live records carry live Arena's; derived scores are open to all
/// classes) and short name. Served verbatim as /arenamode/armory. Also hands the tables to the preset editor.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 98500)]
public class ArenaArmory(TemplateTable templateTable, GlobalTable globalTable, LocaleTable localeTable, ISptLogger<ArenaArmory> logger) : IOnLoad
{
    internal static readonly (string Slot, MongoId Base)[] SlotBases =
    {
        ("Holster", new MongoId("5447b5cf4bdc2d65278b4567")),          // pistols first: they are weapons too
        ("FirstPrimaryWeapon", new MongoId("5422acb9af1c889c16000029")),
        ("ArmorVest", new MongoId("5448e54d4bdc2dcc718b4568")),
        ("TacticalVest", new MongoId("5448e5284bdc2dcb718b4567")),
        ("Headwear", new MongoId("5a341c4086f77401f2541505")),
        ("Earpiece", new MongoId("5645bcb74bdc2ded0b8b4578")),
        ("Eyewear", new MongoId("5448e5724bdc2ddf718b4568")),
        ("FaceCover", new MongoId("5a341c4686f77469e155819e")),
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            ArenaPresetEditor.Bind(templateTable, globalTable, localeTable);
            var items = ArenaPresetEditor.Metascore;
            var armory = new JsonArray();
            var names = localeTable.Global.TryGetValue("en", out var lazy) ? lazy.Value : null;
            foreach (var (tpl, entry) in items)
            {
                if (!templateTable.Items.TryGetValue(new MongoId(tpl), out var template)) continue;
                string? slot = ArenaPresetEditor.SlotOf(template.Id);
                if (slot == null) continue;
                if (templateTable.Handbook?.Items?.Any(i => i.Id == template.Id) != true) continue;
                var classes = new JsonArray();
                var live = entry["live"] as JsonObject;
                if (live?["classes"] is JsonArray liveClasses)
                    foreach (var c in liveClasses) classes.Add(c?.GetValue<string>() ?? "");
                string? name = null;
                names?.TryGetValue(tpl + " ShortName", out name);
                JsonArray? presetNodes = null;
                if (slot is "FirstPrimaryWeapon" or "Holster")
                    try { presetNodes = ArenaPresetEditor.PresetNodes(template.Id, slot); if (presetNodes.Count < 2) presetNodes = null; } catch { presetNodes = null; }
                armory.Add(new JsonObject
                {
                    ["tpl"] = tpl,
                    ["slot"] = slot,
                    ["bar"] = entry["bar"]?.GetValue<string>() ?? "firepower",
                    ["score"] = entry["score"]?.GetValue<double>() ?? 0d,
                    ["live"] = entry["source"]?.GetValue<string>() == "live",
                    ["classes"] = classes,
                    ["unlock"] = live?["unlock"]?.GetValue<string>() ?? "",
                    ["name"] = name ?? template.Name ?? tpl,
                    ["items"] = presetNodes,
                });
            }
            ArenaContext.ArmoryJson = armory.ToJsonString();
            logger.Info($"[ArenaMode] Armory: {armory.Count} items across {SlotBases.Length} slots.");
        }
        catch (Exception ex)
        {
            logger.Warning($"[ArenaMode] Armory unavailable: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
