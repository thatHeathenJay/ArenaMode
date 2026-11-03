using ArenaMode.Kits;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.WeaponModding;
using HarmonyLib;
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
/// Live Arena's weapon build editor (ArenaBuildEditorScreen): the preset's primary rendered in 3D by the game's own
/// WeaponPreview (the same component EFT's modding screen uses), a marker on every mod slot projected from the
/// weapon's bones, a dropdown of the armory attachments that slot accepts, the weapon's name, metascore and
/// characteristics, and live's reset-to-default. Every change is a server swap on the custom preset; the weapon is
/// rebuilt from the preset and shown again.
/// </summary>
internal sealed class ArenaBuildEditor : MonoBehaviour
{
    private static readonly Color Text = new(0.882f, 0.933f, 0.957f, 1f);
    private static readonly Color Muted = new(0.69f, 0.729f, 0.757f, 1f);
    private static readonly Color Gold = new(0.79f, 0.54f, 0f, 1f);

    private static ArenaBuildEditor _instance;
    private ArenaUiPack _pack;
    private ArenaUiBuilder _builder;
    private JObject _layout;
    private RectTransform _root, _screen, _previewPanel, _markers, _dropdown;
    private RawImage _view;
    private CameraImage _image;
    private WeaponPreview _preview;
    private GameObject _previewHolder;
    private KitDefinition _kit;
    private Weapon _weapon;
    private Action _onClosed;
    private readonly List<(Slot Slot, RectTransform Marker, Transform Bone)> _slots = new();
    private Slot _open;
    private bool _dragging;

    internal static bool Visible => _instance != null;

    internal static void Show(KitDefinition kit, Action onClosed)
    {
        if (_instance != null || kit == null || !ArenaUiPack.IsAvailable) return;
        var go = new GameObject("ArenaBuildEditor");
        go.transform.SetParent(Plugin.Instance.transform, false);
        _instance = go.AddComponent<ArenaBuildEditor>();
        _instance._kit = kit;
        _instance._onClosed = onClosed;
        try { _instance.Build(); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena build editor failed: " + ex);
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
        try { _preview?.Hide(); } catch { /* gone */ }
        if (_previewHolder != null) Destroy(_previewHolder);
        _pack?.Dispose();
        _pack = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
        try { _onClosed?.Invoke(); } catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena build editor close: " + ex.Message); }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_dropdown != null && _dropdown.gameObject.activeSelf) _dropdown.gameObject.SetActive(false);
            else Hide();
            return;
        }
        if (_preview == null) return;
        // Live: drag to turn the weapon, wheel to zoom.
        if (Input.GetMouseButtonDown(0) && OverPreview() && !(_dropdown != null && _dropdown.gameObject.activeSelf)) _dragging = true;
        if (Input.GetMouseButtonUp(0)) _dragging = false;
        if (_dragging && Input.GetMouseButton(0))
        {
            float dx = Input.GetAxis("Mouse X"), dy = Input.GetAxis("Mouse Y");
            if (Mathf.Abs(dx) > 0.001f || Mathf.Abs(dy) > 0.001f)
                try { _preview.Rotate(-dx * 6f, dy * 4f, -60f, 60f); } catch { /* preview gone */ }
        }
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) > 0.001f && OverPreview())
            try { _preview.Zoom(-wheel * 2f); } catch { /* preview gone */ }
        PlaceMarkers();
    }

    private bool OverPreview()
    {
        if (_previewPanel == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(_previewPanel, Input.mousePosition);
    }

    // ---------------------------------------------------------------- build

    private void Build()
    {
        _pack = new ArenaUiPack();
        _builder = new ArenaUiBuilder(_pack, key => key switch
        {
            "arena/buildEditor/weapon building" => "WEAPON BUILD", "Vital parts" => "Vital parts", "Functional mods" => "Functional mods",
            "Equipment parts" => "Gear mods", "Show icons" => "Show icons", _ => null,
        });
        _layout = _pack.Layout("ArenaBuildEditorScreen");

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 540;
        canvas.overrideSorting = true;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();
        _root = (RectTransform)transform;

        var fill = NewRect("Backdrop", _root);
        ArenaUiBuilder.Stretch(fill);
        fill.gameObject.AddComponent<Image>().color = new Color(0.03f, 0.035f, 0.04f, 1f);
        var backdrop = _builder.Build(_pack.Layout("ArenaBattleBackgroundPanel"), _root);
        ArenaUiBuilder.Stretch(backdrop);
        backdrop.GetComponentInChildren<Image>().color = new Color(1f, 1f, 1f, 0.08f);

        _screen = _builder.Build(_layout, _root);
        ArenaUiBuilder.Stretch(_screen);
        foreach (var path in new[] { "Genius Camera", "UI Camera", "Loader", "Ellipse Rect", "InspectState", "Context Menu Area", "Tutorial",
                     "Preview Panel/Weapon Preview", "Preview Panel/Weapon Camera", "Preview Panel/ArenaDropdownMenu",
                     "ModificationState/Header/Toggle Group", "ModificationState/Header/ItemNameContainer/presetIcon", "ModificationState/Header/ItemNameContainer/ItemType",
                     "ModificationState/WeaponButtonsPanel", "ModificationState/ArenaQuickCamouflagePaintingPanel",
                     "ModificationState/Buttons/BuildNameInput", "ModificationState/Buttons/ContextButtons", "ModificationState/Buttons/Paint",
                     "ModificationState/Buttons/TopButtons/Save", "ModificationState/Buttons/TopButtons/Assemble" })
            ArenaUiBuilder.SetActive(_screen, path, false);

        _previewPanel = ArenaUiBuilder.Find(_screen, "Preview Panel");
        var panelImage = _previewPanel.GetComponent<Image>();
        if (panelImage != null) { panelImage.color = Color.clear; panelImage.raycastTarget = true; }
        _markers = ArenaUiBuilder.Find(_previewPanel, "Slots Container");
        _markers.SetAsLastSibling();

        var header = ArenaUiBuilder.Find(_screen, "ModificationState/Header");
        ArenaUiBuilder.Text(header, "Caption").text = "WEAPON BUILD";

        var reset = ArenaUiBuilder.Find(_screen, "ModificationState/Buttons/TopButtons/ResetToDefaultWeapon");
        var resetLabel = ArenaUiBuilder.TryFind(reset, "Label")?.GetComponent<TextMeshProUGUI>();
        if (resetLabel != null) { resetLabel.gameObject.SetActive(true); resetLabel.text = "DEFAULT"; }
        var resetHit = ArenaUiBuilder.TryFind(reset, "Button")?.GetComponent<Image>();
        if (resetHit != null) resetHit.raycastTarget = true;
        reset.gameObject.AddComponent<ArenaMenuButton>().Init(reset, ResetToDefault);

        var back = ArenaUiBuilder.Find(_screen, "BackButton");
        var backLabel = ArenaUiBuilder.TryFind(back, "SizeLabel/Label")?.GetComponent<TextMeshProUGUI>();
        if (backLabel != null) backLabel.text = "BACK";
        var size = ArenaUiBuilder.TryFind(back, "SizeLabel")?.GetComponent<TextMeshProUGUI>();
        if (size != null) { size.richText = true; size.text = "<alpha=#00>" + size.text; }
        var backHit = ArenaUiBuilder.TryFind(back, "RaycastTarget") ?? back;
        var backImage = backHit.GetComponent<Image>() ?? backHit.gameObject.AddComponent<Image>();
        backImage.color = Color.clear; backImage.raycastTarget = true;
        back.gameObject.AddComponent<ArenaMenuButton>().Init(back, Hide);

        BuildPreviewView();
        LoadWeapon();
        Plugin.ModLogger.LogInfo($"Arena build editor: {_kit.Id}.");
    }

    /// <summary>The game's weapon preview, cloned from EFT's own modding screen, rendered into a RawImage.</summary>
    private void BuildPreviewView()
    {
        var view = NewRect("WeaponView", _previewPanel);
        ArenaUiBuilder.Stretch(view);
        view.SetAsFirstSibling();
        _view = view.gameObject.AddComponent<RawImage>();
        _view.raycastTarget = false;
        _view.enabled = false;
        _image = view.gameObject.AddComponent<CameraImage>();
        _image.TextureDepth = 24;
        _image.TextureFormat = RenderTextureFormat.ARGB32;
        _image.RenderTextureReadWrite = RenderTextureReadWrite.Default;
        _image.Antialiasing = 2;

        var source = Resources.FindObjectsOfTypeAll<WeaponPreview>().FirstOrDefault(p => p != null && p.gameObject.scene.IsValid());
        if (source == null) throw new InvalidOperationException("the game's WeaponPreview is not loaded in this scene");
        _previewHolder = new GameObject("ArenaWeaponPreviewHolder");
        Object.DontDestroyOnLoad(_previewHolder);
        var clone = Instantiate(source.gameObject, _previewHolder.transform);
        clone.name = "ArenaWeaponPreview";
        clone.SetActive(true);
        _preview = clone.GetComponent<WeaponPreview>();
        try { _preview.Init(); } catch (Exception ex) { Plugin.ModLogger.LogDebug("Arena build editor: preview init: " + ex.Message); }
    }

    private void LoadWeapon()
    {
        _weapon = null;
        try { _weapon = KitBuilder.BuildPrimaryWeapon(_kit, 0, out _); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena build editor: could not build the weapon: " + ex.Message); }
        var header = ArenaUiBuilder.Find(_screen, "ModificationState/Header");
        string name = "";
        try { name = _weapon?.Name?.Localized() ?? _weapon?.ShortName?.Localized() ?? ""; } catch { /* unnamed */ }
        ArenaUiBuilder.Text(header, "ItemNameContainer/Sub-caption").text = name.ToUpperInvariant();
        BindCharacteristics();
        foreach (var (_, marker, _) in _slots) if (marker != null) Destroy(marker.gameObject);
        _slots.Clear();
        if (_weapon == null || _preview == null) return;
        try
        {
            _preview.SetupItemPreview(_weapon, null, null, _ => { }, true, null, true);
            StartCoroutine(AfterPreviewLoaded());
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena build editor: preview failed: " + ex.Message); }
    }

    private System.Collections.IEnumerator AfterPreviewLoaded()
    {
        // The preview spawns the weapon over a few frames; the camera exists once it has positioned the object.
        for (int i = 0; i < 180; i++)
        {
            yield return null;
            Camera camera = null;
            try { camera = _preview.WeaponPreviewCamera; } catch { /* not yet */ }
            var positioned = Traverse.Create(_preview).Field("_positionedObject").GetValue<Transform>();
            if (camera == null || positioned == null || positioned.childCount == 0) continue;
            camera.enabled = true;
            _image.targetCamera = camera;
            try { _image.InitCamera(camera); } catch (Exception ex) { Plugin.ModLogger.LogDebug("Arena build editor: camera image: " + ex.Message); }
            _view.enabled = true;
            BuildMarkers(positioned);
            yield break;
        }
        Plugin.ModLogger.LogWarning("Arena build editor: the weapon preview did not load.");
    }

    // -------------------------------------------------------------- markers

    private void BuildMarkers(Transform weaponObject)
    {
        var bones = weaponObject.GetComponentsInChildren<Transform>(true);
        foreach (var slot in _weapon.AllSlots)
        {
            if (slot?.ID == null || slot.ID.StartsWith("patron", StringComparison.Ordinal) || slot.ID == "cartridges") continue;
            var bone = bones.FirstOrDefault(b => string.Equals(b.name, slot.ID, StringComparison.OrdinalIgnoreCase))
                       ?? bones.FirstOrDefault(b => b.name.IndexOf(slot.ID, StringComparison.OrdinalIgnoreCase) >= 0);
            var marker = NewRect("Marker " + slot.ID, _markers);
            marker.sizeDelta = new Vector2(30f, 30f);
            var ring = marker.gameObject.AddComponent<Image>();
            ring.sprite = Knob();
            bool filled = slot.ContainedItem != null;
            ring.color = filled ? new Color(0.85f, 0.9f, 0.95f, 0.9f) : new Color(1f, 0.6f, 0.2f, 0.9f);
            ring.raycastTarget = true;
            var dot = NewRect("Dot", marker);
            dot.sizeDelta = new Vector2(12f, 12f);
            var dotImage = dot.gameObject.AddComponent<Image>();
            dotImage.sprite = Knob(); dotImage.color = new Color(0.05f, 0.06f, 0.07f, 0.95f); dotImage.raycastTarget = false;
            var label = HudStyle.CreateText(marker, "Label", 13f, TextAlignmentOptions.MidlineLeft);
            label.text = SlotLabel(slot.ID) + (filled ? "  <color=#8A9AA6>" + Safe(() => slot.ContainedItem.ShortName.Localized()) + "</color>" : "");
            label.color = Text;
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            label.rectTransform.pivot = new Vector2(0f, 0.5f);
            label.rectTransform.anchoredPosition = new Vector2(6f, 0f);
            label.rectTransform.sizeDelta = new Vector2(260f, 22f);
            var captured = slot;
            marker.gameObject.AddComponent<ArenaMenuButton>().Init(marker, () => OpenDropdown(captured, marker));
            _slots.Add((slot, marker, bone));
        }
        PlaceMarkers();
    }

    private void PlaceMarkers()
    {
        if (_preview == null || _slots.Count == 0) return;
        Camera camera = null;
        try { camera = _preview.WeaponPreviewCamera; } catch { return; }
        if (camera == null) return;
        var size = _previewPanel.rect.size;
        int unplaced = 0;
        foreach (var (slot, marker, bone) in _slots)
        {
            if (marker == null) continue;
            if (bone == null)
            {
                // No bone of that name: stack along the top edge so the slot stays reachable.
                marker.anchorMin = marker.anchorMax = new Vector2(0f, 1f);
                marker.anchoredPosition = new Vector2(80f + 150f * (unplaced++), -120f);
                continue;
            }
            var viewport = camera.WorldToViewportPoint(bone.position);
            marker.gameObject.SetActive(viewport.z > 0f);
            marker.anchorMin = marker.anchorMax = new Vector2(0.5f, 0.5f);
            marker.anchoredPosition = new Vector2((viewport.x - 0.5f) * size.x, (viewport.y - 0.5f) * size.y);
        }
    }

    // ------------------------------------------------------------- dropdown

    private void OpenDropdown(Slot slot, RectTransform marker)
    {
        if (_dropdown == null)
        {
            _dropdown = NewRect("Dropdown", _root);
            _dropdown.anchorMin = _dropdown.anchorMax = new Vector2(0.5f, 0.5f);
            _dropdown.pivot = new Vector2(0f, 1f);
            _dropdown.sizeDelta = new Vector2(420f, 440f);
            var fill = _dropdown.gameObject.AddComponent<Image>();
            fill.color = new Color(0.05f, 0.06f, 0.07f, 0.97f);
            ArenaUiTheme.Rule(_dropdown, "Edge", ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
            var viewport = NewRect("Viewport", _dropdown);
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
            var scroll = _dropdown.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.scrollSensitivity = 40f;
            scroll.movementType = ScrollRect.MovementType.Clamped;
        }
        if (_open == slot && _dropdown.gameObject.activeSelf) { _dropdown.gameObject.SetActive(false); _open = null; return; }
        _open = slot;
        var list = _dropdown.GetComponent<ScrollRect>().content;
        foreach (Transform child in list) Destroy(child.gameObject);
        var corners = new Vector3[4];
        marker.GetWorldCorners(corners);
        var local = _root.InverseTransformPoint(corners[2]);
        _dropdown.anchoredPosition = new Vector2(Mathf.Min(local.x + 8f, 960f - 420f), Mathf.Max(local.y, -540f + 440f));

        var title = HudStyle.CreateText(list, "Title", 14f, TextAlignmentOptions.MidlineLeft);
        title.text = SlotLabel(slot.ID) + (slot.Required ? "   <color=#8A9AA6>VITAL PART</color>" : "");
        title.color = Muted;
        title.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;
        string currentTpl = slot.ContainedItem?.TemplateId;
        if (!slot.Required && slot.ContainedItem != null)
            Row(list, null, "EMPTY", "", false, () => Apply(slot, ""));
        int rows = 0;
        foreach (var entry in ArenaArmory.ForSlot("Mods", null))
        {
            var candidate = ArenaPresetsScreen.ArmoryItem(entry);
            if (candidate == null || !ArenaPresetsScreen.Accepts(slot, candidate)) continue;
            rows++;
            var captured = entry;
            Row(list, candidate, entry.Name ?? entry.Tpl, entry.Score.ToString("N0"), entry.Tpl == currentTpl, () => Apply(slot, captured.Tpl));
        }
        if (rows == 0)
        {
            var empty = HudStyle.CreateText(list, "None", 15f, TextAlignmentOptions.MidlineLeft);
            empty.text = "Nothing in the armory fits this slot."; empty.color = Muted;
            empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
        }
        _dropdown.gameObject.SetActive(true);
        _dropdown.SetAsLastSibling();
        LayoutRebuilder.ForceRebuildLayoutImmediate(list);
        _dropdown.GetComponent<ScrollRect>().verticalNormalizedPosition = 1f;
    }

    private void Row(RectTransform list, Item item, string name, string score, bool current, Action onClick)
    {
        var row = NewRect("Row " + name, list);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 46f;
        var rowImage = row.gameObject.AddComponent<Image>();
        rowImage.color = current ? new Color(0.79f, 0.54f, 0f, 0.12f) : Color.clear; rowImage.raycastTarget = true;
        if (item != null)
        {
            var icon = NewRect("Icon", row);
            icon.anchorMin = icon.anchorMax = new Vector2(0f, 0.5f); icon.pivot = new Vector2(0f, 0.5f);
            icon.anchoredPosition = new Vector2(8f, 0f);
            var image = icon.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            ArenaItemIcon.Bind(image, item, 60f, 40f);
        }
        var label = HudStyle.CreateText(row, "Name", 15f, TextAlignmentOptions.MidlineLeft);
        label.text = name.ToUpperInvariant();
        label.color = current ? Gold : Text;
        ArenaUiBuilder.Stretch(label.rectTransform);
        label.rectTransform.offsetMin = new Vector2(item != null ? 76f : 16f, 0f); label.rectTransform.offsetMax = new Vector2(-70f, 0f);
        var value = HudStyle.CreateText(row, "Score", 14f, TextAlignmentOptions.MidlineRight);
        value.text = score; value.color = new Color(0.95f, 0.62f, 0.35f);
        ArenaUiBuilder.Stretch(value.rectTransform);
        value.rectTransform.offsetMax = new Vector2(-12f, 0f);
        row.gameObject.AddComponent<ArenaMenuButton>().Init(row, onClick);
    }

    private void Apply(Slot slot, string tpl)
    {
        if (_dropdown != null) _dropdown.gameObject.SetActive(false);
        _open = null;
        string path = ArenaPresetsScreen.ModPath(_weapon, slot);
        if (path == null) return;
        if (!string.IsNullOrEmpty(ArenaArmory.Edit("swap", _kit.Id, path, tpl))) return;
        Reload();
    }

    private void ResetToDefault()
    {
        if (_weapon == null) return;
        if (!string.IsNullOrEmpty(ArenaArmory.Edit("swap", _kit.Id, "FirstPrimaryWeapon", _weapon.TemplateId))) return;
        Reload();
    }

    private void Reload()
    {
        var updated = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, _kit.Id, StringComparison.OrdinalIgnoreCase));
        if (updated != null) _kit = updated;
        LoadWeapon();
    }

    // ------------------------------------------------------ characteristics

    private void BindCharacteristics()
    {
        var panel = ArenaUiBuilder.Find(_screen, "ModificationState/CharacteristicsPanel");
        var score = ArenaMetascore.Score(_kit);
        var bar = ArenaUiBuilder.TryFind(panel, "InfoButton/MetaScore/Handler/APCMetascoreBar (3)");
        if (bar != null)
        {
            ArenaUiBuilder.SetActive(bar, "Tooltip", false);
            var fill = ArenaUiBuilder.TryFind(bar, "BarImage")?.GetComponent<Image>();
            if (fill != null) { fill.type = Image.Type.Filled; fill.fillMethod = Image.FillMethod.Horizontal; fill.fillAmount = Mathf.Clamp01(score.Firepower / (float)Math.Max(1, ArenaMetascore.BandMax)); }
            SetText(bar, "ValuePlaceholder/ValueHandler/CurrentValueField", score.Firepower.ToString("N0"));
            SetText(bar, "ValuePlaceholder/ValueHandler/TipValueField", "");
            SetText(bar, "IndicatorContainer/MiddleBar/Separator/MinValue", "");
            SetText(bar, "IndicatorContainer/RightBorder/Separator/MaxValue", "");
        }
        ArenaUiBuilder.SetActive(panel, "InfoButton/Switcher", false);
        var expanded = ArenaUiBuilder.TryFind(panel, "CharacteristicsExpanded");
        if (expanded == null) return;
        expanded.gameObject.SetActive(true);
        foreach (var path in new[] { "CompactTemplate", "Durability", "Mastering", "CompactPanel" }) ArenaUiBuilder.SetActive(expanded, path, false);
        foreach (Transform child in expanded) if (child.name.StartsWith("Stat ")) Destroy(child.gameObject);
        var template = ArenaUiBuilder.TryFind(expanded, "CharTemplate");
        if (template == null || _weapon == null) return;
        float ergonomics = Safe(() => _weapon.ErgonomicsTotal), recoil = Safe(() => _weapon.RecoilTotal), accuracy = Safe(() => _weapon.TotalAccuracy);
        float range = Safe(() => _weapon.SightingRange), weight = Safe(() => _weapon.TotalWeight);
        foreach (var (label, value, max, shown) in new[]
        {
            ("ERGONOMICS", ergonomics, 100f, ergonomics.ToString("0")), ("RECOIL", recoil, 500f, recoil.ToString("0")),
            ("ACCURACY", accuracy, 100f, accuracy.ToString("0.0")), ("SIGHTING RANGE", range, 1000f, range.ToString("0")),
            ("WEIGHT", weight, 10f, weight.ToString("0.00") + " KG"),
        })
        {
            var row = Instantiate(template.gameObject, expanded).GetComponent<RectTransform>();
            row.name = "Stat " + label;
            row.gameObject.SetActive(true);
            SetText(row, "Name", label);
            var valueText = ArenaUiBuilder.TryFind(row, "Value");
            if (valueText != null) { valueText.gameObject.SetActive(true); valueText.GetComponent<TextMeshProUGUI>().text = shown; }
            ArenaUiBuilder.SetActive(row, "bars/Delta", false);
            ArenaUiBuilder.SetActive(row, "bars/CompareDelta", false);
            var full = ArenaUiBuilder.TryFind(row, "bars/Full");
            if (full != null) full.anchorMax = new Vector2(Mathf.Clamp01(value / max), full.anchorMax.y);
            ArenaUiBuilder.SetActive(row, "Icon", false);
        }
        ArenaUiBuilder.SetActive(expanded, "Weight", false);
    }

    // ---------------------------------------------------------------- helpers

    private static float Safe(Func<float> read) { try { return read(); } catch { return 0f; } }
    private static string Safe(Func<string> read) { try { return read() ?? ""; } catch { return ""; } }

    private static Sprite _knob;
    private static Sprite Knob()
    {
        if (_knob != null) return _knob;
        try { _knob = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd"); } catch { /* no builtin */ }
        if (_knob == null)
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - size / 2f + 0.5f, dy = y - size / 2f + 0.5f;
                    texture.SetPixel(x, y, dx * dx + dy * dy <= (size / 2f - 1f) * (size / 2f - 1f) ? Color.white : Color.clear);
                }
            texture.Apply();
            _knob = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
        return _knob;
    }

    private static string SlotLabel(string slotId)
    {
        string label = slotId ?? "";
        if (label.StartsWith("mod_", StringComparison.Ordinal)) label = label.Substring(4);
        return label.Replace('_', ' ').ToUpperInvariant();
    }

    private static void SetText(RectTransform root, string path, string text)
    {
        var t = ArenaUiBuilder.TryFind(root, path)?.GetComponent<TextMeshProUGUI>();
        if (t != null) t.text = text;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }
}
