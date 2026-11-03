using ArenaMode.Combat;
using ArenaMode.Config;
using ArenaMode.Effects;
using ArenaMode.Events;
using ArenaMode.Modes;
using Comfort.Common;
using EFT;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// The in-match HUD drawn with EFT: Arena's own widgets (score/timer strip, objective line, kill log, round notices,
/// hold-Tab team table, final result) bound to the Team Deathmatch state. Replaces <see cref="ArenaHud"/>'s stock
/// panels whenever the UI pack is installed; the stock hitmarker and friendly markers are kept.
/// </summary>
internal static class ArenaLiveHud
{
    private const float KillRowSeconds = 6f;
    private const int KillRowLimit = 5;
    private const float NoticeSeconds = 2.5f;

    private static readonly Dictionary<string, string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arena/Rank"] = "RANK", ["arena/Player"] = "PLAYER", ["ARENA/MVP"] = "MVP", ["arena/MVP"] = "MVP",
        ["arena/CapturePointsCount"] = "CAPTURED", ["arena/KillShort"] = "K", ["arena/DeathShort"] = "D",
        ["arena/AssistShort"] = "A", ["arena/Round"] = "ROUND", ["arena/Team"] = "TEAM", ["arena/monitors/score"] = "SCORE",
    };

    private static GameObject _canvasGo, _tabCanvasGo;
    private static Canvas _canvas, _tabCanvas;
    private static ArenaUiPack _pack;
    private static ArenaUiBuilder _builder;
    private static ArenaMatch _match;
    private static TeamDeathmatchMode _tdm;
    private static HitmarkerElement _hitmarker;
    private static ArenaFriendlyMarkers _markers;

    // Score strip (ArenaCheckPointWidget) + objective line (ArenaRoundGoalWidget).
    private static TextMeshProUGUI _leftScore, _rightScore, _timer, _goal;

    // Kill log rows (KillLogItemPrefab).
    private static RectTransform _killLogRoot;
    private static JObjectRow _killTemplate;
    private static readonly List<KillRow> _killRows = new();

    // Round notice (ArenaRoundInfoItem).
    private static RectTransform _notice;
    private static TextMeshProUGUI _noticeText;
    private static float _noticeUntil;
    private static string _noticeOverride;

    // Team table (ArenaTeamTabWidget).
    private static RectTransform _tab, _rowTemplate, _leftList, _rightList;
    private static TextMeshProUGUI _tabLeftScore, _tabRightScore;
    private static readonly List<GameObject> _tabRows = new();
    private static int _tabVersion = -1;
    private static float _tabRefreshAt;

    // Final result (ArenaFinalResultWidget).
    private static RectTransform _result;
    private static TextMeshProUGUI _resultText;
    private static float _resultShownAt = -1f;

    private sealed class KillRow { internal GameObject Go; internal float ShownAt; }
    private sealed class JObjectRow { internal Newtonsoft.Json.Linq.JObject Node; }

    internal static bool Active => _match != null;

    internal static bool TryAttach(ArenaMatch match)
    {
        if (!Settings.HudEnabled.Value || match?.Mode is not TeamDeathmatchMode tdm) return false;
        if (!ArenaUiPack.IsAvailable) return false;
        try
        {
            Build(match, tdm);
            _match = match;
            _tdm = tdm;
            match.OnKill += OnKill;
            match.OnCallout += OnAwardCallout;
            match.OnTextCallout += OnTextCallout;
            ArenaEvents.OnLocalDamageDealt += OnDamage;
            Plugin.ModLogger.LogInfo("Arena live HUD attached from " + ArenaUiPack.Directory);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena live HUD build failed — stock HUD instead: {ex}");
            Detach();
            return false;
        }
    }

    internal static void Detach()
    {
        if (_match != null)
        {
            _match.OnKill -= OnKill;
            _match.OnCallout -= OnAwardCallout;
            _match.OnTextCallout -= OnTextCallout;
            ArenaEvents.OnLocalDamageDealt -= OnDamage;
        }
        _match = null;
        _tdm = null;
        RestoreRaidClock();
        _markers?.Dispose();
        _markers = null;
        _hitmarker = null;
        if (_canvasGo != null) Object.Destroy(_canvasGo);
        if (_tabCanvasGo != null) Object.Destroy(_tabCanvasGo);
        _canvasGo = _tabCanvasGo = null;
        _canvas = _tabCanvas = null;
        _pack?.Dispose();
        _pack = null;
        _builder = null;
        _killRows.Clear();
        _tabRows.Clear();
        _tabVersion = -1;
        _noticeOverride = null;
        _resultShownAt = -1f;
        _leftScore = _rightScore = _timer = _goal = _noticeText = _tabLeftScore = _tabRightScore = _resultText = null;
        _killLogRoot = _notice = _tab = _rowTemplate = _leftList = _rightList = _result = null;
    }

    internal static void Tick()
    {
        if (_match == null || _canvasGo == null) return;
        try
        {
            ArenaHudVisibility.Apply(_canvas);
            var snapshot = _tdm.AnnouncementSnapshot();
            TickScores(snapshot);
            TickKillLog();
            TickNotice(snapshot);
            TickResult(snapshot);
            TickTab(snapshot);
            _hitmarker?.Tick();
            _markers?.Tick(_match);
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena live HUD tick failed — HUD disabled for this raid: {ex}");
            Detach();
        }
    }

    // ------------------------------------------------------------------ build

    private static void Build(ArenaMatch match, TeamDeathmatchMode tdm)
    {
        _pack = new ArenaUiPack();
        _builder = new ArenaUiBuilder(_pack, key => English.TryGetValue(key, out var s) ? s : null);

        _canvasGo = NewCanvas("ArenaLiveHudCanvas", 130, out _canvas);
        var root = (RectTransform)_canvasGo.transform;
        _tabCanvasGo = NewCanvas("ArenaLiveTabCanvas", 140, out _tabCanvas);
        _tabCanvas.enabled = false;

        BuildTab((RectTransform)_tabCanvasGo.transform, tdm);   // first: the result card borrows its mode logo
        BuildScoreStrip(root, tdm);
        BuildGoal(root);
        BuildKillLog(root);
        BuildNotice(root);
        BuildResult(root);
        HideRaidClock();

        var combat = new GameObject("ArenaLiveCombat", typeof(RectTransform));
        var combatRect = (RectTransform)combat.transform;
        combatRect.SetParent(root, false);
        ArenaUiBuilder.Stretch(combatRect);
        _hitmarker = new HitmarkerElement();
        _hitmarker.Create(combatRect);
        _markers = new ArenaFriendlyMarkers();
        _markers.Create(root);
    }

    private static GameObject NewCanvas(string name, int order, out Canvas canvas)
    {
        var go = new GameObject(name);
        go.transform.SetParent(Plugin.Instance.transform, false);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        canvas.overrideSorting = true;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        return go;
    }

    private static void BuildScoreStrip(RectTransform root, TeamDeathmatchMode tdm)
    {
        var widget = _builder.Build(_pack.Layout("ArenaCheckPointWidget"), root);
        ArenaUiBuilder.Stretch(widget);
        var content = ArenaUiBuilder.Find(widget, "Content");
        ArenaUiBuilder.SetActive(content, "CapturedPoints", false);
        ArenaUiBuilder.SetActive(content, "LeftTeam/Progress", false);
        ArenaUiBuilder.SetActive(content, "RightTeam/Progress", false);
        Tint(ArenaUiBuilder.Find(content, "LeftTeam/Background"), ArenaTeamPalette.FriendlyUi, .55f);
        Tint(ArenaUiBuilder.Find(content, "RightTeam/Background"), ArenaTeamPalette.OpponentUi, .55f);
        _leftScore = ArenaUiBuilder.Text(content, "LeftTeam/Label");
        _rightScore = ArenaUiBuilder.Text(content, "RightTeam/Label");
        _timer = ArenaUiBuilder.Text(content, "Timer/Label");
        _leftScore.text = _rightScore.text = "0";
        _timer.text = "00:00";
    }

    private static void BuildGoal(RectTransform root)
    {
        var widget = _builder.Build(_pack.Layout("ArenaRoundGoalWidget"), root);
        ArenaUiBuilder.Stretch(widget);
        var main = ArenaUiBuilder.Find(widget, "MainContent");
        main.anchoredPosition = new Vector2(-5f, -55f); // under the score strip
        ArenaUiBuilder.SetActive(main, "ArenaRoundGoalitemTimerMain", false);
        ArenaUiBuilder.SetActive(main, "ArenaRoundGoalitemCapturePoint", false);
        _goal = ArenaUiBuilder.Text(main, "ArenaRoundGoalitemInfoMain/TaskContainer/TaskPanel/Description");
        _goal.text = "";
    }

    private static void BuildKillLog(RectTransform root)
    {
        var holder = new GameObject("ArenaLiveKillLog", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _killLogRoot = (RectTransform)holder.transform;
        _killLogRoot.SetParent(root, false);
        _killLogRoot.anchorMin = _killLogRoot.anchorMax = _killLogRoot.pivot = new Vector2(1f, 1f);
        _killLogRoot.anchoredPosition = new Vector2(-5f, -125f);
        _killLogRoot.sizeDelta = new Vector2(600f, 0f);
        var group = holder.GetComponent<VerticalLayoutGroup>();
        group.childAlignment = TextAnchor.UpperRight;
        group.spacing = 4f;
        group.childForceExpandWidth = group.childForceExpandHeight = false;
        group.childControlWidth = group.childControlHeight = true;
        _killTemplate = new JObjectRow { Node = _pack.Layout("KillLogItemPrefab") };
    }

    private static void BuildNotice(RectTransform root)
    {
        var widget = _builder.Build(_pack.Layout("ArenaRoundInfoWidget"), root);
        ArenaUiBuilder.Stretch(widget);
        var content = ArenaUiBuilder.Find(widget, "Content");
        _notice = _builder.Build(_pack.Layout("ArenaRoundInfoItem"), content);
        ArenaUiBuilder.SetActive(_notice, "CaptureMembers", false);
        ArenaUiBuilder.SetActive(_notice, "Content/Hotkey", false);
        ArenaUiBuilder.SetActive(_notice, "Content/Timer", false);
        ArenaUiBuilder.SetActive(_notice, "Content/Progress", false);
        foreach (Transform child in _notice)
            if (child.name == "BombDeactivatingItem") child.gameObject.SetActive(false);
        _noticeText = ArenaUiBuilder.Text(_notice, "Content/Info");
        _notice.gameObject.SetActive(false);
    }

    private static void BuildResult(RectTransform root)
    {
        _result = _builder.Build(_pack.Layout("ArenaFinalResultWidget"), root);
        ArenaUiBuilder.Stretch(_result);
        _resultText = ArenaUiBuilder.Text(_result, "FinalResultView/WidgetBackgroungImg/Background/WidgetText");
        var modeImage = ArenaUiBuilder.TryFind(_result, "FinalResultView/GameModeImage")?.GetComponent<Image>();
        if (modeImage != null) modeImage.sprite = ModeLogo() ?? modeImage.sprite;
        _result.gameObject.SetActive(false);
    }

    private static void BuildTab(RectTransform root, TeamDeathmatchMode tdm)
    {
        _tab = _builder.Build(_pack.Layout("ArenaTeamTabWidget"), root);
        ArenaUiBuilder.Stretch(_tab);
        foreach (Transform child in _tab)
            if (child.name.EndsWith("View") && child.name != "TeamFightView") child.gameObject.SetActive(false);
        var view = ArenaUiBuilder.Find(_tab, "TeamFightView");
        view.gameObject.SetActive(true);
        _rowTemplate = ArenaUiBuilder.Find(view, "Template/PostMatchTeamFightTemplate");

        var header = ArenaUiBuilder.Find(view, "Header");
        header.gameObject.SetActive(true);
        ArenaUiBuilder.Text(header, "MapInfo/Map/MapObj/MapName").text = MapName();
        ArenaUiBuilder.Text(header, "MapInfo/TeamSizes/TeamSizesLabel").text = tdm.TeamSize + "x" + tdm.TeamSize;

        var table = ArenaUiBuilder.Find(view, "VerticalTableView");
        var scores = ArenaUiBuilder.Find(table, "HorizontalTeamsAndScoresLayout");
        _tabLeftScore = ArenaUiBuilder.Text(scores, "LeftTeamAndScores/LeftScore");
        _tabRightScore = ArenaUiBuilder.Text(scores, "RightTeamAndScores/RightScore");
        ArenaUiBuilder.Text(scores, "LeftTeamAndScores/Content/LeftTeamDesc").text = "YOUR TEAM";
        ArenaUiBuilder.Text(scores, "RightTeamAndScores/Content/RightTeamDesc").text = "ENEMY TEAM";
        bool usec = string.Equals(tdm.FriendlyFaction, "USEC", StringComparison.OrdinalIgnoreCase);
        ArenaUiBuilder.SetActive(scores, "LeftTeamAndScores/BackImages/LeftBlueBackImage", usec);
        ArenaUiBuilder.SetActive(scores, "LeftTeamAndScores/BackImages/LeftRedBackImage", !usec);
        ArenaUiBuilder.SetActive(scores, "RightTeamAndScores/BackImages/RightBlueBackImage", !usec);
        ArenaUiBuilder.SetActive(scores, "RightTeamAndScores/BackImages/RightRedBackImage", usec);
        ArenaUiBuilder.SetActive(scores, "TeamDeathMatchCapturePointsView", false);

        var teams = ArenaUiBuilder.Find(table, "HorizontalTeamsContent");
        _leftList = ArenaUiBuilder.Find(teams, "VerticalTeamContainerLeft/PlayersList");
        _rightList = ArenaUiBuilder.Find(teams, "VerticalTeamContainerRight/PlayersList");
        foreach (var side in new[] { "VerticalTeamContainerLeft", "VerticalTeamContainerRight" })
        {
            ArenaUiBuilder.SetActive(teams, side + "/Header/TitleMVP", false);
            ArenaUiBuilder.SetActive(teams, side + "/Header/TitleCapture", false);
        }
    }

    private static Sprite ModeLogo()
    {
        // The team-tab header carries the mode logo sprite reference ("teamfight").
        var header = ArenaUiBuilder.TryFind(_tab, "TeamFightView/Header/Mode");
        return header?.GetComponent<Image>()?.sprite;
    }

    // ------------------------------------------------------------------ ticks

    private static void TickScores(Audio.ArenaAnnouncementSnapshot snapshot)
    {
        int friendly = _tdm.FriendlyTeamScore, opponent = _tdm.OpponentTeamScore;
        _leftScore.text = friendly.ToString();
        _rightScore.text = opponent.ToString();
        double seconds = snapshot.Countdown ?? snapshot.SecondsRemaining ?? _match.Elapsed;
        _timer.text = Clock(seconds);
        _goal.text = snapshot.KillsRemaining is int remaining
            ? $"Score {Math.Max(friendly, opponent) + remaining} points"
            : "Eliminate the enemy team";
    }

    private static string Clock(double seconds)
    {
        int s = (int)Math.Max(0d, Math.Round(seconds));
        return $"{s / 60:00}:{s % 60:00}";
    }

    private static void TickKillLog()
    {
        float now = Time.unscaledTime;
        for (int i = _killRows.Count - 1; i >= 0; i--)
        {
            var row = _killRows[i];
            float age = now - row.ShownAt;
            if (age > KillRowSeconds || row.Go == null)
            {
                if (row.Go != null) Object.Destroy(row.Go);
                _killRows.RemoveAt(i);
                continue;
            }
            var group = row.Go.GetComponent<CanvasGroup>();
            if (group != null) group.alpha = Mathf.Clamp01((KillRowSeconds - age) / 0.6f);
        }
    }

    private static void TickNotice(Audio.ArenaAnnouncementSnapshot snapshot)
    {
        string text = null;
        if (_noticeOverride != null && Time.unscaledTime < _noticeUntil) text = _noticeOverride;
        else if (snapshot.Preparing) text = "ASSEMBLING TEAMS";
        else if (snapshot.Countdown is int countdown) text = "MATCH STARTS IN " + countdown;
        else if (!snapshot.Active && !snapshot.Terminal && _tdm.HoldRespawn) text = "PREPARING ARENA";
        if (text == null) _noticeOverride = null;
        _notice.gameObject.SetActive(text != null);
        if (text != null) _noticeText.text = text;
    }

    private static void TickResult(Audio.ArenaAnnouncementSnapshot snapshot)
    {
        if (!snapshot.Terminal)
        {
            if (_result.gameObject.activeSelf) _result.gameObject.SetActive(false);
            return;
        }
        if (_resultShownAt < 0f)
        {
            _resultShownAt = Time.unscaledTime;
            _resultText.text = snapshot.Draw ? "DRAW" : snapshot.Lead > 0 ? "VICTORY" : "DEFEAT";
            _result.gameObject.SetActive(true);
        }
        else if (Time.unscaledTime - _resultShownAt > 6f && _result.gameObject.activeSelf)
            _result.gameObject.SetActive(false);
    }

    private static void TickTab(Audio.ArenaAnnouncementSnapshot snapshot)
    {
        // Live Arena shows the team table on its inventory screen, so Tab (inventory) is the key.
        bool show = Input.GetKey(KeyCode.Tab) || IsInventoryScreen()
                    || (snapshot.Terminal && _resultShownAt >= 0f && Time.unscaledTime - _resultShownAt > 3f);
        _tabCanvas.enabled = show;
        if (!show) return;
        var board = _match.Scoreboard;
        if (board.Version == _tabVersion && Time.unscaledTime < _tabRefreshAt) return;
        _tabVersion = board.Version;
        _tabRefreshAt = Time.unscaledTime + 1f;
        _tabLeftScore.text = _tdm.FriendlyTeamScore.ToString();
        _tabRightScore.text = _tdm.OpponentTeamScore.ToString();
        foreach (var row in _tabRows) if (row != null) Object.Destroy(row);
        _tabRows.Clear();
        FillTeam(_leftList, board.Standings(ArenaScoreboardSide.Friendly, true, 12, true));
        FillTeam(_rightList, board.Standings(ArenaScoreboardSide.Opponent, true, 12, false));
    }

    private static void FillTeam(RectTransform list, IReadOnlyList<ArenaScoreboardEntry> rows)
    {
        foreach (var entry in rows)
        {
            var go = Object.Instantiate(_rowTemplate.gameObject, list, false);
            go.name = "Row " + entry.DisplayName;
            var row = (RectTransform)go.transform;
            ArenaUiBuilder.SetActive(row, "SelectionIsMyPlayerBack", entry.IsLocal);
            ArenaUiBuilder.SetActive(row, "MVPBlock", false);
            ArenaUiBuilder.SetActive(row, "CaptureLabel", false);
            ArenaUiBuilder.SetActive(row, "PlayerInfo/RankBlock", false);
            var avatar = "PlayerInfo/PlayerIconAndNickInfo/ObservableAvatar/";
            ArenaUiBuilder.SetActive(row, avatar + "PlayerIconFace", false);
            ArenaUiBuilder.SetActive(row, avatar + "VoipBackground", false);
            ArenaUiBuilder.SetActive(row, avatar + "DiedBackground", !entry.IsActive);
            var letter = ArenaUiBuilder.TryFind(row, avatar + "FirstLetterNick");
            if (letter != null)
            {
                letter.gameObject.SetActive(true);
                var t = letter.GetComponent<TextMeshProUGUI>();
                if (t != null) t.text = string.IsNullOrEmpty(entry.DisplayName) ? "?" : entry.DisplayName.Substring(0, 1).ToUpperInvariant();
            }
            var names = "PlayerInfo/PlayerIconAndNickInfo/NameAndClassContainer/";
            ArenaUiBuilder.Text(row, names + "PlayerName/Name").text = entry.DisplayName ?? "";
            ArenaUiBuilder.Text(row, names + "PlayerClass/ClassName").text = entry.RoleLabel ?? "";
            ArenaUiBuilder.SetActive(row, names + "PlayerName/AccountType", false);
            ArenaUiBuilder.Text(row, "KDA/PlayerKills").text = entry.Kills.ToString();
            ArenaUiBuilder.Text(row, "KDA/PlayerDeaths").text = entry.Deaths.ToString();
            ArenaUiBuilder.Text(row, "KDA/PlayerAssists").text = entry.Assists.ToString();
            go.SetActive(true);
            _tabRows.Add(go);
        }
    }

    // ----------------------------------------------------------------- events

    private static void OnKill(ArenaKill kill)
    {
        if (!Settings.CombatHudEnabled.Value) return;
        if (Settings.ShowHitmarker.Value) _hitmarker?.ShowKill(kill);
        if (!Settings.ShowKillfeed.Value) return;
        if (!kill.KilledByLocal && !kill.LocalIsVictim && !Settings.KillfeedOtherKills.Value) return;
        PushKillRow(kill.KillerName, kill.KillerIsFriendly ?? kill.KilledByLocal, kill.WeaponName,
            KillfeedIcon.Select(kill), kill.VictimName, kill.VictimIsFriendly ?? kill.LocalIsVictim, kill.Headshot);
    }

    internal static void ShowUncreditedDeath(ArenaMatch match, ArenaUncreditedDeath death)
    {
        if (!ReferenceEquals(_match, match) || !Settings.CombatHudEnabled.Value || !Settings.ShowKillfeed.Value
            || (!death.LocalIsVictim && !Settings.KillfeedOtherKills.Value)) return;
        PushKillRow(null, false, null, KillfeedIcon.SelectDeath(death), death.VictimName, death.LocalIsVictim, false);
    }

    private static void PushKillRow(string killer, bool killerFriendly, string weapon, ArenaUiIcon method,
        string victim, bool victimFriendly, bool headshot)
    {
        if (_killLogRoot == null || _killTemplate == null) return;
        try
        {
            var row = _builder.Build(_killTemplate.Node, _killLogRoot);
            var group = row.GetComponent<CanvasGroup>() ?? row.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            var killerText = ArenaUiBuilder.Text(row, "Killer");
            killerText.gameObject.SetActive(!string.IsNullOrEmpty(killer));
            killerText.text = killer ?? "";
            killerText.color = killerFriendly ? ArenaTeamPalette.FriendlyUi : ArenaTeamPalette.OpponentUi;
            var victimText = ArenaUiBuilder.Text(row, "DeadPlayer");
            victimText.text = victim ?? "";
            victimText.color = victimFriendly ? ArenaTeamPalette.FriendlyUi : ArenaTeamPalette.OpponentUi;
            ArenaUiBuilder.SetActive(row, "AdditionalDeadPlayerLable", string.IsNullOrEmpty(killer));
            ArenaUiBuilder.SetActive(row, "ShortInfo", false);

            var icon = ArenaUiBuilder.Find(row, "WeaponIcon").GetComponent<Image>();
            var silhouette = _pack.WholeTexture("Texture2D/" + (weapon ?? "") + ".png");
            if (silhouette != null)
            {
                icon.sprite = silhouette;
                var element = icon.GetComponent<LayoutElement>();
                if (element != null)
                    element.preferredWidth = 18f * silhouette.rect.width / Mathf.Max(1f, silhouette.rect.height);
            }
            else icon.gameObject.SetActive(false);
            for (int i = 1; i <= 5; i++) ArenaUiBuilder.SetActive(row, "AdditionalIcon_" + i, false);
            if (headshot) ArenaUiBuilder.SetActive(row, "AdditionalIcon_1", true);

            _killRows.Add(new KillRow { Go = row.gameObject, ShownAt = Time.unscaledTime });
            while (_killRows.Count > KillRowLimit)
            {
                if (_killRows[0].Go != null) Object.Destroy(_killRows[0].Go);
                _killRows.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena live kill log row failed: " + ex.Message);
        }
    }

    private static void OnAwardCallout(string label)
    {
        if (!Audio.AnnouncerService.IsEnabledForLiveMatch()) return;
        OnTextCallout(label);
    }

    private static void OnTextCallout(string label)
    {
        if (string.IsNullOrEmpty(label)) return;
        _noticeOverride = label.ToUpperInvariant();
        _noticeUntil = Time.unscaledTime + NoticeSeconds;
    }

    private static void OnDamage(ArenaDamage damage)
    {
        if (Settings.CombatHudEnabled.Value && Settings.ShowHitmarker.Value) _hitmarker?.Show(damage);
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsInventoryScreen()
    {
        var screens = EFT.UI.Screens.EftScreenManager.Instance;
        return screens != null && screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.Inventory);
    }

    private static readonly List<GameObject> _hiddenClocks = new();

    /// <summary>Arena has a match clock, not a raid clock: hide EFT's location time panel for the match.</summary>
    private static void HideRaidClock()
    {
        var type = HarmonyLib.AccessTools.TypeByName("LocationTimeUIPanel") ?? HarmonyLib.AccessTools.TypeByName("EFT.UI.LocationTimeUIPanel");
        if (type == null) return;
        foreach (var panel in Object.FindObjectsOfType(type))
            if (panel is Component c && c.gameObject.activeSelf)
            {
                c.gameObject.SetActive(false);
                _hiddenClocks.Add(c.gameObject);
            }
    }

    private static void RestoreRaidClock()
    {
        foreach (var go in _hiddenClocks) if (go != null) go.SetActive(true);
        _hiddenClocks.Clear();
    }

    private static void Tint(RectTransform node, Color color, float alpha)
    {
        var image = node.GetComponent<Image>();
        if (image != null) image.color = new Color(color.r, color.g, color.b, alpha);
    }

    private static string MapName()
    {
        string id = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance?.LocationId : null;
        if (string.IsNullOrEmpty(id)) return "";
        return id.Replace("arena_", "").Replace('_', ' ').ToUpperInvariant();
    }
}
