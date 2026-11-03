using ArenaMode.Config;
using ArenaMode.Modes;
using EFT;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's "Arena Battle" matchmaking flow, built from the ripped screens: SELECT GAME TYPE
/// (ArenaRankingModeSelectionScreen), SELECT UNRANKED GAME MODE (ArenaGameModeSelectionScreen) and SELECT ARENA
/// (ArenaMapSelectionScreen), each under live's breadcrumb strip and over its bottom bar (ArenaMenuChrome). NEXT on
/// the map screen remembers the location and runs EFT's stock play path; ArenaSideSkip then selects that location on
/// EFT's map screen and presses its Next, so the stock matchmaker screens are never seen. Only UNRANKED / TeamFight
/// (the fork's team deathmatch) is playable; the other live modes are listed as live lists them, but NEXT stays off.
/// </summary>
internal sealed class ArenaBattleFlow : MonoBehaviour
{
    private enum Step { GameType, GameMode, Map }

    // Live strings (TestBackendLocaleEn.json); the builder localizes LocalizedText keys through this table.
    private static readonly Dictionary<string, string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arena/matching/SelectGametype"] = "SELECT GAME TYPE", ["Unranked"] = "UNRANKED", ["Ranked"] = "RANKED",
        ["Arena/UI/CustomMode"] = "Custom", ["Arena/UI/TutorialMode"] = "Tutorial",
        ["arena/matching/SelectRankedGamemode"] = "SELECT UNRANKED GAME MODE", ["arena/selection/gameMode"] = "GAME MODE",
        ["Description"] = "Description", ["arena/matching/AvailableArenaMaps"] = "AVAILABLE LOCATIONS", ["SETTINGS"] = "SETTINGS",
        ["arena/matching/SelectArenaMap"] = "SELECT ARENA", ["arena/matching/AvailableModes"] = "AVAILABLE GAME MODES",
    };
    private const string TeamFightDescription =
        "A team battle in the 5v5 format. The objective is to eliminate the opposing team sooner than they kill you.";
    private const string TeamFightRespawnsDescription =
        "A team battle in the 5v5 format with respawns. The team with more kills when the time runs out wins.";
    private const string NotAvailable = "\n\n<color=#FF8D00><i>Not available in this build yet.</i></color>";

    private const string LogoMedium = "Texture2D/Matchmaker_GameMods_Logo_Medium.png";
    private const string LogoBig = "Texture2D/Matchmaker_GameMods_Logo_Big.png";
    private const string LogoSmall = "Texture2D/Matchmaker_GameMods_Logo_Small.png";
    private const string MapIcons = "Texture2D/Matchmaker_MapButton_MapIcons-All.png";
    private const string TeamFightId = "teamfight";

    // Live's unranked modes in live order (LAST HERO, CHECKPOINT, CHECKPOINT BLOODLUST, SHOOTOUT TOURNAMENT,
    // TEAMFIGHT, BLASTGANG). Card = the list logo (medium atlas; BlastGang only exists in the big one), Small = the
    // description logo; rects are the logos' tight bounds measured in the rip (TestBackendLocaleEn descriptions).
    private static readonly (string Id, string Title, string Tag, float TagX, string Players, string Description, string Atlas, float[] Card, float[] Small)[] Modes =
    {
        ("lasthero", "LAST HERO", "LH", ArenaMenuChrome.TagLastHero, null,
            "A chaotic free-for-all gunfight. There will be only one winner.",
            LogoMedium, new[] { 562f, 36f, 241f, 63f }, new[] { 277f, 11f, 130f, 35f }),
        ("checkpoint", "CHECKPOINT", "CPT", ArenaMenuChrome.TagCheckPoint, "5x5",
            "A team battle in the 5v5 format. Fight for control over the game location along with your team by capturing and holding the objectives.",
            LogoMedium, new[] { 2212f, 51f, 219f, 75f }, new[] { 1101f, 19f, 118f, 42f }),
        ("bloodlust", "CHECKPOINT BLOODLUST", "CPBL", ArenaMenuChrome.TagBloodlust, "5x5",
            "A team battle in the 5v5 format. Fight for control over the game location along with your team by capturing and holding the objectives. Bloodlust rules: no armor, no healing.",
            LogoMedium, new[] { 3859f, 37f, 213f, 97f }, new[] { 1926f, 11f, 109f, 50f }),
        ("shootout", "SHOOTOUT TOURNAMENT", "SO", ArenaMenuChrome.TagShootOut, "2x2",
            "Shootout - a round-robin tournament style match where each team plays against the others. The two teams with the highest number of victory points meet in the final, where the winner is determined.",
            LogoMedium, new[] { 1656f, 35f, 238f, 102f }, new[] { 822f, 11f, 130f, 55f }),
        (TeamFightId, "TEAMFIGHT", "TF", ArenaMenuChrome.TagTeamFight, "5x5", TeamFightDescription,
            LogoMedium, new[] { 293f, 36f, 234f, 99f }, new[] { 142f, 11f, 126f, 54f }),
        ("blastgang", "BLASTGANG", "BG", ArenaMenuChrome.TagBlastGang, "5x5",
            "A team battle in the 5v5 format. A classic Search and Destroy game mode with one side attacking and the other defending.",
            LogoBig, new[] { 2642f, 66f, 305f, 120f }, new[] { 965f, 14f, 120f, 47f }),
    };

    // Maps: name plate position over Main_Map_Screen (normalized inside the sprite) and the MapIcons-All rect of
    // the pin symbol that fits the map. ponytail: symbols and positions are picked by eye (the rip has no
    // location -> icon table); swap the rects if live data turns up.
    private static readonly (string Id, string Name, string Banner, Vector2 At, float[] Icon)[] Maps =
    {
        ("arena_airpit", "Air Pit", "AirPit", new Vector2(0.886f, 0.202f), new[] { 268.0761f, 103.07612f, 23.872564f, 33.872566f }),
        ("arena_bay5", "Bay 5", "Bay5", new Vector2(0.251f, 0.140f), new[] { 108.07612f, 103.07612f, 23.872564f, 33.872566f }),
        ("arena_bowl", "Bowl", "Bowl", new Vector2(0.900f, 0.663f), new[] { 188.07613f, 103.07612f, 23.872564f, 33.872566f }),
        ("arena_equator", "Equator", "Equator", new Vector2(0.525f, 0.453f), new[] { 987.02673f, 102.07612f, 25.946497f, 34.872566f }),
    };

    private static ArenaBattleFlow _instance;
    private static string _lastLocation = Maps[0].Id;

    /// <summary>Set once a match was started through this flow; backing out of what follows returns to SELECT ARENA.</summary>
    internal static bool UsedThisSession;

    /// <summary>False after QUICK GAME: backing out returns to the main menu instead of SELECT ARENA.</summary>
    internal static bool ReturnToMap = true;

    internal static void ReopenAtMap()
    {
        Show();
        if (_instance != null) _instance.Open(Step.Map);
    }

    /// <summary>Location chosen on SELECT ARENA, consumed by ArenaSideSkip on EFT's location screen.</summary>
    internal static string PendingLocationId;

    private ArenaUiPack _pack;
    private ArenaUiBuilder _builder;
    private RectTransform _root, _screen, _pins, _description, _info, _header, _nextButton, _respawnsToggle;
    private TextMeshProUGUI _modeDescription, _respawnsLabel;
    private readonly List<RectTransform> _cards = new();
    private Step _step;
    private string _location, _mode = TeamFightId;

    internal static bool Visible => _instance != null;

    internal static void Show()
    {
        if (_instance != null || !ArenaUiPack.IsAvailable) return;
        var go = new GameObject("ArenaBattleFlow");
        go.transform.SetParent(Plugin.Instance.transform, false);
        _instance = go.AddComponent<ArenaBattleFlow>();
        try { _instance.Build(); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena battle flow failed: " + ex);
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

    /// <summary>Live's QUICK GAME: straight into matchmaking, here TeamFight on a random arena.</summary>
    internal static void QuickGame()
    {
        var map = Maps[UnityEngine.Random.Range(0, Maps.Length)];
        Start(map.Id, false);
    }

    private static void Start(string locationId, bool returnToMap)
    {
        Settings.TrySelectGameMode(ArenaModeCatalog.TeamDeathmatchId);   // TeamFight is the fork's team deathmatch
        Hide();
        PendingLocationId = locationId;
        UsedThisSession = true;
        ReturnToMap = returnToMap;
        Plugin.ModLogger.LogInfo($"Arena battle flow: TeamFight on {locationId}, starting matchmaking.");
        ArenaMainMenuOverlay.StartStockMatchmaking();
    }

    private void OnDestroy()
    {
        _pack?.Dispose();
        _pack = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;
        if (_step == Step.GameType) Hide();
        else Open(_step - 1);
    }

    // ---------------------------------------------------------------- build

    private void Build()
    {
        _pack = new ArenaUiPack();
        _builder = new ArenaUiBuilder(_pack, key => English.TryGetValue(key, out var s) ? s : null);

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 520;
        canvas.overrideSorting = true;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();
        _root = (RectTransform)transform;
        // No backdrop: live draws these screens over the faded menu scene (each layout's Underlay); the main menu
        // overlay switches itself off while this flow is up.
        Open(Step.GameType);
    }

    private void Open(Step step)
    {
        if (_screen != null) Destroy(_screen.gameObject);
        _screen = _pins = _description = _info = _header = _nextButton = _respawnsToggle = null;
        _cards.Clear();
        _step = step;
        switch (step)
        {
            case Step.GameType: BuildGameType(); break;
            case Step.GameMode: BuildGameMode(); break;
            default: BuildMap(); break;
        }
        Plugin.ModLogger.LogInfo("Arena battle flow: " + step switch
        {
            Step.GameType => "SELECT GAME TYPE",
            Step.GameMode => "SELECT UNRANKED GAME MODE",
            _ => "SELECT ARENA",
        });
    }

    private RectTransform BuildScreen(string layout)
    {
        _screen = _builder.Build(_pack.Layout(layout), _root);
        ArenaUiBuilder.SetActive(_screen, "ArenaMenuSparks", false);     // a render texture in live
        ArenaMenuChrome.BuildFooter(_pack, _screen, SessionProfile(), Hide, ArenaMainMenuOverlay.ShowSettings,
            () => { Hide(); ArenaPresetListScreen.Show(); });
        return _screen;
    }

    private void Header(string modeTag, float? tagX, string location)
    {
        if (_header != null) Destroy(_header.gameObject);
        _header = ArenaMenuChrome.BuildHeader(_pack, _screen, "UNRANKED", modeTag, tagX, location);
    }

    // ------------------------------------------------------- SELECT GAME TYPE

    private void BuildGameType()
    {
        var screen = BuildScreen("ArenaRankingModeSelectionScreen");
        ArenaUiBuilder.Text(screen, "TitlePanel/Header").text = "SELECT GAME TYPE";
        // Ranked, Custom games and Tutorial keep their UNAVAILABLE block and take no clicks.
        var unranked = ArenaUiBuilder.Find(screen, "ModeButtons/UnrankedButton");
        ArenaUiBuilder.SetActive(unranked, "UnavailableBlock", false);
        Clickable(unranked, "ClickHandler", () => Open(Step.GameMode));
        FlowButton(screen, "BackButton", Hide);
    }

    // ---------------------------------------------- SELECT UNRANKED GAME MODE

    private void BuildGameMode()
    {
        var layout = _pack.Layout("ArenaGameModeSelectionScreen");
        var screen = BuildScreen("ArenaGameModeSelectionScreen");
        ArenaUiBuilder.Text(screen, "TitlePanel/Header").text = "SELECT UNRANKED GAME MODE";
        ArenaUiBuilder.Text(screen, "Modes List/Header/GameModes").text = "GAME MODE";

        var list = ArenaUiBuilder.Find(screen, "Modes List/Viewport/DepensOfTiers/DependsContainer");
        var template = ArenaMenuChrome.Find(layout, "GameModeViewTemplate");
        foreach (var mode in Modes)
        {
            var card = _builder.Build(template, list);
            card.name = mode.Id;
            card.gameObject.SetActive(true);
            var image = ArenaUiBuilder.TryFind(card, "ModeIcon/ModeImage")?.GetComponent<Image>();
            if (image != null) image.sprite = _pack.Sprite(SpriteRef(mode.Atlas, mode.Id, mode.Card, 78f)) ?? image.sprite;
            ArenaUiBuilder.SetActive(card, "ModeIcon/UnavailableBackground", false);
            var hover = ArenaUiBuilder.TryFind(card, "ModeIcon/ModeHover");
            if (hover != null) hover.gameObject.SetActive(true);
            string id = mode.Id;
            Clickable(card, "ClickHandler", () => SelectMode(id), hover?.GetComponent<Image>());
            _cards.Add(card);
        }

        _info = ArenaUiBuilder.Find(screen, "Mode Description");
        var group = _info.GetComponent<CanvasGroup>();
        if (group != null) group.alpha = 1f;                                // faded in by live's panel
        ArenaUiBuilder.SetActive(_info, "Content/InvalidModeDescriptionBlock", false);
        _modeDescription = ArenaUiBuilder.Text(_info, "Content/DescriptionBlock/ModeDecriprion");
        var settings = ArenaUiBuilder.Find(_info, "Content/SettingsBlock");
        var players = _builder.Build(ArenaMenuChrome.Find(layout, "PlayersTemplate"), ArenaUiBuilder.Find(settings, "IconsPanel/Players/PlayersList"));
        players.gameObject.SetActive(true);
        BuildRespawnsToggle(settings);

        _nextButton = FlowButton(screen, "NextButton", () => Open(Step.Map));
        _nextButton.gameObject.AddComponent<CanvasGroup>();
        FlowButton(screen, "BackButton", () => Open(Step.GameType));
        SelectMode(_mode);
    }

    private void SelectMode(string id)
    {
        var mode = Modes.FirstOrDefault(m => m.Id == id);
        if (mode.Id == null) mode = Modes.First(m => m.Id == TeamFightId);
        _mode = mode.Id;
        bool teamFight = mode.Id == TeamFightId;
        foreach (var card in _cards)
        {
            bool on = card.name == mode.Id;
            ArenaUiBuilder.SetActive(card, "ModeIcon/ModeSelectedBack", on);
            ArenaUiBuilder.SetActive(card, "ModeIcon/ModeSelected", on);
        }

        var icon = ArenaUiBuilder.TryFind(_info, "ModeIcon/ModeImage")?.GetComponent<Image>();
        if (icon != null) icon.sprite = _pack.Sprite(SpriteRef(LogoSmall, mode.Id, mode.Small, 68f)) ?? icon.sprite;
        RefreshDescription();

        // Locations: this fork's arenas all run TeamFight; the other modes have none here.
        var maps = ArenaUiBuilder.Find(_info, "Content/MapsBlock");
        maps.gameObject.SetActive(teamFight);
        var mapsList = ArenaUiBuilder.Find(maps, "MapsList");
        foreach (Transform old in mapsList.Cast<Transform>().ToArray()) Destroy(old.gameObject);
        var mapTemplate = ArenaMenuChrome.Find(_pack.Layout("ArenaGameModeSelectionScreen"), "MapTemplate");
        if (teamFight)
            foreach (var map in Maps)
            {
                var row = _builder.Build(mapTemplate, mapsList);
                row.gameObject.SetActive(true);
                var ico = ArenaUiBuilder.TryFind(row, "MapIcoBlock/MapIco")?.GetComponent<Image>();
                if (ico != null) ico.sprite = _pack.Sprite(SpriteRef(MapIcons, map.Name, map.Icon, 100f)) ?? ico.sprite;
                ArenaUiBuilder.Text(row, "MapName").text = map.Name;
            }

        var settings = ArenaUiBuilder.Find(_info, "Content/SettingsBlock");
        var playersBlock = ArenaUiBuilder.Find(settings, "IconsPanel/Players");
        playersBlock.gameObject.SetActive(mode.Players != null);
        if (mode.Players != null) ArenaUiBuilder.Text(playersBlock, "PlayersList/PlayersTemplate").text = mode.Players;
        ArenaUiBuilder.SetActive(settings, "IconsPanel/Playtime", teamFight);
        if (teamFight) RefreshPlaytime(settings);
        if (_respawnsToggle != null) _respawnsToggle.gameObject.SetActive(teamFight);

        var next = _nextButton.GetComponent<CanvasGroup>();
        next.alpha = teamFight ? 1f : 0.35f;
        next.interactable = next.blocksRaycasts = teamFight;
        Header(mode.Tag, mode.TagX, null);
    }

    private void RefreshDescription()
    {
        if (_modeDescription == null) return;
        var mode = Modes.First(m => m.Id == _mode);
        _modeDescription.text = mode.Id == TeamFightId
            ? Settings.TeamFightRespawns.Value ? TeamFightRespawnsDescription : TeamFightDescription
            : mode.Description + NotAvailable;
    }

    /// <summary>Live's clock: match minutes with respawns, best-of rounds without.</summary>
    private static void RefreshPlaytime(RectTransform settings)
    {
        var text = ArenaUiBuilder.TryFind(settings, "IconsPanel/Playtime/Playtime")?.GetComponent<TextMeshProUGUI>();
        if (text == null) return;
        text.text = Settings.TeamFightRespawns.Value
            ? Mathf.Max(1, Settings.TeamDeathmatchTimeLimitSeconds.Value / 60) + " MIN"
            : "BO" + (2 * Settings.TeamFightRoundsToWin.Value - 1);
    }

    /// <summary>TeamFight's one option: Respawns on (kill count, continuous respawns) or off (live rounds).</summary>
    private void BuildRespawnsToggle(RectTransform settingsBlock)
    {
        var toggle = _respawnsToggle = NewRect("RespawnsToggle", settingsBlock);
        toggle.sizeDelta = new Vector2(500f, 30f);
        var element = toggle.gameObject.AddComponent<LayoutElement>();
        element.preferredWidth = 500f; element.preferredHeight = 30f; element.minHeight = 30f;
        var hit = toggle.gameObject.AddComponent<Image>();
        hit.color = Color.clear; hit.raycastTarget = true;
        _respawnsLabel = HudStyle.CreateText(toggle, "Label", 16f, TextAlignmentOptions.MidlineLeft);
        ArenaUiBuilder.Stretch(_respawnsLabel.rectTransform);
        _respawnsLabel.rectTransform.offsetMin = new Vector2(12f, 0f);
        RefreshRespawnsToggle();
        toggle.gameObject.AddComponent<ArenaMenuButton>().Init(toggle, () =>
        {
            Settings.TeamFightRespawns.Value = !Settings.TeamFightRespawns.Value;
            RefreshRespawnsToggle();
            RefreshDescription();
            RefreshPlaytime(settingsBlock);
            Plugin.ModLogger.LogInfo("Arena battle flow: TeamFight respawns " + (Settings.TeamFightRespawns.Value ? "on" : "off"));
        });
    }

    private void RefreshRespawnsToggle()
    {
        if (_respawnsLabel == null) return;
        bool on = Settings.TeamFightRespawns.Value;
        _respawnsLabel.text = (on ? "<color=#C98A00>[X]</color> " : "[  ] ") + "RESPAWNS";
        _respawnsLabel.color = on ? new Color(0.882f, 0.933f, 0.957f) : ArenaUiTheme.Muted;
    }

    // ------------------------------------------------------------ SELECT ARENA

    private void BuildMap()
    {
        var layout = _pack.Layout("ArenaMapSelectionScreen");
        var screen = BuildScreen("ArenaMapSelectionScreen");
        ArenaUiBuilder.SetActive(screen, "TestButton", false);
        ArenaUiBuilder.SetActive(screen, "UnderlayRight", false);
        ArenaUiBuilder.Text(screen, "TitlePanel/Header").text = "SELECT ARENA";

        // The ripped image carries an AspectRatioFitter (height controls width, 1:1) the builder skips.
        var mapImage = ArenaUiBuilder.Find(screen, "Map/Image");
        var fitter = mapImage.gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
        fitter.aspectRatio = 1f;
        _pins = ArenaUiBuilder.Find(mapImage, "LocationsContainer");
        _pins.SetAsLastSibling();
        // Live's name plates (ArenaMatchingScreen's Location Template): grey plate, green under the pointer,
        // orange when selected, the map's symbol on the left and the mode tags underneath.
        var plate = ArenaMenuChrome.Find(_pack.Layout("ArenaMatchingScreen"), "Location Template");
        foreach (var map in Maps) BuildPlate(plate, map.Id, map.Name, map.At, map.Icon);

        _description = ArenaUiBuilder.Find(screen, "Description");
        var group = _description.GetComponent<CanvasGroup>();
        if (group != null) group.alpha = 1f;
        // Every map here runs TeamFight only.
        var modeList = ArenaUiBuilder.Find(_description, "Content/ModesBlock/ModeList");
        var mode = _builder.Build(ArenaMenuChrome.Find(layout, "ModeTemplate"), modeList);
        mode.gameObject.SetActive(true);
        ArenaUiBuilder.Text(mode, "ModeTemplate/Title").text = "TEAMFIGHT";
        var tag = ArenaUiBuilder.TryFind(mode, "ModeTemplate/Background")?.GetComponent<Image>();
        if (tag != null) tag.sprite = _pack.Sprite(ArenaMenuChrome.TagSprite(ArenaMenuChrome.TagTeamFight)) ?? tag.sprite;
        var playersValue = ArenaUiBuilder.TryFind(mode, "PlayersHolder/PlayersValue");
        if (playersValue != null)
        {
            playersValue.gameObject.SetActive(true);
            playersValue.GetComponent<TextMeshProUGUI>().text = "5x5";
        }

        FlowButton(screen, "NextButton", StartMatch);
        FlowButton(screen, "BackButton", () => Open(Step.GameMode));
        SelectLocation(_lastLocation);
    }

    private void BuildPlate(JObject template, string id, string name, Vector2 at, float[] icon)
    {
        var plate = _builder.Build(template, _pins);
        plate.name = id;
        plate.gameObject.SetActive(true);
        plate.anchorMin = plate.anchorMax = at;
        plate.pivot = new Vector2(15f / 186f, 0.5f);                       // the map symbol sits on the spot
        plate.anchoredPosition = Vector2.zero;
        ArenaUiBuilder.Text(plate, "Button/MapButton/View/Label").text = name.ToUpperInvariant();
        var symbol = ArenaUiBuilder.TryFind(plate, "Button/IconHandler/Available Icon")?.GetComponent<Image>();
        if (symbol != null) symbol.sprite = _pack.Sprite(SpriteRef(MapIcons, name, icon, 100f)) ?? symbol.sprite;
        ArenaUiBuilder.SetActive(plate, "Button/IconHandler/Scanline Icon", false);
        var tags = ArenaUiBuilder.Find(plate, "AvailbaleModesContiainer");
        var tag = _builder.Build(ArenaMenuChrome.Find(template, "AvailableModeTemplate"), tags);
        tag.gameObject.SetActive(true);
        ArenaUiBuilder.Text(tag, "Text").text = "TF";
        var tagImage = tag.GetComponent<Image>();
        if (tagImage != null) tagImage.sprite = _pack.Sprite(ArenaMenuChrome.TagSprite(ArenaMenuChrome.TagTeamFight)) ?? tagImage.sprite;
        var hover = ArenaUiBuilder.TryFind(plate, "Button/MapButton/ScanlineBackground")?.GetComponent<Image>();
        Clickable(plate, "ClickHandler", () => SelectLocation(id), hover, false);
    }

    private void SelectLocation(string id)
    {
        var map = Maps.FirstOrDefault(m => m.Id == id);
        if (map.Id == null) map = Maps[0];
        _location = _lastLocation = map.Id;
        foreach (Transform pin in _pins)
        {
            var selected = ArenaUiBuilder.TryFind((RectTransform)pin, "Button/MapButton/View")?.GetComponent<Image>();
            if (selected != null) selected.enabled = pin.name == map.Id;
        }

        ArenaUiBuilder.Text(_description, "TitleBlock/NameBlock/Location Name").text = map.Name.ToUpperInvariant();
        var icon = ArenaUiBuilder.TryFind(_description, "TitleBlock/NameBlock/MapIco")?.GetComponent<Image>();
        if (icon != null) icon.sprite = _pack.Sprite(SpriteRef(MapIcons, map.Name, map.Icon, 100f)) ?? icon.sprite;
        // The ripped locale has no arena map descriptions; show EFT's own when the server provides one.
        string description = MapDescription(map.Id);
        var text = ArenaUiBuilder.Text(_description, "Content/DescriptionBlock/Descriprion");
        text.gameObject.SetActive(description != null);
        if (description != null) text.text = description;
        var banner = ArenaUiBuilder.TryFind(_description, "MapPreview/Banner")?.GetComponent<Image>();
        var art = LocationBanner(map.Id) ?? _pack.WholeTexture($"Texture2D/ArenaBackgroundPanel_{map.Banner}.png");
        if (banner != null && art != null) banner.sprite = art;
        ArenaUiBuilder.Text(_screen, "MapsCounter/Counter/Counter").text = "01";
        Header("TF", ArenaMenuChrome.TagTeamFight, map.Name);
    }

    private void StartMatch()
    {
        if (_location != null) Start(_location, true);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Live's NEXT / BACK buttons; their transparent "___NEXT___" sizing label stays invisible on hover.</summary>
    private static RectTransform FlowButton(RectTransform screen, string path, Action action)
    {
        var button = ArenaUiBuilder.Find(screen, path);
        button.gameObject.SetActive(true);
        var size = ArenaUiBuilder.TryFind(button, "SizeLabel")?.GetComponent<TextMeshProUGUI>();
        if (size != null)
        {
            size.richText = true;
            size.text = "<alpha=#00>" + size.text;   // ArenaMenuButton tints the first label white on hover
        }
        Clickable(button, "RaycastTarget", action);
        return button;
    }

    private static void Clickable(RectTransform node, string hitPath, Action action, Image hover = null, bool tintLabel = true)
    {
        var hit = ArenaUiBuilder.TryFind(node, hitPath) ?? node;
        var image = hit.GetComponent<Image>();
        if (image == null)
        {
            image = hit.gameObject.AddComponent<Image>();
            image.color = Color.clear;
        }
        image.raycastTarget = true;
        node.gameObject.AddComponent<ArenaMenuButton>().Init(node, action, hover, tintLabel);
    }

    private static JObject SpriteRef(string atlas, string name, float[] rect, float ppu) => new()
    {
        ["sprite"] = atlas, ["name"] = name, ["rect"] = new JArray(rect[0], rect[1], rect[2], rect[3]),
        ["pivot"] = new JArray(0.5, 0.5), ["ppu"] = ppu,
    };

    private static readonly Dictionary<string, Sprite> Banners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The map's banner as the server serves it to EFT's own location screen (ArenaMapPort's banners folder).</summary>
    private static Sprite LocationBanner(string locationId)
    {
        if (Banners.TryGetValue(locationId, out var cached)) return cached;
        Sprite sprite = null;
        foreach (var route in new[] { "/files/banners/" + locationId, "/files/location/" + locationId })
        {
            try
            {
                var bytes = SPT.Common.Http.RequestHandler.GetData(route);
                if (bytes == null || bytes.Length < 512) continue;
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes)) { Destroy(texture); continue; }
                sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                break;
            }
            catch (Exception ex) { Plugin.ModLogger.LogDebug($"Arena battle flow: banner {route} unavailable: {ex.Message}"); }
        }
        Banners[locationId] = sprite;
        return sprite;
    }

    private static string MapDescription(string locationId)
    {
        try
        {
            var locations = SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.LocationSettings?.locations;
            var location = locations?.Values.FirstOrDefault(l => string.Equals(l?.Id, locationId, StringComparison.OrdinalIgnoreCase));
            if (location == null) return null;
            string key = location._Id + " Description";
            string text = key.Localized();
            return string.IsNullOrWhiteSpace(text) || text == key ? null : text;
        }
        catch { return null; }
    }

    private static Profile SessionProfile()
    {
        try { return SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile; }
        catch { return null; }
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
