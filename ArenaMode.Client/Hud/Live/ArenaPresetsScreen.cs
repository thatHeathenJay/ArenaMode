using ArenaMode.Config;
using ArenaMode.Kits;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's preset screen (ArenaPresetViewScreen): EFT's gear cells with the preset's items as real icons, the
/// character in the preset, the rig and pocket contents, the metascore against the live band; class tabs and a
/// preset switcher in Arena's preset-customization header; Select makes the preset the kit for every life.
/// Customize copies a stock preset into a custom one; editing a custom preset makes the gear cells clickable and
/// swaps a slot from the server's armory (class-allowed items, live ones first), re-scored against the band.
/// </summary>
internal sealed class ArenaPresetsScreen : MonoBehaviour
{
    private static readonly (string Id, string Label)[] Classes =
    {
        ("assault", "ASSAULT"), ("enforcer", "ENFORCER"), ("scout", "SCOUT"), ("marksman", "MARKSMAN"),
    };
    // Gear cell -> equipment slot. Arena's panel has one weapon cell (On Back) for the preset's primary.
    // Gear slots the armory can swap; the rest (armband, scabbard) are fixed.
    private static readonly HashSet<EquipmentSlot> Editable = new()
    {
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.Holster, EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest,
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.Eyewear, EquipmentSlot.FaceCover,
    };
    private static readonly (string Cell, EquipmentSlot Slot)[] Cells =
    {
        ("first line/Earpiece Slot", EquipmentSlot.Earpiece), ("first line/Headwear Slot", EquipmentSlot.Headwear),
        ("first line/FaceCover Slot", EquipmentSlot.FaceCover), ("second line/Armband Slot", EquipmentSlot.ArmBand),
        ("second line/BodyArmor Slot", EquipmentSlot.ArmorVest), ("second line/Eyewear Slot", EquipmentSlot.Eyewear),
        ("third line/On Belt Slot", EquipmentSlot.TacticalVest), ("third line/Holster Slot", EquipmentSlot.Holster),
        ("forth line/On Back Slot", EquipmentSlot.FirstPrimaryWeapon), ("forth line/Scabbard Slot", EquipmentSlot.Scabbard),
    };
    private static readonly Color Text = new(0.882f, 0.933f, 0.957f, 1f);
    private static readonly Color Muted = new(0.69f, 0.729f, 0.757f, 1f);
    private static readonly Color Gold = new(0.79f, 0.54f, 0f, 1f);
    private static readonly Color Bad = new(0.85f, 0.25f, 0.25f, 1f);

    private static ArenaPresetsScreen _instance;
    private ArenaUiPack _pack;
    private ArenaUiBuilder _builder;
    private JObject _apcLayout, _viewLayout, _editorLayout;
    private RectTransform _body, _presetList, _nameField, _armoryList;
    private bool _editing;
    private TextMeshProUGUI _status, _presetName;
    private readonly List<(string id, RectTransform tab)> _tabViews = new();
    private string _class = "assault";
    private KitDefinition _shown;
    private Session.ArenaCharacterPreview _preview;
    private InventoryEquipment _equipment;

    internal static bool Visible => _instance != null;

    internal static void Show()
    {
        if (_instance != null || !ArenaUiPack.IsAvailable) return;
        var go = new GameObject("ArenaPresetsScreen");
        go.transform.SetParent(Plugin.Instance.transform, false);
        _instance = go.AddComponent<ArenaPresetsScreen>();
        try { _instance.Build(); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena presets screen failed: " + ex);
            Hide();
        }
    }

    internal static void Hide()
    {
        if (_instance == null) return;
        var instance = _instance;
        _instance = null;
        Destroy(instance.gameObject);
    }

    private void OnDestroy()
    {
        try { _preview?.Dispose(); } catch { /* gone */ }
        _preview = null;
        _pack?.Dispose();
        _pack = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_armoryList != null && _armoryList.gameObject.activeSelf) _armoryList.gameObject.SetActive(false);
            else Hide();
            return;
        }
        if (_preview != null && Input.GetMouseButton(0))
        {
            var mouse = Input.mousePosition;
            float x = mouse.x / Screen.width, y = mouse.y / Screen.height;
            if (x > 0.34f && x < 0.66f && y < 0.9f) _preview.Rotate(-Input.GetAxis("Mouse X"), 1f);
        }
    }

    // ---------------------------------------------------------------- build

    private void Build()
    {
        _pack = new ArenaUiPack();
        _builder = new ArenaUiBuilder(_pack, key => key switch
        {
            "APC/CustomizationTab" => "CUSTOMIZATION", "APC/ViewPreset" => "VIEW", "Arena/EnterPresetName" => "",
            "ArenaUI/PresetView/LockedPreset" => "LOCKED", "ArenaUI/PresetView/FreePreset" => "FREE",
            "ArenaUI/PresetView/PresetPreview" => "PRESET PREVIEW", _ => null,
        });
        _apcLayout = _pack.Layout("ArenaPresetCustomizationScreen");
        _viewLayout = _pack.Layout("ArenaPresetViewScreen");
        _editorLayout = _pack.Layout("ArenaBuildEditorScreen");

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 520;
        canvas.overrideSorting = true;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();
        var root = (RectTransform)transform;

        var fill = NewRect("Backdrop", root);
        ArenaUiBuilder.Stretch(fill);
        fill.gameObject.AddComponent<Image>().color = new Color(0.03f, 0.035f, 0.04f, 0.97f);
        var backdrop = _builder.Build(_pack.Layout("ArenaBattleBackgroundPanel"), root);
        ArenaUiBuilder.Stretch(backdrop);
        backdrop.GetComponentInChildren<Image>().color = new Color(1f, 1f, 1f, 0.3f);
        var glows = _builder.Build(Find(_viewLayout, "ScreenGlows"), root);
        ArenaUiBuilder.Stretch(glows);

        // Arena's preset view body: gear panel, model, containers.
        _body = _builder.Build(Find(_viewLayout, "Body"), root);
        _body.anchorMin = new Vector2(0.5f, 1f); _body.anchorMax = new Vector2(0.5f, 1f);
        _body.pivot = new Vector2(0.5f, 1f); _body.anchoredPosition = new Vector2(0f, -70f);
        _body.sizeDelta = new Vector2(1918f, 900f);

        BuildHeader(root);

        string selected = Settings.SelectedKit.Value;
        var selectedKit = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, selected, StringComparison.OrdinalIgnoreCase));
        _class = ClassOf(selectedKit?.Id) ?? "assault";
        SelectClass(_class, selectedKit);
    }

    private void BuildHeader(RectTransform root)
    {
        var header = _builder.Build(Find(_apcLayout, "PresetViewLabel"), root);
        header.anchorMin = new Vector2(0f, 1f); header.anchorMax = new Vector2(1f, 1f);
        header.pivot = new Vector2(0.5f, 1f); header.anchoredPosition = Vector2.zero; header.sizeDelta = new Vector2(0f, 60f);
        ArenaUiBuilder.SetActive(header, "ResetButton", false);
        ArenaUiBuilder.SetActive(header, "HighlightToggle", false);
        var midline = ArenaUiBuilder.Find(header, "HeaderLeftContent/MidlineInfo");
        ArenaUiBuilder.SetActive(midline, "TutorialButton", false);
        ArenaUiBuilder.SetActive(midline, "ButtonsPreset", false);
        _status = ArenaUiBuilder.Text(midline, "StatusPanel/Title");
        _presetName = ArenaUiBuilder.Text(midline, "NameFieldContainer/NameField/Text Area/Text");
        ArenaUiBuilder.SetActive(midline, "NameFieldContainer/FavoriteToggle", false);
        ArenaUiBuilder.SetActive(midline, "NameFieldContainer/NameField/Text Area/Placeholder", false);
        // The name field doubles as the preset switcher: click to list the class's presets.
        _nameField = ArenaUiBuilder.Find(midline, "NameFieldContainer");
        var nameHit = _nameField.GetComponent<Image>() ?? _nameField.gameObject.AddComponent<Image>();
        nameHit.raycastTarget = true;
        _nameField.gameObject.AddComponent<ArenaMenuButton>().Init(_nameField, TogglePresetList);
        _presetList = NewRect("PresetList", root);
        _presetList.anchorMin = _presetList.anchorMax = new Vector2(0.5f, 0.5f);
        _presetList.pivot = new Vector2(0f, 1f);
        _presetList.sizeDelta = new Vector2(380f, 0f);
        var listFill = _presetList.gameObject.AddComponent<Image>();
        listFill.color = new Color(0.05f, 0.06f, 0.07f, 0.97f);
        var listLayout = _presetList.gameObject.AddComponent<VerticalLayoutGroup>();
        listLayout.childForceExpandWidth = true; listLayout.childForceExpandHeight = false;
        listLayout.childControlWidth = true; listLayout.childControlHeight = true;
        _presetList.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        ArenaUiTheme.Rule(_presetList, "Edge", ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
        _presetList.gameObject.SetActive(false);

        var tabsHost = ArenaUiBuilder.Find(header, "HeaderLeftContent/TabsSelector");
        foreach (Transform child in tabsHost) Destroy(child.gameObject);
        var tabsLayout = tabsHost.gameObject.AddComponent<HorizontalLayoutGroup>();
        tabsLayout.spacing = 4f; tabsLayout.childForceExpandWidth = tabsLayout.childForceExpandHeight = false;
        tabsLayout.childControlWidth = tabsLayout.childControlHeight = true; tabsLayout.childAlignment = TextAnchor.MiddleLeft;
        var tabsElement = tabsHost.gameObject.AddComponent<LayoutElement>();
        tabsElement.preferredWidth = 4 * 200f + 12f; tabsElement.preferredHeight = 32f;
        var tabTemplate = Find(_apcLayout, "CustomizationTab");
        foreach (var (id, label) in Classes)
        {
            var tab = _builder.Build(tabTemplate, tabsHost);
            tab.gameObject.SetActive(true);
            var element = tab.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = 200f; element.preferredHeight = 32f;
            foreach (var state in new[] { "SelectedState", "IdleState", "HoverState" })
            {
                var title = ArenaUiBuilder.TryFind(tab, state + "/Title")?.GetComponent<TextMeshProUGUI>();
                if (title != null)
                {
                    title.text = label; title.enableAutoSizing = false; title.fontSize = 14f; title.alignment = TextAlignmentOptions.Midline;
                    title.rectTransform.anchorMin = Vector2.zero; title.rectTransform.anchorMax = Vector2.one;
                    title.rectTransform.offsetMin = new Vector2(12f, 0f); title.rectTransform.offsetMax = new Vector2(-12f, 0f);
                }
                ArenaUiBuilder.SetActive(tab, state + "/Image", false);
            }
            ArenaUiBuilder.SetActive(tab, "DisabledState", false);
            var hit = tab.GetComponent<Image>() ?? tab.gameObject.AddComponent<Image>();
            hit.raycastTarget = true;
            string captured = id;
            tab.gameObject.AddComponent<ArenaMenuButton>().Init(tab, () => SelectClass(captured, null));
            _tabViews.Add((id, tab));
        }

        var back = _builder.Build(Find(_apcLayout, "CloseButton"), root);
        back.anchorMin = back.anchorMax = new Vector2(1f, 1f); back.pivot = new Vector2(1f, 1f);
        back.anchoredPosition = new Vector2(-20f, -2f); back.sizeDelta = new Vector2(250f, 56f);
        foreach (var state in new[] { "Hover", "Idle" })
        {
            var text = ArenaUiBuilder.TryFind(back, state + "/Text (TMP)")?.GetComponent<TextMeshProUGUI>();
            if (text != null) text.text = "BACK";
        }
        var backHit = back.GetComponent<Image>() ?? back.gameObject.AddComponent<Image>();
        backHit.raycastTarget = true;
        back.gameObject.AddComponent<ArenaMenuButton>().Init(back, Hide);
    }

    // ------------------------------------------------------------- presets

    private static List<KitDefinition> PresetsOf(string classId)
        => KitCatalog.Kits.Where(k => ClassOf(k.Id) == classId).OrderBy(k => k.Order).ToList();

    private void SelectClass(string id, KitDefinition preferred)
    {
        _class = id;
        foreach (var (tabId, tab) in _tabViews)
        {
            ArenaUiBuilder.SetActive(tab, "SelectedState", tabId == id);
            ArenaUiBuilder.SetActive(tab, "IdleState", tabId != id);
        }
        _presetList.gameObject.SetActive(false);
        var presets = PresetsOf(id);
        var kit = preferred != null && ClassOf(preferred.Id) == id ? preferred
            : presets.FirstOrDefault(k => string.Equals(k.Id, Settings.SelectedKit.Value, StringComparison.OrdinalIgnoreCase)) ?? presets.FirstOrDefault();
        ShowPreset(kit);
    }

    private void TogglePresetList()
    {
        if (_presetList.gameObject.activeSelf) { _presetList.gameObject.SetActive(false); return; }
        foreach (Transform child in _presetList) Destroy(child.gameObject);
        var root = (RectTransform)transform;
        var corners = new Vector3[4];
        _nameField.GetWorldCorners(corners);          // 0 = bottom-left
        var local = root.InverseTransformPoint(corners[0]);
        _presetList.anchoredPosition = new Vector2(local.x, local.y);
        foreach (var kit in PresetsOf(_class))
        {
            var row = NewRect("Preset " + kit.Id, _presetList);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 40f;
            var rowImage = row.gameObject.AddComponent<Image>();
            rowImage.color = Color.clear; rowImage.raycastTarget = true;
            var label = HudStyle.CreateText(row, "Label", 18f, TextAlignmentOptions.MidlineLeft);
            label.text = (kit.Name ?? kit.Id).ToUpperInvariant();
            bool active = !Settings.RandomKit.Value && string.Equals(Settings.SelectedKit.Value, kit.Id, StringComparison.OrdinalIgnoreCase);
            label.color = active ? Gold : Text;
            ArenaUiBuilder.Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(16f, 0f);
            var captured = kit;
            row.gameObject.AddComponent<ArenaMenuButton>().Init(row, () => { _presetList.gameObject.SetActive(false); ShowPreset(captured); });
        }
        _presetList.gameObject.SetActive(true);
        _presetList.SetAsLastSibling();
    }

    private void ShowPreset(KitDefinition kit)
    {
        if (!ReferenceEquals(kit, _shown) && kit?.Id != _shown?.Id) _editing = false;
        if (_armoryList != null) _armoryList.gameObject.SetActive(false);
        _shown = kit;
        try { _preview?.Dispose(); } catch { /* gone */ }
        _preview = null;
        _equipment = null;
        RefreshStatus();
        if (kit == null) return;

        var profile = SessionProfile();
        try { if (profile != null) _equipment = TeamArenaEntryKit.Prepare(profile, kit); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning($"Arena preset screen: could not build '{kit.Id}': {ex.Message}"); }

        BindGear();
        BindContainers();
        BindModelAndStats(profile, kit);
    }

    private void RefreshStatus()
    {
        bool active = _shown != null && !Settings.RandomKit.Value
                      && string.Equals(Settings.SelectedKit.Value, _shown.Id, StringComparison.OrdinalIgnoreCase);
        _status.text = active ? "READY FOR BATTLE" : Settings.RandomKit.Value ? "RANDOM PRESET" : "PRESET PREVIEW";
        _presetName.text = _shown == null ? "" : (_shown.Name ?? _shown.Id);
    }

    private void BindGear()
    {
        var content = ArenaUiBuilder.Find(_body, "Gear Panel/Scrollview/Content");
        ArenaUiBuilder.SetActive(_body, "Gear Panel/Health Parameters", false);
        ArenaUiBuilder.SetActive(_body, "Gear Panel/Scrollbar", false);
        foreach (var (cellPath, slot) in Cells)
        {
            var cell = ArenaUiBuilder.TryFind(content, cellPath);
            if (cell == null) continue;
            var old = cell.Find("ItemIcon");
            if (old != null) Destroy(old.gameObject);
            var item = _equipment?.GetSlot(slot)?.ContainedItem
                       ?? (slot == EquipmentSlot.FirstPrimaryWeapon ? _equipment?.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem : null);
            bool filled = item != null;
            ArenaUiBuilder.SetActive(cell, "BackImage", !filled);
            ArenaUiBuilder.SetActive(cell, "Full Border", filled);
            ArenaUiBuilder.SetActive(cell, "Empty Border", !filled);
            ArenaUiBuilder.SetActive(cell, "Selected Border", false);
            var oldHit = cell.GetComponent<ArenaMenuButton>();
            if (oldHit != null) Destroy(oldHit);
            if (_editing && Editable.Contains(slot))
            {
                var hit = cell.GetComponent<Image>() ?? cell.gameObject.AddComponent<Image>();
                hit.color = Color.clear; hit.raycastTarget = true;
                var capturedCell = cell; var capturedSlot = slot;
                cell.gameObject.AddComponent<ArenaMenuButton>().Init(cell, () => ShowArmory(capturedCell, capturedSlot));
            }
            if (!filled) continue;
            var icon = NewRect("ItemIcon", cell);
            icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f);
            icon.pivot = new Vector2(0.5f, 0.5f);
            icon.anchoredPosition = new Vector2(0f, -8f);
            var image = icon.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            var size = cell.sizeDelta;
            ArenaItemIcon.Bind(image, item, size.x - 16f, size.y - 32f);
        }

        var bottom = ArenaUiBuilder.Find(_body, "Gear Panel/BottomPanel");
        float weight = 0f;
        try { weight = _equipment?.TotalWeight ?? 0f; } catch { /* unavailable */ }
        ArenaUiBuilder.Text(bottom, "SimpleWeightPanel/Value").text = $"{weight:0.0} KG";
        ArenaUiBuilder.SetActive(bottom, "PricePanel/ShopPrice", false);
        ArenaUiBuilder.SetActive(bottom, "PricePanel/LockedText", false);
        ArenaUiBuilder.SetActive(bottom, "PricePanel/FreeText", false);
        foreach (var name in new[] { "Select", "Customize", "Delete" })
        {
            var old = bottom.Find(name);
            if (old != null) Destroy(old.gameObject);
        }
        if (_shown == null) return;
        bool active = !Settings.RandomKit.Value
                      && string.Equals(Settings.SelectedKit.Value, _shown.Id, StringComparison.OrdinalIgnoreCase);
        bool custom = IsCustom(_shown.Id);
        BottomButton(bottom, "Select", active ? "SELECTED" : "SELECT", -16f, 240f, active, () =>
        {
            Settings.SelectedKit.Value = _shown.Id;
            Settings.RandomKit.Value = false;
            Plugin.ModLogger.LogInfo($"Arena preset selected: {_shown.Id}");
            ArenaMainMenuOverlay.RefreshCharacter();
            RefreshStatus();
            BindGear();
        });
        BottomButton(bottom, "Customize", custom ? (_editing ? "DONE" : "EDIT") : "CUSTOMIZE", -270f, 200f, _editing, () =>
        {
            if (custom) { _editing = !_editing; BindGear(); return; }
            if (ArenaArmory.Edit("create", _shown.Id) != null) return;
            var created = PresetsOf(_class).LastOrDefault(k => IsCustom(k.Id));
            if (created == null) return;
            ShowPreset(created);
            _editing = true;
            BindGear();
        });
        if (custom)
            BottomButton(bottom, "Delete", "DELETE", -484f, 160f, false, () =>
            {
                string id = _shown.Id, fallback = _shown.Base;
                if (ArenaArmory.Edit("delete", id) != null) return;
                if (string.Equals(Settings.SelectedKit.Value, id, StringComparison.OrdinalIgnoreCase))
                {
                    Settings.SelectedKit.Value = fallback ?? PresetsOf(_class).FirstOrDefault()?.Id ?? "";
                    ArenaMainMenuOverlay.RefreshCharacter();
                }
                SelectClass(_class, null);
            });
    }

    private void BottomButton(RectTransform bottom, string name, string caption, float x, float width, bool highlighted, Action onClick)
    {
        var button = NewRect(name, bottom);
        button.anchorMin = button.anchorMax = new Vector2(1f, 0.5f); button.pivot = new Vector2(1f, 0.5f);
        button.anchoredPosition = new Vector2(x, 0f); button.sizeDelta = new Vector2(width, 48f);
        var fill = button.gameObject.AddComponent<Image>();
        fill.color = highlighted ? new Color(0.79f, 0.54f, 0f, 0.18f) : new Color(0.52f, 0.60f, 0.67f, 0.12f);
        fill.raycastTarget = true;
        ArenaUiTheme.Rule(button, "Edge", highlighted ? ArenaUiTheme.WithAlpha(Gold, 0.8f) : ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
        var label = HudStyle.CreateText(button, "Label", 22f, TextAlignmentOptions.Midline);
        label.text = caption;
        label.color = highlighted ? Gold : Text;
        ArenaUiBuilder.Stretch(label.rectTransform);
        button.gameObject.AddComponent<ArenaMenuButton>().Init(button, onClick);
    }

    // ---------------------------------------------------------------- armory

    /// <summary>The armory for one gear slot, beside its cell: name and metascore per row, live items first.</summary>
    private void ShowArmory(RectTransform cell, EquipmentSlot slot)
    {
        var root = (RectTransform)transform;
        if (_armoryList == null)
        {
            _armoryList = NewRect("ArmoryList", root);
            _armoryList.anchorMin = _armoryList.anchorMax = new Vector2(0.5f, 0.5f);
            _armoryList.pivot = new Vector2(0f, 1f);
            _armoryList.sizeDelta = new Vector2(440f, 520f);
            var fill = _armoryList.gameObject.AddComponent<Image>();
            fill.color = new Color(0.05f, 0.06f, 0.07f, 0.97f);
            ArenaUiTheme.Rule(_armoryList, "Edge", ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
            var viewport = NewRect("Viewport", _armoryList);
            ArenaUiBuilder.Stretch(viewport);
            viewport.offsetMin = new Vector2(2f, 2f); viewport.offsetMax = new Vector2(-2f, -2f);
            viewport.gameObject.AddComponent<Image>().color = Color.white;
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var content = NewRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = _armoryList.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.scrollSensitivity = 40f;
        }
        var list = _armoryList.GetComponent<ScrollRect>().content;
        foreach (Transform child in list) Destroy(child.gameObject);
        var corners = new Vector3[4];
        cell.GetWorldCorners(corners);          // 3 = top-right
        var local = root.InverseTransformPoint(corners[3]);
        _armoryList.anchoredPosition = new Vector2(Mathf.Min(local.x + 8f, 960f - 440f), Mathf.Max(local.y, -540f + 520f));

        var title = HudStyle.CreateText(list, "Title", 14f, TextAlignmentOptions.MidlineLeft);
        title.text = slot.ToString().ToUpperInvariant() + "   -   " + _class.ToUpperInvariant() + " ARMORY";
        title.color = Muted;
        title.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;
        string current = _equipment?.GetSlot(slot)?.ContainedItem?.TemplateId
                         ?? (slot == EquipmentSlot.FirstPrimaryWeapon ? _equipment?.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem?.TemplateId : null);
        int rows = 0;
        foreach (var entry in ArenaArmory.ForSlot(slot.ToString(), _class))
        {
            rows++;
            var row = NewRect("Item " + entry.Tpl, list);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
            var rowImage = row.gameObject.AddComponent<Image>();
            rowImage.color = Color.clear; rowImage.raycastTarget = true;
            bool isCurrent = entry.Tpl == current;
            var name = HudStyle.CreateText(row, "Name", 16f, TextAlignmentOptions.MidlineLeft);
            name.text = (entry.Name ?? entry.Tpl).ToUpperInvariant() + (entry.Live ? "" : "  <size=11><color=#8A9AA6>(unranked)</color></size>");
            name.color = isCurrent ? Gold : Text;
            ArenaUiBuilder.Stretch(name.rectTransform);
            name.rectTransform.offsetMin = new Vector2(16f, 0f); name.rectTransform.offsetMax = new Vector2(-90f, 0f);
            var score = HudStyle.CreateText(row, "Score", 15f, TextAlignmentOptions.MidlineRight);
            score.text = entry.Score.ToString("N0");
            score.color = entry.Bar == "defense" ? new Color(0.45f, 0.72f, 0.95f) : entry.Bar == "utility" ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.95f, 0.62f, 0.35f);
            ArenaUiBuilder.Stretch(score.rectTransform);
            score.rectTransform.offsetMax = new Vector2(-14f, 0f);
            var captured = entry;
            row.gameObject.AddComponent<ArenaMenuButton>().Init(row, () =>
            {
                _armoryList.gameObject.SetActive(false);
                if (_shown == null || ArenaArmory.Edit("swap", _shown.Id, slot.ToString(), captured.Tpl) != null) return;
                var updated = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, _shown.Id, StringComparison.OrdinalIgnoreCase));
                bool keepEditing = _editing;
                ShowPreset(updated);
                _editing = keepEditing;
                BindGear();
                if (string.Equals(Settings.SelectedKit.Value, _shown?.Id, StringComparison.OrdinalIgnoreCase))
                    ArenaMainMenuOverlay.RefreshCharacter();
            });
        }
        if (rows == 0)
        {
            var empty = HudStyle.CreateText(list, "Empty", 15f, TextAlignmentOptions.MidlineLeft);
            empty.text = "Nothing in the armory for this slot."; empty.color = Muted;
            empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
        }
        _armoryList.gameObject.SetActive(true);
        _armoryList.SetAsLastSibling();
        _armoryList.GetComponent<ScrollRect>().verticalNormalizedPosition = 1f;
    }

    private void BindContainers()
    {
        var content = ArenaUiBuilder.Find(_body, "Containers Panel/Scrollview Parent/Containers Scrollview/Content");
        ArenaUiBuilder.SetActive(_body, "Containers Panel/Scrollbar", false);
        foreach (Transform child in content)
            if (!child.name.EndsWith("Template")) Destroy(child.gameObject);
        if (_equipment == null) return;
        var items = new List<Item>();
        foreach (var container in KitBuilder.GridContainersOf(_equipment))
        {
            if (ReferenceEquals(container, _equipment.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem)) continue;
            foreach (var g in container.Grids) items.AddRange(g.Items);
        }
        var title = HudStyle.CreateText(content, "Title", 14f, TextAlignmentOptions.MidlineLeft);
        title.text = "RIG AND POCKETS";
        title.color = Muted;
        title.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;
        var grid = NewRect("Grid", content);
        var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
        layout.cellSize = new Vector2(96f, 96f);
        layout.spacing = new Vector2(6f, 6f);
        layout.padding = new RectOffset(8, 8, 4, 4);
        layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        layout.constraintCount = 4;
        grid.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        foreach (var item in items)
        {
            var cell = NewRect("Cell " + item.TemplateId, grid);
            var back = cell.gameObject.AddComponent<Image>();
            back.color = new Color(0.08f, 0.1f, 0.12f, 0.9f);
            ArenaUiTheme.Rule(cell, "Edge", ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
            var icon = NewRect("ItemIcon", cell);
            icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f); icon.pivot = new Vector2(0.5f, 0.5f);
            var image = icon.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            ArenaItemIcon.Bind(image, item, 84f, 84f);
            if (item.StackObjectsCount > 1)
            {
                var count = HudStyle.CreateText(cell, "Count", 13f, TextAlignmentOptions.BottomRight);
                count.text = item.StackObjectsCount.ToString();
                ArenaUiBuilder.Stretch(count.rectTransform);
                count.rectTransform.offsetMin = new Vector2(0f, 3f); count.rectTransform.offsetMax = new Vector2(-5f, 0f);
            }
        }
    }

    private void BindModelAndStats(Profile profile, KitDefinition kit)
    {
        var overlay = ArenaUiBuilder.Find(_body, "PlayerCameraUIOverlay");
        ArenaUiBuilder.SetActive(overlay, "BlockedHighlight", false);
        ArenaUiBuilder.SetActive(overlay, "BlockedPreset", false);
        ArenaUiBuilder.SetActive(overlay, "RankingPresetHeader", false);
        ArenaUiBuilder.SetActive(overlay, "AddToFavorite", false);
        var host = ArenaUiBuilder.Find(overlay, "PlayerModelImage");
        foreach (Transform child in host) Destroy(child.gameObject);
        if (profile != null)
        {
            try { _preview = new Session.ArenaCharacterPreview(profile, host, () => { }, _equipment); }
            catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena preset screen: model unavailable: " + ex.Message); }
        }

        var header = ArenaUiBuilder.Find(overlay, "ClassPresetHeader");
        ArenaUiBuilder.Text(header, "NameInfo/ClassName").text = (kit.Name ?? kit.Id).ToUpperInvariant();
        ArenaUiBuilder.SetActive(header, "PresetRatingAndRankDescription", false);
        var classIcon = ArenaUiBuilder.TryFind(header, "ClassImage")?.GetComponent<Image>();
        int index = Array.FindIndex(Classes, c => c.Id == _class);
        if (classIcon != null && index >= 0)
        {
            var reference = new JObject
            {
                ["sprite"] = "Texture2D/Classes_Icon.png", ["name"] = "Classes_Icon_" + index,
                ["rect"] = new JArray(index * 53, 3, 52, 52), ["pivot"] = new JArray(0.5, 0.5), ["ppu"] = 100,
            };
            classIcon.sprite = _pack.Sprite(reference) ?? classIcon.sprite;
        }

        var stats = ArenaUiBuilder.Find(overlay, "Stats");
        foreach (Transform child in stats) Destroy(child.gameObject);
        stats.sizeDelta = new Vector2(420f, 0f);
        var score = ArenaMetascore.Score(kit);
        int max = Math.Max(ArenaMetascore.BandMax, 1);
        Bar(stats, "FIREPOWER", score.Firepower, max);
        Bar(stats, "DEFENSE", score.Defense, max);
        Bar(stats, "UTILITY", score.Utility, max);
        bool inBand = score.Total >= ArenaMetascore.BandMin && score.Total <= ArenaMetascore.BandMax;
        var total = HudStyle.CreateText(stats, "Total", 15f, TextAlignmentOptions.MidlineLeft);
        total.text = $"METASCORE {score.Total:N0}   ·   BAND {ArenaMetascore.BandMin:N0} – {ArenaMetascore.BandMax:N0}";
        total.color = inBand ? Text : Bad;
        total.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
    }

    private void Bar(RectTransform parent, string label, int value, int max)
    {
        var row = NewRect("Bar " + label, parent);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;
        var text = HudStyle.CreateText(row, "Label", 13f, TextAlignmentOptions.MidlineLeft);
        text.color = Muted; text.text = label;
        var textRect = text.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0f); textRect.anchorMax = new Vector2(0f, 1f); textRect.pivot = new Vector2(0f, 0.5f);
        textRect.anchoredPosition = Vector2.zero; textRect.sizeDelta = new Vector2(100f, 0f);
        var bar = _builder.Build(Find(_editorLayout, "APCMetascoreBar"), row);
        bar.gameObject.SetActive(true);
        bar.anchorMin = new Vector2(0f, 0.5f); bar.anchorMax = new Vector2(1f, 0.5f); bar.pivot = new Vector2(0f, 0.5f);
        bar.anchoredPosition = new Vector2(105f, 0f); bar.sizeDelta = new Vector2(-165f, 12f);
        ArenaUiBuilder.SetActive(bar, "Icon", false);
        ArenaUiBuilder.SetActive(bar, "Tooltip", false);
        ArenaUiBuilder.SetActive(bar, "BarImage (1)", false);
        var fillImage = ArenaUiBuilder.TryFind(bar, "BarImage")?.GetComponent<Image>();
        if (fillImage != null)
        {
            fillImage.type = Image.Type.Filled; fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillAmount = Mathf.Clamp01(value / (float)max);
        }
        foreach (var path in new[] { "IndicatorContainer/MiddleBar/Separator/MinValue", "IndicatorContainer/RightBorder/Separator/MaxValue", "ValuePlaceholder/ValueHandler/CurrentValueField" })
        {
            var t = ArenaUiBuilder.TryFind(bar, path)?.GetComponent<TextMeshProUGUI>();
            if (t != null) t.text = "";
        }
        var number = HudStyle.CreateText(row, "Value", 13f, TextAlignmentOptions.MidlineRight);
        number.color = Text; number.text = value.ToString("N0");
        var numberRect = number.rectTransform;
        numberRect.anchorMin = new Vector2(1f, 0f); numberRect.anchorMax = new Vector2(1f, 1f); numberRect.pivot = new Vector2(1f, 0.5f);
        numberRect.anchoredPosition = Vector2.zero; numberRect.sizeDelta = new Vector2(56f, 0f);
    }

    // ---------------------------------------------------------------- helpers

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private static JObject Find(JObject node, string name)
    {
        if (node.Value<string>("name") == name) return node;
        if (node["children"] is JArray children)
            foreach (var child in children)
                if (child is JObject c && Find(c, name) is { } found) return found;
        return null;
    }

    private static bool IsCustom(string kitId)
        => kitId != null && kitId.StartsWith("custom-", StringComparison.OrdinalIgnoreCase);

    private static string ClassOf(string kitId)
    {
        if (string.IsNullOrEmpty(kitId)) return null;
        if (IsCustom(kitId)) kitId = kitId.Substring(7);
        int dash = kitId.IndexOf('-');
        string prefix = dash > 0 ? kitId.Substring(0, dash) : kitId;
        return Classes.Any(c => c.Id == prefix) ? prefix : null;
    }

    private static Profile SessionProfile()
    {
        try { return SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile; }
        catch { return null; }
    }
}
