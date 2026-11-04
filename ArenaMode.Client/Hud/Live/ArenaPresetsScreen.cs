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
        ["Mods"] = new[] { "Mods" },
    };
    private static readonly (string Tab, string Key)[] ItemTabs =
    {
        ("WeaponTab", "Weapons"), ("WearingsTab", "Wearings"), ("MedsTab", "Meds"), ("ModsTab", "Mods"), ("AmmoTab", "Ammo"),
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
    private TextMeshProUGUI _status;
    private TMP_InputField _nameInput;
    private RectTransform _saveButton;
    private RectTransform _favoriteToggle;
    private Image _classIcon;
    private bool _customization;
    private string _itemsTab = "Weapons";
    private string _filterClass;      // armory class filter; null = every class
    private string _search = "";
    private EquipmentSlot? _focusSlot;
    private string _focusPlate;            // plate slot id of the focused armor / rig being edited
    private string _class = "assault";
    private KitDefinition _shown;
    private Session.ArenaCharacterPreview _preview;
    private InventoryEquipment _equipment;
    private ArenaKitInventoryEditor _editor;     // EFT's inventory UI over the kit while CUSTOMIZATION is up

    internal static bool Visible => _instance != null;

    internal static void Show() => Show(null, false);

    /// <summary>Opens on a given preset (from the presets list), on its VIEW or CUSTOMIZATION tab.</summary>
    internal static void Show(KitDefinition kit, bool customization)
    {
        if (_instance != null || !ArenaUiPack.IsAvailable) return;
        var go = new GameObject("ArenaPresetsScreen");
        go.transform.SetParent(Plugin.Instance.transform, false);
        _instance = go.AddComponent<ArenaPresetsScreen>();
        _instance._initial = kit;
        _instance._initialCustomization = customization;
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
        instance.CloseEditor(true);
        Destroy(instance.gameObject);
        ArenaPresetListScreen.Refresh();
    }

    /// <summary>Tears the EFT editor down; with <paramref name="apply"/> its changes go to the server first.</summary>
    private void CloseEditor(bool apply)
    {
        var editor = _editor;
        _editor = null;
        if (editor == null) return;
        try { if (apply) editor.Apply(); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena preset screen: applying the kit failed: " + ex.Message); }
        try { editor.Dispose(); } catch { /* gone */ }
    }

    private KitDefinition _initial;
    private bool _initialCustomization;

    private void OnDestroy()
    {
        CloseEditor(false);
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
            if (_nameInput != null && _nameInput.isFocused) { _nameInput.DeactivateInputField(); return; }
            Hide();
            return;
        }
        if (!_customization && _preview != null && Input.GetMouseButton(0) && _viewBody != null
            && RectTransformUtility.RectangleContainsScreenPoint(_viewBody, Input.mousePosition))
            _preview.Rotate(-Input.GetAxis("Mouse X"), 1f);
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
        canvas.sortingOrder = 530;
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

        // Live's tab body: the model panel (VIEW), the gear-and-containers panel (both tabs), the items panel
        // (CUSTOMIZATION), side by side as its TabsContainer lays them out.
        _customBody = NewRect("Customization", root);
        _customBody.anchorMin = new Vector2(0.5f, 1f); _customBody.anchorMax = new Vector2(0.5f, 1f);
        _customBody.pivot = new Vector2(0.5f, 1f); _customBody.anchoredPosition = new Vector2(0f, -60f);
        _customBody.sizeDelta = new Vector2(1890f, 960f);
        _viewBody = _builder.Build(Find(_apcLayout, "PlayerCameraUIOverlay"), _customBody);
        _viewBody.anchorMin = new Vector2(0f, 1f); _viewBody.anchorMax = new Vector2(0f, 1f); _viewBody.pivot = new Vector2(0f, 1f);
        _viewBody.anchoredPosition = Vector2.zero; _viewBody.sizeDelta = new Vector2(700f, 960f);
        ArenaUiBuilder.SetActive(_viewBody, "ZoomSlider", false);          // no zoom on the preview camera
        ArenaUiBuilder.SetActive(_viewBody, "ClothingSwitcher", false);    // clothing comes with camouflage
        var modelHost = ArenaUiBuilder.Find(_viewBody, "PlayerImage/PlayerModelImage");
        ArenaUiBuilder.SetActive(modelHost, "HoverArea", false);
        ArenaUiBuilder.Stretch(modelHost);
        _gears = _builder.Build(Find(_apcLayout, "GearsAndContainers"), _customBody);
        _gears.anchorMin = new Vector2(0f, 1f); _gears.anchorMax = new Vector2(0f, 1f); _gears.pivot = new Vector2(0f, 1f);
        _gears.anchoredPosition = Vector2.zero; _gears.sizeDelta = new Vector2(1190f, 960f);
        ArenaUiBuilder.SetActive(_gears, "DiscardItemView", false);
        ArenaUiBuilder.SetActive(_gears, "PointerBlocker", false);
        var itemsBackground = _builder.Build(Find(_apcLayout, "ItemsPanelBackground"), _customBody);
        itemsBackground.anchorMin = new Vector2(0f, 1f); itemsBackground.anchorMax = new Vector2(0f, 1f); itemsBackground.pivot = new Vector2(0f, 1f);
        itemsBackground.anchoredPosition = new Vector2(1190f, 0f); itemsBackground.sizeDelta = new Vector2(700f, 960f);
        _items = ArenaUiBuilder.Find(itemsBackground, "APCItemsPanel");
        BuildItemsPanel();

        BuildHeader(root);
        ArenaMenuChrome.BuildFooter(_pack, root, SessionProfile(), () => { Hide(); ArenaPresetListScreen.Hide(); },
            ArenaMainMenuOverlay.ShowSettings, () => { });

        string selected = Settings.SelectedKit.Value;
        var selectedKit = _initial
                          ?? KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, selected, StringComparison.OrdinalIgnoreCase))
                          ?? KitCatalog.Kits.FirstOrDefault();
        ShowPreset(selectedKit);
        SetTab(_initialCustomization);
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
        var nameNode = ArenaUiBuilder.Find(midline, "NameFieldContainer/NameField");
        var namePlaceholder = ArenaUiBuilder.TryFind(nameNode, "Text Area/Placeholder")?.GetComponent<TextMeshProUGUI>();
        if (namePlaceholder != null) namePlaceholder.text = "Enter preset name";
        _nameInput = nameNode.gameObject.AddComponent<TMP_InputField>();
        _nameInput.textViewport = ArenaUiBuilder.TryFind(nameNode, "Text Area");
        _nameInput.textComponent = ArenaUiBuilder.TryFind(nameNode, "Text Area/Text")?.GetComponent<TextMeshProUGUI>();
        _nameInput.placeholder = namePlaceholder;
        _nameInput.characterLimit = 24;
        _nameInput.onSubmit.AddListener(_ => CommitName());
        var nameImage = nameNode.GetComponent<Image>() ?? nameNode.gameObject.AddComponent<Image>();
        nameImage.color = Color.clear; nameImage.raycastTarget = true;
        _classIcon = ArenaUiBuilder.TryFind(midline, "NameFieldContainer/ClassIconContainer/ClassIcon")?.GetComponent<Image>();
        _favoriteToggle = ArenaUiBuilder.TryFind(midline, "NameFieldContainer/FavoriteToggle");
        if (_favoriteToggle != null)
        {
            var favHit = _favoriteToggle.GetComponent<Image>() ?? _favoriteToggle.gameObject.AddComponent<Image>();
            favHit.color = Color.clear; favHit.raycastTarget = true;
            _favoriteToggle.gameObject.AddComponent<ArenaMenuButton>().Init(_favoriteToggle, () =>
            {
                if (_shown == null) return;
                ArenaPresetListScreen.ToggleFavorite(_shown.Id);
                RefreshStatus();
            });
        }
        _nameField = ArenaUiBuilder.Find(midline, "NameFieldContainer");

        // Live's header buttons: save (the name), copy (customize into a custom preset), delete (custom presets).
        var buttons = ArenaUiBuilder.Find(midline, "ButtonsPreset");
        _saveButton = ArenaUiBuilder.Find(buttons, "SaveButton");
        HeaderButton(_saveButton, CommitName);
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
        if (_customization && !customization && _editor != null)
        {
            // Leaving CUSTOMIZATION: what was dragged around becomes the preset, then the view rebuilds from it.
            bool changed = _editor.Apply();
            CloseEditor(false);
            if (changed) { ReloadShown(); SetTab(false); return; }
        }
        _customization = customization;
        _viewBody.gameObject.SetActive(!customization);
        _gears.anchoredPosition = new Vector2(customization ? 0f : 700f, 0f);
        _items.parent.gameObject.SetActive(customization);
        ArenaUiBuilder.SetActive(_viewTab, "SelectedState", !customization);
        ArenaUiBuilder.SetActive(_viewTab, "IdleState", customization);
        ArenaUiBuilder.SetActive(_customTab, "SelectedState", customization);
        ArenaUiBuilder.SetActive(_customTab, "IdleState", !customization);
        BindCustomization();
        if (!customization) BindModel();
        else { try { _preview?.Dispose(); } catch { /* gone */ } _preview = null; }
    }

    // ------------------------------------------------------------- presets

    /// <summary>The name field's text becomes the custom preset's name (live's save).</summary>
    private void CommitName()
    {
        if (_shown == null || !IsCustom(_shown.Id) || _nameInput == null) return;
        string name = (_nameInput.text ?? "").Trim();
        if (name.Length == 0 || name == (_shown.Name ?? "")) { RefreshStatus(); return; }
        if (!string.IsNullOrEmpty(ArenaArmory.Edit("rename", _shown.Id, null, name))) { RefreshStatus(); return; }
        ReloadShown();
        ArenaPresetListScreen.Refresh();
    }

    private void TogglePresetList()
    {
        if (_presetList == null) return;
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
        CloseEditor(false);
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
        if (_nameInput != null)
        {
            bool custom = _shown != null && IsCustom(_shown.Id);
            _nameInput.SetTextWithoutNotify(_shown == null ? "" : (_shown.Name ?? _shown.Id));
            _nameInput.readOnly = !custom;                 // stock presets keep their names
            if (_saveButton != null)
            {
                ArenaUiBuilder.SetActive(_saveButton, "Idle", custom);
                ArenaUiBuilder.SetActive(_saveButton, "Disabled", !custom);
            }
        }
        if (_classIcon != null) _classIcon.sprite = ClassSprite(_class) ?? _classIcon.sprite;
        if (_favoriteToggle != null)
        {
            bool starred = _shown != null && ArenaPresetListScreen.IsFavorite(_shown.Id);
            ArenaUiBuilder.SetActive(_favoriteToggle, "ActiveIcon", starred);
            ArenaUiBuilder.SetActive(_favoriteToggle, "IdleIcon", !starred);
        }
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

    /// <summary>The character wearing the preset in live's model panel (VIEW tab).</summary>
    private void BindModel()
    {
        var host = ArenaUiBuilder.Find(_viewBody, "PlayerImage/PlayerModelImage");
        foreach (Transform child in host) Destroy(child.gameObject);
        try { _preview?.Dispose(); } catch { /* gone */ }
        _preview = null;
        var profile = SessionProfile();
        if (profile != null && _shown != null)
        {
            try { _preview = new Session.ArenaCharacterPreview(profile, host, () => { }, _equipment); }
            catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena preset screen: model unavailable: " + ex.Message); }
        }
    }

    // ------------------------------------------------------- customization

    private void BindCustomization()
    {
        var gearsNode = ArenaUiBuilder.Find(_gears, "Gear Panel/Gears");
        var containers = ArenaUiBuilder.Find(_gears, "Containers Panel/Scrollview Parent/Containers Scrollview");
        bool eft = _customization && _shown != null && _equipment != null && IsCustom(_shown.Id);
        if (eft && _editor == null)
        {
            try
            {
                _editor = new ArenaKitInventoryEditor(_shown, _equipment, SessionProfile(),
                    ArenaUiBuilder.Find(_gears, "Gear Panel"), (RectTransform)containers.parent, EftArmoryHost());
            }
            catch (Exception ex)
            {
                Plugin.ModLogger.LogWarning("Arena preset screen: EFT inventory editor unavailable, ripped cells instead: " + ex);
                _editor = null;
            }
        }
        else if (!eft) CloseEditor(false);
        bool viaEft = _editor != null;
        gearsNode.gameObject.SetActive(!viaEft);
        containers.gameObject.SetActive(!viaEft);
        if (!viaEft)
        {
            BindCells(gearsNode, _customization);
            BindContainers(ArenaUiBuilder.Find(containers, "Content"), _customization);
        }
        if (_customization && _shown != null && !IsCustom(_shown.Id) && _editor == null)
            Plugin.ModLogger.LogDebug("Arena preset screen: a stock preset is read-only until copied.");
        ArenaUiBuilder.SetActive(_gears, "Containers Panel/Horizontal Scrollbar", false);
        var containerScroll = containers.GetComponent<ScrollRect>();
        if (containerScroll == null)
        {
            containerScroll = containers.gameObject.AddComponent<ScrollRect>();
            containerScroll.viewport = (RectTransform)containers.parent; containerScroll.content = ArenaUiBuilder.Find(containers, "Content");
            containerScroll.horizontal = false; containerScroll.scrollSensitivity = 40f;
            containerScroll.movementType = ScrollRect.MovementType.Clamped;
            var containerHit = containers.GetComponent<Image>() ?? containers.gameObject.AddComponent<Image>();
            containerHit.raycastTarget = true;
            WireScrollbar(containerScroll, ArenaUiBuilder.TryFind(_gears, "Containers Panel/ArenaVertical Scrollbar"));
        }

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

    /// <summary>Live's vertical scrollbar (ripped as plain images) driving a ScrollRect; hidden while nothing overflows.</summary>
    private static void WireScrollbar(ScrollRect scroll, RectTransform bar)
    {
        if (scroll == null || bar == null) return;
        bar.gameObject.SetActive(true);
        var handle = ArenaUiBuilder.TryFind(bar, "Sliding Area/Handle") ?? ArenaUiBuilder.TryFind(bar, "Handle");
        if (handle == null) return;
        var scrollbar = bar.GetComponent<Scrollbar>() ?? bar.gameObject.AddComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.handleRect = handle;
        scrollbar.targetGraphic = handle.GetComponent<Image>();
        var handleImage = handle.GetComponent<Image>();
        if (handleImage != null) { handleImage.raycastTarget = true; if (handleImage.color.a < 0.05f) handleImage.color = new Color(0.69f, 0.73f, 0.76f, 0.9f); }
        var track = bar.GetComponent<Image>();
        if (track != null) track.raycastTarget = true;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
    }

    internal static void FillBar(RectTransform bar, float value, float max, string min, string maxLabel, Color? colour = null, string shown = null)
    {
        if (bar == null) return;
        bar.gameObject.SetActive(true);
        ArenaUiBuilder.SetActive(bar, "Tooltip", false);
        ArenaUiBuilder.SetActive(bar, "BarImage (1)", false);
        float amount = Mathf.Clamp01(value / Mathf.Max(1f, max));
        var fill = ArenaUiBuilder.TryFind(bar, "BarImage")?.GetComponent<Image>();
        if (fill != null)
        {
            fill.type = Image.Type.Filled; fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = amount;
            if (colour.HasValue) fill.color = colour.Value;
        }
        SetText(bar, "IndicatorContainer/MiddleBar/Separator/MinValue", string.IsNullOrEmpty(min) ? "0" : min);
        SetText(bar, "IndicatorContainer/RightBorder/Separator/MaxValue", string.IsNullOrEmpty(maxLabel) ? max.ToString("N0") : maxLabel);
        // Live positions the value above the end of the fill from code; the rip carries no rect for it.
        var handler = ArenaUiBuilder.TryFind(bar, "ValuePlaceholder/ValueHandler");
        if (handler != null) ArenaUiBuilder.Stretch(handler);
        var current = ArenaUiBuilder.TryFind(bar, "ValuePlaceholder/ValueHandler/CurrentValueField");
        if (current != null)
        {
            current.gameObject.SetActive(true);
            current.anchorMin = current.anchorMax = new Vector2(amount, 1f);
            current.pivot = new Vector2(0.5f, 0f);
            current.anchoredPosition = Vector2.zero;
            current.sizeDelta = new Vector2(96f, 14f);
            var text = current.GetComponent<TextMeshProUGUI>();
            if (text != null)
            {
                text.alignment = TextAlignmentOptions.Bottom;
                text.enableWordWrapping = false;
                text.text = shown ?? value.ToString("N0");
            }
        }
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
                // Live's header arrow on a weapon cell opens the build editor for that weapon.
                bool weaponCell = filled && (slot == EquipmentSlot.FirstPrimaryWeapon || slot == EquipmentSlot.Holster);
                ArenaUiBuilder.SetActive(cell, "Header/RightHeader/Arrow", weaponCell);
                var arrow = ArenaUiBuilder.TryFind(cell, "Header/RightHeader/Arrow");
                if (arrow != null && weaponCell)
                {
                    var oldArrow = arrow.GetComponent<ArenaMenuButton>();
                    if (oldArrow != null) Destroy(oldArrow);
                    var arrowImage = arrow.GetComponent<Image>();
                    if (arrowImage != null) arrowImage.raycastTarget = true;
                    bool holsterCell = slot == EquipmentSlot.Holster;
                    arrow.gameObject.AddComponent<ArenaMenuButton>().Init(arrow, () => OpenBuildEditor(holsterCell));
                }
                SetText(cell, "Header/RightHeader/MetaScore/MetaScoreValue", filled ? ArenaMetascore.ItemScore(item.TemplateId).ToString("N0") : "0");
                var oldHit = cell.GetComponent<ArenaMenuButton>();
                if (oldHit != null) Destroy(oldHit);
                var target = cell.GetComponent<ArenaDropTarget>() ?? cell.gameObject.AddComponent<ArenaDropTarget>();
                target.Tag = "slot:" + slot;
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
        _focusPlate = null;
        _itemsTab = WeaponSlots.Contains(slot) ? "Weapons" : "Wearings";
        Plugin.ModLogger.LogDebug($"Arena presets: focus {(_focusSlot.HasValue ? slot.ToString() : "none")}; "
            + $"{(_focusSlot.HasValue ? ArenaArmory.ForSlot(slot.ToString(), _filterClass).Count() : 0)} armory items, {PlateSlots().Count} plate slots.");
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
        // The list is as wide as its panel; blocks take their height from their layout elements.
        var listGroup = content.GetComponent<VerticalLayoutGroup>();
        if (listGroup != null)
        {
            listGroup.childControlWidth = listGroup.childControlHeight = true;
            listGroup.childForceExpandWidth = true; listGroup.childForceExpandHeight = false;
            listGroup.spacing = 6f;
        }
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero; content.sizeDelta = new Vector2(0f, content.sizeDelta.y);
        float listWidth = ((RectTransform)content.parent).rect.width;
        if (listWidth < 100f) listWidth = 540f;
        if (customization)
        {
            var panel = content.parent?.parent?.parent as RectTransform ?? content;
            var panelHit = panel.GetComponent<Image>() ?? panel.gameObject.AddComponent<Image>();
            panelHit.raycastTarget = true;
            if (panelHit.sprite == null && panelHit.color == Color.white) panelHit.color = Color.clear;
            (panel.GetComponent<ArenaDropTarget>() ?? panel.gameObject.AddComponent<ArenaDropTarget>()).Tag = "supplies";
        }
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
            // Live heads each container block with the container's own metascore; the name sits at the right.
            var head = NewRect("Title " + index, content);
            head.gameObject.AddComponent<LayoutElement>().preferredHeight = 24f;
            var headBack = head.gameObject.AddComponent<Image>();
            headBack.color = new Color(0f, 0f, 0f, 0.45f); headBack.raycastTarget = false;
            var headIcon = NewRect("Icon", head).gameObject.AddComponent<Image>();
            headIcon.rectTransform.anchorMin = headIcon.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            headIcon.rectTransform.pivot = new Vector2(0f, 0.5f); headIcon.rectTransform.anchoredPosition = new Vector2(10f, 0f);
            headIcon.rectTransform.sizeDelta = new Vector2(16f, 16f);
            headIcon.sprite = ScoreSprite(); headIcon.raycastTarget = false; headIcon.enabled = headIcon.sprite != null;
            var headScore = HudStyle.CreateText(head, "Score", 13f, TextAlignmentOptions.MidlineLeft);
            ArenaUiBuilder.Stretch(headScore.rectTransform);
            headScore.rectTransform.offsetMin = new Vector2(32f, 0f);
            headScore.text = ArenaMetascore.ItemScore(container.TemplateId).ToString("N0");
            headScore.color = Text;
            var headName = HudStyle.CreateText(head, "Name", 12f, TextAlignmentOptions.MidlineRight);
            ArenaUiBuilder.Stretch(headName.rectTransform);
            headName.rectTransform.offsetMax = new Vector2(-10f, 0f);
            headName.text = name; headName.color = Muted;
            var row = NewRect("Grids " + index, content);
            // Grids flow left to right and wrap, so every grid of a rig shows (one row was clipped by the panel).
            float maxWidth = listWidth - 24f;
            float cursorX = 8f, cursorY = 2f, lineHeight = 0f;
            int gridIndex = 0;
            foreach (var grid in container.Grids)
            {
                int w = Math.Max(1, grid.GridWidth), h = Math.Max(1, grid.GridHeight);
                float gw = w * cell, gh = h * cell;
                if (cursorX > 8f && cursorX + gw > maxWidth) { cursorX = 8f; cursorY += lineHeight + 8f; lineHeight = 0f; }
                var panel = NewRect("Grid " + gridIndex++, row);
                panel.anchorMin = panel.anchorMax = new Vector2(0f, 1f); panel.pivot = new Vector2(0f, 1f);
                panel.anchoredPosition = new Vector2(cursorX, -cursorY);
                panel.sizeDelta = new Vector2(gw, gh);
                cursorX += gw + 8f;
                lineHeight = Mathf.Max(lineHeight, gh);
                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                    {
                        var back = NewRect($"Cell {x},{y}", panel);
                        Place(back, x, y, 1, 1, cell);
                        var backImage = back.gameObject.AddComponent<Image>();
                        backImage.color = new Color(0.1f, 0.12f, 0.14f, 0.85f); backImage.raycastTarget = false;
                        ArenaUiTheme.Rule(back, "Edge", ArenaUiTheme.WithAlpha(ArenaUiTheme.Muted, 0.35f), inset: 0f, thickness: 1f);
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
                        // Dragging a supply out of the rig throws it away, as in live.
                        slot.gameObject.AddComponent<ArenaDragSource>().Init(image, (RectTransform)transform, dropped =>
                        {
                            if (dropped != "supplies") Remove(tpl);
                        });
                    }
                }
            }
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = cursorY + lineHeight + 8f;
        }
    }

    private Sprite _scoreSprite;

    /// <summary>The metascore glyph from the rip's bar icon.</summary>
    private Sprite ScoreSprite()
    {
        if (_scoreSprite != null) return _scoreSprite;
        try
        {
            var bar = Find(_apcLayout, "APCMetascoreBar");
            var icon = bar == null ? null : Find(bar, "Icon");
            if (icon?["components"] is JArray components)
                foreach (var component in components)
                    if (component is JObject c && (c.Value<string>("type") ?? "").EndsWith(".Image") && c["fields"]?["m_Sprite"] is JObject sprite)
                        return _scoreSprite = _pack.Sprite(sprite);
        }
        catch { /* no glyph */ }
        return null;
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
                _itemsTab = target; _focusSlot = null; _focusPlate = null;
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
        (_items.GetComponent<ArenaDropTarget>() ?? _items.gameObject.AddComponent<ArenaDropTarget>()).Tag = "armory";
        foreach (var panel in new[] { "WeaponsPanel", "WearingsPanel", "MedsPanel", "AmmoPanel", "ModsPanel" })
        {
            var content = ArenaUiBuilder.Find(_items, panel + "/Content");
            ArenaUiBuilder.SetActive(content, "SortButton", false);
            ArenaUiBuilder.SetActive(content, "Filter Panel", false);

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
            areaImage.color = Color.clear;       // live masks this graphic out; raycasts still land on it
            var scroll = area.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = area; scroll.content = grid; scroll.horizontal = false; scroll.scrollSensitivity = 40f;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            WireScrollbar(scroll, ArenaUiBuilder.TryFind(content, "ArenaVertical Scrollbar"));
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
        foreach (var panel in new[] { "WeaponsPanel", "WearingsPanel", "MedsPanel", "AmmoPanel", "ModsPanel" })
            ArenaUiBuilder.SetActive(_items, panel, panel == _itemsTab + "Panel");

        bool big = _itemsTab == "Weapons";
        var contentNode = ArenaUiBuilder.Find(_items, _itemsTab + "Panel/Content");
        var grid = (RectTransform)ArenaUiBuilder.Find(contentNode, "Scroll Area").GetChild(0);
        foreach (Transform child in grid) Destroy(child.gameObject);
        // Plates: the focused armor or rig lists its plate slots on a strip above the items; one selected lists plates.
        var plateSlots = PlateSlots();
        if (_focusPlate != null && plateSlots.All(s => s.ID != _focusPlate)) _focusPlate = null;
        var oldStrip = contentNode.Find("PlateStrip");
        if (oldStrip != null) Destroy(oldStrip.gameObject);
        var scrollArea = ArenaUiBuilder.Find(contentNode, "Scroll Area");
        scrollArea.offsetMax = new Vector2(scrollArea.offsetMax.x, plateSlots.Count > 0 ? -36f : -0.7f);
        if (plateSlots.Count > 0) BuildPlateStrip(contentNode, plateSlots);
        var plateSlot = plateSlots.FirstOrDefault(s => s.ID == _focusPlate);
        Slot magazineSlot = null;
        try { magazineSlot = (ItemIn(EquipmentSlot.FirstPrimaryWeapon) as CompoundItem)?.AllSlots.FirstOrDefault(s => s.ID == "mod_magazine"); } catch { /* none */ }
        var slots = plateSlot != null ? new[] { "Plates" } : _focusSlot.HasValue ? new[] { _focusSlot.Value.ToString() } : TabSlots[_itemsTab];
        // What the preset already carries: gear by slot, loaded rounds, rig supplies.
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, slot) in Cells) if (ItemIn(slot) is { } worn) current.Add(worn.TemplateId);
        foreach (var node in _shown?.Items ?? new List<KitItemNode>())
            if (node.SlotId == "cartridges" || node.SlotId == "mod_magazine" || (node.SlotId?.StartsWith("patron_in_weapon") ?? false)) current.Add(node.Tpl);
        if (plateSlot?.ContainedItem != null) current.Add(plateSlot.ContainedItem.TemplateId);
        foreach (var supply in _shown?.Fill?.RigContents ?? new List<KitStackedItem>()) current.Add(supply.Tpl);
        var calibres = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in WeaponSlots)
            try { if ((ItemIn(slot) as Weapon)?.AmmoCaliber is { } calibre) calibres.Add(calibre); } catch { /* none */ }
        string search = _search.Trim();
        var eftItems = _editor != null ? new List<Item>() : null;
        if (eftItems != null && _itemsTab == "Wearings") slots = slots.Concat(new[] { "Plates" }).ToArray();
        foreach (var slot in slots)
            foreach (var entry in ArenaArmory.ForSlot(slot, _filterClass))
            {
                if (slot == "Ammo" && (entry.Caliber == null || !calibres.Contains(entry.Caliber))) continue;
                if (eftItems == null && slot == "Plates" && !Accepts(plateSlot, ArmoryItem(entry))) continue;
                if (eftItems == null && slot == "Mods" && (magazineSlot == null || !Accepts(magazineSlot, ArmoryItem(entry)))) continue;
                if (search.Length > 0 && (entry.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (eftItems != null) { var made = ArmoryItem(entry); if (made != null) eftItems.Add(made); }
                else BuildCard(grid, big ? _cardBig : _cardSmall, entry, slot, current.Contains(entry.Tpl));
            }
        var eftHost = EftArmoryHost();
        eftHost.gameObject.SetActive(eftItems != null);
        if (eftItems != null)
        {
            // The whole armory grid lives in one host; the per-tab scroll areas stay empty while EFT's grid is up.
            foreach (var p in new[] { "WeaponsPanel", "WearingsPanel", "MedsPanel", "AmmoPanel", "ModsPanel" })
                ArenaUiBuilder.SetActive(_items, p, false);
            _editor.ShowArmory(eftItems);
            return;
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
        card.gameObject.AddComponent<ArenaMenuButton>().Init(card, () => Place(slot, captured));
        var cardIcon = imageNode?.GetComponent<Image>();
        if (cardIcon != null)
            card.gameObject.AddComponent<ArenaDragSource>().Init(cardIcon, (RectTransform)transform, dropped =>
            {
                if (dropped == null || dropped == "armory") return;
                if (dropped == "supplies") { if (slot is "Meds" or "Throwable" or "Ammo") Place(slot, captured); return; }
                if (dropped.StartsWith("slot:") && (slot == dropped.Substring(5) || (slot == "Mods" && dropped == "slot:FirstPrimaryWeapon"))) Place(slot, captured);
            });
    }

    /// <summary>One armory item lands where it belongs: a gear slot, the supplies, the ammo, or the weapon's slot.</summary>
    private void Place(string slot, ArenaArmory.Entry entry)
    {
        if (slot is "Meds" or "Throwable") Add(entry);
        else if (slot == "Mods") Swap("mod:primary/mod_magazine", entry);
        else if (slot == "Plates") Swap((_focusSlot == EquipmentSlot.TacticalVest ? "mod:rig/" : "mod:armor/") + _focusPlate, entry);
        else Swap(slot, entry);
    }

    /// <summary>The plate slots of the focused armor or rig (front, back, sides), none when nothing plated is focused.</summary>
    private List<Slot> PlateSlots()
    {
        var list = new List<Slot>();
        if (_focusSlot is not (EquipmentSlot.ArmorVest or EquipmentSlot.TacticalVest)) return list;
        if (ItemIn(_focusSlot.Value) is not CompoundItem worn) return list;
        try
        {
            foreach (var slot in worn.AllSlots)
                if (ReferenceEquals(slot.ParentItem, worn) && slot.ID.IndexOf("plate", StringComparison.OrdinalIgnoreCase) >= 0) list.Add(slot);
        }
        catch { /* no slots */ }
        return list;
    }

    private static string PlateLabel(string slotId) => slotId.ToLowerInvariant() switch
    {
        "front_plate" => "FRONT", "back_plate" => "BACK", "left_side_plate" => "LEFT", "right_side_plate" => "RIGHT",
        _ => slotId.Replace("_", " ").ToUpperInvariant(),
    };

    /// <summary>One button per plate slot above the wearings list; the chosen one lists the plates that fit it.</summary>
    private void BuildPlateStrip(RectTransform parent, List<Slot> plateSlots)
    {
        var strip = NewRect("PlateStrip", parent);
        strip.anchorMin = new Vector2(0f, 1f); strip.anchorMax = Vector2.one; strip.pivot = new Vector2(0.5f, 1f);
        strip.anchoredPosition = Vector2.zero; strip.sizeDelta = new Vector2(0f, 34f);
        var layout = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(44, 8, 4, 4); layout.spacing = 6f;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        layout.childControlWidth = false; layout.childControlHeight = true;
        var caption = HudStyle.CreateText(strip, "Caption", 13f, TextAlignmentOptions.MidlineLeft);
        caption.text = "PLATES"; caption.color = Muted;
        caption.gameObject.AddComponent<LayoutElement>().preferredWidth = 58f;
        foreach (var slot in plateSlots)
        {
            string id = slot.ID;
            bool selected = id == _focusPlate;
            string name = "—";
            try { if (slot.ContainedItem != null) name = slot.ContainedItem.ShortName.Localized(); } catch { /* unnamed */ }
            var button = NewRect("Plate " + id, strip);
            button.gameObject.AddComponent<LayoutElement>().preferredWidth = 128f;
            var fill = button.gameObject.AddComponent<Image>();
            fill.color = selected ? new Color(0.79f, 0.54f, 0f, 0.18f) : new Color(0.52f, 0.60f, 0.67f, 0.12f);
            fill.raycastTarget = true;
            ArenaUiTheme.Rule(button, "Edge", selected ? ArenaUiTheme.WithAlpha(Gold, 0.8f) : ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
            var label = HudStyle.CreateText(button, "Label", 12f, TextAlignmentOptions.Midline);
            label.text = $"{PlateLabel(id)}: {name}";
            label.color = selected ? Gold : Text;
            label.enableWordWrapping = false; label.overflowMode = TextOverflowModes.Ellipsis;
            ArenaUiBuilder.Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(4f, 0f); label.rectTransform.offsetMax = new Vector2(-4f, 0f);
            button.gameObject.AddComponent<ArenaMenuButton>().Init(button, () =>
            {
                _focusPlate = _focusPlate == id ? null : id;
                BindItemsPanel();
            });
        }
    }

    // ---------------------------------------------------------------- mods

    /// <summary>Live's weapon build editor on this preset's primary; a stock preset becomes a custom one first.</summary>
    private void OpenBuildEditor(bool holster = false)
    {
        if (!EnsureCustom()) return;
        if (ItemIn(holster ? EquipmentSlot.Holster : EquipmentSlot.FirstPrimaryWeapon) == null) return;
        gameObject.SetActive(false);
        Action back = () =>
        {
            if (this == null || _instance != this) return;
            gameObject.SetActive(true);
            ReloadShown();
        };
        // EFT's own build screen (the extraction client's) with metascore for price; the live-styled editor only
        // when EFT's item context is not available.
        if (!ArenaStockBuildEditor.TryShow(_shown, holster, back)) ArenaBuildEditor.Show(_shown, holster, back);
    }

    /// <summary>The slot's own filters against the item's template family (the live editor's compatibility rule).</summary>
    internal static bool Accepts(Slot slot, Item item)
    {
        if (slot?.Filters == null || item?.Template == null) return false;
        var family = new HashSet<string>(StringComparer.Ordinal);
        for (var template = item.Template; template != null; template = template.Parent) family.Add(template._id.ToString());
        bool allowed = false;
        foreach (var filter in slot.Filters)
        {
            if (filter == null) continue;
            if (filter.ExcludedFilter != null && filter.ExcludedFilter.Any(x => family.Contains(x.ToString()))) return false;
            if (filter.Filter != null && filter.Filter.Any(x => family.Contains(x.ToString()))) allowed = true;
        }
        return allowed;
    }

    /// <summary>"mod:primary/&lt;slot&gt;/..." from the weapon down to the slot, for the server's attachment swap.</summary>
    internal static string ModPath(CompoundItem weapon, Slot target, bool holster = false)
    {
        var ids = new List<string> { target.ID };
        Item owner = target.ParentItem;
        for (int guard = 0; owner != null && !ReferenceEquals(owner, weapon) && guard < 16; guard++)
        {
            if (owner.Parent?.Container is not Slot parentSlot) return null;
            ids.Insert(0, parentSlot.ID);
            owner = parentSlot.ParentItem;
        }
        return (holster ? "mod:holster/" : "mod:primary/") + string.Join("/", ids);
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
        if (slot == null || !EnsureCustom()) return;
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

    /// <summary>Where EFT's armory grid sits: over the items panel's list area, scrolling on its own.</summary>
    private RectTransform EftArmoryHost()
    {
        var existing = ArenaUiBuilder.TryFind(_items, "EftArmory");
        if (existing != null) return existing;
        var host = NewRect("EftArmory", _items);
        host.anchorMin = new Vector2(0f, 0f); host.anchorMax = new Vector2(1f, 1f);
        host.offsetMin = new Vector2(12f, 8f); host.offsetMax = new Vector2(-12f, -172f);
        var hit = host.gameObject.AddComponent<Image>();
        hit.color = Color.clear; hit.raycastTarget = true;
        host.gameObject.AddComponent<RectMask2D>();
        var content = NewRect("Content", host);
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero; content.sizeDelta = new Vector2(0f, 2000f);
        var scroll = host.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = host; scroll.content = content; scroll.horizontal = false; scroll.scrollSensitivity = 40f;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        host.gameObject.SetActive(false);
        return content;
    }

    /// <summary>An item instance for the card icon: a weapon from its default preset nodes, anything else bare.</summary>
    internal static Item ArmoryItem(ArenaArmory.Entry entry)
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
