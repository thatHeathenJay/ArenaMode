using ArenaMode.Kits;
using ArenaMode.Session;
using EFT.UI.Screens;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's deploy screen ("Arena Final Countdown": logo, mode, map, nickname and preset, spinner with status
/// and clock) drawn over the game's loading and countdown screens from the moment the raid starts loading until the
/// battle UI is up. Shown by the Fika companion around Fika's loading-screen lifecycle.
/// </summary>
internal sealed class ArenaDeployScreen : MonoBehaviour
{
    private static ArenaDeployScreen _instance;

    private ArenaUiPack _pack;
    private TextMeshProUGUI _status, _time, _preset;
    private RectTransform _spinner;
    private float _shownAt, _deployAt = -1f;
    private string _nickname;

    internal static bool Visible => _instance != null;

    internal static void Show(string nickname)
    {
        if (!ArenaUiPack.IsAvailable || !ArenaSession.ArmedForNextRaid) return;
        Hide();
        try
        {
            var go = new GameObject("ArenaDeployScreen");
            go.transform.SetParent(Plugin.Instance.transform, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 2000;     // above the game's loading screens and Fika's player list
            canvas.overrideSorting = true;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            var screen = go.AddComponent<ArenaDeployScreen>();
            screen._nickname = nickname;
            screen.Build((RectTransform)go.transform);
            _instance = screen;
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena deploy screen failed — stock loading screen instead: {ex}");
            Hide();
        }
    }

    /// <summary>The loading is over and the deploy countdown runs: switch the caption and show the countdown.</summary>
    internal static void BeginCountdown(float seconds)
    {
        if (_instance == null) return;
        _instance._deployAt = Time.unscaledTime + Mathf.Max(0f, seconds);
        _instance._status.text = "DEPLOYING";
    }

    internal static void Hide()
    {
        if (_instance == null) return;
        var instance = _instance;
        _instance = null;
        Destroy(instance.gameObject);
    }

    private void Build(RectTransform root)
    {
        _pack = new ArenaUiPack();
        var builder = new ArenaUiBuilder(_pack, _ => null);

        var fill = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        var fillRect = (RectTransform)fill.transform;
        fillRect.SetParent(root, false);
        ArenaUiBuilder.Stretch(fillRect);
        fill.GetComponent<Image>().color = new Color(0.03f, 0.035f, 0.04f, 1f);
        var backdrop = builder.Build(_pack.Layout("ArenaBattleBackgroundPanel"), root);
        ArenaUiBuilder.Stretch(backdrop);

        var screen = builder.Build(_pack.Layout("Arena Final Countdown"), root);
        ArenaUiBuilder.Stretch(screen);
        _shownAt = Time.unscaledTime;

        string map = ArenaSession.ArmedMap ?? "";
        ArenaUiBuilder.Text(screen, "GameInfoPanel/MapInfo/MapButton/View/Label").text =
            map.Replace("arena_", "").Replace('_', ' ').ToUpperInvariant();
        ArenaUiBuilder.Text(screen, "GameInfoPanel/MapInfo/Mode/View/Label").text = "5 X 5";
        ArenaUiBuilder.Text(screen, "PlayerInfoPanel/AccountName/Name").text = (_nickname ?? "").ToUpperInvariant();
        ArenaUiBuilder.SetActive(screen, "PlayerInfoPanel/AccountName/AccountType", false);
        _preset = ArenaUiBuilder.Text(screen, "PlayerInfoPanel/PresetName");
        _preset.text = PresetName();
        _status = ArenaUiBuilder.Text(screen, "LoadingPanel/StatusCaption");
        _status.text = "LOADING EQUIPMENT";
        _time = ArenaUiBuilder.Text(screen, "LoadingPanel/Time");
        _spinner = ArenaUiBuilder.Find(screen, "LoadingPanel/LoaderBG/Loader");
    }

    private static string PresetName()
    {
        var kit = CoopEntryKit.LastIssued;
        return (kit?.Name ?? kit?.Id ?? "").ToUpperInvariant();
    }

    private void Update()
    {
        try
        {
            var screens = EftScreenManager.Instance;
            if ((screens != null && screens.CheckCurrentScreen(EEftScreenType.BattleUI))
                || Time.unscaledTime - _shownAt > 900f)
            {
                Hide();
                return;
            }
            if (_spinner != null) _spinner.Rotate(0f, 0f, -180f * Time.unscaledDeltaTime);
            float seconds = _deployAt >= 0f ? Mathf.Max(0f, _deployAt - Time.unscaledTime) : Time.unscaledTime - _shownAt;
            int s = Mathf.RoundToInt(seconds);
            _time.text = $"({s / 60:00}:{s % 60:00})";
            if (_preset.text.Length == 0) _preset.text = PresetName();
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena deploy screen tick failed: {ex.Message}");
            Hide();
        }
    }

    private void OnDestroy()
    {
        _pack?.Dispose();
        _pack = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
