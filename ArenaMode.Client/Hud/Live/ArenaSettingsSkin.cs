using EFT.UI;
using EFT.UI.Screens;
using EFT.UI.Settings;
using System;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's settings chrome on EFT's settings screen: Arena's underlay behind the tabs and its SETTINGS header
/// above them (ArenaSettingsScreen). EFT's tabs, controls and SAVE / BACK / REVERT stay as they are; live's screen is
/// EFT's with this dressing.
/// </summary>
internal sealed class ArenaSettingsSkin : MonoBehaviour
{
    private static readonly EEftScreenType? SettingsScreenType =
        Enum.TryParse("Settings", out EEftScreenType parsed) ? parsed : null;

    private ArenaUiPack _pack;
    private GameObject _underlay, _header;
    private SettingsScreen _screen;

    private void Update()
    {
        bool wanted = false;
        try
        {
            var screens = EftScreenManager.Instance;
            wanted = ArenaUiPack.IsAvailable && screens != null && SettingsScreenType is EEftScreenType type && screens.CheckCurrentScreen(type);
        }
        catch { /* not ready */ }
        if (wanted && _underlay == null) Apply();
        else if (!wanted && _underlay != null) Remove();
    }

    private void OnDestroy() => Remove();

    private void Apply()
    {
        try
        {
            _screen = Object.FindObjectOfType<SettingsScreen>();
            if (_screen == null) return;
            _pack = new ArenaUiPack();
            var builder = new ArenaUiBuilder(_pack, key => key == "SETTINGS" ? "SETTINGS" : null);
            var layout = _pack.Layout("ArenaSettingsScreen");
            var underlay = builder.Build(Find(layout, "ArenaMatchmakerUnderlay"), _screen.transform);
            ArenaUiBuilder.Stretch(underlay);
            underlay.SetAsFirstSibling();
            foreach (var image in underlay.GetComponentsInChildren<Image>(true)) image.raycastTarget = false;
            _underlay = underlay.gameObject;
            var header = builder.Build(Find(layout, "Header"), _screen.transform);
            header.anchorMin = header.anchorMax = new Vector2(0.5f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.anchoredPosition = new Vector2(0f, -8f);
            foreach (var image in header.GetComponentsInChildren<Image>(true)) image.raycastTarget = false;
            var caption = ArenaUiBuilder.TryFind(header, "Caption")?.GetComponent<TMPro.TextMeshProUGUI>();
            if (caption != null) { caption.text = "SETTINGS"; caption.raycastTarget = false; }
            _header = header.gameObject;
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena settings skin failed: " + ex.Message);
            Remove();
        }
    }

    private void Remove()
    {
        if (_underlay != null) Destroy(_underlay);
        if (_header != null) Destroy(_header);
        _underlay = _header = null;
        _pack?.Dispose();
        _pack = null;
        _screen = null;
    }

    private static Newtonsoft.Json.Linq.JObject Find(Newtonsoft.Json.Linq.JObject node, string name)
    {
        if (node.Value<string>("name") == name) return node;
        if (node["children"] is Newtonsoft.Json.Linq.JArray children)
            foreach (var child in children)
                if (child is Newtonsoft.Json.Linq.JObject c && Find(c, name) is { } found) return found;
        return null;
    }
}
