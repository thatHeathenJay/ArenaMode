using ArenaMode.Config;
using ArenaMode.Kits;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's preset screen (ArenaPresetCustomizationScreen header with its VIEW / CUSTOMIZATION tabs).
/// VIEW is Arena's ArenaPresetViewScreen body: EFT's gear cells with the preset's items as real icons, the character
/// in the preset, the rig and pocket contents, and Select. CUSTOMIZATION is Arena's GearsAndContainers panel (cells
/// with per-item metascore, bars against the band, weight) beside the APCItemsPanel armory (category tabs, class
/// filter, item cards). Clicking a card swaps it into its slot on the server; a stock preset is copied into a custom
/// one first. The name field doubles as the preset switcher; the header's copy and delete buttons manage customs.
/// </summary>
internal sealed class ArenaPresetsScreen : MonoBehaviour
{
    private static readonly (string Id, string Label)[] Classes =
    {
        ("assault", "ASSAULT"), ("enforcer", "ENFORCER"), ("scout", "SCOUT"), ("marksman", "MARKSMAN"),
    };
    // Gear cell -> equipment slot; the same cell names in both the view and the customization gear panels.
    private static readonly (string Cell, EquipmentSlot Slot)[] Cells =
    {
        ("first line/Earpiece Slot", EquipmentSlot.Earpiece), ("first line/Headwear Slot", EquipmentSlot.Headwear),
        ("first line/FaceCover Slot", EquipmentSlot.FaceCover), ("second line/Armband Slot", EquipmentSlot.ArmBand),
        ("second line/BodyArmor Slot", EquipmentSlot.ArmorVest), ("second line/Eyewear Slot", EquipmentSlot.Eyewear),
        ("third line/On Belt Slot", EquipmentSlot.TacticalVest), ("third line/Holster Slot", EquipmentSlot.Holster),
        ("forth line/On Back Slot", EquipmentSlot.FirstPrimaryWeapon), ("forth line/Scabbard Slot", EquipmentSlot.Scabbard),
    };
    private static readonly EquipmentSlot[] WeaponSlots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.Holster };
    // Armory tab -> the server's armory slots it lists.
    private static readonly Dictionary<string, string[]> TabSlots = new()
    {
        ["Weapons"] = new[] { "FirstPrimaryWeapon", "Holster", "Throwable" },
        ["Wearings"] = new[] { "ArmorVest", "TacticalVest", "Headwear", "Earpiece", "Eyewear", "FaceCover" },
        ["Meds"] = new[] { "Meds" },
        ["Ammo"] = new[] { "Ammo" },
    };
    private static readonly (string Tab, string Key)[] ItemTabs =
    {
        ("WeaponTab", "Weapons"), ("WearingsTab", "Wearings"), ("MedsTab", "Meds"), ("AmmoTab", "Ammo"),
    };
    private static readonly EquipmentSlot[] WearingSlots =
    {
        EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Headwear, EquipmentSlot.Earpiece,
        EquipmentSlot.Eyewear, EquipmentSlot.FaceCover,
    };
    private static readonly Color Text = new(0.882f, 0.933f, 0.957f, 1f);
    private static readonly Color Muted = new(0.69f, 0.729f, 0.757f, 1f);
    private static readonly Color Gold = new(0.79f, 0.54f, 0f, 1f);
    private static readonly Color Bad = new(0.85f, 0.25f, 0.25f, 1f);

    private static ArenaPresetsScreen _instance;
    private static readonly Dictionary<string, Item> ArmoryItems = new(StringComparer.Ordinal);

    private ArenaUiPack _pack;
    private ArenaUiBuilder _builder;
    private JObject _apcLayout, _viewLayout, _cardBig, _cardSmall;
    private RectTransform _viewBody, _customBody, _gears, _items, _presetList, _nameField, _viewTab, _customTab, _removeButton;
    private TextMeshProUGUI _status, _presetName;
    private Image _classIcon;
    private bool _customization;
    private string _itemsTab = "Weapons";
    private string _filterClass;      // armory class filter; null = every class
    private string _search = "";
    private EquipmentSlot? _focusSlot;
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
            if (_presetList != null && _presetList.gameObject.activeSelf) _presetList.gameObject.SetActive(false);
            else Hide();
            return;
        }
        if (!_customization && _preview != null && Input.GetMouseButton(0))
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
        _cardBig = _pack.Layout("APCItemViewBig");
        _cardSmall = _pack.Layout("APCItemViewSmall");

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
        fill.gameObject.AddComponent<Image>().color = new Color(0.03f, 0.035f, 0.04f, 1f);
        var backdrop = _builder.Build(_pack.Layout("ArenaBattleBackgroundPanel"), root);
        ArenaUiBuilder.Stretch(backdrop);
        backdrop.GetComponentInChildren<Image>().color = new Color(1f, 1f, 1f, 0.08f);
        var glows = _builder.Build(Find(_viewLayout, "ScreenGlows"), root);
        ArenaUiBuilder.Stretch(glows);

        // VIEW: Arena's preset view body (gear panel, model, containers).
        _viewBody = _builder.Build(Find(_viewLayout, "Body"), root);
        _viewBody.anchorMin = new Vector2(0.5f, 1f); _viewBody.anchorMax = new Vector2(0.5f, 1f);
        _viewBody.pivot = new Vector2(0.5f, 1f); _viewBody.anchoredPosition = new Vector2(0f, -70f);
        _viewBody.sizeDelta = new Vector2(1918f, 900f);

        // CUSTOMIZATION: Arena's gear-and-containers panel beside its items panel.
        _customBody = NewRect("Customization", root);
        _customBody.anchorMin = new Vector2(0.5f, 1f); _customBody.anchorMax = new Vector2(0.5f, 1f);
        _customBody.pivot = new Vector2(0.5f, 1f); _customBody.anchoredPosition = new Vector2(0f, -60f);
        _customBody.sizeDelta = new Vector2(1890f, 1000f);
        _gears = _builder.Build(Find(_apcLayout, "GearsAndContainers"), _customBody);
        _gears.anchorMin = new Vector2(0f, 1f); _gears.anchorMax = new Vector2(0f, 1f); _gears.pivot = new Vector2(0f, 1f);
        _gears.anchoredPosition = Vector2.zero; _gears.sizeDelta = new Vector2(1190f, 1000f);
        ArenaUiBuilder.SetActive(_gears, "DiscardItemView", false);
        ArenaUiBuilder.SetActive(_gears, "PointerBlocker", false);
        var itemsBackground = _builder.Build(Find(_apcLayout, "ItemsPanelBackground"), _customBody);
        itemsBackground.anchorMin = new Vector2(0f, 1f); itemsBackground.anchorMax = new Vector2(0f, 1f); itemsBackground.pivot = new Vector2(0f, 1f);
        itemsBackground.anchoredPosition = new Vector2(1190f, 0f); itemsBackground.sizeDelta = new Vector2(700f, 1000f);
        _items = ArenaUiBuilder.Find(itemsBackground, "APCItemsPanel");
        BuildItemsPanel();

        BuildHeader(root);

        string selected = Settings.SelectedKit.Value;
        var selectedKit = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, selected, StringComparison.OrdinalIgnoreCase))
                          ?? KitCatalog.Kits.FirstOrDefault();
        ShowPreset(selectedKit);
        SetTab(false);
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
        _status = ArenaUiBuilder.Text(midline, "StatusPanel/Title");
        _presetName = ArenaUiBuilder.Text(midline, "NameFieldContainer/NameField/Text Area/Text");
        _classIcon = ArenaUiBuilder.TryFind(midline, "NameFieldContainer/ClassIconContainer/ClassIcon")?.GetComponent<Image>();
        ArenaUiBuilder.SetActive(midline, "NameFieldContainer/FavoriteToggle", false);
        ArenaUiBuilder.SetActive(midline, "NameFieldContainer/NameField/Text Area/Placeholder", false);
        // The name field doubles as the preset switcher: click to list every preset.
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

        // Live's header buttons: copy (customize into a custom preset), delete (custom presets); save is implicit.
        var buttons = ArenaUiBuilder.Find(midline, "ButtonsPreset");
        ArenaUiBuilder.SetActive(buttons, "SaveButton", false);
        var copy = ArenaUiBuilder.Find(buttons, "CopyPresetButton");
        ArenaUiBuilder.SetActive(copy, "Content", false);
        HeaderButton(copy, () =>
        {
            if (_shown == null || !string.IsNullOrEmpty(ArenaArmory.Edit("create", _shown.Id))) return;
            var created = KitCatalog.Kits.Where(k => IsCustom(k.Id) && ClassOf(k.Id) == _class).OrderBy(k => k.Order).LastOrDefault();
            if (created != null) { ShowPreset(created); SetTab(true); }
        });
        _removeButton = ArenaUiBuilder.Find(buttons, "RemoveButton");
        HeaderButton(_removeButton, () =>
        {
            if (_shown == null || !IsCustom(_shown.Id)) return;
            string id = _shown.Id, fallback = _shown.Base;
            if (!string.IsNullOrEmpty(ArenaArmory.Edit("delete", id))) return;
            if (string.Equals(Settings.SelectedKit.Value, id, StringComparison.OrdinalIgnoreCase))
            {
                Settings.SelectedKit.Value = fallback ?? KitCatalog.Kits.FirstOrDefault()?.Id ?? "";
                ArenaMainMenuOverlay.RefreshCharacter();
            }
            ShowPreset(KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, fallback, StringComparison.OrdinalIgnoreCase))
                       ?? KitCatalog.Kits.FirstOrDefault());
            SetTab(false);
        });

        // VIEW / CUSTOMIZATION tabs, live's own toggles.
        var tabs = ArenaUiBuilder.Find(header, "HeaderLeftContent/TabsSelector");
        _viewTab = ArenaUiBuilder.Find(tabs, "ViewTab");
        _customTab = ArenaUiBuilder.Find(tabs, "CustomizationTab");
        foreach (var tab in new[] { _viewTab, _customTab })
        {
            ArenaUiBuilder.SetActive(tab, "DisabledState", false);
            ArenaUiBuilder.SetActive(tab, "HoverState", false);
            var hit = tab.GetComponent<Image>() ?? tab.gameObject.AddComponent<Image>();
            hit.raycastTarget = true;
        }
        _viewTab.gameObject.AddComponent<ArenaMenuButton>().Init(_viewTab, () => SetTab(false));
        _customTab.gameObject.AddComponent<ArenaMenuButton>().Init(_customTab, () => SetTab(true));

        var right = _builder.Build(Find(_apcLayout, "HeaderRightContent"), root);
        right.anchorMin = right.anchorMax = new Vector2(1f, 1f); right.pivot = new Vector2(1f, 1f);
        right.anchoredPosition = Vector2.zero; right.sizeDelta = new Vector2(520f, 60f);
        ArenaUiBuilder.SetActive(right, "ShootingRange", false);
        var back = ArenaUiBuilder.Find(right, "CloseButton");
        foreach (var state in new[] { "Hover", "Idle" })
        {
            var text = ArenaUiBuilder.TryFind(back, state + "/Text (TMP)")?.GetComponent<TextMeshProUGUI>();
            if (text != null) text.text = "BACK";
        }
        var backHit = back.GetComponent<Image>() ?? back.gameObject.AddComponent<Image>();
        backHit.raycastTarget = true;
        back.gameObject.AddComponent<ArenaMenuButton>().Init(back, Hide);
    }

    private static void HeaderButton(RectTransform button, Action onClick)
    {
        foreach (var state in new[] { "Hovered", "Pressed", "Disabled" }) ArenaUiBuilder.SetActive(button, state, false);
        var hit = button.GetComponent<Image>() ?? button.gameObject.AddComponent<Image>();
        hit.color = Color.clear; hit.raycastTarget = true;
        button.gameObject.AddComponent<ArenaMenuButton>().Init(button, onClick);
    }

    private void SetTab(bool customization)
    {
        _customization = customization;
        _viewBody.gameObject.SetActive(!customization);
        _customBody.gameObject.SetActive(customization);
        ArenaUiBuilder.SetActive(_viewTab, "SelectedState", !customization);
        ArenaUiBuilder.SetActive(_viewTab, "IdleState", customization);
        ArenaUiBuilder.SetActive(_customTab, "SelectedState", customization);
        ArenaUiBuilder.SetActive(_customTab, "IdleState", !customization);
        if (customization) BindCustomization();
        else BindView();
    }

    // ------------------------------------------------------------- presets

    private void TogglePresetList()
    {
        if (_presetList.gameObject.activeSelf) { _presetList.gameObject.SetActive(false); return; }
        foreach (Transform child in _presetList) Destroy(child.gameObject);
        var root = (RectTransform)transform;
        var corners = new Vector3[4];
        _nameField.GetWorldCorners(corners);          // 0 = bottom-left
        var local = root.InverseTransformPoint(corners[0]);
        _presetList.anchoredPosition = new Vector2(local.x, local.y);
        foreach (var (classId, label) in Classes)
            foreach (var kit in KitCatalog.Kits.Where(k => ClassOf(k.Id) == classId).OrderBy(k => k.Order))
            {
                var row = NewRect("Preset " + kit.Id, _presetList);
                row.gameObject.AddComponent<LayoutElement>().preferredHeight = 36f;
                var rowImage = row.gameObject.AddComponent<Image>();
                rowImage.color = Color.clear; rowImage.raycastTarget = true;
                var text = HudStyle.CreateText(row, "Label", 17f, TextAlignmentOptions.MidlineLeft);
                text.text = $"<color=#8A9AA6>{label}</color>   {(kit.Name ?? kit.Id).ToUpperInvariant()}";
                bool active = !Settings.RandomKit.Value && string.Equals(Settings.SelectedKit.Value, kit.Id, StringComparison.OrdinalIgnoreCase);
                text.color = active ? Gold : Text;
                ArenaUiBuilder.Stretch(text.rectTransform);
                text.rectTransform.offsetMin = new Vector2(16f, 0f);
                var captured = kit;
                row.gameObject.AddComponent<ArenaMenuButton>().Init(row, () =>
                {
                    _presetList.gameObject.SetActive(false);
                    ShowPreset(captured);
                    SetTab(_customization);
                });
            }
        _presetList.gameObject.SetActive(true);
        _presetList.SetAsLastSibling();
    }

    private void ShowPreset(KitDefinition kit)
    {
        _shown = kit;
        _focusSlot = null;
        try { _preview?.Dispose(); } catch { /* gone */ }
        _preview = null;
        _equipment = null;
        if (kit == null) { RefreshStatus(); return; }
        _class = ClassOf(kit.Id) ?? "assault";
        _filterClass = _class;
        var profile = SessionProfile();
        try { if (profile != null) _equipment = TeamArenaEntryKit.Prepare(profile, kit); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning($"Arena preset screen: could not build '{kit.Id}': {ex.Message}"); }
        RefreshStatus();
    }

    /// <summary>Re-reads the shown preset after a server edit and rebinds whichever tab is up.</summary>
    private void ReloadShown()
    {
        string id = _shown?.Id;
        var updated = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));
        ShowPreset(updated ?? KitCatalog.Kits.FirstOrDefault());
        SetTab(_customization);
        if (string.Equals(Settings.SelectedKit.Value, id, StringComparison.OrdinalIgnoreCase)) ArenaMainMenuOverlay.RefreshCharacter();
    }

    private static bool InBand(ArenaMetascore.KitScore score) => score.Total >= ArenaMetascore.BandMin && score.Total <= ArenaMetascore.BandMax;

    private void RefreshStatus()
    {
        bool selected = _shown != null && !Settings.RandomKit.Value
                        && string.Equals(Settings.SelectedKit.Value, _shown.Id, StringComparison.OrdinalIgnoreCase);
        bool inBand = _shown != null && InBand(ArenaMetascore.Score(_shown));
        _status.text = !inBand ? "NOT READY" : selected ? "READY FOR BATTLE" : "PRESET PREVIEW";
        _status.color = inBand ? Text : Bad;
        _presetName.text = _shown == null ? "" : (_shown.Name ?? _shown.Id);
        if (_classIcon != null) _classIcon.sprite = ClassSprite(_class) ?? _classIcon.sprite;
        if (_removeButton != null)
        {
            bool custom = _shown != null && IsCustom(_shown.Id);
            ArenaUiBuilder.SetActive(_removeButton, "Idle", custom);
            ArenaUiBuilder.SetActive(_removeButton, "Disabled", !custom);
        }
    }

    private Sprite ClassSprite(string classId)
    {
        int index = Array.FindIndex(Classes, c => c.Id == classId);
        if (index < 0) return null;
        var reference = new JObject
        {
            ["sprite"] = "Texture2D/Classes_Icon.png", ["name"] = "Classes_Icon_" + index,
            ["rect"] = new JArray(index * 53, 3, 52, 52), ["pivot"] = new JArray(0.5, 0.5), ["ppu"] = 100,
        };
        return _pack.Sprite(reference);
    }

    private Item ItemIn(EquipmentSlot slot)
        => _equipment?.GetSlot(slot)?.ContainedItem
           ?? (slot == EquipmentSlot.FirstPrimaryWeapon ? _equipment?.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem : null);

    // ---------------------------------------------------------------- view

    private void BindView()
    {
        var content = ArenaUiBuilder.Find(_viewBody, "Gear Panel/Scrollview/Content");
        ArenaUiBuilder.SetActive(_viewBody, "Gear Panel/Health Parameters", false);
        ArenaUiBuilder.SetActive(_viewBody, "Gear Panel/Scrollbar", false);
        BindCells(content, false);

        var bottom = ArenaUiBuilder.Find(_viewBody, "Gear Panel/BottomPanel");
        float weight = 0f;
        try { weight = _equipment?.TotalWeight ?? 0f; } catch { /* unavailable */ }
        ArenaUiBuilder.Text(bottom, "SimpleWeightPanel/Value").text = $"{weight:0.0} KG";
        ArenaUiBuilder.SetActive(bottom, "PricePanel/ShopPrice", false);
        ArenaUiBuilder.SetActive(bottom, "PricePanel/LockedText", false);
        ArenaUiBuilder.SetActive(bottom, "PricePanel/FreeText", false);
        var old = bottom.Find("Select");
        if (old != null) Destroy(old.gameObject);
        if (_shown != null)
        {
            bool active = !Settings.RandomKit.Value && string.Equals(Settings.SelectedKit.Value, _shown.Id, StringComparison.OrdinalIgnoreCase);
            var button = NewRect("Select", bottom);
            button.anchorMin = button.anchorMax = new Vector2(1f, 0.5f); button.pivot = new Vector2(1f, 0.5f);
            button.anchoredPosition = new Vector2(-16f, 0f); button.sizeDelta = new Vector2(240f, 48f);
            var fill = button.gameObject.AddComponent<Image>();
            fill.color = active ? new Color(0.79f, 0.54f, 0f, 0.18f) : new Color(0.52f, 0.60f, 0.67f, 0.12f);
            fill.raycastTarget = true;
            ArenaUiTheme.Rule(button, "Edge", active ? ArenaUiTheme.WithAlpha(Gold, 0.8f) : ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
            var label = HudStyle.CreateText(button, "Label", 22f, TextAlignmentOptions.Midline);
            label.text = active ? "SELECTED" : "SELECT";
            label.color = active ? Gold : Text;
            ArenaUiBuilder.Stretch(label.rectTransform);
            button.gameObject.AddComponent<ArenaMenuButton>().Init(button, () =>
            {
                if (_shown == null) return;
                Settings.SelectedKit.Value = _shown.Id;
                Settings.RandomKit.Value = false;
                Plugin.ModLogger.LogInfo($"Arena preset selected: {_shown.Id}");
                ArenaMainMenuOverlay.RefreshCharacter();
                RefreshStatus();
                BindView();
            });
        }

        BindContainers(ArenaUiBuilder.Find(_viewBody, "Containers Panel/Scrollview Parent/Containers Scrollview/Content"), false);
        ArenaUiBuilder.SetActive(_viewBody, "Containers Panel/Scrollbar", false);
        BindModelAndStats();
    }

    private void BindModelAndStats()
    {
        var overlay = ArenaUiBuilder.Find(_viewBody, "PlayerCameraUIOverlay");
        foreach (var path in new[] { "BlockedHighlight", "BlockedPreset", "RankingPresetHeader", "AddToFavorite" })
            ArenaUiBuilder.SetActive(overlay, path, false);
        var host = ArenaUiBuilder.Find(overlay, "PlayerModelImage");
        foreach (Transform child in host) Destroy(child.gameObject);
        try { _preview?.Dispose(); } catch { /* gone */ }
        _preview = null;
        var profile = SessionProfile();
        if (profile != null && _shown != null)
        {
            try { _preview = new Session.ArenaCharacterPreview(profile, host, () => { }, _equipment); }
            catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena preset screen: model unavailable: " + ex.Message); }
        }

        var header = ArenaUiBuilder.Find(overlay, "ClassPresetHeader");
        ArenaUiBuilder.Text(header, "NameInfo/ClassName").text = _shown == null ? "" : (_shown.Name ?? _shown.Id).ToUpperInvariant();
        ArenaUiBuilder.SetActive(header, "PresetRatingAndRankDescription", false);
        var classIcon = ArenaUiBuilder.TryFind(header, "ClassImage")?.GetComponent<Image>();
        if (classIcon != null) classIcon.sprite = ClassSprite(_class) ?? classIcon.sprite;

        // Metascore summary at the bottom-left of the model panel, clear of the character.
        var stats = ArenaUiBuilder.Find(overlay, "Stats");
        foreach (Transform child in stats) Destroy(child.gameObject);
        stats.anchorMin = stats.anchorMax = new Vector2(0f, 0f); stats.pivot = new Vector2(0f, 0f);
        stats.anchoredPosition = new Vector2(24f, 40f); stats.sizeDelta = new Vector2(380f, 0f);
        if (_shown == null) return;
        var score = ArenaMetascore.Score(_shown);
        var total = HudStyle.CreateText(stats, "Total", 15f, TextAlignmentOptions.MidlineLeft);
        total.text = $"METASCORE {score.Total:N0}   ·   BAND {ArenaMetascore.BandMin:N0} – {ArenaMetascore.BandMax:N0}";
        total.color = InBand(score) ? Text : Bad;
        total.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
        var detail = HudStyle.CreateText(stats, "Detail", 13f, TextAlignmentOptions.MidlineLeft);
        detail.text = $"FIREPOWER {score.Firepower:N0}   DEFENSE {score.Defense:N0}   UTILITY {score.Utility:N0}";
        detail.color = Muted;
        detail.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;
    }

    // ------------------------------------------------------- customization

    private void BindCustomization()
    {
        BindCells(ArenaUiBuilder.Find(_gears, "Gear Panel/Gears"), true);
        BindContainers(ArenaUiBuilder.Find(_gears, "Containers Panel/Scrollview Parent/Containers Scrollview/Content"), true);
        ArenaUiBuilder.SetActive(_gears, "Containers Panel/Horizontal Scrollbar", false);
        ArenaUiBuilder.SetActive(_gears, "Containers Panel/ArenaVertical Scrollbar", false);

        var score = _shown == null ? default : ArenaMetascore.Score(_shown);
        int max = Math.Max(ArenaMetascore.BandMax, 1);
        var handler = ArenaUiBuilder.Find(_gears, "Metascore/Handler");
        FillBar(ArenaUiBuilder.TryFind(handler, "APCMetascoreBar"), score.Firepower, max, "", "");
        FillBar(ArenaUiBuilder.TryFind(handler, "APCMetascoreBar (1)"), score.Defense, max, "", "");
        FillBar(ArenaUiBuilder.TryFind(handler, "APCMetascoreBar (2)"), score.Utility, max, "", "");
        FillBar(ArenaUiBuilder.TryFind(handler, "APCMetascoreBar (3)"), score.Total, max, ArenaMetascore.BandMin.ToString("N0"), ArenaMetascore.BandMax.ToString("N0"),
            InBand(score) ? (Color?)null : Bad);
        float weight = 0f;
        try { weight = _equipment?.TotalWeight ?? 0f; } catch { /* unavailable */ }
        FillBar(ArenaUiBuilder.TryFind(_gears, "BottomPanel/WeightBar"), weight, 40f, "", "", null, $"{weight:0.0}");

        BindItemsPanel();
    }

    private static void FillBar(RectTransform bar, float value, float max, string min, string maxLabel, Color? colour = null, string shown = null)
    {
        if (bar == null) return;
        bar.gameObject.SetActive(true);
        ArenaUiBuilder.SetActive(bar, "Tooltip", false);
        ArenaUiBuilder.SetActive(bar, "BarImage (1)", false);
        var fill = ArenaUiBuilder.TryFind(bar, "BarImage")?.GetComponent<Image>();
        if (fill != null)
        {
            fill.type = Image.Type.Filled; fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = Mathf.Clamp01(value / Mathf.Max(1f, max));
            if (colour.HasValue) fill.color = colour.Value;
        }
        SetText(bar, "IndicatorContainer/MiddleBar/Separator/MinValue", min);
        SetText(bar, "IndicatorContainer/RightBorder/Separator/MaxValue", maxLabel);
        SetText(bar, "ValuePlaceholder/ValueHandler/CurrentValueField", shown ?? value.ToString("N0"));
        SetText(bar, "ValuePlaceholder/ValueHandler/TipValueField", "");
    }

    private static void SetText(RectTransform root, string path, string text)
    {
        var t = ArenaUiBuilder.TryFind(root, path)?.GetComponent<TextMeshProUGUI>();
        if (t != null) t.text = text;
    }

    private void BindCells(RectTransform content, bool customization)
    {
        foreach (var (cellPath, slot) in Cells)
        {
            var cell = ArenaUiBuilder.TryFind(content, cellPath);
            if (cell == null) continue;
            var old = cell.Find("ItemIcon");
            if (old != null) Destroy(old.gameObject);
            var item = ItemIn(slot);
            bool filled = item != null;
            ArenaUiBuilder.SetActive(cell, "Slot/BackImage", !filled);
            ArenaUiBuilder.SetActive(cell, "BackImage", !filled);
            ArenaUiBuilder.SetActive(cell, "Frame/Full Border", filled);
            ArenaUiBuilder.SetActive(cell, "Frame/Empty Border", !filled);
            ArenaUiBuilder.SetActive(cell, "Full Border", filled);
            ArenaUiBuilder.SetActive(cell, "Empty Border", !filled);
            bool focused = customization && _focusSlot == slot;
            ArenaUiBuilder.SetActive(cell, "Frame/Selected Border", focused);
            ArenaUiBuilder.SetActive(cell, "Selected Border", false);
            foreach (var state in new[] { "SelectState", "AddState", "ReplaceState", "ThrowState", "HoveredState", "HoverState" })
                ArenaUiBuilder.SetActive(cell, state, false);
            if (customization)
            {
                ArenaUiBuilder.SetActive(cell, "Header/RightHeader/Arrow", false);
                SetText(cell, "Header/RightHeader/MetaScore/MetaScoreValue", filled ? ArenaMetascore.ItemScore(item.TemplateId).ToString("N0") : "0");
                var oldHit = cell.GetComponent<ArenaMenuButton>();
                if (oldHit != null) Destroy(oldHit);
                if (WeaponSlots.Contains(slot) || WearingSlots.Contains(slot))
                {
                    var hit = cell.GetComponent<Image>() ?? cell.gameObject.AddComponent<Image>();
                    hit.color = Color.clear; hit.raycastTarget = true;
                    var captured = slot;
                    cell.gameObject.AddComponent<ArenaMenuButton>().Init(cell, () => FocusSlot(captured));
                }
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
    }

    private void FocusSlot(EquipmentSlot slot)
    {
        _focusSlot = _focusSlot == slot ? null : slot;
        _itemsTab = WeaponSlots.Contains(slot) ? "Weapons" : "Wearings";
        BindCells(ArenaUiBuilder.Find(_gears, "Gear Panel/Gears"), true);
        BindItemsPanel();
    }

    /// <summary>
    /// Live's per-container view: every rig and pocket grid drawn cell by cell with its items in place. In
    /// customization the supplies the preset added (its rig contents) can be clicked off again.
    /// </summary>
    private void BindContainers(RectTransform content, bool customization)
    {
        foreach (Transform child in content)
            if (!child.name.EndsWith("Template") && child.name != "ArenaSearchableSlotView") Destroy(child.gameObject);
        ArenaUiBuilder.SetActive(content, "ArenaSearchableSlotView", false);
        if (_equipment == null) return;
        const float cell = 58f;
        var removable = new HashSet<string>((_shown?.Fill?.RigContents ?? new List<KitStackedItem>()).Select(r => r.Tpl), StringComparer.Ordinal);
        int index = 0;
        foreach (var container in KitBuilder.GridContainersOf(_equipment))
        {
            if (ReferenceEquals(container, _equipment.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem)) continue;
            index++;
            string name = "CONTAINER";
            try { name = container.ShortName.Localized().ToUpperInvariant(); } catch { /* unnamed */ }
            var title = HudStyle.CreateText(content, "Title " + index, 14f, TextAlignmentOptions.MidlineLeft);
            title.text = name; title.color = Muted;
            title.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;
            var row = NewRect("Grids " + index, content);
            var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 8f; rowLayout.padding = new RectOffset(8, 8, 2, 6);
            rowLayout.childForceExpandWidth = rowLayout.childForceExpandHeight = false;
            rowLayout.childControlWidth = rowLayout.childControlHeight = false;
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            float rowHeight = 0f;
            int gridIndex = 0;
            foreach (var grid in container.Grids)
            {
                int w = Math.Max(1, grid.GridWidth), h = Math.Max(1, grid.GridHeight);
                var panel = NewRect("Grid " + gridIndex++, row);
                panel.sizeDelta = new Vector2(w * cell, h * cell);
                var element = panel.gameObject.AddComponent<LayoutElement>();
                element.preferredWidth = w * cell; element.preferredHeight = h * cell;
                rowHeight = Mathf.Max(rowHeight, h * cell);
                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                    {
                        var back = NewRect($"Cell {x},{y}", panel);
                        Place(back, x, y, 1, 1, cell);
                        var backImage = back.gameObject.AddComponent<Image>();
                        backImage.color = new Color(0.08f, 0.1f, 0.12f, 0.9f); backImage.raycastTarget = false;
                        ArenaUiTheme.Rule(back, "Edge", ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
                    }
                foreach (KeyValuePair<Item, LocationInGrid> pair in grid.ItemCollection)
                {
                    var item = pair.Key;
                    var location = pair.Value;
                    var size = item.CalculateCellSize();
                    int iw = Math.Max(1, size.X), ih = Math.Max(1, size.Y);
                    if (location.r == ItemRotation.Vertical) (iw, ih) = (ih, iw);
                    var slot = NewRect("Item " + item.Id, panel);
                    Place(slot, location.x, location.y, iw, ih, cell);
                    bool canRemove = customization && removable.Contains(item.TemplateId);
                    var fill = slot.gameObject.AddComponent<Image>();
                    fill.color = canRemove ? new Color(0.2f, 0.24f, 0.27f, 0.95f) : new Color(0.14f, 0.17f, 0.2f, 0.95f);
                    fill.raycastTarget = canRemove;
                    ArenaUiTheme.Rule(slot, "Edge", canRemove ? ArenaUiTheme.WithAlpha(Gold, 0.5f) : ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
                    var icon = NewRect("ItemIcon", slot);
                    icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f); icon.pivot = new Vector2(0.5f, 0.5f);
                    var image = icon.gameObject.AddComponent<Image>();
                    image.raycastTarget = false;
                    ArenaItemIcon.Bind(image, item, iw * cell - 6f, ih * cell - 6f);
                    if (item.StackObjectsCount > 1)
                    {
                        var count = HudStyle.CreateText(slot, "Count", 13f, TextAlignmentOptions.BottomRight);
                        count.text = item.StackObjectsCount.ToString();
                        ArenaUiBuilder.Stretch(count.rectTransform);
                        count.rectTransform.offsetMin = new Vector2(0f, 3f); count.rectTransform.offsetMax = new Vector2(-5f, 0f);
                    }
                    if (canRemove)
                    {
                        string tpl = item.TemplateId;
                        slot.gameObject.AddComponent<ArenaMenuButton>().Init(slot, () => Remove(tpl));
                    }
                }
            }
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = rowHeight + 8f;
        }
    }

    private static void Place(RectTransform rect, int x, int y, int w, int h, float cell)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f); rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x * cell, -y * cell);
        rect.sizeDelta = new Vector2(w * cell, h * cell);
    }

    // -------------------------------------------------------------- armory

    private void BuildItemsPanel()
    {
        var selector = ArenaUiBuilder.Find(_items, "ItemsGroupSelector");
        foreach (var tab in new[] { "WeaponTab", "WearingsTab", "MedsTab", "ModsTab", "AmmoTab", "CamouflagePaintTab", "FavoriteTab" })
        {
            var node = ArenaUiBuilder.TryFind(selector, tab);
            if (node == null) continue;
            ArenaUiBuilder.SetActive(node, "PurchasedMarker", false);
            ArenaUiBuilder.SetActive(node, "HoverState", false);
            string target = ItemTabs.FirstOrDefault(t => t.Tab == tab).Key;
            if (target == null) { ArenaUiBuilder.SetActive(node, "Disabled", true); ArenaUiBuilder.SetActive(node, "IdleState", false); continue; }
            var hit = node.GetComponent<Image>() ?? node.gameObject.AddComponent<Image>();
            hit.color = Color.clear; hit.raycastTarget = true;
            node.gameObject.AddComponent<ArenaMenuButton>().Init(node, () =>
            {
                _itemsTab = target; _focusSlot = null;
                BindCells(ArenaUiBuilder.Find(_gears, "Gear Panel/Gears"), true);
                BindItemsPanel();
            });
        }
        var filters = ArenaUiBuilder.Find(_items, "FiltersPanel");
        ArenaUiBuilder.SetActive(filters, "ToggleDescriptions", false);
        SetText(filters, "PurchaseDropdown/Label", "All");
        ArenaUiBuilder.SetActive(filters, "PurchaseDropdown/Template", false);
        ArenaUiBuilder.SetActive(filters, "Toggles", true);
        var search = ArenaUiBuilder.TryFind(filters, "SearchField");
        if (search != null)
        {
            var placeholder = ArenaUiBuilder.TryFind(search, "Text Area/Placeholder")?.GetComponent<TextMeshProUGUI>();
            if (placeholder != null) placeholder.text = "Enter name";
            var field = search.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = ArenaUiBuilder.TryFind(search, "Text Area");
            field.textComponent = ArenaUiBuilder.TryFind(search, "Text Area/Text")?.GetComponent<TextMeshProUGUI>();
            field.placeholder = placeholder;
            field.onValueChanged.AddListener(value => { _search = value ?? ""; BindItemsPanel(); });
            var searchImage = search.GetComponent<Image>() ?? search.gameObject.AddComponent<Image>();
            searchImage.raycastTarget = true;
        }
        var toggles = ArenaUiBuilder.TryFind(filters, "Toggles");
        if (toggles != null)
            foreach (var (toggle, classId) in ClassToggles)
            {
                var node = ArenaUiBuilder.TryFind(toggles, toggle);
                if (node == null) continue;
                var toggleHit = node.GetComponent<Image>() ?? node.gameObject.AddComponent<Image>();
                toggleHit.color = Color.clear; toggleHit.raycastTarget = true;
                string captured = classId;
                node.gameObject.AddComponent<ArenaMenuButton>().Init(node, () => { _filterClass = captured; BindItemsPanel(); });
            }
        foreach (var panel in new[] { "WeaponsPanel", "WearingsPanel", "MedsPanel", "AmmoPanel" })
        {
            var content = ArenaUiBuilder.Find(_items, panel + "/Content");
            ArenaUiBuilder.SetActive(content, "SortButton", false);
            ArenaUiBuilder.SetActive(content, "Filter Panel", false);
            ArenaUiBuilder.SetActive(content, "ArenaVertical Scrollbar", false);
            ArenaUiBuilder.SetActive(_items, panel + "/Loader", false);
            var area = ArenaUiBuilder.Find(content, "Scroll Area");
            if (area.GetComponent<Mask>() == null) area.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var areaImage = area.GetComponent<Image>() ?? area.gameObject.AddComponent<Image>();
            areaImage.raycastTarget = true;     // wheel events over empty grid space still reach the scroll rect
            var grid = (RectTransform)area.GetChild(0);
            grid.anchorMin = new Vector2(0f, 1f); grid.anchorMax = new Vector2(1f, 1f); grid.pivot = new Vector2(0.5f, 1f);
            grid.anchoredPosition = Vector2.zero; grid.sizeDelta = Vector2.zero;
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            bool big = panel == "WeaponsPanel";
            layout.cellSize = big ? new Vector2(323f, 147f) : new Vector2(128f, 147f);
            layout.spacing = new Vector2(4f, 4f);
            layout.padding = new RectOffset(2, 2, 2, 2);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = big ? 2 : 5;
            grid.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // The ripped Scroll Area carries its own Canvas + raycaster; nested canvases break wheel routing here.
            var raycaster = area.GetComponent<GraphicRaycaster>();
            if (raycaster != null) DestroyImmediate(raycaster);
            var nested = area.GetComponent<Canvas>();
            if (nested != null) DestroyImmediate(nested);
            var scroll = area.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = area; scroll.content = grid; scroll.horizontal = false; scroll.scrollSensitivity = 40f;
            scroll.movementType = ScrollRect.MovementType.Clamped;
        }
    }

    private static readonly (string Toggle, string ClassId)[] ClassToggles =
    {
        ("AssaultToggle", "assault"), ("CQBToggle", "enforcer"), ("ScoutToggle", "scout"), ("MarksmanToggle", "marksman"), ("AllRounderToggle", null),
    };

    private void BindItemsPanel()
    {
        var selector = ArenaUiBuilder.Find(_items, "ItemsGroupSelector");
        foreach (var (tab, key) in ItemTabs)
        {
            var node = ArenaUiBuilder.TryFind(selector, tab);
            if (node == null) continue;
            ArenaUiBuilder.SetActive(node, "SelectedState", _itemsTab == key);
            ArenaUiBuilder.SetActive(node, "IdleState", _itemsTab != key);
        }
        var toggles = ArenaUiBuilder.TryFind(_items, "FiltersPanel/Toggles");
        if (toggles != null)
            foreach (var (toggle, classId) in ClassToggles)
            {
                var node = ArenaUiBuilder.TryFind(toggles, toggle);
                if (node == null) continue;
                ArenaUiBuilder.SetActive(node, "Selected", classId == _filterClass);
                ArenaUiBuilder.SetActive(node, "Idle", classId != _filterClass);
                ArenaUiBuilder.SetActive(node, "Hover", false);
                ArenaUiBuilder.SetActive(node, "Disabled", false);
            }
        foreach (var panel in new[] { "WeaponsPanel", "WearingsPanel", "MedsPanel", "AmmoPanel" })
            ArenaUiBuilder.SetActive(_items, panel, panel == _itemsTab + "Panel");

        bool big = _itemsTab == "Weapons";
        var grid = (RectTransform)ArenaUiBuilder.Find(_items, _itemsTab + "Panel/Content/Scroll Area").GetChild(0);
        foreach (Transform child in grid) Destroy(child.gameObject);
        var slots = _focusSlot.HasValue ? new[] { _focusSlot.Value.ToString() } : TabSlots[_itemsTab];
        // What the preset already carries: gear by slot, loaded rounds, rig supplies.
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, slot) in Cells) if (ItemIn(slot) is { } worn) current.Add(worn.TemplateId);
        foreach (var node in _shown?.Items ?? new List<KitItemNode>())
            if (node.SlotId == "cartridges" || (node.SlotId?.StartsWith("patron_in_weapon") ?? false)) current.Add(node.Tpl);
        foreach (var supply in _shown?.Fill?.RigContents ?? new List<KitStackedItem>()) current.Add(supply.Tpl);
        var calibres = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in WeaponSlots)
            try { if ((ItemIn(slot) as Weapon)?.AmmoCaliber is { } calibre) calibres.Add(calibre); } catch { /* none */ }
        string search = _search.Trim();
        foreach (var slot in slots)
            foreach (var entry in ArenaArmory.ForSlot(slot, _filterClass))
            {
                if (slot == "Ammo" && (entry.Caliber == null || !calibres.Contains(entry.Caliber))) continue;
                if (search.Length > 0 && (entry.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                BuildCard(grid, big ? _cardBig : _cardSmall, entry, slot, current.Contains(entry.Tpl));
            }
        LayoutRebuilder.ForceRebuildLayoutImmediate(grid);
        var scrollRect = grid.parent.GetComponent<ScrollRect>();
        if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
    }

    private void BuildCard(RectTransform grid, JObject layout, ArenaArmory.Entry entry, string slot, bool isCurrent)
    {
        var card = _builder.Build(layout, grid);
        card.gameObject.SetActive(true);
        bool allowed = entry.AllowedFor(_class);
        var up = ArenaUiBuilder.Find(card, "UpPanel");
        SetText(up, "RightHeader/MetaScore/MetaverseValue", entry.Score.ToString("N0"));
        SetText(up, "RightHeader/MetaScore/MetaScoreValue", entry.Score.ToString("N0"));
        foreach (var path in new[] { "RightHeader/Arrow", "RightHeader/NewLevelMarker", "RightHeader/IncompatibilityMarker", "Star", "MetaverseIcon" })
            ArenaUiBuilder.SetActive(up, path, false);
        var slotNode = ArenaUiBuilder.Find(card, "Slot");
        ArenaUiBuilder.SetActive(slotNode, "SecondBackground", isCurrent);
        foreach (var path in new[] { "Loader", "APCProgressionInfo", "APCLevelProgression", "Buy/BuyButton", "Buy/PriceObject", "Buy/WtRaceContainer" })
            ArenaUiBuilder.SetActive(slotNode, path, false);
        ArenaUiBuilder.SetActive(slotNode, "Buy/Lock", !allowed);
        var info = ArenaUiBuilder.Find(slotNode, "APCItemInfo");
        SetText(info, "ItemName", entry.Name ?? entry.Tpl);
        var item = ArmoryItem(entry);
        string line = null;
        try { line = (item as Weapon)?.AmmoCaliber?.Replace("Caliber", "") ?? entry.Caliber?.Replace("Caliber", ""); } catch { /* none */ }
        ArenaUiBuilder.SetActive(info, "FirstLine", !string.IsNullOrEmpty(line));
        if (!string.IsNullOrEmpty(line)) SetText(info, "FirstLine/Value", line);
        ArenaUiBuilder.SetActive(info, "SecondLine", false);
        ArenaUiBuilder.SetActive(info, "ThirdLine", false);
        var classes = ArenaUiBuilder.Find(slotNode, "APCClassesInfo");
        bool all = entry.Classes.Count == 0;
        ArenaUiBuilder.SetActive(classes, "Assault", !all && entry.Classes.Contains("assault"));
        ArenaUiBuilder.SetActive(classes, "CQB", !all && entry.Classes.Contains("enforcer"));
        ArenaUiBuilder.SetActive(classes, "Scout", !all && entry.Classes.Contains("scout"));
        ArenaUiBuilder.SetActive(classes, "Marksman", !all && entry.Classes.Contains("marksman"));
        ArenaUiBuilder.SetActive(classes, "AllRounder", all);
        var imageNode = ArenaUiBuilder.TryFind(slotNode, "ItemImage");
        if (imageNode != null && item != null)
        {
            imageNode.gameObject.SetActive(true);
            var image = imageNode.GetComponent<Image>() ?? imageNode.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            var size = imageNode.sizeDelta;
            ArenaItemIcon.Bind(image, item, size.x - 8f, size.y - 8f);
        }
        // Every card is a raycast target, locked ones too: the mouse wheel only scrolls over something it can hit.
        var hit = card.GetComponent<Image>() ?? card.gameObject.AddComponent<Image>();
        hit.raycastTarget = true;
        if (!allowed)
        {
            card.gameObject.AddComponent<CanvasGroup>().alpha = 0.45f;
            return;
        }
        var captured = entry;
        card.gameObject.AddComponent<ArenaMenuButton>().Init(card, () =>
        {
            if (slot is "Meds" or "Throwable") Add(captured);
            else Swap(slot, captured);
        });
    }

    /// <summary>Edits land on a custom preset: a stock one is copied first, as live does.</summary>
    private bool EnsureCustom()
    {
        if (_shown == null) return false;
        if (IsCustom(_shown.Id)) return true;
        string baseId = _shown.Id;
        if (!string.IsNullOrEmpty(ArenaArmory.Edit("create", baseId))) return false;
        var created = KitCatalog.Kits.Where(k => IsCustom(k.Id) && k.Base == baseId).OrderBy(k => k.Order).LastOrDefault();
        if (created == null) return false;
        _shown = created;
        return true;
    }

    private void Swap(string slot, ArenaArmory.Entry entry)
    {
        if (!EnsureCustom()) return;
        if (!string.IsNullOrEmpty(ArenaArmory.Edit("swap", _shown.Id, slot, entry.Tpl))) return;
        ReloadShown();
    }

    private void Add(ArenaArmory.Entry entry)
    {
        if (!EnsureCustom()) return;
        if (!string.IsNullOrEmpty(ArenaArmory.Edit("add", _shown.Id, entry.Slot, entry.Tpl))) return;
        ReloadShown();
    }

    private void Remove(string tpl)
    {
        if (!EnsureCustom()) return;
        if (!string.IsNullOrEmpty(ArenaArmory.Edit("remove", _shown.Id, "Meds", tpl))) return;
        ReloadShown();
    }

    /// <summary>An item instance for the card icon: a weapon from its default preset nodes, anything else bare.</summary>
    private static Item ArmoryItem(ArenaArmory.Entry entry)
    {
        if (ArmoryItems.TryGetValue(entry.Tpl, out var cached)) return cached;
        Item item = null;
        try
        {
            if (entry.Items != null && entry.Items.Count > 0)
            {
                var kit = new KitDefinition
                {
                    Id = "armory-" + entry.Tpl,
                    Items = entry.Items.Select(n => new KitItemNode
                    {
                        Id = n.Id, Tpl = n.Tpl, ParentId = n.ParentId, Upd = n.Upd,
                        SlotId = n.ParentId == "root" ? "FirstPrimaryWeapon" : n.SlotId,
                    }).ToList(),
                };
                item = KitBuilder.BuildPrimaryWeapon(kit, 0, out _);
            }
            if (item == null)
            {
                var factory = Comfort.Common.Singleton<ItemFactory>.Instance;
                item = factory?.CreateItem(factory.NextId, entry.Tpl, null);
            }
        }
        catch (Exception ex) { Plugin.ModLogger.LogDebug($"Arena armory item {entry.Tpl} unavailable: {ex.Message}"); }
        ArmoryItems[entry.Tpl] = item;
        return item;
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
