using ArenaMode.Modes;
using ArenaMode.Session;
using EFT;
using EFT.UI;
using EFT.UI.SessionEnd;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's post-match: the match result is captured when the arena match ends, EFT's raid-end screens
/// (kill list, statistics, experience, health) are skipped by pressing their Main Menu button the moment the first
/// one shows, and Arena's result screen (ArenaResultScreen: header, score line, both teams' tables, mode logo)
/// is drawn over the main menu until Continue.
/// </summary>
internal static class ArenaPostMatch
{
    internal sealed class Row
    {
        internal string Name, Role;
        internal int Kills, Deaths, Assists;
        internal bool Local, Friendly;
    }

    internal sealed class Result
    {
        internal bool Won, Draw;
        internal int FriendlyScore, OpponentScore;
        internal string FriendlyFaction, OpponentFaction, Map;
        internal readonly List<Row> Rows = new();
    }

    internal static Result Last { get; private set; }
    internal static bool Pending { get; private set; }

    internal static void Enable() => new SkipRaidEndPatch().Enable();

    internal static void Capture(ArenaMatch match, TeamDeathmatchMode tdm, bool won, bool draw)
    {
        if (match == null || tdm == null) return;
        var result = new Result
        {
            Won = won, Draw = draw,
            FriendlyScore = tdm.FriendlyTeamScore, OpponentScore = tdm.OpponentTeamScore,
            FriendlyFaction = tdm.FriendlyFaction, OpponentFaction = tdm.OpponentFaction,
            Map = Comfort.Common.Singleton<GameWorld>.Instantiated ? Comfort.Common.Singleton<GameWorld>.Instance?.LocationId : null,
        };
        foreach (var side in new[] { ArenaScoreboardSide.Friendly, ArenaScoreboardSide.Opponent })
            foreach (var entry in match.Scoreboard.Standings(side, true, 12, side == ArenaScoreboardSide.Friendly))
                result.Rows.Add(new Row
                {
                    Name = entry.DisplayName, Role = entry.RoleLabel, Kills = entry.Kills, Deaths = entry.Deaths,
                    Assists = entry.Assists, Local = entry.IsLocal, Friendly = side == ArenaScoreboardSide.Friendly,
                });
        Last = result;
        Pending = true;
    }

    internal static void Consume() => Pending = false;

    /// <summary>EFT's first raid-end screen: its Main Menu button bypasses the whole sequence.</summary>
    private sealed class SkipRaidEndPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => typeof(SessionResultExitStatus).GetMethod(nameof(SessionResultExitStatus.Show), new[]
            {
                typeof(Profile), typeof(PlayerVisualRepresentation), typeof(ESideType), typeof(ExitStatus),
                typeof(TimeSpan), typeof(IEftSession), typeof(bool),
            });

        [PatchPostfix]
        private static void Postfix(DefaultUIButton ____mainMenuButton)
        {
            if (!Pending || !ArenaUiPack.IsAvailable || !ArenaSession.LastRaidWasArena) return;
            try
            {
                ____mainMenuButton?.OnClick?.Invoke();
                Plugin.ModLogger.LogInfo("Arena post-match: raid-end screens skipped.");
            }
            catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena post-match: raid-end skip failed: " + ex.Message); }
        }
    }
}

/// <summary>Arena's result screen over the main menu.</summary>
internal sealed class ArenaResultOverlay : MonoBehaviour
{
    private static ArenaResultOverlay _instance;
    private ArenaUiPack _pack;

    internal static bool Visible => _instance != null;

    internal static void Show()
    {
        if (_instance != null || ArenaPostMatch.Last == null || !ArenaUiPack.IsAvailable) return;
        ArenaPostMatch.Consume();
        var go = new GameObject("ArenaResultOverlay");
        go.transform.SetParent(Plugin.Instance.transform, false);
        _instance = go.AddComponent<ArenaResultOverlay>();
        try { _instance.Build(ArenaPostMatch.Last); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena result screen failed: " + ex);
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
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)) Hide();
    }

    private void Build(ArenaPostMatch.Result result)
    {
        _pack = new ArenaUiPack();
        var builder = new ArenaUiBuilder(_pack, key => key switch
        {
            "arena/Rank" => "RANK", "arena/Player" => "PLAYER", "ARENA/MVP" => "MVP", "arena/MVP" => "MVP",
            "arena/KillShort" => "K", "arena/DeathShort" => "D", "arena/AssistShort" => "A", "arena/Round" => "ROUND", _ => null,
        });
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

        var fill = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        var fillRect = (RectTransform)fill.transform;
        fillRect.SetParent(root, false);
        ArenaUiBuilder.Stretch(fillRect);
        fill.GetComponent<Image>().color = new Color(0.02f, 0.025f, 0.03f, 0.96f);

        var screen = builder.Build(_pack.Layout("ArenaResultScreen"), root);
        ArenaUiBuilder.Stretch(screen);
        var animator = ArenaUiBuilder.Find(screen, "Animator");
        string verdict = result.Draw ? "DRAW" : result.Won ? "VICTORY" : "DEFEAT";
        ArenaUiBuilder.Text(animator, "Header/Header").text = verdict;
        ArenaUiBuilder.Text(animator, "Header/SubHeader").text = "TEAM FIGHT" + (string.IsNullOrEmpty(result.Map) ? "" :
            " · " + result.Map.Replace("arena_", "").Replace('_', ' ').ToUpperInvariant());

        // Score line in the scores panel (live builds its line content at runtime).
        var scores = ArenaUiBuilder.Find(animator, "ScoresPanelContainer");
        var line = HudStyle.CreateText(scores, "Score", 64f, TextAlignmentOptions.Midline);
        line.text = $"<color={Effects.ArenaTeamPalette.FriendlyHex}>{result.FriendlyFaction} {result.FriendlyScore}</color>" +
                    $"   :   <color={Effects.ArenaTeamPalette.OpponentHex}>{result.OpponentScore} {result.OpponentFaction}</color>";
        ArenaUiBuilder.Stretch(line.rectTransform);

        var finalView = ArenaUiBuilder.Find(animator, "FinalResultView");
        var widgetText = ArenaUiBuilder.TryFind(finalView, "WidgetBackgroungImg/Background/WidgetText")?.GetComponent<TextMeshProUGUI>()
                         ?? finalView.GetComponentInChildren<TextMeshProUGUI>(true);
        if (widgetText != null) widgetText.text = verdict;
        var modeImage = ArenaUiBuilder.TryFind(finalView, "GameModeImage")?.GetComponent<Image>();
        var tabHeader = _pack.Layout("ArenaTeamTabWidget");
        var modeRef = FindSpriteRef(tabHeader, "Mode");
        if (modeImage != null && modeRef != null) modeImage.sprite = _pack.Sprite(modeRef) ?? modeImage.sprite;

        var table = ArenaUiBuilder.Find(animator, "TeamsTable");
        var template = ArenaUiBuilder.Find(table, "Template/PlayerStatsTemplate");
        FillTeam(ArenaUiBuilder.Find(table, "TeamContainerLeft/PlayersContainer/PlayersList"), template, result, true);
        FillTeam(ArenaUiBuilder.Find(table, "TeamContainerRight/PlayersContainer/PlayersList"), template, result, false);
        foreach (var side in new[] { "TeamContainerLeft", "TeamContainerRight" })
        {
            ArenaUiBuilder.SetActive(table, side + "/Header/TitleRankBlock", false);
            ArenaUiBuilder.SetActive(table, side + "/Header/TitleMVPBlock", false);
            ArenaUiBuilder.SetActive(table, side + "/Header/TitleRoundsBlock", false);
            var colour = ArenaUiBuilder.TryFind(table, side + "/PlayersContainer/TeamColorImage")?.GetComponent<Image>();
            if (colour != null)
            {
                var c = side == "TeamContainerLeft" ? Effects.ArenaTeamPalette.FriendlyUi : Effects.ArenaTeamPalette.OpponentUi;
                colour.color = new Color(c.r, c.g, c.b, 0.18f);
            }
        }

        var button = new GameObject("Continue", typeof(RectTransform), typeof(Image));
        var buttonRect = (RectTransform)button.transform;
        buttonRect.SetParent(root, false);
        buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0.5f, 0f);
        buttonRect.pivot = new Vector2(0.5f, 0f);
        buttonRect.anchoredPosition = new Vector2(0f, 60f);
        buttonRect.sizeDelta = new Vector2(320f, 56f);
        var buttonImage = button.GetComponent<Image>();
        buttonImage.color = new Color(0.52f, 0.60f, 0.67f, 0.14f);
        ArenaUiTheme.Rule(buttonRect, "Edge", ArenaUiTheme.Edge, inset: 0f, thickness: 1f);
        var label = HudStyle.CreateText(buttonRect, "Label", 26f, TextAlignmentOptions.Midline);
        label.text = "CONTINUE";
        ArenaUiBuilder.Stretch(label.rectTransform);
        button.AddComponent<ArenaMenuButton>().Init(buttonRect, Hide);

        ArenaLiveSounds.Play(result.Draw ? "result_music_start.wav" : result.Won ? "result_music_end_winner.wav" : "result_music_end_looser.wav", 0.9f);
    }

    private static void FillTeam(RectTransform list, RectTransform template, ArenaPostMatch.Result result, bool friendly)
    {
        foreach (var row in result.Rows)
        {
            if (row.Friendly != friendly) continue;
            var go = Object.Instantiate(template.gameObject, list, false);
            go.name = "Row " + row.Name;
            var rect = (RectTransform)go.transform;
            ArenaUiBuilder.SetActive(rect, "RankBlock", false);
            ArenaUiBuilder.SetActive(rect, "MVPBlock", false);
            ArenaUiBuilder.SetActive(rect, "RoundBlock", false);
            ArenaUiBuilder.SetActive(rect, "PlayerBlock/ObservableAvatar/PlayerIconFace", false);
            var letter = ArenaUiBuilder.TryFind(rect, "PlayerBlock/ObservableAvatar/FirstLetterNick");
            if (letter != null)
            {
                letter.gameObject.SetActive(true);
                letter.GetComponent<TextMeshProUGUI>().text = string.IsNullOrEmpty(row.Name) ? "?" : row.Name.Substring(0, 1).ToUpperInvariant();
            }
            var name = ArenaUiBuilder.Text(rect, "PlayerBlock/NameAndClassContainer/AccountName/Name");
            name.text = row.Name ?? "";
            if (row.Local) name.color = new Color(0.79f, 0.54f, 0f, 1f);
            ArenaUiBuilder.SetActive(rect, "PlayerBlock/NameAndClassContainer/AccountName/AccountType", false);
            ArenaUiBuilder.Text(rect, "PlayerBlock/NameAndClassContainer/Class/ClassName").text = row.Role ?? "";
            ArenaUiBuilder.Text(rect, "KDA/PlayerKills").text = row.Kills.ToString();
            ArenaUiBuilder.Text(rect, "KDA/PlayerDeaths").text = row.Deaths.ToString();
            ArenaUiBuilder.Text(rect, "KDA/PlayerAssists").text = row.Assists.ToString();
            go.SetActive(true);
        }
    }

    private static Newtonsoft.Json.Linq.JObject FindSpriteRef(Newtonsoft.Json.Linq.JObject node, string name)
    {
        if (node.Value<string>("name") == name && node["components"] is Newtonsoft.Json.Linq.JArray comps)
            foreach (var c in comps)
                if (c is Newtonsoft.Json.Linq.JObject co && co["fields"]?["m_Sprite"] is Newtonsoft.Json.Linq.JObject sprite && sprite["sprite"] != null)
                    return sprite;
        if (node["children"] is Newtonsoft.Json.Linq.JArray children)
            foreach (var child in children)
                if (child is Newtonsoft.Json.Linq.JObject c && FindSpriteRef(c, name) is { } found) return found;
        return null;
    }
}
