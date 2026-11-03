using ArenaMode.Config;
using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using EFT.UI.Screens;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly string[] Dimmed = { "ArmoryButton", "MerchantsButton", "CareerButton" };
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
    private MenuTaskBar _taskbar;
    private bool _taskbarHidden;
    private Session.ArenaCharacterPreview _preview;
    private RectTransform _modelHost;

    internal static bool Visible { get; private set; }

    private static readonly EEftScreenType? SettingsScreen =
        Enum.TryParse(typeof(EEftScreenType).Name == "EEftScreenType" ? "Settings" : "", out EEftScreenType parsed) ? parsed : null;

    private Canvas _canvas;
    private GraphicRaycaster _raycaster;

    private void Update()
    {
        bool inMenu = false, inSettings = false;
        try
        {
            var screens = EftScreenManager.Instance;
            if (ArenaUiPack.IsAvailable && screens != null && !Singleton<GameWorld>.Instantiated)
            {
                inMenu = screens.CheckCurrentScreen(EEftScreenType.MainMenu);
                // Settings opens over the menu in live: keep EFT's menu hidden underneath, draw nothing of ours.
                inSettings = SettingsScreen is EEftScreenType settings && screens.CheckCurrentScreen(settings);
            }
        }
        catch { /* screen manager not ready */ }
        bool wanted = inMenu || inSettings;
        if (wanted && _canvasGo == null) Show();
        else if (!wanted && _canvasGo != null) Hide();
        if (_canvas != null)
        {
            _canvas.enabled = inMenu;
            if (_raycaster != null) _raycaster.enabled = inMenu;
        }
        if (_preview != null && inMenu && Input.GetMouseButton(0) && Input.mousePosition.x > Screen.width * 0.55f)
            _preview.Rotate(-Input.GetAxis("Mouse X"), 1f);
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
            _canvas = canvas;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _raycaster = _canvasGo.AddComponent<GraphicRaycaster>();

            var menu = builder.Build(_pack.Layout("ArenaMainMenu"), _canvasGo.transform);
            foreach (var path in Hidden) ArenaUiBuilder.SetActive(menu, path, false);
            foreach (var shadow in new[] { "LeftShadow", "LeftShadow/LeftShadow_2" })
            {
                var image = ArenaUiBuilder.TryFind(menu, shadow)?.GetComponent<Image>();
                if (image == null) continue;
                image.type = Image.Type.Simple;
                image.sprite = _pack.WholeTexture("Texture2D/MainMenu_Background_Left_Shadow.png") ?? image.sprite;
            }
            BindProfile(menu);
            var buttons = ArenaUiBuilder.Find(menu, "ArenaMainMenuLeftPanel/MainMenuSimpleButtons");
            Wire(buttons, "MatchingButtons/StartMatchingButton", ArenaBattleFlow.Show);
            Wire(buttons, "Aresenal_Trading/PresetsButton", ArenaPresetListScreen.Show);
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

            _taskbar = Object.FindObjectOfType<MenuTaskBar>();   // before it is hidden: Settings drives its toggle
            HideStockMenu();
            if (MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            {
                MonoBehaviourSingleton<PreloaderUI>.Instance.SetMenuTaskBarVisibility(false);
                _taskbarHidden = true;
            }
            _modelHost = ArenaUiBuilder.TryFind(menu, "PlayerModelImage");
            ShowCharacter();
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
        try { _preview?.Dispose(); } catch { /* already gone */ }
        _preview = null;
        ArenaPresetsScreen.Hide();
        ArenaBattleFlow.Hide();
        ArenaPresetListScreen.Hide();
        ArenaResultOverlay.Hide();
        _menu = null;
        _taskbar = null;
        if (_taskbarHidden && MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            MonoBehaviourSingleton<PreloaderUI>.Instance.SetMenuTaskBarVisibility(true);
        _taskbarHidden = false;
        if (_canvasGo != null) Object.Destroy(_canvasGo);
        _canvasGo = null;
        _canvas = null;
        _raycaster = null;
        _pack?.Dispose();
        _pack = null;
    }

    // ---------------------------------------------------------------- binding

    private static void BindProfile(RectTransform menu)
    {
        var profile = SessionProfile();
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

    /// <summary>
    /// EFT re-drives the screen's own fade group every frame, so each child gets its own group instead; the scene
    /// logo, warning labels, version label and Fika's player panel are plain objects switched off while we are up.
    /// </summary>
    private void HideStockMenu()
    {
        foreach (Transform child in _menu.transform)
        {
            var group = child.GetComponent<CanvasGroup>();
            if (group == null) group = child.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
        }
        HideObject(_menu._warningGameObject);
        HideObject(_menu._alphaWarningGameObject);
        foreach (var root in Object.FindObjectsOfType<EnvironmentUIRoot>())
        {
            HideObject(Traverse.Create(root).Field("_regularLogo").GetValue<GameObject>());
            HideObject(Traverse.Create(root).Field("_pveLogo").GetValue<GameObject>());
        }
        if (MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            HideObject(Traverse.Create(MonoBehaviourSingleton<PreloaderUI>.Instance).Field("_alphaVersionLabel").GetValue<LocalizedText>()?.gameObject);
        try
        {
            var fikaMenu = AccessTools.TypeByName("Fika.Core.UI.Custom.MainMenuUIScript");
            var instance = fikaMenu == null ? null : AccessTools.Property(fikaMenu, "Instance")?.GetValue(null) as MonoBehaviour;
            if (instance != null) HideObject(instance.gameObject);
        }
        catch { /* no Fika */ }
    }

    private static void HideObject(GameObject go)
    {
        if (go != null && go.activeSelf) go.SetActive(false);
    }

    /// <summary>
    /// The stock 0.16 menu has no character; live keeps one on the right. The mod's own character preview (a
    /// separate camera around the native side-selection model view) renders the profile into the layout's model image.
    /// </summary>
    private void ShowCharacter()
    {
        if (_modelHost == null) return;
        try
        {
            var profile = SessionProfile();
            if (profile == null)
            {
                Plugin.ModLogger.LogInfo("Arena main menu: no profile yet; character skipped.");
                return;
            }
            _preview = new Session.ArenaCharacterPreview(profile, _modelHost,
                () => Plugin.ModLogger.LogWarning("Arena main menu character unavailable."), SelectedKitEquipment(profile));
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena main menu character failed: " + ex.Message);
            try { _preview?.Dispose(); } catch { /* ignore */ }
            _preview = null;
        }
    }

    /// <summary>The selected preset worn by the menu character, as live shows it; null keeps the generic set.</summary>
    private static EFT.InventoryLogic.InventoryEquipment SelectedKitEquipment(Profile profile)
    {
        try
        {
            if (Settings.RandomKit.Value) return null;
            var kit = Kits.KitCatalog.Kits.FirstOrDefault(k => string.Equals(k.Id, Settings.SelectedKit.Value, StringComparison.OrdinalIgnoreCase));
            return kit == null ? null : Kits.TeamArenaEntryKit.Prepare(profile, kit);
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena main menu: preset preview equipment unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>Rebuilds the menu character (after a preset selection).</summary>
    internal static void RefreshCharacter()
    {
        var overlay = Plugin.Instance?.GetComponent<ArenaMainMenuOverlay>();
        if (overlay == null || overlay._canvasGo == null) return;
        try { overlay._preview?.Dispose(); } catch { /* gone */ }
        overlay._preview = null;
        overlay.ShowCharacter();
    }

    private static Profile SessionProfile()
    {
        try { return SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile; }
        catch { return null; }
    }

    private void Play() => _menu?._playButton?.OnClick?.Invoke();

    /// <summary>EFT's stock play path (the menu's Play button); the Arena battle flow ends here.</summary>
    internal static void StartStockMatchmaking()
    {
        var overlay = Plugin.Instance?.GetComponent<ArenaMainMenuOverlay>();
        if (overlay != null) overlay.Play();
    }

    // EFT's exit path first warns about the hideout generator; Arena has no hideout. ponytail: plain quit until the
    // Arena exit popup (ArenaExitGamePopup in the rip) replaces the confirmation.
    private void Quit() => Application.Quit();

    private void OpenSettings()
    {
        try
        {
            var toggles = _taskbar == null ? null : Traverse.Create(_taskbar).Field("_toggleButtons").GetValue<Dictionary<EMenuType, AnimatedToggle>>();
            if (toggles == null) { Plugin.ModLogger.LogWarning("Arena main menu: taskbar toggles unavailable."); return; }
            if (!toggles.TryGetValue(EMenuType.Settings, out var toggle) || toggle == null)
            {
                Plugin.ModLogger.LogWarning("Arena main menu: no Settings toggle; taskbar has " + string.Join(", ", toggles.Keys));
                return;
            }
            toggle.isOn = false;
            toggle.isOn = true;
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena main menu: settings unavailable: " + ex.Message); }
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
