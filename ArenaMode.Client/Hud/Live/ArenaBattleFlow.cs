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
    private enum Step { GameMode, Map }     // live's SELECT GAME TYPE is skipped: only UNRANKED exists here

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
            LogoMedium, new[] { 553.21f, 27.08f, 257.85f, 79.85f }, new[] { 273.14f, 8.03f, 136.92f, 41.9f }),
        ("checkpoint", "CHECKPOINT", "CPT", ArenaMenuChrome.TagCheckPoint, "5x5",
            "A team battle in the 5v5 format. Fight for control over the game location along with your team by capturing and holding the objectives.",
            LogoMedium, new[] { 2209.61f, 47.08f, 224.9f, 81.85f }, new[] { 1096.29f, 14.03f, 125.9f, 49.9f }),
        ("bloodlust", "CHECKPOINT BLOODLUST", "CPBL", ArenaMenuChrome.TagBloodlust, "5x5",
            "A team battle in the 5v5 format. Fight for control over the game location along with your team by capturing and holding the objectives. Bloodlust rules: no armor, no healing.",
            LogoMedium, new[] { 3848.01f, 29.01f, 229.85f, 106.91f }, new[] { 1911.47f, 8.01f, 126.92f, 54.92f }),
        ("shootout", "SHOOTOUT TOURNAMENT", "SO", ArenaMenuChrome.TagShootOut, "2x2",
            "Shootout - a round-robin tournament style match where each team plays against the others. The two teams with the highest number of victory points meet in the final, where the winner is determined.",
            LogoMedium, new[] { 1647.48f, 26.08f, 255.85f, 119.9f }, new[] { 819.24f, 7.08f, 136.91f, 60.92f }),
        (TeamFightId, "TEAMFIGHT", "TF", ArenaMenuChrome.TagTeamFight, "5x5", TeamFightDescription,
            LogoMedium, new[] { 284.09f, 27.08f, 251.9f, 117.85f }, new[] { 137.61f, 8.03f, 134.9f, 59.97f }),
        ("blastgang", "BLASTGANG", "BG", ArenaMenuChrome.TagBlastGang, "5x5",
            "A team battle in the 5v5 format. A classic Search and Destroy game mode with one side attacking and the other defending.",
            LogoBig, new[] { 2639.62f, 63f, 309.9f, 124.97f }, new[] { 962.81f, 12.08f, 124.85f, 50.85f }),
    };

    // Maps: name plate position over Main_Map_Screen (normalized inside the sprite) and the MapIcons-All rect of
    // the pin symbol that fits the map. ponytail: symbols and positions are picked by eye (the rip has no
    // location -> icon table); swap the rects if live data turns up.
    private static readonly (string Id, string Name, string Banner, Vector2 At, float[] Icon)[] Maps =
    {
        ("arena_airpit", "Air Pit", "AirPit", new Vector2(0.80f, 0.27f), new[] { 268.0761f, 103.07612f, 23.872564f, 33.872566f }),
        ("arena_bay5", "Bay 5", "Bay5", new Vector2(0.251f, 0.140f), new[] { 108.07612f, 103.07612f, 23.872564f, 33.872566f }),
        ("arena_bowl", "Bowl", "Bowl", new Vector2(0.82f, 0.66f), new[] { 188.07613f, 103.07612f, 23.872564f, 33.872566f }),
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
    private TextMeshProUGUI _modeDescription, _respawnsLabel, _playersValue;
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

    /// <summary>Players per side on an arena: live's 5, or fewer where the arena's team boxes hold fewer.</summary>
    internal static int TeamSizeFor(string locationId)
    {
        int cap = Shared.ArenaParticipantPolicy.MaximumFor(locationId, ArenaModeCatalog.TeamDeathmatchId) / 2;
        return Mathf.Clamp(Mathf.Min(Settings.TeamDeathmatchSize.Value, cap), 2, 5);
    }

    private static void Start(string locationId, bool returnToMap)
    {
        Settings.TrySelectGameMode(ArenaModeCatalog.TeamDeathmatchId);   // TeamFight is the fork's team deathmatch
        int size = TeamSizeFor(locationId);
        if (!ArenaParticipantSettings.TrySelect(locationId, ArenaModeCatalog.TeamDeathmatchId, 2 * size))
            Plugin.ModLogger.LogWarning($"Arena battle flow: could not set {size}v{size} participants for {locationId}; the participant setting applies.");
        Hide();
        PendingLocationId = locationId;
        UsedThisSession = true;
        ReturnToMap = returnToMap;
        Plugin.ModLogger.LogInfo($"Arena battle flow: TeamFight on {locationId}, starting matchmaking.");
        ArenaCurtain.Show();                                    // EFT's own side / map screens pass by unseen
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
        if (_step == Step.GameMode) Hide();
        else Open(Step.GameMode);
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
        Open(Step.GameMode);
    }

    private void Open(Step step)
    {
        if (_screen != null) Destroy(_screen.gameObject);
        _screen = _pins = _description = _info = _header = _nextButton = _respawnsToggle = null;
        _cards.Clear();
        _step = step;
        if (step == Step.GameMode) BuildGameMode(); else BuildMap();
        Plugin.ModLogger.LogInfo("Arena battle flow: " + (step == Step.GameMode ? "SELECT UNRANKED GAME MODE" : "SELECT ARENA"));
    }

    private RectTransform BuildScreen(string layout)
    {
        _screen = _builder.Build(_pack.Layout(layout), _root);
        ArenaUiBuilder.SetActive(_screen, "ArenaMenuSparks", false);     // a render texture in live
        ArenaMenuChrome.BuildFooter(_pack, _screen, SessionProfile(), Hide, ArenaMainMenuOverlay.ShowSettings,
            () => { Hide(); ArenaPresetListScreen.Show(); });
        return _screen;
    }

    private void Header(string modeTag, float? tagX, IReadOnlyList<string> locations)
    {
        if (_header != null) Destroy(_header.gameObject);
        _header = ArenaMenuChrome.BuildHeader(_pack, _screen, "UNRANKED", modeTag, tagX, locations);
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
            var element = card.GetComponent<LayoutElement>();
            if (element != null) element.preferredHeight = 120f;
            var image = ArenaUiBuilder.TryFind(card, "ModeIcon/ModeImage")?.GetComponent<Image>();
            if (image != null)
            {
                image.sprite = _pack.Sprite(SpriteRef(mode.Atlas, mode.Id, mode.Card, 78f)) ?? image.sprite;
                image.rectTransform.anchorMin = image.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                image.rectTransform.sizeDelta = new Vector2(280f, 150f);
            }
            bool available = mode.Id == TeamFightId;
            ArenaUiBuilder.SetActive(card, "ModeIcon/UnavailableBackground", !available);
            if (!available)
            {
                var reason = ArenaUiBuilder.TryFind(card, "ModeIcon/UnavailableBackground/UnavailableReason")?.GetComponent<TextMeshProUGUI>();
                if (reason != null) reason.text = "Unavailable";
                _cards.Add(card);
                continue;
            }
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
        FlowButton(screen, "BackButton", Hide);
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
                if (ico != null)
                {
                    ico.sprite = _pack.Sprite(SpriteRef(MapIcons, map.Name, map.Icon, 100f)) ?? ico.sprite;
                    ico.rectTransform.sizeDelta = new Vector2(map.Icon[2], map.Icon[3]);   // native, as live sizes it
                }
                ArenaUiBuilder.Text(row, "MapName").text = map.Name;
            }

        var settings = ArenaUiBuilder.Find(_info, "Content/SettingsBlock");
        var playersBlock = ArenaUiBuilder.Find(settings, "IconsPanel/Players");
        playersBlock.gameObject.SetActive(mode.Players != null);
        if (mode.Players != null)
            ArenaUiBuilder.Text(playersBlock, "PlayersList/PlayersTemplate").text = teamFight
                ? string.Join(" / ", Maps.Select(m => TeamSizeFor(m.Id)).Distinct().OrderByDescending(n => n).Select(n => $"{n}x{n}"))
                : mode.Players;
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
        var title = ArenaUiBuilder.Text(mode, "ModeTemplate/Title");
        title.text = "TEAMFIGHT";
        title.alignment = TextAlignmentOptions.Center;
        title.enableWordWrapping = false;
        title.overflowMode = TextOverflowModes.Overflow;
        title.fontSize = 12f;
        var tag = ArenaUiBuilder.TryFind(mode, "ModeTemplate/Background")?.GetComponent<Image>();
        if (tag != null) tag.sprite = _pack.Sprite(ArenaMenuChrome.TagSprite(ArenaMenuChrome.TagTeamFight)) ?? tag.sprite;
        var playersValue = ArenaUiBuilder.TryFind(mode, "PlayersHolder/PlayersValue");
        if (playersValue != null)
        {
            playersValue.gameObject.SetActive(true);
            _playersValue = playersValue.GetComponent<TextMeshProUGUI>();
        }

        FlowButton(screen, "NextButton", StartMatch);
        FlowButton(screen, "BackButton", () => Open(Step.GameMode));
        if (SelectedMaps.Count == 0) foreach (var map in Maps) SelectedMaps.Add(map.Id);
        RefreshPlates();
        ShowDescription(_lastLocation);
    }

    /// <summary>Arenas ticked for matchmaking, as live's multi-select; NEXT draws one of them.</summary>
    private static readonly HashSet<string> SelectedMaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RectTransform> _plates = new(StringComparer.OrdinalIgnoreCase);
    private string _hoverMap;

    private const string PlateAtlas = "Texture2D/Matchmaker_MapButton_Background2.png";
    private static readonly float[] PlateOrange = { 490.0761f, 0.07413995f, 219.84775f, 99.84974f };   // ticked (glow)
    private static readonly float[] PlateGrey = { 302.0761f, 32.042908f, 155.84775f, 35.880974f };    // under the pointer

    /// <summary>
    /// Live's location plate, drawn from its sprites: the pin symbol, the name, and the mode tags underneath; the
    /// orange glowing plate when ticked, the grey plate under the pointer, bare text otherwise.
    /// </summary>
    private void BuildPlate(JObject template, string id, string name, Vector2 at, float[] icon)
    {
        var plate = NewRect(id, _pins);
        plate.anchorMin = plate.anchorMax = at;
        plate.pivot = new Vector2(15f / 186f, 0.5f);                       // the pin symbol sits on the spot
        plate.anchoredPosition = Vector2.zero;
        plate.sizeDelta = new Vector2(186f, 34f);
        var hit = plate.gameObject.AddComponent<Image>();
        hit.color = Color.clear; hit.raycastTarget = true;

        Image Part(string node, float[] rect, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var image = NewRect(node, plate).gameObject.AddComponent<Image>();
            image.sprite = _pack.Sprite(SpriteRef(PlateAtlas, node, rect, 100f));
            image.raycastTarget = false;
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = anchor;
            image.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            image.rectTransform.anchoredPosition = pos;
            image.rectTransform.sizeDelta = size;
            return image;
        }
        // The plate images cover the name part (x 30..186); the orange one carries its glow around it.
        Part("Orange", PlateOrange, new Vector2(0f, 0.5f), new Vector2(108f, 0f), new Vector2(220f, 100f)).enabled = false;
        Part("Grey", PlateGrey, new Vector2(0f, 0.5f), new Vector2(108f, 0f), new Vector2(156f, 36f)).enabled = false;
        var symbol = NewRect("Pin", plate).gameObject.AddComponent<Image>();
        symbol.sprite = _pack.Sprite(SpriteRef(MapIcons, name, icon, 100f));
        symbol.raycastTarget = false;
        symbol.rectTransform.anchorMin = symbol.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        symbol.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        symbol.rectTransform.anchoredPosition = new Vector2(15f, 0f);
        symbol.rectTransform.sizeDelta = new Vector2(icon[2], icon[3]);
        var label = HudStyle.CreateText(plate, "Label", 20f, TextAlignmentOptions.MidlineLeft);
        label.text = name.ToUpperInvariant();
        label.characterSpacing = 4f; label.enableWordWrapping = false; label.overflowMode = TextOverflowModes.Overflow;
        label.rectTransform.anchorMin = new Vector2(0f, 0f); label.rectTransform.anchorMax = new Vector2(1f, 1f);
        label.rectTransform.offsetMin = new Vector2(42f, 0f); label.rectTransform.offsetMax = new Vector2(-4f, 0f);
        // Mode tags under the plate: one colour-coded abbreviation per mode the arena runs (TeamFight only here).
        var tag = NewRect("Tag TF", plate).gameObject.AddComponent<Image>();
        tag.sprite = _pack.Sprite(ArenaMenuChrome.TagSprite(ArenaMenuChrome.TagTeamFight));
        tag.type = Image.Type.Sliced; tag.raycastTarget = false;
        tag.rectTransform.anchorMin = tag.rectTransform.anchorMax = new Vector2(0f, 0f);
        tag.rectTransform.pivot = new Vector2(0f, 1f);
        tag.rectTransform.anchoredPosition = new Vector2(34f, -2f);
        tag.rectTransform.sizeDelta = new Vector2(34f, 17f);
        var tagText = HudStyle.CreateText(tag.rectTransform, "Text", 10f, TextAlignmentOptions.Midline);
        tagText.text = "TF"; tagText.color = new Color(0.07f, 0.078f, 0.078f, 1f);
        ArenaUiBuilder.Stretch(tagText.rectTransform);

        _plates[id] = plate;
        Clickable(plate, null, () => ToggleLocation(id), null, false);
        plate.GetComponent<ArenaMenuButton>().Hovered = over =>
        {
            _hoverMap = over ? id : (_hoverMap == id ? null : _hoverMap);
            if (over) ShowDescription(id);
            RefreshPlates();
        };
    }

    /// <summary>Orange plate when ticked, grey under the pointer, bare pin and name otherwise.</summary>
    private void RefreshPlates()
    {
        foreach (var pair in _plates)
        {
            bool on = SelectedMaps.Contains(pair.Key), hover = pair.Key == _hoverMap;
            var orange = pair.Value.Find("Orange")?.GetComponent<Image>();
            if (orange != null) orange.enabled = on;
            var grey = pair.Value.Find("Grey")?.GetComponent<Image>();
            if (grey != null) grey.enabled = !on && hover;
            var label = pair.Value.Find("Label")?.GetComponent<TextMeshProUGUI>();
            if (label != null) label.color = on || hover ? new Color(0.07f, 0.078f, 0.078f, 1f) : Color.white;
        }
        Header("TF", ArenaMenuChrome.TagTeamFight,
            Maps.Where(m => SelectedMaps.Contains(m.Id)).Select(m => m.Name).ToList());
    }

    private void ToggleLocation(string id)
    {
        if (SelectedMaps.Contains(id))
        {
            if (SelectedMaps.Count > 1) SelectedMaps.Remove(id);   // at least one arena stays ticked
        }
        else SelectedMaps.Add(id);
        _lastLocation = id;
        RefreshPlates();
        ShowDescription(id);
    }

    private void ShowDescription(string id)
    {
        var map = Maps.FirstOrDefault(m => m.Id == id);
        if (map.Id == null) map = Maps[0];
        _location = map.Id;
        ArenaUiBuilder.Text(_description, "TitleBlock/NameBlock/Location Name").text = map.Name.ToUpperInvariant();
        if (_playersValue != null) { int size = TeamSizeFor(map.Id); _playersValue.text = $"{size}x{size}"; }
        var icon = ArenaUiBuilder.TryFind(_description, "TitleBlock/NameBlock/MapIco")?.GetComponent<Image>();
        if (icon != null)
        {
            icon.sprite = _pack.Sprite(SpriteRef(MapIcons, map.Name, map.Icon, 100f)) ?? icon.sprite;
            icon.rectTransform.sizeDelta = new Vector2(map.Icon[2] * 1.5f, map.Icon[3] * 1.5f);
        }
        // The ripped locale has no arena map descriptions; show EFT's own when the server provides one.
        string description = MapDescription(map.Id);
        var text = ArenaUiBuilder.Text(_description, "Content/DescriptionBlock/Descriprion");
        text.gameObject.SetActive(description != null);
        if (description != null) text.text = description;
        var banner = ArenaUiBuilder.TryFind(_description, "MapPreview/Banner")?.GetComponent<Image>();
        var art = LocationBanner(map.Id) ?? _pack.WholeTexture($"Texture2D/ArenaBackgroundPanel_{map.Banner}.png");
        if (banner != null && art != null) banner.sprite = art;
        ArenaUiBuilder.SetActive(_screen, "MapsCounter", false);          // live's multi-map counter; the strip lists them
    }

    /// <summary>NEXT: one of the ticked arenas, as live's matchmaker would pick.</summary>
    private void StartMatch()
    {
        var ticked = Maps.Where(m => SelectedMaps.Contains(m.Id)).ToList();
        if (ticked.Count == 0) return;
        var map = ticked[UnityEngine.Random.Range(0, ticked.Count)];
        Plugin.ModLogger.LogInfo($"Arena battle flow: {ticked.Count} arena(s) ticked, drew {map.Id}.");
        Start(map.Id, true);
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
        var hit = (hitPath == null ? null : ArenaUiBuilder.TryFind(node, hitPath)) ?? node;
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
