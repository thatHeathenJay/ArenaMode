using ArenaMode.Modes;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's in-match ESC menu (ArenaBattleMenuScreen) over EFT's: SETTINGS, RESUME and DISCONNECT, with live's
/// warning panel (leaving early while the match runs) and its confirmation popup. EFT's own menu stays underneath,
/// faded out, and its buttons do the work.
/// </summary>
internal sealed class ArenaBattleMenu : MonoBehaviour
{
    private ArenaUiPack _pack;
    private GameObject _canvasGo;
    private MenuScreen _menu;
    private readonly List<CanvasGroup> _faded = new();

    private void Update()
    {
        bool wanted = false;
        try
        {
            var screens = EftScreenManager.Instance;
            wanted = ArenaUiPack.IsAvailable && screens != null && Singleton<GameWorld>.Instantiated
                     && screens.CheckCurrentScreen(EEftScreenType.MainMenu);
        }
        catch { /* not ready */ }
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
            var builder = new ArenaUiBuilder(_pack, key => key switch
            {
                "SETTINGS" => "SETTINGS", "RESUME" => "RESUME", "DISCONNECT" => "DISCONNECT", _ => null,
            });
            _canvasGo = new GameObject("ArenaBattleMenuCanvas");
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 505;
            canvas.overrideSorting = true;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            var root = builder.Build(_pack.Layout("ArenaBattleMenuScreen"), _canvasGo.transform);
            ArenaUiBuilder.Stretch(root);
            foreach (var path in new[] { "Image", "Particles", "ArenaBattleMenuPopup" }) ArenaUiBuilder.SetActive(root, path, false);
            var tdm = ArenaMatchService.Current?.Mode as TeamDeathmatchMode;
            bool live = tdm != null && !tdm.IsComplete;
            ArenaUiBuilder.SetActive(root, "LeavingForbiddenWarningPanel", live);
            ArenaUiBuilder.SetActive(root, "LeavingPermittedWarningPanel", !live);
            SetText(root, "LeavingForbiddenWarningPanel/Warning Caption", "WARNING! YOU ARE LEAVING THE MATCH.");
            SetText(root, "LeavingForbiddenWarningPanel/Warning Description", "If you leave now you let your team down. The match goes on without you.");
            SetText(root, "LeavingPermittedWarningPanel/Warning Caption", "You can leave the match freely.");

            var buttons = ArenaUiBuilder.Find(root, "ButtonsContainer");
            Wire(buttons, "SettingsButton", "SETTINGS", OpenSettings);
            Wire(buttons, "Bottom/ResumeOrPresetButton", "RESUME", Resume);
            Wire(buttons, "Bottom/DisconnectOrMainMenuButton", "DISCONNECT", () =>
                ArenaExitPopup.Show("DISCONNECT", live ? "Are you sure you want to leave the game early?" : "Are you sure you want to leave before the game ends?",
                    "YES", "NO", Disconnect));

            foreach (Transform child in _menu.transform)
            {
                var group = child.GetComponent<CanvasGroup>() ?? child.gameObject.AddComponent<CanvasGroup>();
                if (group.alpha <= 0f) continue;
                group.alpha = 0f;
                group.blocksRaycasts = false;
                _faded.Add(group);
            }
            Plugin.ModLogger.LogInfo("Arena battle menu shown.");
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena battle menu failed: " + ex.Message);
            Hide();
        }
    }

    private void Hide()
    {
        foreach (var group in _faded)
            if (group != null) { group.alpha = 1f; group.blocksRaycasts = true; }
        _faded.Clear();
        ArenaExitPopup.Hide();
        if (_canvasGo != null) Destroy(_canvasGo);
        _canvasGo = null;
        _pack?.Dispose();
        _pack = null;
        _menu = null;
    }

    private void Wire(RectTransform buttons, string path, string caption, Action action)
    {
        var node = ArenaUiBuilder.TryFind(buttons, path);
        if (node == null) return;
        foreach (var state in new[] { "Hover", "Idle" })
        {
            var text = ArenaUiBuilder.TryFind(node, state + "/Text (TMP)")?.GetComponent<TextMeshProUGUI>();
            if (text != null) text.text = caption;
        }
        ArenaUiBuilder.SetActive(node, "Hover", false);
        var hit = node.GetComponent<Image>() ?? node.gameObject.AddComponent<Image>();
        hit.raycastTarget = true;
        node.gameObject.AddComponent<ArenaMenuButton>().Init(node, action);
    }

    private void Resume()
    {
        try { _menu?._hideScreenButton?.OnClick?.Invoke(); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena battle menu: resume failed: " + ex.Message); }
    }

    private void Disconnect()
    {
        try
        {
            var button = _menu?._disconnectButton != null && _menu._disconnectButton.gameObject.activeInHierarchy ? _menu._disconnectButton : _menu?._exitButton;
            button?.OnClick?.Invoke();
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena battle menu: disconnect failed: " + ex.Message); }
    }

    private static void OpenSettings()
    {
        try
        {
            var taskbar = Object.FindObjectOfType<MenuTaskBar>();
            var toggles = taskbar == null ? null : Traverse.Create(taskbar).Field("_toggleButtons").GetValue<Dictionary<EMenuType, AnimatedToggle>>();
            if (toggles == null || !toggles.TryGetValue(EMenuType.Settings, out var toggle) || toggle == null)
            { Plugin.ModLogger.LogWarning("Arena battle menu: settings toggle unavailable."); return; }
            toggle.isOn = false;
            toggle.isOn = true;
        }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena battle menu: settings failed: " + ex.Message); }
    }

    private static void SetText(RectTransform root, string path, string text)
    {
        var t = ArenaUiBuilder.TryFind(root, path)?.GetComponent<TextMeshProUGUI>();
        if (t != null) t.text = text;
    }
}
