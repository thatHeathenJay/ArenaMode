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
/// The Presets screen: Arena's preset-customization header (class tabs, status, preset name, Back) over three
/// preset cards per class, each listing the kit and its metascore bars against the live band, with Select making
/// it the kit every life starts with. ponytail: selection only; swapping items inside a preset is the next step.
/// </summary>
internal sealed class ArenaPresetsScreen : MonoBehaviour
{
    private static readonly (string Id, string Label)[] Classes =
    {
        ("assault", "ASSAULT"), ("enforcer", "ENFORCER"), ("scout", "SCOUT"), ("marksman", "MARKSMAN"),
    };
    private static readonly Color Text = new(0.882f, 0.933f, 0.957f, 1f);
    private static readonly Color Muted = new(0.69f, 0.729f, 0.757f, 1f);
    private static readonly Color Gold = new(0.79f, 0.54f, 0f, 1f);
    private static readonly Color Bad = new(0.85f, 0.25f, 0.25f, 1f);

    private static ArenaPresetsScreen _instance;
    private ArenaUiPack _pack;
    private ArenaUiBuilder _builder;
    private JObject _menuLayout, _apcLayout, _editorLayout;
    private RectTransform _tabs, _cards;
    private TextMeshProUGUI _status, _presetName;
    private string _class = "assault";
    private readonly List<(string id, RectTransform tab)> _tabViews = new();

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
        _pack?.Dispose();
        _pack = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) Hide();
    }

    private void Build()
    {
        _pack = new ArenaUiPack();
        _builder = new ArenaUiBuilder(_pack, key => key switch
        {
            "APC/CustomizationTab" => "CUSTOMIZATION", "APC/ViewPreset" => "VIEW", "Arena/EnterPresetName" => "", _ => null,
        });
        _menuLayout = _pack.Layout("ArenaMainMenu");
        _apcLayout = _pack.Layout("ArenaPresetCustomizationScreen");
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
        backdrop.GetComponentInChildren<Image>().color = new Color(1f, 1f, 1f, 0.35f);

        // Header: Arena's preset-customization title bar (tabs become the class tabs).
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

        var tabsHost = ArenaUiBuilder.Find(header, "HeaderLeftContent/TabsSelector");
        foreach (Transform child in tabsHost) Destroy(child.gameObject);
        var tabsLayout = tabsHost.gameObject.AddComponent<HorizontalLayoutGroup>();
        tabsLayout.spacing = 4f; tabsLayout.childForceExpandWidth = tabsLayout.childForceExpandHeight = false;
        tabsLayout.childControlWidth = tabsLayout.childControlHeight = true; tabsLayout.childAlignment = TextAnchor.MiddleLeft;
        var tabsElement = tabsHost.gameObject.AddComponent<LayoutElement>();
        tabsElement.preferredWidth = 4 * 200f + 12f; tabsElement.preferredHeight = 32f;
        _tabs = tabsHost;
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
                    title.text = label;
                    title.enableAutoSizing = false;
                    title.fontSize = 14f;
                    title.alignment = TextAlignmentOptions.Midline;
                    title.rectTransform.anchorMin = Vector2.zero; title.rectTransform.anchorMax = Vector2.one;
                    title.rectTransform.offsetMin = new Vector2(12f, 0f); title.rectTransform.offsetMax = new Vector2(-12f, 0f);
                }
                ArenaUiBuilder.SetActive(tab, state + "/Image", false);
            }
            ArenaUiBuilder.SetActive(tab, "DisabledState", false);
            var hit = tab.GetComponent<Image>() ?? tab.gameObject.AddComponent<Image>();
            hit.raycastTarget = true;
            string captured = id;
            tab.gameObject.AddComponent<ArenaMenuButton>().Init(tab, () => SelectClass(captured));
            _tabViews.Add((id, tab));
        }

        // Back (Arena's own button from the same screen).
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

        // Cards.
        _cards = NewRect("Cards", root);
        _cards.anchorMin = new Vector2(0.5f, 0.5f); _cards.anchorMax = new Vector2(0.5f, 0.5f);
        _cards.pivot = new Vector2(0.5f, 0.5f); _cards.anchoredPosition = new Vector2(0f, -20f);
        _cards.sizeDelta = new Vector2(1560f, 820f);
        var cardsLayout = _cards.gameObject.AddComponent<HorizontalLayoutGroup>();
        cardsLayout.spacing = 30f; cardsLayout.childForceExpandWidth = cardsLayout.childForceExpandHeight = true;
        cardsLayout.childControlWidth = cardsLayout.childControlHeight = true;

        string selected = Settings.SelectedKit.Value;
        var selectedKit = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, selected, StringComparison.OrdinalIgnoreCase));
        _class = ClassOf(selectedKit?.Id) ?? "assault";
        SelectClass(_class);
    }

    private void SelectClass(string id)
    {
        _class = id;
        foreach (var (tabId, tab) in _tabViews)
        {
            ArenaUiBuilder.SetActive(tab, "SelectedState", tabId == id);
            ArenaUiBuilder.SetActive(tab, "IdleState", tabId != id);
        }
        foreach (Transform child in _cards) Destroy(child.gameObject);
        var kits = KitCatalog.Kits.Where(k => ClassOf(k.Id) == id).OrderBy(k => k.Order).ToList();
        foreach (var kit in kits) BuildCard(kit);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        string selected = Settings.SelectedKit.Value;
        var kit = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, selected, StringComparison.OrdinalIgnoreCase));
        bool random = Settings.RandomKit.Value || kit == null;
        _status.text = random ? "RANDOM PRESET" : "READY FOR BATTLE";
        _presetName.text = random ? "Random kit each life" : (kit.Name ?? kit.Id);
    }

    private void BuildCard(KitDefinition kit)
    {
        var card = NewRect("Card " + kit.Id, _cards);
        var panel = card.gameObject.AddComponent<Image>();
        panel.color = new Color(0.04f, 0.05f, 0.06f, 0.88f);
        panel.raycastTarget = false;
        ArenaUiTheme.Rule(card, "Edge", ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
        var inner = card.gameObject.AddComponent<VerticalLayoutGroup>();
        inner.padding = new RectOffset(24, 24, 20, 20); inner.spacing = 8f;
        inner.childForceExpandWidth = true; inner.childForceExpandHeight = false;
        inner.childControlWidth = true; inner.childControlHeight = true;

        var score = ArenaMetascore.Score(kit);
        bool selected = !Settings.RandomKit.Value && string.Equals(Settings.SelectedKit.Value, kit.Id, StringComparison.OrdinalIgnoreCase);

        Label(card, (kit.Name ?? kit.Id).ToUpperInvariant(), 24f, selected ? Gold : Text, 34f);
        string weaponTpl = TplInSlot(kit, "FirstPrimaryWeapon");
        string weaponName = NameOf(weaponTpl) ?? "";
        var silhouette = _pack.WholeTexture("Texture2D/" + weaponName + ".png");
        if (silhouette != null)
        {
            var art = NewRect("Weapon", card);
            var image = art.gameObject.AddComponent<Image>();
            image.sprite = silhouette; image.preserveAspect = true; image.color = Text;
            art.gameObject.AddComponent<LayoutElement>().preferredHeight = 90f;
        }
        Line(card, "WEAPON", weaponName);
        Line(card, "AMMO", NameOf(kit.Items.FirstOrDefault(n => n.SlotId == "cartridges")?.Tpl) ?? "");
        Line(card, "ARMOR", NameOf(TplInSlot(kit, "ArmorVest")) ?? NameOf(TplInSlot(kit, "TacticalVest")) ?? "none");
        Line(card, "RIG", NameOf(TplInSlot(kit, "TacticalVest")) ?? "none");
        Line(card, "HELMET", NameOf(TplInSlot(kit, "Headwear")) ?? "none");
        Line(card, "HEADSET", NameOf(TplInSlot(kit, "Earpiece")) ?? "none");
        Line(card, "SPARE MAGS", (kit.Fill?.SpareMagazines ?? 0).ToString());
        var meds = kit.Items.Where(n => n.SlotId != null && n.SlotId.StartsWith("pocket", StringComparison.OrdinalIgnoreCase))
            .Select(n => NameOf(n.Tpl)).Concat((kit.Fill?.RigContents ?? new List<KitStackedItem>()).Select(r => NameOf(r.Tpl)))
            .Where(n => n != null).Distinct().ToList();
        Line(card, "SUPPLIES", string.Join(", ", meds));

        Spacer(card, 10f);
        int max = Math.Max(ArenaMetascore.BandMax, 1);
        Bar(card, "FIREPOWER", score.Firepower, max);
        Bar(card, "DEFENSE", score.Defense, max);
        Bar(card, "UTILITY", score.Utility, max);
        bool inBand = score.Total >= ArenaMetascore.BandMin && score.Total <= ArenaMetascore.BandMax;
        Label(card, $"METASCORE {score.Total:N0}   ·   BAND {ArenaMetascore.BandMin:N0} – {ArenaMetascore.BandMax:N0}", 16f, inBand ? Text : Bad, 24f);

        Spacer(card, 6f);
        var button = NewRect("Select", card);
        button.gameObject.AddComponent<LayoutElement>().preferredHeight = 52f;
        var buttonFill = button.gameObject.AddComponent<Image>();
        buttonFill.color = selected ? new Color(0.79f, 0.54f, 0f, 0.18f) : new Color(0.52f, 0.60f, 0.67f, 0.12f);
        buttonFill.raycastTarget = true;
        ArenaUiTheme.Rule(button, "Edge", selected ? ArenaUiTheme.WithAlpha(Gold, 0.8f) : ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
        var label = HudStyle.CreateText(button, "Label", 24f, TextAlignmentOptions.Midline);
        label.text = selected ? "SELECTED" : "SELECT";
        label.color = selected ? Gold : Text;
        ArenaUiBuilder.Stretch(label.rectTransform);
        button.gameObject.AddComponent<ArenaMenuButton>().Init(button, () =>
        {
            Settings.SelectedKit.Value = kit.Id;
            Settings.RandomKit.Value = false;
            Plugin.ModLogger.LogInfo($"Arena preset selected: {kit.Id}");
            SelectClass(_class);
            ArenaMainMenuOverlay.RefreshCharacter();
        });
    }

    private void Bar(RectTransform card, string label, int value, int max)
    {
        var row = NewRect("Bar " + label, card);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;
        var text = HudStyle.CreateText(row, "Label", 13f, TextAlignmentOptions.MidlineLeft);
        text.color = Muted; text.text = label;
        var textRect = text.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0f); textRect.anchorMax = new Vector2(0f, 1f); textRect.pivot = new Vector2(0f, 0.5f);
        textRect.anchoredPosition = Vector2.zero; textRect.sizeDelta = new Vector2(110f, 0f);
        var bar = _builder.Build(Find(_editorLayout, "APCMetascoreBar"), row);
        bar.gameObject.SetActive(true);
        bar.anchorMin = new Vector2(0f, 0.5f); bar.anchorMax = new Vector2(1f, 0.5f); bar.pivot = new Vector2(0f, 0.5f);
        bar.anchoredPosition = new Vector2(115f, 0f); bar.sizeDelta = new Vector2(-175f, 12f);
        ArenaUiBuilder.SetActive(bar, "Icon", false);
        ArenaUiBuilder.SetActive(bar, "Tooltip", false);
        ArenaUiBuilder.SetActive(bar, "BarImage (1)", false);
        var fillImage = ArenaUiBuilder.TryFind(bar, "BarImage")?.GetComponent<Image>();
        if (fillImage != null)
        {
            fillImage.type = Image.Type.Filled; fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillAmount = Mathf.Clamp01(value / (float)max);
        }
        var minText = ArenaUiBuilder.TryFind(bar, "IndicatorContainer/MiddleBar/Separator/MinValue")?.GetComponent<TextMeshProUGUI>();
        if (minText != null) minText.text = "";
        var maxText = ArenaUiBuilder.TryFind(bar, "IndicatorContainer/RightBorder/Separator/MaxValue")?.GetComponent<TextMeshProUGUI>();
        if (maxText != null) maxText.text = "";
        var current = ArenaUiBuilder.TryFind(bar, "ValuePlaceholder/ValueHandler/CurrentValueField")?.GetComponent<TextMeshProUGUI>();
        if (current != null) current.text = value.ToString("N0");
        var number = HudStyle.CreateText(row, "Value", 13f, TextAlignmentOptions.MidlineRight);
        number.color = Text; number.text = value.ToString("N0");
        var numberRect = number.rectTransform;
        numberRect.anchorMin = new Vector2(1f, 0f); numberRect.anchorMax = new Vector2(1f, 1f); numberRect.pivot = new Vector2(1f, 0.5f);
        numberRect.anchoredPosition = Vector2.zero; numberRect.sizeDelta = new Vector2(56f, 0f);
    }

    private static void Line(RectTransform card, string label, string value)
    {
        var row = NewRect("Line " + label, card);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;
        var key = HudStyle.CreateText(row, "Key", 13f, TextAlignmentOptions.MidlineLeft);
        key.color = Muted; key.text = label;
        var keyRect = key.rectTransform;
        keyRect.anchorMin = new Vector2(0f, 0f); keyRect.anchorMax = new Vector2(0f, 1f); keyRect.pivot = new Vector2(0f, 0.5f);
        keyRect.anchoredPosition = Vector2.zero; keyRect.sizeDelta = new Vector2(110f, 0f);
        var val = HudStyle.CreateText(row, "Value", 15f, TextAlignmentOptions.MidlineLeft);
        val.color = Text; val.text = value; val.overflowMode = TextOverflowModes.Ellipsis;
        var valRect = val.rectTransform;
        valRect.anchorMin = new Vector2(0f, 0f); valRect.anchorMax = new Vector2(1f, 1f); valRect.pivot = new Vector2(0f, 0.5f);
        valRect.anchoredPosition = new Vector2(115f, 0f); valRect.sizeDelta = new Vector2(-115f, 0f);
    }

    private static void Label(RectTransform card, string text, float size, Color color, float height)
    {
        var label = HudStyle.CreateText(card, "Label", size, TextAlignmentOptions.Midline);
        label.color = color; label.text = text;
        label.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
    }

    private static void Spacer(RectTransform card, float height)
        => NewRect("Spacer", card).gameObject.AddComponent<LayoutElement>().preferredHeight = height;

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

    private static string ClassOf(string kitId)
    {
        if (string.IsNullOrEmpty(kitId)) return null;
        int dash = kitId.IndexOf('-');
        string prefix = dash > 0 ? kitId.Substring(0, dash) : kitId;
        return Classes.Any(c => c.Id == prefix) ? prefix : null;
    }

    private static string TplInSlot(KitDefinition kit, string slotId)
        => kit.Items?.FirstOrDefault(n => string.Equals(n.SlotId, slotId, StringComparison.OrdinalIgnoreCase))?.Tpl;

    private static string NameOf(string tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return null;
        try
        {
            var factory = Singleton<ItemFactory>.Instance;
            if (factory == null || !factory.ItemTemplates.TryGetValue(new MongoID(tpl), out var template)) return null;
            var name = template.ShortNameLocalizationKey?.Localized();
            return string.IsNullOrWhiteSpace(name) ? template.Name : name;
        }
        catch { return null; }
    }
}
