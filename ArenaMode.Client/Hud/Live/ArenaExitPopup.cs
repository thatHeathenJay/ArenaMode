using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Live Arena's confirmation popup (ArenaExitGamePopup: full-width art band, header, question, ACCEPT / CANCEL),
/// reused for every yes/no question the menus ask: quitting the game, leaving a match.
/// </summary>
internal sealed class ArenaExitPopup : MonoBehaviour
{
    private static ArenaExitPopup _instance;
    private ArenaUiPack _pack;
    private Action _onAccept;

    internal static bool Visible => _instance != null;

    internal static void Show(string header, string question, string accept, string cancel, Action onAccept, int sortingOrder = 600)
    {
        Hide();
        if (!ArenaUiPack.IsAvailable) { onAccept?.Invoke(); return; }
        var go = new GameObject("ArenaExitPopup");
        go.transform.SetParent(Plugin.Instance.transform, false);
        _instance = go.AddComponent<ArenaExitPopup>();
        _instance._onAccept = onAccept;
        try { _instance.Build(header, question, accept, cancel, sortingOrder); }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning("Arena popup failed: " + ex.Message);
            Hide();
            onAccept?.Invoke();
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
        if (Input.GetKeyDown(KeyCode.Escape)) Hide();
    }

    private void Build(string header, string question, string accept, string cancel, int sortingOrder)
    {
        _pack = new ArenaUiPack();
        var builder = new ArenaUiBuilder(_pack, _ => null);
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        canvas.overrideSorting = true;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();

        var root = builder.Build(_pack.Layout("ArenaExitGamePopup"), transform);
        ArenaUiBuilder.Stretch(root);
        var shade = root.GetComponent<Image>() ?? ArenaUiBuilder.TryFind(root, "MainBackground")?.GetComponent<Image>();
        if (shade != null) shade.raycastTarget = true;        // swallows clicks on whatever is underneath
        var container = ArenaUiBuilder.Find(root, "MainBackground/ContainerBackground/Container");
        ArenaUiBuilder.Text(container, "Header/Label").text = header;
        ArenaUiBuilder.Text(container, "Content/InputObject/Label").text = question;
        ArenaUiBuilder.SetActive(container, "Footer/Title", false);
        Button(ArenaUiBuilder.Find(container, "Footer/Buttons/AcceptButton"), accept, () => { var action = _onAccept; Hide(); action?.Invoke(); });
        Button(ArenaUiBuilder.Find(container, "Footer/Buttons/CancelButton"), cancel, Hide);
    }

    private static void Button(RectTransform button, string caption, Action onClick)
    {
        var label = ArenaUiBuilder.TryFind(button, "SizeLabel/Label")?.GetComponent<TextMeshProUGUI>();
        if (label != null) label.text = caption;
        var size = ArenaUiBuilder.TryFind(button, "SizeLabel")?.GetComponent<TextMeshProUGUI>();
        if (size != null) { size.richText = true; size.text = "<alpha=#00>" + caption; }
        var hit = ArenaUiBuilder.TryFind(button, "RaycastTarget") ?? button;
        var image = hit.GetComponent<Image>() ?? hit.gameObject.AddComponent<Image>();
        image.color = Color.clear; image.raycastTarget = true;
        button.gameObject.AddComponent<ArenaMenuButton>().Init(button, onClick);
    }
}
