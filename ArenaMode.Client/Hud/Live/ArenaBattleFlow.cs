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
/// (ArenaMapSelectionScreen). NEXT on the map screen remembers the location and runs EFT's stock play path;
/// ArenaSideSkip then selects that location on EFT's map screen and presses its Next, so the stock matchmaker
/// screens are never seen. Only UNRANKED / TeamFight (the fork's team deathmatch) are playable.
/// </summary>
internal sealed class ArenaBattleFlow : MonoBehaviour
{
    private enum Step { GameType, GameMode, Map }

    // Live strings (TestBackendLocaleEn.json); the builder localizes LocalizedText keys through this table.
    private static readonly Dictionary<string, string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arena/matching/SelectGametype"] = "SELECT GAME TYPE", ["Unranked"] = "UNRANKED", ["Ranked"] = "RANKED",
        ["Arena/UI/CustomMode"] = "Custom", ["Arena/UI/TutorialMode"] = "Tutorial",
        ["arena/matching/SelectRankedGamemode"] = "SELECT UNRANKED GAME MODE", ["arena/selection/gameMode"] = "game mode",
        ["Description"] = "Description", ["arena/matching/AvailableArenaMaps"] = "AVAILABLE LOCATIONS", ["SETTINGS"] = "SETTINGS",
        ["arena/matching/SelectArenaMap"] = "SELECT ARENA", ["arena/matching/AvailableModes"] = "Available game modes",
    };
    private const string Unavailable = "Unavailable";                       // arena/matching/UnavailableReason
    private const string TeamFightDescription =
        "A team battle in the 5v5 format. The objective is to eliminate the opposing team sooner than they kill you.";

    private const string LogoMedium = "Texture2D/Matchmaker_GameMods_Logo_Medium.png";
    private const string LogoBlocked = "Texture2D/Matchmaker_GameMods_Logo_Blocked_Medium.png";
    private const string LogoSmall = "Texture2D/Matchmaker_GameMods_Logo_Small.png";
    private const string MapIcons = "Texture2D/Matchmaker_MapButton_MapIcons-All.png";

    // Live's unranked modes in live order; logo rects from the rip's sprite assets (unavailable ones use the
    // blocked atlas where it has the mode; the medium atlas has no blastgang, the small one does).
    private static readonly (string Name, bool Available, string Atlas, float[] Logo)[] Modes =
    {
        ("TeamFight", true, LogoMedium, new[] { 284.0934f, 27.07612f, 251.89714f, 117.847755f }),
        ("CheckPoint", false, LogoBlocked, new[] { 1505.4973f, 31.07612f, 154.88097f, 56.847763f }),
        ("BlastGang", false, LogoBlocked, new[] { 1320.3489f, 25.012913f, 153.87256f, 63.967297f }),
        ("ShootOut", false, LogoBlocked, new[] { 1123.1176f, 17.07612f, 174.89713f, 82.84776f }),
        ("LastHero", false, LogoMedium, new[] { 553.2094f, 27.07612f, 257.84778f, 79.84776f }),
    };

    // Map pins: position over Main_Map_Screen (normalized inside the sprite), and the MapIcons-All rects of the
    // pin column whose symbol fits the map (top row = hover glow, middle = idle, bottom = selected).
    // ponytail: pin symbols and positions are picked by eye (the rip has no location -> icon table); swap the
    // rects if live data turns up.
    private static readonly (string Id, string Name, string Banner, Vector2 At, float[] Hover, float[] Idle, float[] Selected)[] Maps =
    {
        ("arena_airpit", "Air Pit", "AirPit", new Vector2(0.886f, 0.202f),
            new[] { 246.07613f, 161.07613f, 67.84776f, 77.84776f }, new[] { 268.0761f, 103.07612f, 23.872564f, 33.872566f },
            new[] { 245.07613f, 2.0761204f, 69.84776f, 76.84776f }),
        ("arena_bay5", "Bay 5", "Bay5", new Vector2(0.251f, 0.140f),
            new[] { 86.07612f, 161.07613f, 67.84776f, 77.84776f }, new[] { 108.07612f, 103.07612f, 23.872564f, 33.872566f },
            new[] { 85.07612f, 2.0761204f, 69.84776f, 76.84776f }),
        ("arena_bowl", "Bowl", "Bowl", new Vector2(0.900f, 0.663f),
            new[] { 166.07613f, 161.07613f, 67.84776f, 77.84776f }, new[] { 188.07613f, 103.07612f, 23.872564f, 33.872566f },
            new[] { 165.07613f, 2.0761204f, 69.84776f, 76.84776f }),
        ("arena_equator", "Equator", "Equator", new Vector2(0.525f, 0.453f),
            new[] { 965.0761f, 161.07613f, 69.84776f, 77.84776f }, new[] { 987.02673f, 102.07612f, 25.946497f, 34.872566f },
            new[] { 965.0761f, 2.0761204f, 69.84776f, 76.84776f }),
    };

    private static ArenaBattleFlow _instance;
    private static string _lastLocation = Maps[0].Id;

    /// <summary>Location chosen on SELECT ARENA, consumed by ArenaSideSkip on EFT's location screen.</summary>
    internal static string PendingLocationId;

    private ArenaUiPack _pack;
    private ArenaUiBuilder _builder;
    private RectTransform _root, _screen, _pins, _description;
    private Step _step;
    private string _location;

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

        // Same backdrop as the presets screen: it also swallows clicks meant for the menu underneath.
        var fill = NewRect("Backdrop", _root);
        ArenaUiBuilder.Stretch(fill);
        fill.gameObject.AddComponent<Image>().color = new Color(0.03f, 0.035f, 0.04f, 1f);
        var backdrop = _builder.Build(_pack.Layout("ArenaBattleBackgroundPanel"), _root);
        ArenaUiBuilder.Stretch(backdrop);
        backdrop.GetComponentInChildren<Image>().color = new Color(1f, 1f, 1f, 0.08f);

        Open(Step.GameType);
    }

    private void Open(Step step)
    {
        if (_screen != null) Destroy(_screen.gameObject);
        _screen = _pins = _description = null;
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
        return _screen;
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

        var list = ArenaUiBuilder.Find(screen, "Modes List/Viewport/DepensOfTiers/DependsContainer");
        var template = Find(layout, "GameModeViewTemplate");
        foreach (var (name, available, atlas, logo) in Modes)
        {
            var card = _builder.Build(template, list);
            card.name = name;
            card.gameObject.SetActive(true);
            var image = ArenaUiBuilder.TryFind(card, "ModeIcon/ModeImage")?.GetComponent<Image>();
            if (image != null) image.sprite = _pack.Sprite(SpriteRef(atlas, name, logo, 78f)) ?? image.sprite;
            // TeamFight, the only playable mode, is the selected one.
            ArenaUiBuilder.SetActive(card, "ModeIcon/ModeSelectedBack", available);
            ArenaUiBuilder.SetActive(card, "ModeIcon/ModeSelected", available);
            ArenaUiBuilder.SetActive(card, "ModeIcon/UnavailableBackground", !available);
            if (!available)
            {
                var reason = ArenaUiBuilder.TryFind(card, "ModeIcon/UnavailableBackground/UnavailableReason")?.GetComponent<TextMeshProUGUI>();
                if (reason != null) reason.text = Unavailable;
                continue;
            }
            Clickable(card, "ClickHandler", null);                          // already selected: the click only sounds
        }

        var info = ArenaUiBuilder.Find(screen, "Mode Description");
        var group = info.GetComponent<CanvasGroup>();
        if (group != null) group.alpha = 1f;                                // faded in by live's panel
        ArenaUiBuilder.SetActive(info, "Content/InvalidModeDescriptionBlock", false);
        ArenaUiBuilder.Text(info, "Content/DescriptionBlock/ModeDecriprion").text = TeamFightDescription;
        var maps = ArenaUiBuilder.Find(info, "Content/MapsBlock/MapsList");
        foreach (var map in Maps)
        {
            var row = _builder.Build(Find(layout, "MapTemplate"), maps);
            row.gameObject.SetActive(true);
            var icon = ArenaUiBuilder.TryFind(row, "MapIcoBlock/MapIco")?.GetComponent<Image>();
            if (icon != null) icon.sprite = _pack.Sprite(SpriteRef(MapIcons, map.Name, map.Idle, 100f)) ?? icon.sprite;
            ArenaUiBuilder.Text(row, "MapName").text = map.Name;
        }
        // Live's settings block: players per match. Its "90 MIN" playtime is not this fork's rule, so it goes.
        var settings = ArenaUiBuilder.Find(info, "Content/SettingsBlock");
        ArenaUiBuilder.SetActive(settings, "IconsPanel/Playtime", false);
        var players = _builder.Build(Find(layout, "PlayersTemplate"), ArenaUiBuilder.Find(settings, "IconsPanel/Players/PlayersList"));
        players.gameObject.SetActive(true);
        players.GetComponent<TextMeshProUGUI>().text = "5x5";

        FlowButton(screen, "NextButton", () => Open(Step.Map));
        FlowButton(screen, "BackButton", () => Open(Step.GameType));
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
        foreach (var map in Maps) BuildPin(map.Id, map.Name, map.At, map.Hover, map.Idle, map.Selected);

        _description = ArenaUiBuilder.Find(screen, "Description");
        var group = _description.GetComponent<CanvasGroup>();
        if (group != null) group.alpha = 1f;
        // Every map here runs TeamFight only.
        var modeList = ArenaUiBuilder.Find(_description, "Content/ModesBlock/ModeList");
        var mode = _builder.Build(Find(layout, "ModeTemplate"), modeList);
        mode.gameObject.SetActive(true);
        ArenaUiBuilder.Text(mode, "ModeTemplate/Title").text = "TeamFight";
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

    private void BuildPin(string id, string name, Vector2 at, float[] hover, float[] idle, float[] selected)
    {
        var pin = NewRect(id, _pins);
        pin.anchorMin = pin.anchorMax = at;
        pin.sizeDelta = new Vector2(80f, 80f);
        var hit = pin.gameObject.AddComponent<Image>();
        hit.color = Color.clear;
        hit.raycastTarget = true;
        // ArenaMenuButton fades a child named "Background" in on hover: the glowing pin.
        PinImage(pin, "Idle", idle, name, Color.white);
        PinImage(pin, "Background", hover, name, Color.clear);
        PinImage(pin, "Selected", selected, name, Color.white);
        var label = HudStyle.CreateText(pin, "Label", 16f, TextAlignmentOptions.Midline);
        label.text = name.ToUpperInvariant();
        label.color = ArenaUiTheme.Muted;
        label.rectTransform.anchoredPosition = new Vector2(0f, -36f);
        label.rectTransform.sizeDelta = new Vector2(160f, 22f);
        pin.gameObject.AddComponent<ArenaMenuButton>().Init(pin, () => SelectLocation(id));
    }

    private void PinImage(RectTransform pin, string node, float[] rect, string name, Color color)
    {
        var image = NewRect(node, pin).gameObject.AddComponent<Image>();
        image.sprite = _pack.Sprite(SpriteRef(MapIcons, name, rect, 100f));
        image.color = color;
        image.raycastTarget = false;
        image.rectTransform.sizeDelta = new Vector2(rect[2], rect[3]);
    }

    private void SelectLocation(string id)
    {
        var map = Maps.FirstOrDefault(m => m.Id == id);
        if (map.Id == null) map = Maps[0];
        _location = _lastLocation = map.Id;
        foreach (Transform pin in _pins)
        {
            bool on = pin.name == map.Id;
            ArenaUiBuilder.SetActive((RectTransform)pin, "Selected", on);
            ArenaUiBuilder.SetActive((RectTransform)pin, "Idle", !on);
        }

        ArenaUiBuilder.Text(_description, "TitleBlock/NameBlock/Location Name").text = map.Name.ToUpperInvariant();
        var icon = ArenaUiBuilder.TryFind(_description, "TitleBlock/NameBlock/MapIco")?.GetComponent<Image>();
        if (icon != null) icon.sprite = _pack.Sprite(SpriteRef(MapIcons, map.Name, map.Idle, 100f)) ?? icon.sprite;
        // The ripped locale has no arena map descriptions; show EFT's own when the server provides one.
        string description = MapDescription(map.Id);
        var text = ArenaUiBuilder.Text(_description, "Content/DescriptionBlock/Descriprion");
        text.gameObject.SetActive(description != null);
        if (description != null) text.text = description;
        var banner = ArenaUiBuilder.TryFind(_description, "MapPreview/Banner")?.GetComponent<Image>();
        var art = LocationBanner(map.Id) ?? _pack.WholeTexture($"Texture2D/ArenaBackgroundPanel_{map.Banner}.png");
        if (banner != null && art != null) banner.sprite = art;
        ArenaUiBuilder.Text(_screen, "MapsCounter/Counter/Counter").text = "01";
    }

    private void StartMatch()
    {
        string id = _location;
        if (id == null) return;
        Settings.TrySelectGameMode(ArenaModeCatalog.TeamDeathmatchId);   // TeamFight is the fork's team deathmatch
        Hide();
        PendingLocationId = id;
        Plugin.ModLogger.LogInfo($"Arena battle flow: TeamFight on {id}, starting matchmaking.");
        ArenaMainMenuOverlay.StartStockMatchmaking();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Live's NEXT / BACK buttons; their transparent "___NEXT___" sizing label stays invisible on hover.</summary>
    private static void FlowButton(RectTransform screen, string path, Action action)
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
    }

    private static void Clickable(RectTransform node, string hitPath, Action action)
    {
        var hit = ArenaUiBuilder.TryFind(node, hitPath) ?? node;
        var image = hit.GetComponent<Image>();
        if (image == null)
        {
            image = hit.gameObject.AddComponent<Image>();
            image.color = Color.clear;
        }
        image.raycastTarget = true;
        node.gameObject.AddComponent<ArenaMenuButton>().Init(node, action);
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
}
