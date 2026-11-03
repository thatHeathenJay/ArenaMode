using EFT;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// The chrome every live pre-match screen shares: the breadcrumb strip on top (MatchInfoPanel: TYPE / RULES /
/// LOCATIONS) and the stat bar at the bottom (ProfileInfoPanel: menu buttons, party slots, career stats, BP / GP /
/// roubles). Both are the ripped layouts bound to this profile.
/// </summary>
internal static class ArenaMenuChrome
{
    internal const string TagAtlas = "Texture2D/AllScreens_ModeStatistic_backgrounds.png";

    // Mode tag tiles in AllScreens_ModeStatistic_backgrounds (x of each 34x24 tile, measured from the rip).
    // ponytail: colour per mode picked by eye from live; swap the x when a live table turns up.
    internal const float TagTeamFight = 165f, TagCheckPoint = 85f, TagLastHero = 325f, TagShootOut = 45f,
        TagBlastGang = 205f, TagBloodlust = 245f;

    private static readonly Dictionary<string, string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arena/matching/Type"] = "TYPE", ["arena/matching/Rules"] = "RULES", ["arena/matching/Maps"] = "LOCATIONS",
        ["arena/info/Kills"] = "KILLS", ["arena/info/Headshots"] = "HEADSHOTS", ["arena/info/KD Ratio"] = "K/D",
        ["arena/info/Matches"] = "MATCHES", ["arena/info/Win Matches"] = "MATCHES WON", ["arena/info/Win Rate"] = "WIN RATE",
        ["arena/info/MVP"] = "MATCH MVP", ["arena/info/BattlePoints"] = "BP", ["arena/info/GP"] = "GP",
        ["arena/info/Rubles"] = "ROUBLES", ["arena/info/Total GLP"] = "TOTAL ARP", ["arena/info/GLP Left"] = "ARP LEFT",
        ["arena/info/Rank"] = "RANK", ["arena/info/Main GLP Scores"] = "ARP SCORE RATIO", ["FASTGAME"] = "QUICK GAME",
    };

    internal static string Localize(string key) => English.TryGetValue(key, out var s) ? s : null;

    internal static JObject TagSprite(float x) => new()
    {
        ["sprite"] = TagAtlas, ["name"] = "tag", ["rect"] = new JArray(x, 3f, 34f, 24f),
        ["border"] = new JArray(4f, 4f, 4f, 4f), ["pivot"] = new JArray(0.5, 0.5), ["ppu"] = 100f,
    };

    /// <summary>The breadcrumb strip. Null arguments hide that group (nothing chosen yet).</summary>
    internal static RectTransform BuildHeader(ArenaUiPack pack, Transform parent, string type, string modeTag,
        float? tagX, string location)
    {
        var builder = new ArenaUiBuilder(pack, Localize);
        var layout = pack.Layout("MatchInfoPanel");
        var header = builder.Build(layout, parent);
        header.anchorMin = new Vector2(0f, 1f);
        header.anchorMax = Vector2.one;
        header.pivot = new Vector2(0.5f, 1f);
        header.anchoredPosition = Vector2.zero;
        header.sizeDelta = new Vector2(0f, 50f);

        var content = ArenaUiBuilder.Find(header, "Content");
        var ranking = ArenaUiBuilder.Find(content, "RankingMode");
        ranking.gameObject.SetActive(type != null);
        if (type != null) ArenaUiBuilder.Text(ranking, "Group/Labels/ModeChosen").text = type;

        var rules = ArenaUiBuilder.Find(content, "Presets_Modes");
        rules.gameObject.SetActive(modeTag != null);
        ArenaUiBuilder.SetActive(rules, "Presets", false);
        var modes = ArenaUiBuilder.Find(rules, "GameModes/Group/ModesList");
        ArenaUiBuilder.SetActive(modes, "FastGameMode", false);
        if (modeTag != null)
        {
            var tag = builder.Build(Find(layout, "ModeTemplate"), modes);
            tag.gameObject.SetActive(true);
            ArenaUiBuilder.Text(tag, "Title").text = modeTag;
            var back = ArenaUiBuilder.TryFind(tag, "Background")?.GetComponent<Image>();
            if (back != null && tagX is float x) back.sprite = pack.Sprite(TagSprite(x)) ?? back.sprite;
        }

        var maps = ArenaUiBuilder.Find(content, "Maps");
        maps.gameObject.SetActive(location != null);
        if (location != null)
        {
            var plate = builder.Build(Find(layout, "MapTemplate"), ArenaUiBuilder.Find(maps, "Group/MapsList"));
            plate.gameObject.SetActive(true);
            ArenaUiBuilder.Text(plate, "Title").text = location.ToUpperInvariant();
        }
        // The glows mark the step being chosen: the last filled group.
        ArenaUiBuilder.SetActive(ranking, "RankedGlow", modeTag == null);
        ArenaUiBuilder.SetActive(rules, "ModeGlow", modeTag != null && location == null);
        ArenaUiBuilder.SetActive(maps, "MapGlow", location != null);
        return header;
    }

    /// <summary>The bottom bar: MAIN MENU / SETTINGS / PRESETS / party, career stats, BP / GP / roubles.</summary>
    internal static RectTransform BuildFooter(ArenaUiPack pack, Transform parent, Profile profile,
        Action mainMenu, Action settings, Action presets)
    {
        var builder = new ArenaUiBuilder(pack, Localize);
        var layout = pack.Layout("ProfileInfoPanel");
        var root = builder.Build(layout, parent);
        ArenaUiBuilder.Stretch(root);
        ArenaUiBuilder.SetActive(root, "Image", false);
        var bar = ArenaUiBuilder.Find(root, "ProfileInfoPanel");

        var icons = ArenaUiBuilder.Find(bar, "IconsPanel");
        IconButton(icons, "MainMenu", mainMenu);
        IconButton(icons, "Settings", settings);
        StateButton(icons, "Presets", presets);
        StateButton(icons, "Squad", null);
        BuildParty(builder, layout, ArenaUiBuilder.Find(icons, "ArenaGroupPanel/GroupContainer"), profile);

        var account = ArenaUiBuilder.Find(bar, "AccountInfoPanel");
        foreach (var hidden in new[] { "Rank", "TotalARP", "ARPLeft", "GLP Scores" })
            ArenaUiBuilder.SetActive(account, hidden, false);
        // ponytail: career stats are zero until progression lands; live shows the profile's numbers here.
        foreach (var stat in new[] { "Kills", "Headshots", "KDRatioValue", "MatchesValue", "WinCont", "WinRate", "MvpMatch" })
        {
            var label = ArenaUiBuilder.TryFind(account, $"Stats/{stat}/Info/Count/CountLabel")?.GetComponent<TextMeshProUGUI>();
            if (label != null) label.text = "0";
            ArenaUiBuilder.SetActive(account, $"Stats/{stat}/Info/Title/Panel", false);
        }

        Currency(bar, "BPInfoPanel", "0");
        Currency(bar, "GPInfoPanel", "0");
        Currency(bar, "MoneyInfoPanel", ArenaMainMenuOverlay.Roubles(profile).ToString("N0"));
        ArenaUiBuilder.SetActive(bar, "ArenaQuestsTab", false);
        IconButton(bar, "QuestsButton", null);
        return root;
    }

    private static void Currency(RectTransform bar, string path, string value)
    {
        var panel = ArenaUiBuilder.TryFind(bar, path);
        if (panel == null) return;
        panel.gameObject.SetActive(true);
        var text = panel.GetComponent<TextMeshProUGUI>();
        if (text != null) text.text = value;
        ArenaUiBuilder.SetActive(panel, "GlpArrowInc", false);
        ArenaUiBuilder.SetActive(panel, "GlpArrowDec", false);
        ArenaUiBuilder.SetActive(panel, "Glow", false);
    }

    private static void IconButton(RectTransform parent, string name, Action action)
    {
        var node = ArenaUiBuilder.TryFind(parent, name);
        if (node == null) return;
        if (action == null) { Dim(node); return; }
        Hit(node);
        node.gameObject.AddComponent<ArenaMenuButton>().Init(node, action, null, false);
    }

    /// <summary>Live's four-state buttons: IdleState shown, HoverState swapped in under the pointer.</summary>
    private static void StateButton(RectTransform parent, string name, Action action)
    {
        var node = ArenaUiBuilder.TryFind(parent, name);
        if (node == null) return;
        ArenaUiBuilder.SetActive(node, "PressedState", false);
        ArenaUiBuilder.SetActive(node, "DisableState", false);
        if (action == null) { Dim(node); return; }
        Hit(node);
        var hover = ArenaUiBuilder.TryFind(node, "HoverState");
        node.gameObject.AddComponent<ArenaMenuButton>().Init(node, action, null, false, hover);
    }

    private static void Dim(RectTransform node)
    {
        var group = node.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0.45f;
        group.interactable = group.blocksRaycasts = false;
    }

    private static void Hit(RectTransform node)
    {
        var image = node.GetComponent<Image>() ?? node.gameObject.AddComponent<Image>();
        if (image.sprite == null) image.color = Color.clear;
        image.raycastTarget = true;
    }

    /// <summary>Party slots as live draws them: the player first, then four empty "+" slots.</summary>
    internal static void BuildParty(ArenaUiBuilder builder, JObject layout, RectTransform container, Profile profile)
    {
        var template = Find(layout, "ArenaGroupMemberTemplate");
        if (template == null) return;
        ArenaUiBuilder.SetActive(container, "GoInRaid", false);
        for (int i = 0; i < 5; i++)
        {
            var slot = builder.Build(template, container);
            slot.name = "Member" + i;
            slot.gameObject.SetActive(true);
            bool me = i == 0;
            ArenaUiBuilder.SetActive(slot, "Image", me);
            ArenaUiBuilder.SetActive(slot, "EmptyImage", !me);
            ArenaUiBuilder.SetActive(slot, "IsMeIcon", me);
            ArenaUiBuilder.SetActive(slot, "LeaderBorder", me);
            ArenaUiBuilder.SetActive(slot, "NormalBorder", !me);
            var letter = ArenaUiBuilder.TryFind(slot, "NicknameFistLetter")?.GetComponent<TextMeshProUGUI>();
            if (letter != null && me && !string.IsNullOrEmpty(profile?.Nickname))
            {
                ArenaUiBuilder.SetActive(slot, "Image", false);
                letter.gameObject.SetActive(true);
                letter.text = profile.Nickname.Substring(0, 1).ToUpperInvariant();
            }
        }
    }

    internal static JObject Find(JObject node, string name)
    {
        if (node.Value<string>("name") == name) return node;
        if (node["children"] is JArray children)
            foreach (var child in children)
                if (child is JObject c && Find(c, name) is { } found) return found;
        return null;
    }
}
