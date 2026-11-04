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
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Preset customization on EFT's own inventory UI: the equipment tab and containers panel of the character screen
/// show the kit's equipment, the armory is an endless grid beside them, and every drag, swap, plate, magazine and
/// load goes through EFT's drag-and-drop. One fake item controller owns the lot (the way the weapon build screen
/// owns its parts), so nothing touches the profile; when the editor closes, the difference between the edited
/// equipment and the preset is sent to the server as the same swaps the live-styled editor used.
/// </summary>
internal sealed class ArenaKitInventoryEditor : IDisposable
{
    private static readonly EquipmentSlot[] GearSlots =
    {
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.Holster, EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest,
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.Eyewear, EquipmentSlot.FaceCover,
    };
    private static readonly EquipmentSlot[] ContainerSlots = { EquipmentSlot.TacticalVest, EquipmentSlot.Pockets };

    internal static ArenaKitInventoryEditor Current { get; private set; }
    private static bool _patched;

    private readonly KitDefinition _kit;
    private readonly InventoryEquipment _equipment;
    private readonly Profile _profile;
    private readonly Stash _stash;
    private readonly ItemController _controller;
    private readonly ItemContext _context;
    private readonly List<SlotView> _slotViews = new();
    private EquipmentTab _gear;
    private ContainersPanel _containers;
    private GridView _armory;
    private CornucopiaGrid _armoryGrid;
    private bool _disposed;

    internal ItemController Controller => _controller;

    internal ArenaKitInventoryEditor(KitDefinition kit, InventoryEquipment equipment, Profile profile,
        RectTransform gearHost, RectTransform containersHost, Action<bool> openBuild)
    {
        _kit = kit ?? throw new ArgumentNullException(nameof(kit));
        _equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        _profile = profile;
        Enable();
        // The owner of everything on screen: a fake stash whose first grid is the endless armory and whose second,
        // never shown, holds the equipment so its slots resolve to this controller.
        _stash = Comfort.Common.Singleton<ItemFactory>.Instance.CreateFakeStash(null);
        _armoryGrid = NewArmoryGrid();
        var hidden = new CornucopiaGrid(Guid.NewGuid().ToString(), 1, 1, true, Array.Empty<ItemFilter>(), _stash);
        _stash.Grids = new Grid[] { _armoryGrid, hidden };
        _controller = new ItemController(_stash, profile?.Id ?? "arena", "arena kit editor", false);
        if (_equipment.CurrentAddress != null) throw new InvalidOperationException("the kit equipment is already placed somewhere");
        var placed = hidden.AddAnywhere(_equipment, EErrorHandlingType.Ignore);
        if (placed.Failed) throw new InvalidOperationException("the kit equipment could not be owned: " + placed.Error);
        _context = new DefaultItemContext(_equipment, EItemViewType.Inventory);
        var ui = ItemUiContext.Instance ?? throw new InvalidOperationException("EFT's item UI context is not ready");
        try { ui.RegisterView(_context); } catch { /* older signature */ }

        var panel = Resources.FindObjectsOfTypeAll<ItemsPanel>().FirstOrDefault(p => p != null && p.gameObject.scene.IsValid())
                    ?? throw new InvalidOperationException("EFT's inventory items panel is not loaded in this scene");
        BuildGear(panel, gearHost, ui);
        BuildContainers(panel, containersHost, ui);
        BuildButtons(openBuild);
        Plugin.Instance.StartCoroutine(SettleLayout());
        Current = this;
        Plugin.ModLogger.LogInfo($"Arena kit editor: EFT inventory UI on '{_kit.Id}' ({_slotViews.Count} slot views).");
    }

    private CornucopiaGrid NewArmoryGrid()
        => new CornucopiaGrid(Guid.NewGuid().ToString(), 10, 1, true, Array.Empty<ItemFilter>(), _stash);

    private void BuildGear(ItemsPanel panel, RectTransform host, ItemUiContext ui)
    {
        var template = panel._equipmentPanel ?? throw new InvalidOperationException("no equipment tab template");
        _gear = Object.Instantiate(template, host, false);
        _gear.name = "ArenaKitGear";
        var rect = (RectTransform)_gear.transform;
        rect.anchorMin = new Vector2(0.5f, 1f); rect.anchorMax = new Vector2(0.5f, 1f); rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -8f);
        rect.localScale = Vector3.one * 0.92f;
        _gear.gameObject.SetActive(true);
        var views = new (EquipmentSlot Slot, SlotView View)[]
        {
            (EquipmentSlot.Earpiece, _gear._earpieceSlot), (EquipmentSlot.Headwear, _gear._headwearSlot),
            (EquipmentSlot.FaceCover, _gear._faceCoverSlot), (EquipmentSlot.ArmBand, _gear._armbandSlot),
            (EquipmentSlot.ArmorVest, _gear._armorSlot), (EquipmentSlot.Eyewear, _gear._eyewearSlot),
            (EquipmentSlot.FirstPrimaryWeapon, _gear._primaryWeaponSlot), (EquipmentSlot.Holster, _gear._holsterSlot),
            (EquipmentSlot.SecondPrimaryWeapon, _gear._seconaryWeaponSlot), (EquipmentSlot.Scabbard, _gear._scabbardSlot),
            (EquipmentSlot.Dogtag, _gear._dogTagSlot),
        };
        foreach (var (slotType, view) in views)
        {
            if (view == null) continue;
            var slot = _equipment.GetSlot(slotType);
            bool shown = slot != null && slotType != EquipmentSlot.Dogtag && slotType != EquipmentSlot.ArmBand;
            view.gameObject.SetActive(shown);
            if (!shown) continue;
            view.Show(slot, _context, _controller, ui, _profile?.Skills, null, false);
            _slotViews.Add(view);
        }
    }

    private void BuildContainers(ItemsPanel panel, RectTransform host, ItemUiContext ui)
    {
        var template = panel._containers ?? throw new InvalidOperationException("no containers panel template");
        _containers = Object.Instantiate(template, host, false);
        _containers.name = "ArenaKitContainers";
        var rect = (RectTransform)_containers.transform;
        rect.anchorMin = new Vector2(0f, 1f); rect.anchorMax = new Vector2(0f, 1f); rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(8f, -8f);
        rect.sizeDelta = new Vector2(Mathf.Max(300f, host.rect.width / 0.92f - 16f), 100f);
        rect.localScale = Vector3.one * 0.92f;
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, 900f);
        foreach (var group in _containers.GetComponentsInChildren<LayoutGroup>(true)) group.enabled = false;
        foreach (var fit in _containers.GetComponentsInChildren<ContentSizeFitter>(true)) fit.enabled = false;
        _containers._slotViewsContainer.gameObject.AddComponent<ArenaStacker>();
        _containers.gameObject.SetActive(true);
        foreach (Transform old in _containers._slotViewsContainer) Object.Destroy(old.gameObject);
        foreach (var slotType in ContainerSlots)
        {
            var slot = _equipment.GetSlot(slotType);
            if (slot == null) continue;
            var view = _containers.InstantiateSlotView(slotType);
            view.transform.SetParent(_containers._slotViewsContainer, false);
            view.Show(slot, _context, _controller, ui, _profile?.Skills, null, false);
            _slotViews.Add(view);
        }
    }

    /// <summary>Live's build-editor arrow, as a BUILD tag on the two weapon slots.</summary>
    private void BuildButtons(Action<bool> openBuild)
    {
        if (openBuild == null || _gear == null) return;
        foreach (var (view, holster) in new[] { (_gear._primaryWeaponSlot, false), (_gear._holsterSlot, true) })
        {
            if (view == null || !view.gameObject.activeSelf) continue;
            var rect = new GameObject("ArenaBuild", typeof(RectTransform)).GetComponent<RectTransform>();
            rect.gameObject.layer = 5;
            rect.SetParent(view.transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f); rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-4f, -4f); rect.sizeDelta = new Vector2(84f, 24f);
            var fill = rect.gameObject.AddComponent<UnityEngine.UI.Image>();
            fill.color = new Color(0.79f, 0.54f, 0f, 0.25f); fill.raycastTarget = true;
            var label = HudStyle.CreateText(rect, "Label", 13f, TextAlignmentOptions.Midline);
            label.text = "BUILD \u203A"; label.color = new Color(0.98f, 0.9f, 0.7f);
            ArenaUiBuilder.Stretch(label.rectTransform);
            bool captured = holster;
            rect.gameObject.AddComponent<ArenaMenuButton>().Init(rect, () => openBuild(captured), null, false);
        }
    }

    private System.Collections.IEnumerator SettleLayout()
    {
        yield break;
    }

    /// <summary>Stacks its children top-down by their rendered bounds (EFT's slot views grow after they show).</summary>
    private sealed class ArenaStacker : MonoBehaviour
    {
        private void LateUpdate()
        {
            float cursor = 0f;
            foreach (Transform t in transform)
            {
                if (t is not RectTransform child || !child.gameObject.activeSelf) continue;
                var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(child, child);
                if (bounds.size.y < 1f) continue;
                child.anchorMin = child.anchorMax = new Vector2(0f, 1f);
                var wanted = new Vector2(-bounds.min.x, -cursor - bounds.max.y);
                if ((child.anchoredPosition - wanted).sqrMagnitude > 0.25f) child.anchoredPosition = wanted;
                cursor += bounds.size.y + 14f;
            }
        }
    }

    private void BuildArmory(RectTransform host, ItemUiContext ui)
    {
        var template = Resources.FindObjectsOfTypeAll<GridView>().FirstOrDefault(g => g != null && g._highlightPanel != null && g.gameObject.scene.IsValid())
                       ?? throw new InvalidOperationException("no grid view template is loaded in this scene");
        _armory = Object.Instantiate(template, host, false);
        _armory.name = "ArenaKitArmory";
        var rect = (RectTransform)_armory.transform;
        rect.anchorMin = new Vector2(0f, 1f); rect.anchorMax = new Vector2(0f, 1f); rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = Vector2.zero;
        _armory.gameObject.SetActive(true);
        _armory.Show(_armoryGrid, _context, _controller, ui);
    }

    /// <summary>Fills the armory grid with these items (the current tab); earlier contents are dropped.</summary>
    internal void ShowArmory(IEnumerable<Item> items)
    {
        if (_disposed || _armory == null) return;
        try { _armory.Close(); } catch { /* never shown */ }
        _armoryGrid = NewArmoryGrid();
        _stash.Grids[0] = _armoryGrid;
        int added = 0;
        foreach (var item in items)
        {
            if (item == null) continue;
            try
            {
                item.StackObjectsCount = Math.Max(1, item.StackMaxSize);
                foreach (var part in item.GetAllItems()) part.PinLockState = EItemPinLockState.Free;
                if (_armoryGrid.AddAnywhere(item, EErrorHandlingType.Ignore).Succeeded) added++;
            }
            catch (Exception ex) { Plugin.ModLogger.LogDebug($"Arena kit editor: {item.TemplateId} skipped: {ex.Message}"); }
        }
        _armory.Show(_armoryGrid, _context, _controller, ItemUiContext.Instance);
        Plugin.ModLogger.LogDebug($"Arena kit editor: armory shows {added} items.");
    }

    // ---------------------------------------------------------------- apply

    /// <summary>Sends what changed to the server as preset edits; true when anything was sent.</summary>
    internal bool Apply()
    {
        if (_disposed) return false;
        int sent = 0;
        try
        {
            var kit = Latest();
            var root = kit.Items.FirstOrDefault(n => n.ParentId == null);
            if (root == null) return false;
            // 1. Gear slots: the item worn now against the preset's.
            foreach (var slot in GearSlots)
            {
                string now = _equipment.GetSlot(slot)?.ContainedItem?.TemplateId;
                string was = kit.Items.FirstOrDefault(n => n.ParentId == root.Id && string.Equals(n.SlotId, slot.ToString(), StringComparison.OrdinalIgnoreCase))?.Tpl;
                if (now == null || string.Equals(now, was, StringComparison.OrdinalIgnoreCase)) continue;
                if (ArenaArmory.Edit("swap", kit.Id, slot.ToString(), now) == null) sent++;
            }
            kit = Latest();
            root = kit.Items.FirstOrDefault(n => n.ParentId == null) ?? root;
            // 2. Weapon trees and plates, parents before children.
            sent += ApplyTree(kit, root, EquipmentSlot.FirstPrimaryWeapon, "mod:primary/", false);
            sent += ApplyTree(kit, root, EquipmentSlot.Holster, "mod:holster/", false);
            sent += ApplyTree(kit, root, EquipmentSlot.ArmorVest, "mod:armor/", true);
            sent += ApplyTree(kit, root, EquipmentSlot.TacticalVest, "mod:rig/", true);
            // 3. Ammo: what the primary's magazine holds.
            kit = Latest();
            string ammoNow = null;
            try
            {
                var weapon = _equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem as Weapon;
                ammoNow = weapon?.GetCurrentMagazine()?.Cartridges?.Items?.FirstOrDefault()?.TemplateId;
            }
            catch { /* no magazine */ }
            string ammoWas = kit.Items.FirstOrDefault(n => n.SlotId == "cartridges")?.Tpl;
            if (ammoNow != null && !string.Equals(ammoNow, ammoWas, StringComparison.OrdinalIgnoreCase)
                && ArenaArmory.Edit("swap", kit.Id, "Ammo", ammoNow) == null) sent++;
            // 4. Supplies in the rig and pockets (magazines are the preset's spare-magazine count, not supplies).
            kit = Latest();
            var now2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in KitBuilder.GridContainersOf(_equipment))
                foreach (var grid in container.Grids)
                    foreach (var pair in grid.ItemCollection)
                        if (pair.Key is not Magazine) now2[pair.Key.TemplateId] = now2.TryGetValue(pair.Key.TemplateId, out int c) ? c + 1 : 1;
            var was2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var supply in kit.Fill?.RigContents ?? new List<KitStackedItem>())
                was2[supply.Tpl] = was2.TryGetValue(supply.Tpl, out int c) ? c + 1 : 1;
            // Items the kit itself places inside the rig or pockets (grenades, authored meds) are not supplies to add.
            var rigIds = new HashSet<string>(kit.Items.Where(n => n.ParentId == root.Id
                && n.SlotId is "TacticalVest" or "Pockets").Select(n => n.Id), StringComparer.Ordinal);
            foreach (var node in kit.Items)
                if (node.ParentId != null && rigIds.Contains(node.ParentId) && (node.SlotId ?? "").IndexOf("plate", StringComparison.OrdinalIgnoreCase) < 0)
                    was2[node.Tpl] = was2.TryGetValue(node.Tpl, out int c2) ? c2 + 1 : 1;
            foreach (var tpl in now2.Keys.ToList())
                if (Comfort.Common.Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), tpl, null) is Magazine or ThrowWeap) now2.Remove(tpl);
            foreach (var tpl in now2.Keys.Union(was2.Keys, StringComparer.OrdinalIgnoreCase).ToList())
            {
                int have = now2.TryGetValue(tpl, out int a) ? a : 0, had = was2.TryGetValue(tpl, out int b) ? b : 0;
                for (int i = had; i < have; i++) if (ArenaArmory.Edit("add", kit.Id, "Meds", tpl) == null) sent++;
                for (int i = have; i < had; i++) if (ArenaArmory.Edit("remove", kit.Id, "Meds", tpl) == null) sent++;
            }
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena kit editor: applying the preset failed: " + ex.Message); }
        Plugin.ModLogger.LogInfo($"Arena kit editor: {sent} change(s) sent for '{_kit.Id}'.");
        return sent > 0;
    }

    private KitDefinition Latest()
        => KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, _kit.Id, StringComparison.OrdinalIgnoreCase)) ?? _kit;

    private int ApplyTree(KitDefinition kit, KitItemNode root, EquipmentSlot slot, string prefix, bool platesOnly)
    {
        var worn = _equipment.GetSlot(slot)?.ContainedItem as CompoundItem;
        var node = kit.Items.FirstOrDefault(n => n.ParentId == root.Id && string.Equals(n.SlotId, slot.ToString(), StringComparison.OrdinalIgnoreCase));
        if (worn == null || node == null || !string.Equals(worn.TemplateId, node.Tpl, StringComparison.OrdinalIgnoreCase)) return 0;
        var was = new Dictionary<string, string>(StringComparer.Ordinal);
        KitPaths(kit, node, "", was);
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(worn, "", now);
        int sent = 0;
        foreach (var change in now.Where(kv => !was.TryGetValue(kv.Key, out var old) || !string.Equals(old, kv.Value, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(kv => kv.Key.Count(c => c == '/')).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (platesOnly && change.Key.IndexOf("plate", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (change.Value.Length == 0 && !was.ContainsKey(change.Key)) continue;      // empty then, empty now
            if (change.Key.StartsWith("cartridges", StringComparison.Ordinal) || change.Key.Contains("/cartridges")) continue;
            if (ArenaArmory.Edit("swap", kit.Id, prefix + change.Key, change.Value) == null) sent++;
        }
        return sent;
    }

    private static void KitPaths(KitDefinition kit, KitItemNode parent, string prefix, Dictionary<string, string> map)
    {
        foreach (var child in kit.Items.Where(n => n.ParentId == parent.Id))
        {
            if (string.IsNullOrEmpty(child.SlotId)) continue;
            string path = prefix + child.SlotId;
            map[path] = child.Tpl;
            KitPaths(kit, child, path + "/", map);
        }
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

    // ---------------------------------------------------------------- lifetime

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        foreach (var view in _slotViews) { try { view.Close(); } catch { /* gone */ } }
        _slotViews.Clear();
        try { _armory?.Close(); } catch { /* gone */ }
        try { ItemUiContext.Instance?.UnregisterView(_context); } catch { /* older signature */ }
        if (_gear != null) Object.Destroy(_gear.gameObject);
        if (_containers != null) Object.Destroy(_containers.gameObject);
        if (_armory != null) Object.Destroy(_armory.gameObject);
        _gear = null; _containers = null; _armory = null;
    }

    // ---------------------------------------------------------------- score labels

    private static void Enable()
    {
        if (_patched) return;
        _patched = true;
        new ScorePatch().Enable();
    }

    /// <summary>Every item view in the editor's armory carries its metascore, where a price would sit elsewhere.</summary>
    private sealed class ScorePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GridItemView), nameof(GridItemView.Create));

        [PatchPostfix]
        private static void Postfix(Item item, ItemController itemController, GridItemView __result)
        {
            if (__result == null) return;
            var rect = (RectTransform)__result.transform;
            var label = rect.Find("ArenaScore")?.GetComponent<TextMeshProUGUI>();
            var editor = Current;
            if (editor == null || item == null || !ReferenceEquals(itemController, editor._controller))
            {
                if (label != null) label.gameObject.SetActive(false);   // pooled view reused elsewhere
                return;
            }
            if (label == null)
            {
                label = HudStyle.CreateText(rect, "ArenaScore", 11f, TextAlignmentOptions.BottomLeft);
                label.rectTransform.anchorMin = Vector2.zero;
                label.rectTransform.anchorMax = new Vector2(1f, 0f);
                label.rectTransform.pivot = Vector2.zero;
                label.rectTransform.anchoredPosition = new Vector2(3f, 2f);
                label.rectTransform.sizeDelta = new Vector2(-6f, 13f);
                label.raycastTarget = false;
            }
            label.gameObject.SetActive(true);
            label.transform.SetAsLastSibling();
            label.text = ArenaMetascore.ItemScore(item.TemplateId).ToString("N0");
            label.color = new Color(0.35f, 0.8f, 0.75f);
        }
    }
}
