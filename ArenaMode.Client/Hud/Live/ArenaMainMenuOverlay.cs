using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Screens;
using HarmonyLib;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's main menu drawn over EFT's: the stock menu screen and taskbar are hidden (still alive, so their
/// buttons can be driven), the Arena layout renders on top with the menu's 3D character left visible, and the
/// working entries forward to EFT (Arena Battle = Play, Settings = taskbar settings, Quit = Exit). Entries without
/// a backing feature yet are dimmed. Only when the UI pack is installed.
/// </summary>
internal sealed class ArenaMainMenuOverlay : MonoBehaviour
{
    private static readonly Dictionary<string, string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arena/button/ArenaBattle"] = "ARENA BATTLE", ["FASTGAME"] = "FAST GAME", ["arena/button/Presets"] = "PRESETS",
        ["arena/button/Armory"] = "ARMORY", ["MERCHANTS"] = "TRADERS", ["arena/button/Career"] = "CAREER",
        ["SETTINGS"] = "SETTINGS", ["arena/button/QuitGame"] = "QUIT GAME",
        ["arena/info/Total GLP"] = "TOTAL GLP", ["arena/info/Main GLP Scores"] = "MAIN GLP SCORES",
        ["QuestWidget/Objectives"] = "OBJECTIVES",
    };
    private static readonly string[] Dimmed = { "PresetsButton", "ArmoryButton", "MerchantsButton", "CareerButton" };
    private static readonly string[] Hidden =
    {
        "PatchNewsButton", "PatchNewsButtonTarget", "ArenaQuestsWidgetDropdown", "SurveyButton", "SquadPanel",
        "ArenaMainMenuLeftPanel/WidgetContainer", "ArenaMainMenuLeftPanel/MainMenuSimpleButtons/MatchingButtons/FastGameButton",
        "AccountInfoPanel/GLP Scores Big", "ProfileInfoPanel/InfoGroup/RankNameGroup", "ProfileInfoPanel/InfoGroup/BpBlock",
        "ProfileInfoPanel/InfoGroup/TcBlock", "ProfileInfoPanel/RankImageFroup",
    };

    private GameObject _canvasGo;
    private ArenaUiPack _pack;
    private MenuScreen _menu;
    private CanvasGroup _menuGroup;
    private bool _taskbarHidden, _anchorsLogged;

    internal static bool Visible { get; private set; }

    private void Update()
    {
        bool wanted = false;
        try
        {
            var screens = EftScreenManager.Instance;
            wanted = ArenaUiPack.IsAvailable && screens != null && screens.CheckCurrentScreen(EEftScreenType.MainMenu)
                     && !Singleton<GameWorld>.Instantiated;
        }
        catch { /* screen manager not ready */ }
        if (wanted && _canvasGo == null) Show();
        else if (!wanted && _canvasGo != null) Hide();
    }

    private void OnDestroy() => Hide();

    private void Show()
    {
        try
        {
            _menu = Object.FindObjectOfType<MenuScreen>();
            if (_menu == null) return;
            _pack = new ArenaUiPack();
            var builder = new ArenaUiBuilder(_pack, key => English.TryGetValue(key, out var s) ? s : null);

            _canvasGo = new GameObject("ArenaMainMenuCanvas");
            _canvasGo.transform.SetParent(Plugin.Instance.transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            canvas.overrideSorting = true;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            var menu = builder.Build(_pack.Layout("ArenaMainMenu"), _canvasGo.transform);
            foreach (var path in Hidden) ArenaUiBuilder.SetActive(menu, path, false);
            BindProfile(menu);
            var buttons = ArenaUiBuilder.Find(menu, "ArenaMainMenuLeftPanel/MainMenuSimpleButtons");
            Wire(buttons, "MatchingButtons/StartMatchingButton", Play);
            Wire(buttons, "Settings_Quit/SettingsButton", OpenSettings);
            Wire(buttons, "Settings_Quit/QuitButton", Quit);
            foreach (var name in Dimmed)
            {
                var node = buttons.GetComponentsInChildren<RectTransform>(true);
                foreach (var rect in node)
                    if (rect.name == name)
                    {
                        var group = rect.gameObject.AddComponent<CanvasGroup>();
                        group.alpha = 0.35f;
                        group.interactable = group.blocksRaycasts = false;
                    }
            }

            _menuGroup = _menu.GetComponent<CanvasGroup>() ?? _menu.gameObject.AddComponent<CanvasGroup>();
            _menuGroup.alpha = 0f;
            _menuGroup.blocksRaycasts = false;
            if (MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            {
                MonoBehaviourSingleton<PreloaderUI>.Instance.SetMenuTaskBarVisibility(false);
                _taskbarHidden = true;
            }
            PlaceCharacter();
            Visible = true;
            Plugin.ModLogger.LogInfo("Arena main menu shown over EFT's.");
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena main menu failed — stock menu instead: {ex}");
            Hide();
        }
    }

    private void Hide()
    {
        Visible = false;
        if (_menuGroup != null)
        {
            _menuGroup.alpha = 1f;
            _menuGroup.blocksRaycasts = true;
        }
        _menuGroup = null;
        _menu = null;
        if (_taskbarHidden && MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            MonoBehaviourSingleton<PreloaderUI>.Instance.SetMenuTaskBarVisibility(true);
        _taskbarHidden = false;
        if (_canvasGo != null) Object.Destroy(_canvasGo);
        _canvasGo = null;
        _pack?.Dispose();
        _pack = null;
    }

    // ---------------------------------------------------------------- binding

    private static void BindProfile(RectTransform menu)
    {
        Profile profile = null;
        try { profile = SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile; }
        catch { /* no session yet */ }
        var info = ArenaUiBuilder.Find(menu, "ProfileInfoPanel/InfoGroup");
        ArenaUiBuilder.Text(info, "PlayerNameLabel").text = profile?.Nickname ?? "";
        ArenaUiBuilder.Text(info, "MoneyBlock/Money").text = Roubles(profile).ToString("N0");
        ArenaUiBuilder.Text(info, "GpBlock/GP").text = "0";
        var stats = ArenaUiBuilder.Find(menu, "AccountInfoPanel");
        foreach (var block in new[] { "GLP", "GLPLeft", "KDRatioValue", "MatchesValue" })
        {
            var label = ArenaUiBuilder.TryFind(stats, block + "/Info/Count/CountLabel")?.GetComponent<TextMeshProUGUI>();
            if (label != null) label.text = "0";
            ArenaUiBuilder.SetActive(stats, block + "/Info/Count/Panel", false);
        }
        ArenaUiBuilder.SetActive(stats, "GLPLeft", false);
    }

    private static long Roubles(Profile profile)
    {
        const string rub = "5449016a4bdc2d6f028b456f";
        long total = 0;
        try
        {
            foreach (var item in profile?.Inventory?.GetPlayerItems(EFT.InventoryLogic.EPlayerItems.Stash) ?? Array.Empty<EFT.InventoryLogic.Item>())
                if (item.TemplateId == rub) total += item.StackObjectsCount;
        }
        catch { /* stash not loaded */ }
        return total;
    }

    private void Wire(RectTransform buttons, string path, Action action)
    {
        var node = ArenaUiBuilder.Find(buttons, path);
        var hit = ArenaUiBuilder.TryFind(node, "RaycastTarget") ?? node;
        var image = hit.GetComponent<Image>() ?? hit.gameObject.AddComponent<Image>();
        image.color = Color.clear;
        image.raycastTarget = true;
        var button = node.gameObject.AddComponent<ArenaMenuButton>();
        button.Init(node, action);
    }

    private void Play() => _menu?._playButton?.OnClick?.Invoke();

    private void Quit() => _menu?._exitButton?.OnClick?.Invoke();

    private void OpenSettings()
    {
        try
        {
            var taskbar = Object.FindObjectOfType<MenuTaskBar>();
            var toggles = Traverse.Create(taskbar).Field("_toggleButtons").GetValue<Dictionary<EMenuType, AnimatedToggle>>();
            if (toggles != null && toggles.TryGetValue(EMenuType.Settings, out var toggle) && toggle != null)
                toggle.isOn = true;
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena main menu: settings unavailable: " + ex.Message); }
    }

    /// <summary>Live keeps the character on the right. The menu scene anchors it by a ScreenPositionAnchor.</summary>
    private void PlaceCharacter()
    {
        try
        {
            foreach (var root in Object.FindObjectsOfType<EnvironmentUIRoot>())
            {
                if (root.ScreenAnchors == null) continue;
                foreach (var anchor in root.ScreenAnchors)
                {
                    if (anchor == null) continue;
                    var t = Traverse.Create(anchor);
                    var alignment = t.Field("_alignment").GetValue<TextAnchor>();
                    var position = t.Field("_position").GetValue<Vector2>();
                    if (!_anchorsLogged)
                        Plugin.ModLogger.LogInfo($"Arena menu anchor: {root.name}/{anchor.name} alignment={alignment} position={position}");
                    if (anchor.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0
                        || anchor.name.IndexOf("character", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        t.Field("_alignment").SetValue(TextAnchor.MiddleRight);
                        t.Field("_position").SetValue(new Vector2(-0.25f, position.y));
                    }
                }
            }
            _anchorsLogged = true;
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena main menu: character placement skipped: " + ex.Message); }
    }
}

/// <summary>Arena's animated menu button: label only at rest, background and highlight icon on hover.</summary>
internal sealed class ArenaMenuButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    private Action _action;
    private Image _background, _highlight;
    private TextMeshProUGUI _label;
    private Color _labelColor;

    internal void Init(RectTransform node, Action action)
    {
        _action = action;
        _background = ArenaUiBuilder.TryFind(node, "Background")?.GetComponent<Image>();
        _highlight = ArenaUiBuilder.TryFind(node, "IconContainer/IconHighlight")?.GetComponent<Image>();
        _label = node.GetComponentInChildren<TextMeshProUGUI>(true);
        if (_label != null) _labelColor = _label.color;
        var idle = ArenaUiBuilder.TryFind(node, "IconContainer/IconIdle")?.GetComponent<Image>();
        if (idle != null) idle.color = new Color(1f, 1f, 1f, 1f);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        Tint(1f);
        ArenaLiveSounds.Play("arena_button_over.wav", 0.6f);
    }

    public void OnPointerExit(PointerEventData eventData) => Tint(0f);

    public void OnPointerClick(PointerEventData eventData)
    {
        ArenaLiveSounds.Play("arena_button_click.wav", 0.8f);
        try { _action?.Invoke(); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena main menu action failed: " + ex.Message); }
    }

    private void Tint(float alpha)
    {
        if (_background != null) _background.color = new Color(1f, 1f, 1f, alpha);
        if (_highlight != null) _highlight.color = new Color(1f, 1f, 1f, alpha);
        if (_label != null) _label.color = alpha > 0f ? Color.white : _labelColor;
    }
}
