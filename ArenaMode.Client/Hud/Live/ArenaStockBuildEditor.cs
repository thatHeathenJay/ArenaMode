using ArenaMode.Kits;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Preset weapon customization on EFT's own weapon build screen (the one the extraction client uses): the kit's
/// weapon is built as a detached item and handed to the stock editor; every compatible mod lists with its metascore
/// in place of a price, mods outside the arena armory are marked unavailable, and when the screen closes the
/// slots that changed are sent to the server as the same swaps the live-styled editor used.
/// </summary>
internal static class ArenaStockBuildEditor
{
    internal static bool Active { get; private set; }

    private static bool _patched;
    private static KitDefinition _kit;
    private static bool _holster;
    private static Action _onClosed;
    private static Dictionary<string, string> _original;
    private static HashSet<string> _armory;
    private static double _originalTreeScore;
    private static ArenaMetascore.KitScore _baseline;
    private static ArenaBuildScoreOverlay _overlay;

    /// <summary>False when the stock screen cannot be used here (no item context yet, weapon failed to build).</summary>
    internal static bool TryShow(KitDefinition kit, bool holster, Action onClosed)
    {
        var context = ItemUiContext.Instance;
        if (context == null || context.Session == null)
        {
            Plugin.ModLogger.LogWarning("Arena build editor: EFT's item context is not ready; using the fallback editor.");
            return false;
        }
        Weapon weapon;
        try { weapon = KitBuilder.BuildPrimaryWeapon(holster ? ArenaBuildEditor.HolsterAsPrimary(kit) : kit, 0, out _); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena build editor: weapon could not be built: " + ex.Message);
            return false;
        }
        if (weapon == null) return false;
        Enable();
        _kit = kit;
        _holster = holster;
        _onClosed = onClosed;
        _original = Snapshot(weapon);
        _originalTreeScore = TreeScore(weapon);
        try { _baseline = ArenaMetascore.Score(kit); } catch { _baseline = default; }
        _armory = new HashSet<string>(ArenaArmory.Entries.Select(e => e.Tpl), StringComparer.Ordinal);
        foreach (var tpl in _original.Values) if (tpl.Length > 0) _armory.Add(tpl);   // the kit's own parts stay usable
        Active = true;
        ArenaPresetListScreen.SetDrawn(false);
        try { context.EditWeaponBuild(weapon); }
        catch (Exception ex)
        {
            Active = false;
            ArenaPresetListScreen.SetDrawn(true);
            Plugin.ModLogger.LogWarning("Arena build editor: EFT's build screen refused the weapon: " + ex.Message);
            return false;
        }
        Plugin.ModLogger.LogInfo($"Arena build editor: EFT build screen on {weapon.ShortName.Localized()} ({_original.Count} slots).");
        return true;
    }

    private static void Enable()
    {
        if (_patched) return;
        _patched = true;
        new ShowPatch().Enable();
        new MessagePatch().Enable();
        new ClosePatch().Enable();
        new ItemViewPatch().Enable();
    }

    // ---------------------------------------------------------------- tree

    /// <summary>Every slot in the weapon by path ("mod_stock/mod_pistol_grip"), empty string for an empty slot.</summary>
    private static Dictionary<string, string> Snapshot(CompoundItem root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, "", map);
        return map;
    }

    private static void Walk(CompoundItem item, string prefix, Dictionary<string, string> map)
    {
        foreach (var slot in item.AllSlots)
        {
            if (!ReferenceEquals(slot.ParentItem, item)) continue;
            string path = prefix + slot.ID;
            if (slot.ContainedItem is Item child)
            {
                map[path] = child.TemplateId;
                if (child is CompoundItem compound) Walk(compound, path + "/", map);
            }
            else map[path] = "";
        }
    }

    private static double TreeScore(CompoundItem root)
    {
        double total = 0d;
        try
        {
            total += ArenaMetascore.ItemScore(root.TemplateId);
            foreach (var slot in root.AllSlots)
                if (slot.ContainedItem != null) total += ArenaMetascore.ItemScore(slot.ContainedItem.TemplateId);
        }
        catch { /* partial */ }
        return total;
    }

    /// <summary>The preset's score with the weapon as it stands on the screen: firepower follows the build.</summary>
    internal static ArenaMetascore.KitScore CurrentScore(CompoundItem body)
    {
        int delta = body == null ? 0 : (int)Math.Round(TreeScore(body) - _originalTreeScore);
        return new ArenaMetascore.KitScore(_baseline.Firepower + delta, _baseline.Defense, _baseline.Utility);
    }

    /// <summary>Slots that differ from the opened weapon, parents before children, as server swaps.</summary>
    private static void Apply(CompoundItem body)
    {
        var final = Snapshot(body);
        var changes = final.Where(kv => !_original.TryGetValue(kv.Key, out var was) || was != kv.Value)
            .OrderBy(kv => kv.Key.Count(c => c == '/')).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (changes.Count == 0) { Plugin.ModLogger.LogInfo("Arena build editor: no changes."); return; }
        string prefix = _holster ? "mod:holster/" : "mod:primary/";
        int applied = 0;
        foreach (var change in changes)
        {
            string error = ArenaArmory.Edit("swap", _kit.Id, prefix + change.Key, change.Value);
            if (error == null) applied++;
            else if (change.Value.Length > 0) Plugin.ModLogger.LogWarning($"Arena build editor: {change.Key} -> {change.Value} refused: {error}");
        }
        Plugin.ModLogger.LogInfo($"Arena build editor: {applied}/{changes.Count} slot changes applied to {_kit.Id}.");
    }

    // ------------------------------------------------------------- patches

    private sealed class ShowPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(EditBuildScreen), nameof(EditBuildScreen.Show),
                new[] { typeof(Item), typeof(Item), typeof(InventoryController), typeof(IEftSession) });

        [PatchPostfix]
        private static void Postfix(EditBuildScreen __instance)
        {
            if (!Active || __instance == null) return;
            var t = Traverse.Create(__instance);
            try { _overlay = ArenaBuildScoreOverlay.Show(__instance); }
            catch (Exception ex) { Plugin.ModLogger.LogDebug("Arena build editor: score bars unavailable: " + ex.Message); }
            // Builds, assembling, publishing and the name field are stash features; the arena kit is the build.
            foreach (var field in new[] { "_newBuildButton", "_openBuildButton", "_saveAsBuildButton", "_selectItemButton",
                "_findPartsButton", "_publishButton", "_deleteBuildButton", "_saveButton", "_assembleButton", "_buildName",
                "_itemReadyToAssemble", "_onlyAvailableToggle" })
            {
                try
                {
                    var value = t.Field(field).GetValue();
                    var go = value as GameObject ?? (value as Component)?.gameObject;
                    if (field == "_onlyAvailableToggle" && value is UnityEngine.UI.Toggle toggle) toggle.isOn = false;
                    if (go != null) go.SetActive(false);
                }
                catch { /* field moved; cosmetic */ }
            }
        }
    }

    /// <summary>The "discard changes?" prompt on leaving: the arena applies the build itself, so it just closes.</summary>
    private sealed class MessagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(EditBuildScreen), nameof(EditBuildScreen.TryShowMessage));

        [PatchPrefix]
        private static void Prefix(ref bool autoAccept)
        {
            if (Active) autoAccept = true;
        }
    }

    private sealed class ClosePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(EditBuildScreen), nameof(EditBuildScreen.Close));

        [PatchPrefix]
        private static void Prefix(EditBuildScreen __instance)
        {
            if (!Active) return;
            Active = false;
            ArenaPresetListScreen.SetDrawn(true);
            if (_overlay != null) { UnityEngine.Object.Destroy(_overlay.gameObject); _overlay = null; }
            try
            {
                var body = Traverse.Create(__instance).Field("_itemBody").GetValue<Item>() as CompoundItem;
                if (body != null) Apply(body);
                else Plugin.ModLogger.LogWarning("Arena build editor: the build screen holds no weapon; nothing applied.");
            }
            catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena build editor: applying the build failed: " + ex.Message); }
            var closed = _onClosed;
            _onClosed = null;
            try { closed?.Invoke(); } catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena build editor: return failed: " + ex.Message); }
        }
    }

    /// <summary>Every mod in a slot's dropdown: metascore where a price would be, unavailable when not in the armory.</summary>
    private sealed class ItemViewPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ModdingSelectableItemView), nameof(ModdingSelectableItemView.Create));

        [PatchPostfix]
        private static void Postfix(Item item, ModdingSelectableItemView __result)
        {
            if (__result == null) return;
            var rect = (RectTransform)__result.transform;
            var label = rect.Find("ArenaScore")?.GetComponent<TextMeshProUGUI>();
            if (!Active || item == null)
            {
                if (label != null) label.gameObject.SetActive(false);   // pooled view reused elsewhere
                return;
            }
            bool allowed = _armory.Contains(item.TemplateId);
            var t = Traverse.Create(__result);
            try
            {
                t.Field("_notAvailable").GetValue<GameObject>()?.SetActive(!allowed);
                t.Field("_availableFromMerchants").GetValue<GameObject>()?.SetActive(false);
                if (!allowed) t.Field("_canSelect").SetValue(false);
            }
            catch { /* cosmetic */ }
            if (label == null)
            {
                label = HudStyle.CreateText(rect, "ArenaScore", 12f, TextAlignmentOptions.BottomLeft);
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = new Vector2(1f, 0f);
                label.rectTransform.pivot = Vector2.zero;
                label.rectTransform.anchoredPosition = new Vector2(4f, 2f);
                label.rectTransform.sizeDelta = new Vector2(-8f, 14f);
                label.raycastTarget = false;
            }
            label.gameObject.SetActive(true);
            label.transform.SetAsLastSibling();
            label.text = ArenaMetascore.ItemScore(item.TemplateId).ToString("N0");
            label.color = allowed ? new Color(0.35f, 0.8f, 0.75f) : ArenaUiTheme.Muted;
        }
    }
}

/// <summary>Live's four metascore bars at the bottom left of the weapon build screen, following the build.</summary>
internal sealed class ArenaBuildScoreOverlay : MonoBehaviour
{
    private EditBuildScreen _screen;
    private ArenaUiPack _pack;
    private readonly RectTransform[] _bars = new RectTransform[4];
    private float _nextTick;

    internal static ArenaBuildScoreOverlay Show(EditBuildScreen screen)
    {
        if (!ArenaUiPack.IsAvailable) return null;
        var go = new GameObject("ArenaBuildScoreOverlay");
        go.transform.SetParent(Plugin.Instance.transform, false);
        var overlay = go.AddComponent<ArenaBuildScoreOverlay>();
        overlay._screen = screen;
        overlay.Build();
        return overlay;
    }

    private void Build()
    {
        _pack = new ArenaUiPack();
        var builder = new ArenaUiBuilder(_pack, _ => null);
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 545;
        canvas.overrideSorting = true;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        var layout = ArenaMenuChrome.Find(_pack.Layout("ArenaPresetCustomizationScreen"), "APCMetascoreBar")
            ?? throw new InvalidOperationException("APCMetascoreBar missing from the rip");
        var panel = new GameObject("Bars", typeof(RectTransform)).GetComponent<RectTransform>();
        panel.SetParent(transform, false);
        panel.anchorMin = panel.anchorMax = new Vector2(0f, 0f); panel.pivot = new Vector2(0f, 0f);
        panel.anchoredPosition = new Vector2(40f, 70f); panel.sizeDelta = new Vector2(330f, 150f);
        for (int i = 0; i < 4; i++)
        {
            var bar = builder.Build(layout, panel);
            bar.name = "Bar" + i;
            bar.anchorMin = bar.anchorMax = new Vector2(0f, 1f); bar.pivot = new Vector2(0f, 1f);
            bar.anchoredPosition = new Vector2(30f, -(i * 36f) - 10f);
            bar.sizeDelta = new Vector2(290f, 12f);
            _bars[i] = bar;
        }
        Refresh();
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 0.5f;
        Refresh();
    }

    private void Refresh()
    {
        CompoundItem body = null;
        try { body = _screen == null ? null : Traverse.Create(_screen).Field("_itemBody").GetValue<Item>() as CompoundItem; } catch { /* closing */ }
        var score = ArenaStockBuildEditor.CurrentScore(body);
        float max = Mathf.Max(ArenaMetascore.BandMax, 1f);
        ArenaPresetsScreen.FillBar(_bars[0], score.Firepower, max, "", "");
        ArenaPresetsScreen.FillBar(_bars[1], score.Defense, max, "", "");
        ArenaPresetsScreen.FillBar(_bars[2], score.Utility, max, "", "");
        bool inBand = score.Total >= ArenaMetascore.BandMin && score.Total <= ArenaMetascore.BandMax;
        ArenaPresetsScreen.FillBar(_bars[3], score.Total, max, ArenaMetascore.BandMin.ToString("N0"), ArenaMetascore.BandMax.ToString("N0"),
            inBand ? null : new Color(0.85f, 0.25f, 0.25f, 1f));
    }

    private void OnDestroy()
    {
        _pack?.Dispose();
        _pack = null;
    }
}
