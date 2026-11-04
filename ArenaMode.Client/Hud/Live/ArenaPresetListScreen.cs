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
/// Live Arena's Presets screen (PresetsScreen / ArenaPresetsUnlockScreen): class tabs across the header, and per class
/// a scrolling page with a BASE section and a CUSTOM section of preset cards (PresetViewCard shell with live's
/// Header, PresetItems cells, Footer, Frame and AdditionalVisual parts). The selected preset wears the pressed frame;
/// a card opens the preset in ArenaPresetsScreen (VIEW / CUSTOMIZATION); the create card copies the class's first
/// base preset into a new custom one.
/// </summary>
internal sealed class ArenaPresetListScreen : MonoBehaviour
{
    private static readonly (string Id, string Tab, string Viewer, string Label)[] Classes =
    {
        ("assault", "AssaultTab", "Assault", "ASSAULT"), ("enforcer", "CqbTab", "Cqb", "ENFORCER"),
        ("scout", "ScoutTab", "Scout", "SCOUT"), ("marksman", "MarksmanTab", "Marksman", "MARKSMAN"),
    };
    private static readonly Color Text = new(0.882f, 0.933f, 0.957f, 1f);
    private static readonly Color Bad = new(0.85f, 0.25f, 0.25f, 1f);
    private static readonly Color Muted = new(0.69f, 0.729f, 0.757f, 1f);
    private const float CardW = 306f, CardH = 287f, GapX = 55f, GapY = 50f, PadTop = 111f, PadBottom = 30f;

    private static ArenaPresetListScreen _instance;
    private ArenaUiPack _pack;
    private ArenaUiBuilder _builder;
    private JObject _layout, _card, _header, _items, _footer, _frame, _visual, _create;
    /// <summary>Live's eye toggle on every card: the character still (default) or the kit's items.</summary>
    private static bool _itemsMode;
    /// <summary>Live's "expand / collapse" on the Standard section.</summary>
    private static bool _standardCollapsed;
    /// <summary>Custom presets per class until progression unlocks more (live: 1 free, 2 bought).</summary>
    private const int CustomSlots = 3;
    private RectTransform _root, _viewers;
    private readonly Dictionary<string, RectTransform> _tabs = new(StringComparer.Ordinal);
    private readonly List<InventoryEquipment> _built = new();
    private string _class = "assault";

    internal static bool Visible => _instance != null;

    internal static void Show()
    {
        if (_instance != null || !ArenaUiPack.IsAvailable) return;
        var go = new GameObject("ArenaPresetListScreen");
        go.transform.SetParent(Plugin.Instance.transform, false);
        _instance = go.AddComponent<ArenaPresetListScreen>();
        try { _instance.Build(); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena presets list failed: " + ex);
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

    /// <summary>Rebuilds the current page (after the preset screen above it changed or selected something).</summary>
    internal static void Refresh()
    {
        if (_instance == null) return;
        try { _instance.SelectClass(_instance._class); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena presets list refresh failed: " + ex.Message); }
    }

    private void OnDestroy()
    {
        _pack?.Dispose();
        _pack = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape) && !ArenaPresetsScreen.Visible) Hide();
    }

    // ---------------------------------------------------------------- build

    private void Build()
    {
        _pack = new ArenaUiPack();
        _builder = new ArenaUiBuilder(_pack, _ => null);
        _layout = _pack.Layout("PresetsScreen");
        _card = _pack.Layout("PresetViewCard");
        _header = _pack.Layout("Header");
        _items = _pack.Layout("PresetItems");
        _footer = _pack.Layout("Footer");
        _frame = _pack.Layout("Frame");
        _visual = _pack.Layout("AdditionalVisual");
        _create = _pack.Layout("BigCustomCreateCard");

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 515;
        canvas.overrideSorting = true;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();

        // Nothing underneath (the main menu) may take a click while the list is up.
        var blocker = (RectTransform)new GameObject("Blocker", typeof(RectTransform)).transform;
        blocker.gameObject.layer = 5;
        blocker.SetParent(transform, false);
        ArenaUiBuilder.Stretch(blocker);
        var blockerImage = blocker.gameObject.AddComponent<Image>();
        blockerImage.color = Color.clear; blockerImage.raycastTarget = true;
        _root = _builder.Build(_layout, transform);
        ArenaUiBuilder.Stretch(_root);
        foreach (var path in new[] { "Mask", "InteractionBlocker", "ScreenEntryTutorialStep", "Header/ViewTabs", "Header/PresetTabs/ClassTypesTutorialStepRect" })
            ArenaUiBuilder.SetActive(_root, path, false);
        ArenaMenuChrome.BuildFooter(_pack, _root, SessionProfile(), Hide, ArenaMainMenuOverlay.ShowSettings, () => { });

        var tabs = ArenaUiBuilder.Find(_root, "Header/PresetTabs");
        foreach (var (id, tabName, _, label) in Classes)
        {
            var tab = ArenaUiBuilder.TryFind(tabs, tabName);
            if (tab == null) continue;
            foreach (var state in new[] { "SelectedState", "IdleState", "HoverState" })
            {
                var title = ArenaUiBuilder.TryFind(tab, state + "/Title")?.GetComponent<TextMeshProUGUI>();
                if (title != null) title.text = label;
            }
            ArenaUiBuilder.SetActive(tab, "HoverState", false);
            ArenaUiBuilder.SetActive(tab, "DailyExpBonusIcon", false);
            var hit = tab.GetComponent<Image>() ?? tab.gameObject.AddComponent<Image>();
            hit.color = Color.clear; hit.raycastTarget = true;
            string captured = id;
            tab.gameObject.AddComponent<ArenaMenuButton>().Init(tab, () => SelectClass(captured));
            _tabs[id] = tab;
        }
        // Live's favourites tab; collections and the BlastGang shop do not exist here.
        ArenaUiBuilder.SetActive(tabs, "AllRounderTab", false);
        var favorite = ArenaUiBuilder.TryFind(tabs, "FavoriteTab");
        if (favorite != null)
        {
            favorite.gameObject.SetActive(true);
            favorite.anchoredPosition = new Vector2(925.5f, -44f);
            ArenaUiBuilder.SetActive(favorite, "HoverState", false);
            ArenaUiBuilder.SetActive(favorite, "DisableState", false);
            var favHit = favorite.GetComponent<Image>() ?? favorite.gameObject.AddComponent<Image>();
            favHit.color = Color.clear; favHit.raycastTarget = true;
            favorite.gameObject.AddComponent<ArenaMenuButton>().Init(favorite, () => SelectClass(FavoritesPage));
            _tabs[FavoritesPage] = favorite;
        }
        foreach (var disabled in new[] { "CollectionsTab", "BlastGangShopTab" })
        {
            var tab = ArenaUiBuilder.TryFind(tabs, disabled);
            if (tab == null) continue;
            foreach (var state in new[] { "SelectedState", "IdleState", "HoverState", "InvalidShopIndicator" }) ArenaUiBuilder.SetActive(tab, state, false);
            ArenaUiBuilder.SetActive(tab, "DisableState", true);
            var title = ArenaUiBuilder.TryFind(tab, "DisableState/Title")?.GetComponent<TextMeshProUGUI>();
            if (title != null) title.text = disabled == "CollectionsTab" ? "COLLECTION" : "BLASTGANG SHOP";
        }

        var back = ArenaUiBuilder.Find(_root, "Header/BackButton");
        var backLabel = ArenaUiBuilder.TryFind(back, "SizeLabel/Label")?.GetComponent<TextMeshProUGUI>();
        if (backLabel != null) backLabel.text = "BACK";
        var backHit = back.GetComponent<Image>() ?? back.gameObject.AddComponent<Image>();
        backHit.color = Color.clear; backHit.raycastTarget = true;
        back.gameObject.AddComponent<ArenaMenuButton>().Init(back, Hide);

        _viewers = ArenaUiBuilder.Find(_root, "ViewersContainer");
        ArenaUiBuilder.SetActive(_viewers, "ArenaBlastGangShopTabContent", false);
        foreach (var (_, _, viewer, _) in Classes)
        {
            var page = ArenaUiBuilder.TryFind(_viewers, viewer);
            if (page == null) continue;
            var scrollView = ArenaUiBuilder.Find(page, "Scroll View");
            var viewport = ArenaUiBuilder.Find(scrollView, "Viewport");
            var content = ArenaUiBuilder.Find(viewport, "Content");
            if (viewport.GetComponent<Mask>() == null) viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var viewportImage = viewport.GetComponent<Image>() ?? viewport.gameObject.AddComponent<Image>();
            viewportImage.raycastTarget = true;
            var scroll = scrollView.GetComponent<ScrollRect>() ?? scrollView.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false;
            scroll.scrollSensitivity = 40f; scroll.movementType = ScrollRect.MovementType.Clamped;
            ArenaUiBuilder.SetActive(scrollView, "ArenaVerticalScrollbar", false);
        }

        var selected = KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, Settings.SelectedKit.Value, StringComparison.OrdinalIgnoreCase));
        SelectClass(ClassOf(selected?.Id) ?? "assault");
        Plugin.ModLogger.LogInfo("Arena presets list shown.");
    }

    private const string FavoritesPage = "favorites";

    private void SelectClass(string classId)
    {
        _class = classId;
        foreach (var (id, _, viewer, _) in Classes)
        {
            if (_tabs.TryGetValue(id, out var tab))
            {
                ArenaUiBuilder.SetActive(tab, "SelectedState", id == classId);
                ArenaUiBuilder.SetActive(tab, "IdleState", id != classId);
            }
            var page = ArenaUiBuilder.TryFind(_viewers, viewer);
            if (page == null) continue;
            page.gameObject.SetActive(id == classId);
            if (id == classId) FillPage(page, classId);
        }
        if (_tabs.TryGetValue(FavoritesPage, out var favoriteTab))
        {
            ArenaUiBuilder.SetActive(favoriteTab, "SelectedState", classId == FavoritesPage);
            ArenaUiBuilder.SetActive(favoriteTab, "IdleState", classId != FavoritesPage);
        }
        var favoritesPage = ArenaUiBuilder.TryFind(_viewers, "Favorite");
        if (favoritesPage != null)
        {
            favoritesPage.gameObject.SetActive(classId == FavoritesPage);
            if (classId == FavoritesPage) FillPage(favoritesPage, null);
        }
    }

    // ------------------------------------------------------------ favourites

    internal static HashSet<string> Favorites()
        => new((Settings.FavoritePresets?.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

    internal static bool IsFavorite(string kitId) => kitId != null && Favorites().Contains(kitId);

    internal static void ToggleFavorite(string kitId)
    {
        if (string.IsNullOrEmpty(kitId) || Settings.FavoritePresets == null) return;
        var set = Favorites();
        if (!set.Remove(kitId)) set.Add(kitId);
        Settings.FavoritePresets.Value = string.Join(",", set);
    }

    // ---------------------------------------------------------------- pages

    private void FillPage(RectTransform page, string classId)
    {
        var content = ArenaUiBuilder.Find(page, "Scroll View/Viewport/Content");
        var favorites = Favorites();
        var kits = (classId == null
            ? KitCatalog.Kits.Where(k => favorites.Contains(k.Id)).OrderBy(k => ClassOf(k.Id)).ThenBy(k => k.Order)
            : KitCatalog.Kits.Where(k => ClassOf(k.Id) == classId).OrderBy(k => k.Order)).ToList();
        var baseKits = kits.Where(k => !IsCustom(k.Id)).ToList();
        var customKits = kits.Where(k => IsCustom(k.Id)).ToList();
        float total = 0f;
        total += FillSection(ArenaUiBuilder.Find(content, "Base"), "Standard", classId, baseKits, false);
        total += FillSection(ArenaUiBuilder.Find(content, "Custom"), "Custom", classId, customKits, classId != null);
        content.sizeDelta = new Vector2(content.sizeDelta.x, total);
        var fitter = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        var scroll = page.GetComponentInChildren<ScrollRect>();
        if (scroll != null) scroll.verticalNormalizedPosition = 1f;
    }

    private float FillSection(RectTransform section, string title, string classId, List<KitDefinition> kits, bool custom)
    {
        var typeInfo = ArenaUiBuilder.Find(section, "Title/Content/TypeInfo");
        var name = ArenaUiBuilder.TryFind(typeInfo, "Name")?.GetComponent<TextMeshProUGUI>();
        if (name != null) name.text = title;
        var icon = ArenaUiBuilder.TryFind(typeInfo, "Icon")?.GetComponent<Image>();
        if (icon != null) { var sprite = classId == null ? null : ClassSprite(classId); icon.sprite = sprite ?? icon.sprite; icon.enabled = sprite != null; }
        ArenaUiBuilder.SetActive(section, "Title/Content/Panel", false);
        ArenaUiBuilder.SetActive(section, "CustomPresetTutorialStepRect", false);
        bool collapsed = !custom && _standardCollapsed;
        var titleBar = ArenaUiBuilder.Find(section, "Title");
        var oldToggle = titleBar.Find("Collapse");
        if (oldToggle != null) Destroy(oldToggle.gameObject);
        if (!custom)
        {
            // Live folds the Standard section away behind "expand / collapse" at the right of its title.
            var toggle = HudStyle.CreateText(titleBar, "Collapse", 16f, TextAlignmentOptions.MidlineRight);
            toggle.text = collapsed ? "expand  \u25BE" : "collapse  \u25B4";
            toggle.color = Muted;
            ArenaUiBuilder.Stretch(toggle.rectTransform);
            toggle.rectTransform.offsetMax = new Vector2(-40f, 0f);
            var titleHit = titleBar.GetComponent<Image>() ?? titleBar.gameObject.AddComponent<Image>();
            if (titleHit.sprite == null) titleHit.color = Color.clear;
            titleHit.raycastTarget = true;
            var oldButton = titleBar.GetComponent<ArenaMenuButton>();
            if (oldButton != null) Destroy(oldButton);
            titleBar.gameObject.AddComponent<ArenaMenuButton>().Init(titleBar, () => { _standardCollapsed = !_standardCollapsed; Refresh(); }, null, false);
        }

        var grid = ArenaUiBuilder.Find(section, "PresetsContainerGrid");
        foreach (Transform child in grid) Destroy(child.gameObject);
        grid.gameObject.SetActive(!collapsed);
        if (collapsed)
        {
            section.sizeDelta = new Vector2(section.sizeDelta.x, 71f);
            var folded = section.GetComponent<LayoutElement>() ?? section.gameObject.AddComponent<LayoutElement>();
            folded.preferredHeight = 71f; folded.minHeight = 71f;
            return 71f;
        }
        var layout = grid.GetComponent<GridLayoutGroup>() ?? grid.gameObject.AddComponent<GridLayoutGroup>();
        layout.cellSize = new Vector2(CardW, CardH); layout.spacing = new Vector2(GapX, GapY);
        layout.padding = new RectOffset(70, 0, (int)PadTop, (int)PadBottom);
        layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount; layout.constraintCount = 5;
        layout.startCorner = GridLayoutGroup.Corner.UpperLeft; layout.childAlignment = TextAnchor.UpperLeft;

        var profile = SessionProfile();
        foreach (var kit in kits) BuildCard(grid, kit, profile);
        bool canCreate = custom && kits.Count < CustomSlots;
        if (canCreate) BuildCreateCard(grid, classId);
        Plugin.ModLogger.LogDebug($"Arena presets list: {title} section, {kits.Count} cards" + (custom ? " + create" : "") + $" for {classId ?? "favorites"}.");

        int count = kits.Count + (canCreate ? 1 : 0);
        int rows = Math.Max(1, (count + 4) / 5);
        float gridHeight = PadTop + rows * CardH + (rows - 1) * GapY + PadBottom;
        grid.sizeDelta = new Vector2(grid.sizeDelta.x, gridHeight);
        var gridElement = grid.GetComponent<LayoutElement>() ?? grid.gameObject.AddComponent<LayoutElement>();
        gridElement.preferredHeight = gridHeight; gridElement.minHeight = gridHeight;
        float height = 71f + gridHeight;
        section.sizeDelta = new Vector2(section.sizeDelta.x, height);
        var element = section.GetComponent<LayoutElement>() ?? section.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = height; element.minHeight = height;
        return height;
    }

    // ---------------------------------------------------------------- cards

    private void BuildCard(RectTransform grid, KitDefinition kit, Profile profile)
    {
        var card = _builder.Build(_card, grid);
        card.gameObject.SetActive(true);
        card.name = "Card " + kit.Id;
        ArenaUiBuilder.SetActive(card, "HoverSelectedBackground", false);
        var container = ArenaUiBuilder.Find(card, "ConstructorContainer");
        var group = container.GetComponent<VerticalLayoutGroup>();
        if (group != null)
        {
            group.childControlHeight = false; group.childControlWidth = false;
            group.childForceExpandHeight = false; group.childForceExpandWidth = false;
            group.childAlignment = TextAnchor.UpperCenter;
        }

        InventoryEquipment equipment = null;
        try { if (profile != null) equipment = TeamArenaEntryKit.Prepare(profile, kit); }
        catch (Exception ex) { Plugin.ModLogger.LogDebug($"Arena presets list: '{kit.Id}' did not build: {ex.Message}"); }
        bool selected = !Settings.RandomKit.Value && string.Equals(Settings.SelectedKit.Value, kit.Id, StringComparison.OrdinalIgnoreCase);
        var score = ArenaMetascore.Score(kit);
        bool ready = score.Total >= ArenaMetascore.BandMin && score.Total <= ArenaMetascore.BandMax && equipment != null;

        // Header: name; live's favourite star and view switch are not in play here.
        var header = _builder.Build(_header, container);
        header.gameObject.SetActive(true);
        header.anchoredPosition = Vector2.zero;
        ArenaUiBuilder.SetActive(header, "TimerPanel", false);
        ArenaUiBuilder.SetActive(header, "HeaderIcons/NewIcon", false);
        ArenaUiBuilder.SetActive(header, "HoverSelectedBackground", false);
        // Live's eye: switches every card between the character still and the items.
        var eye = ArenaUiBuilder.TryFind(header, "ModeSwitchButton");
        if (eye != null)
        {
            ArenaUiBuilder.SetActive(eye, "NormalState", !_itemsMode);
            ArenaUiBuilder.SetActive(eye, "SelecredState", _itemsMode);
            var eyeHit = eye.GetComponent<Image>() ?? eye.gameObject.AddComponent<Image>();
            eyeHit.color = Color.clear; eyeHit.raycastTarget = true;
            eye.gameObject.AddComponent<ArenaMenuButton>().Init(eye, () => { _itemsMode = !_itemsMode; Refresh(); }, null, false);
        }
        // Live's favourite star: lit when starred, dim otherwise; its own click, so the card does not open.
        var star = ArenaUiBuilder.TryFind(header, "FavoriteButton");
        if (star != null)
        {
            bool starred = IsFavorite(kit.Id);
            ArenaUiBuilder.SetActive(star, "SelectedState", starred);
            ArenaUiBuilder.SetActive(star, "NormalState", !starred);
            var normal = ArenaUiBuilder.TryFind(star, "NormalState")?.GetComponent<Image>();
            if (normal != null) normal.color = new Color(1f, 1f, 1f, 0.35f);
            var starHit = star.GetComponent<Image>() ?? star.gameObject.AddComponent<Image>();
            starHit.color = Color.clear; starHit.raycastTarget = true;
            string starredId = kit.Id;
            star.gameObject.AddComponent<ArenaMenuButton>().Init(star, () => { ToggleFavorite(starredId); Refresh(); });
        }
        ArenaUiBuilder.SetActive(header, "Separator", false);
        var title = ArenaUiBuilder.TryFind(header, "Title")?.GetComponent<TextMeshProUGUI>();
        if (title != null) { title.text = (kit.Name ?? kit.Id).ToUpperInvariant(); title.rectTransform.offsetMin = new Vector2(10f, title.rectTransform.offsetMin.y); }

        // Body: the character wearing the kit (a still), or the items when the eye is switched.
        var body = NewRect("Body", container);
        body.sizeDelta = new Vector2(306f, 221f);
        var bodyElement = body.gameObject.AddComponent<LayoutElement>();
        bodyElement.preferredWidth = 306f; bodyElement.preferredHeight = 221f;
        var items = _builder.Build(_items, body);
        items.gameObject.SetActive(_itemsMode);
        items.anchorMin = items.anchorMax = new Vector2(0.5f, 1f); items.pivot = new Vector2(0.5f, 1f);
        items.anchoredPosition = Vector2.zero;
        FillItems(items, equipment, kit);
        if (!_itemsMode)
        {
            var portraitHost = NewRect("PortraitHost", body);
            ArenaUiBuilder.Stretch(portraitHost);
            var portrait = NewRect("Portrait", body).gameObject.AddComponent<RawImage>();
            ArenaUiBuilder.Stretch(portrait.rectTransform);
            portrait.raycastTarget = false;
            portrait.enabled = false;
            ArenaKitPortraits.Request(kit, profile, equipment, portraitHost, texture =>
            {
                if (portrait == null) return;
                portrait.texture = texture;
                portrait.enabled = texture != null;
            });
        }
        // Metascore under the header, as live prints it ("10 979.1").
        var scoreRow = NewRect("Score", body);
        scoreRow.anchorMin = scoreRow.anchorMax = new Vector2(0f, 1f); scoreRow.pivot = new Vector2(0f, 1f);
        scoreRow.anchoredPosition = new Vector2(8f, -4f); scoreRow.sizeDelta = new Vector2(200f, 20f);
        var scoreIcon = NewRect("Icon", scoreRow).gameObject.AddComponent<Image>();
        scoreIcon.rectTransform.anchorMin = scoreIcon.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        scoreIcon.rectTransform.pivot = new Vector2(0f, 0.5f); scoreIcon.rectTransform.sizeDelta = new Vector2(18f, 18f);
        scoreIcon.sprite = ScoreSprite(); scoreIcon.raycastTarget = false; scoreIcon.enabled = scoreIcon.sprite != null;
        var scoreText = HudStyle.CreateText(scoreRow, "Value", 14f, TextAlignmentOptions.MidlineLeft);
        ArenaUiBuilder.Stretch(scoreText.rectTransform);
        scoreText.rectTransform.offsetMin = new Vector2(22f, 0f);
        scoreText.text = score.Total.ToString("N1");
        scoreText.color = ready ? Text : Bad;

        // Footer: VIEW on standard presets, EDIT on custom ones; its own click opens the preset.
        var footer = _builder.Build(_footer, container);
        footer.gameObject.SetActive(true);
        footer.anchoredPosition = Vector2.zero;
        ArenaUiBuilder.SetActive(footer, "InsprectModification/HoveredState", false);
        ArenaUiBuilder.SetActive(footer, "InsprectModification/DisableState", false);
        bool custom = IsCustom(kit.Id);
        foreach (var state in new[] { "IdleState", "HoveredState" })
        {
            var footerText = ArenaUiBuilder.TryFind(footer, "InsprectModification/" + state + "/InspectText")?.GetComponent<TextMeshProUGUI>();
            if (footerText != null) footerText.text = custom ? "EDIT" : "VIEW";
            ArenaUiBuilder.SetActive(footer, "InsprectModification/" + state + "/IconImage", true);
        }
        var footerButton = ArenaUiBuilder.Find(footer, "InsprectModification");
        var footerHit = footerButton.GetComponent<Image>() ?? footerButton.gameObject.AddComponent<Image>();
        footerHit.color = Color.clear; footerHit.raycastTarget = true;
        var opened = kit;
        footerButton.gameObject.AddComponent<ArenaMenuButton>().Init(footerButton, () => ArenaPresetsScreen.Show(opened, IsCustom(opened.Id)),
            null, false, ArenaUiBuilder.TryFind(footer, "InsprectModification/HoveredState"));

        var frame = _builder.Build(_frame, card);
        frame.gameObject.SetActive(true);
        frame.anchoredPosition = Vector2.zero;
        var visual = _builder.Build(_visual, card);
        visual.gameObject.SetActive(true);
        visual.anchoredPosition = Vector2.zero;
        ArenaUiBuilder.SetActive(visual, "BonusCorner", false);
        ArenaUiBuilder.SetActive(visual, "PresetBlockPanel", false);
        ArenaUiBuilder.SetActive(visual, "AdditionalFrame", selected);

        // The card itself selects the preset; hover lights the card and header as live does.
        var hit = card.GetComponent<Image>() ?? card.gameObject.AddComponent<Image>();
        hit.color = Color.clear; hit.raycastTarget = true;
        var captured = kit;
        var cardHover = ArenaUiBuilder.TryFind(card, "HoverSelectedBackground");
        if (cardHover != null) cardHover.gameObject.SetActive(true);
        card.gameObject.AddComponent<ArenaMenuButton>().Init(card, () =>
        {
            Settings.SelectedKit.Value = captured.Id;
            Settings.RandomKit.Value = false;
            Plugin.ModLogger.LogInfo($"Arena preset selected: {captured.Id}");
            ArenaMainMenuOverlay.RefreshCharacter();
            Refresh();
        }, cardHover?.GetComponent<Image>(), false, ArenaUiBuilder.TryFind(header, "HoverSelectedBackground"));
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private Sprite _scoreSprite;

    /// <summary>The metascore glyph, taken from the customization screen's bar icon.</summary>
    private Sprite ScoreSprite()
    {
        if (_scoreSprite != null) return _scoreSprite;
        try
        {
            var bar = ArenaMenuChrome.Find(_pack.Layout("ArenaPresetCustomizationScreen"), "APCMetascoreBar");
            var icon = bar == null ? null : ArenaMenuChrome.Find(bar, "Icon");
            if (icon?["components"] is JArray components)
                foreach (var component in components)
                    if (component is JObject c && (c.Value<string>("type") ?? "").EndsWith(".Image") && c["fields"]?["m_Sprite"] is JObject sprite)
                        return _scoreSprite = _pack.Sprite(sprite);
        }
        catch { /* no glyph */ }
        return null;
    }

    private void FillItems(RectTransform items, InventoryEquipment equipment, KitDefinition kit)
    {
        var weapon = Slot(equipment, EquipmentSlot.FirstPrimaryWeapon) ?? Slot(equipment, EquipmentSlot.SecondPrimaryWeapon) ?? Slot(equipment, EquipmentSlot.Holster);
        Cell(ArenaUiBuilder.Find(items, "WeaponInfo/WeaponCategory/WeaponItem"), weapon, 160f, 60f, name: weapon?.ShortName?.Localized(), plus: Slot(equipment, EquipmentSlot.Holster) != null && weapon != Slot(equipment, EquipmentSlot.Holster));

        Item round = null;
        int rounds = 0;
        try
        {
            if (weapon is Weapon w)
            {
                var magazine = w.GetCurrentMagazine();
                round = magazine?.Cartridges?.Items?.FirstOrDefault();
                rounds = (magazine?.Cartridges?.Items?.Sum(i => i.StackObjectsCount) ?? 0) * (1 + Math.Max(0, kit.Fill?.SpareMagazines ?? 0));
            }
        }
        catch { /* no magazine */ }
        Cell(ArenaUiBuilder.Find(items, "WeaponInfo/AmmoCategory/BulletItem"), round, 60f, 60f, name: round?.ShortName?.Localized(), bottomRight: rounds > 0 ? rounds.ToString() : null);

        var carried = Carried(equipment);
        var armour = Slot(equipment, EquipmentSlot.ArmorVest) ?? (Slot(equipment, EquipmentSlot.TacticalVest) is { } rig && IsArmoured(rig) ? rig : null);
        Cell(ArenaUiBuilder.Find(items, "OtherItems/ArmorCategory/Armor"), armour, 42f, 42f, keepBottomRightIcon: armour != null);
        var grenades = carried.Where(i => i is ThrowWeap).ToList();
        Cell(ArenaUiBuilder.Find(items, "OtherItems/GrenadeCategory/GrenadeAndSpec"), grenades.FirstOrDefault(), 42f, 42f, bottomRight: grenades.Count > 0 ? grenades.Count.ToString() : null, plus: grenades.Select(g => g.TemplateId).Distinct().Count() > 1);
        var meds = carried.Where(i => i is Meds || i is FoodDrink).ToList();
        Cell(ArenaUiBuilder.Find(items, "OtherItems/MedicineCategory/Medicine"), meds.FirstOrDefault(), 42f, 42f, bottomRight: meds.Count > 0 ? meds.Sum(m => Math.Max(1, m.StackObjectsCount)).ToString() : null, plus: meds.Select(m => m.TemplateId).Distinct().Count() > 1);
        var other = carried.Where(i => !(i is ThrowWeap) && !(i is Meds) && !(i is FoodDrink) && !(i is Magazine) && !(i is Ammo)).ToList();
        Cell(ArenaUiBuilder.Find(items, "OtherItems/OtherImtesCategory/Other"), other.FirstOrDefault(), 42f, 42f, central: other.Count > 0 ? other.Count.ToString() : null);
    }

    /// <summary>One PresetItems cell: live's icon, name, counters and plus marker, hidden when nothing fills it.</summary>
    private static void Cell(RectTransform cell, Item item, float iconW, float iconH, string name = null, string bottomRight = null,
        string central = null, bool plus = false, bool keepBottomRightIcon = false)
    {
        if (cell == null) return;
        ArenaUiBuilder.SetActive(cell, "Loader", false);
        ArenaUiBuilder.SetActive(cell, "PlusIcon", plus);
        var icon = ArenaUiBuilder.TryFind(cell, "ItemIcon");
        if (icon != null)
        {
            icon.gameObject.SetActive(item != null);
            if (item != null)
            {
                var image = icon.GetComponent<Image>() ?? icon.gameObject.AddComponent<Image>();
                image.raycastTarget = false;
                ArenaItemIcon.Bind(image, item, iconW, iconH);
            }
        }
        SetText(cell, "Name", name);
        SetText(cell, "BottomRightInfo", bottomRight);
        SetText(cell, "BottomLeftInfo", null);
        SetText(cell, "CentralInfo", central);
        if (!keepBottomRightIcon) ArenaUiBuilder.SetActive(cell, "BottomRightIcon", false);
    }

    private static void SetText(RectTransform root, string path, string text)
    {
        var node = ArenaUiBuilder.TryFind(root, path);
        if (node == null) return;
        node.gameObject.SetActive(!string.IsNullOrEmpty(text));
        var t = node.GetComponent<TextMeshProUGUI>();
        if (t != null) t.text = text ?? "";
    }

    private void BuildCreateCard(RectTransform grid, string classId)
    {
        var card = _builder.Build(_create, grid);
        card.gameObject.SetActive(true);
        card.name = "Create";
        ArenaUiBuilder.SetActive(card, "Container/HoverBackground", false);
        var label = HudStyle.CreateText(ArenaUiBuilder.Find(card, "Container"), "Label", 20f, TextAlignmentOptions.Midline);
        label.text = "CREATE CUSTOM PRESET";
        label.color = Text;
        ArenaUiBuilder.Stretch(label.rectTransform);
        label.rectTransform.offsetMin = new Vector2(20f, 0f); label.rectTransform.offsetMax = new Vector2(-20f, 0f);
        var hit = card.GetComponent<Image>() ?? card.gameObject.AddComponent<Image>();
        hit.color = Color.clear; hit.raycastTarget = true;
        card.gameObject.AddComponent<ArenaMenuButton>().Init(card, () =>
        {
            var source = KitCatalog.Kits.Where(k => ClassOf(k.Id) == classId && !IsCustom(k.Id)).OrderBy(k => k.Order).FirstOrDefault();
            if (source == null || !string.IsNullOrEmpty(ArenaArmory.Edit("create", source.Id))) return;
            var created = KitCatalog.Kits.Where(k => IsCustom(k.Id) && ClassOf(k.Id) == classId).OrderBy(k => k.Order).LastOrDefault();
            Refresh();
            if (created != null) ArenaPresetsScreen.Show(created, true);
        });
    }

    // ---------------------------------------------------------------- helpers

    private static Item Slot(InventoryEquipment equipment, EquipmentSlot slot)
    {
        try { return equipment?.GetSlot(slot)?.ContainedItem; } catch { return null; }
    }

    private static bool IsArmoured(Item rig)
    {
        try { return rig.GetItemComponent<ArmorHolderComponent>() != null; } catch { return false; }
    }

    private static List<Item> Carried(InventoryEquipment equipment)
    {
        var list = new List<Item>();
        if (equipment == null) return list;
        try
        {
            foreach (var container in KitBuilder.GridContainersOf(equipment))
            {
                if (ReferenceEquals(container, equipment.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem)) continue;
                foreach (var grid in container.Grids) list.AddRange(grid.Items);
            }
        }
        catch { /* partial */ }
        return list;
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
