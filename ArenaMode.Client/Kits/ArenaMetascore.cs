using ArenaMode.Shared;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using System;
using System.Collections.Generic;

namespace ArenaMode.Kits;

/// <summary>
/// Live Arena metascore (MP) for a kit, from the server's metascore.json (tools/generate-metascore.py): every item
/// template carries a bar (firepower / defense / utility) and a score; ammo is scored per round; a live-scored
/// weapon's score already covers its default preset mods, which are skipped. The band is the live preset band.
/// </summary>
internal static class ArenaMetascore
{
    internal readonly struct Entry
    {
        internal readonly string Bar;
        internal readonly double Score;
        internal readonly bool PerUnit;
        internal readonly bool Live;
        internal readonly HashSet<string> DefaultPresetMods;
        internal Entry(string bar, double score, bool perUnit, bool live, HashSet<string> mods)
        { Bar = bar; Score = score; PerUnit = perUnit; Live = live; DefaultPresetMods = mods; }
    }

    internal readonly struct KitScore
    {
        internal readonly int Firepower, Defense, Utility;
        internal int Total => Firepower + Defense + Utility;
        internal KitScore(int firepower, int defense, int utility) { Firepower = firepower; Defense = defense; Utility = utility; }
    }

    private static Dictionary<string, Entry> _items;
    internal static int BandMin { get; private set; } = 7500;
    internal static int BandMax { get; private set; } = 10000;

    internal static bool IsAvailable
    {
        get { EnsureLoaded(); return _items != null && _items.Count > 0; }
    }

    internal static void Reset() => _items = null;

    /// <summary>One item's metascore (0 when unscored).</summary>
    internal static double ItemScore(string tpl)
    {
        EnsureLoaded();
        return tpl != null && _items.TryGetValue(tpl, out var entry) ? entry.Score : 0d;
    }

    private static void EnsureLoaded()
    {
        if (_items != null) return;
        try
        {
            var root = JObject.Parse(RequestHandler.GetJson(Protocol.MetascoreRoute));
            var table = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (root["band"] is JObject band)
            {
                BandMin = band.Value<int?>("min") ?? BandMin;
                BandMax = band.Value<int?>("max") ?? BandMax;
            }
            if (root["items"] is JObject items)
                foreach (var pair in items)
                {
                    if (pair.Value is not JObject e) continue;
                    HashSet<string> mods = null;
                    if (e["defaultPresetMods"] is JArray array)
                    {
                        mods = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var m in array) mods.Add(m.ToString());
                    }
                    table[pair.Key] = new Entry(e.Value<string>("bar") ?? "firepower", e.Value<double?>("score") ?? 0,
                        e.Value<bool?>("perUnit") ?? false, e.Value<string>("source") == "live", mods);
                }
            _items = table;
            Plugin.ModLogger.LogInfo($"Arena metascore table: {table.Count} items, band {BandMin}-{BandMax}.");
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena metascore table unavailable: {ex.Message}");
            _items = new Dictionary<string, Entry>();
        }
    }

    internal static KitScore Score(KitDefinition kit)
    {
        EnsureLoaded();
        double fire = 0, def = 0, util = 0;
        if (kit?.Items == null || _items == null) return default;

        // A live-scored weapon's score covers its default preset mods: skip those nodes under it.
        var skip = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, KitItemNode>(StringComparer.Ordinal);
        foreach (var node in kit.Items) if (node.Id != null) byId[node.Id] = node;
        foreach (var node in kit.Items)
        {
            if (node.SlotId != "FirstPrimaryWeapon" || !_items.TryGetValue(node.Tpl ?? "", out var weapon)
                || !weapon.Live || weapon.DefaultPresetMods == null) continue;
            bool grew = true;
            var below = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            while (grew)
            {
                grew = false;
                foreach (var n in kit.Items)
                    if (n.ParentId != null && below.Contains(n.ParentId) && !below.Contains(n.Id) && weapon.DefaultPresetMods.Contains(n.Tpl))
                    { below.Add(n.Id); skip.Add(n.Id); grew = true; }
            }
        }

        void Add(string tpl, double count)
        {
            if (tpl == null || !_items.TryGetValue(tpl, out var e)) return;
            double value = e.Score * (e.PerUnit ? count : 1);
            switch (e.Bar)
            {
                case "defense": def += value; break;
                case "utility": util += value; break;
                default: fire += value; break;
            }
        }

        KitItemNode magazine = null;
        foreach (var node in kit.Items)
        {
            if (skip.Contains(node.Id)) continue;
            int stack = node.Upd?["StackObjectsCount"]?.Value<int>() ?? 1;
            Add(node.Tpl, stack);
            if (node.SlotId == "mod_magazine" && magazine == null) magazine = node;
        }
        int spares = kit.Fill?.SpareMagazines ?? 0;
        if (magazine != null && spares > 0)
        {
            Add(magazine.Tpl, 1);
            if (_items.TryGetValue(magazine.Tpl ?? "", out var mag) && !mag.PerUnit) { for (int i = 1; i < spares; i++) Add(magazine.Tpl, 1); }
            foreach (var node in kit.Items)
                if (node.ParentId == magazine.Id && node.SlotId == "cartridges")
                    Add(node.Tpl, (node.Upd?["StackObjectsCount"]?.Value<int>() ?? 1) * spares);
        }
        if (kit.Fill?.RigContents != null)
            foreach (var stack in kit.Fill.RigContents) Add(stack.Tpl, Math.Max(1, stack.Count));

        return new KitScore((int)Math.Round(fire), (int)Math.Round(def), (int)Math.Round(util));
    }
}
