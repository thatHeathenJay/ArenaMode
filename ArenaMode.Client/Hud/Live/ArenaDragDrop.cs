using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>Something a dragged item can land on; the tag says what (a gear slot, the supplies grid, the armory).</summary>
internal sealed class ArenaDropTarget : MonoBehaviour
{
    public string Tag;
}

/// <summary>
/// Live-style drag of an item icon: a ghost follows the pointer and the drop reports the target's tag (null when
/// nothing accepted it). Clicks still work through the ordinary button on the same node.
/// </summary>
internal sealed class ArenaDragSource : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private Image _icon;
    private RectTransform _canvas;
    private Action<string> _onDrop;
    private RectTransform _ghost;

    internal void Init(Image icon, RectTransform canvasRoot, Action<string> onDrop)
    {
        _icon = icon;
        _canvas = canvasRoot;
        _onDrop = onDrop;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (_icon == null || _icon.sprite == null || _canvas == null) return;
        var go = new GameObject("DragGhost", typeof(RectTransform), typeof(CanvasGroup));
        _ghost = (RectTransform)go.transform;
        _ghost.SetParent(_canvas, false);
        _ghost.SetAsLastSibling();
        var image = go.AddComponent<Image>();
        image.sprite = _icon.sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        _ghost.sizeDelta = _icon.rectTransform.sizeDelta;
        go.GetComponent<CanvasGroup>().alpha = 0.85f;
        go.GetComponent<CanvasGroup>().blocksRaycasts = false;
        Follow(eventData);
    }

    public void OnDrag(PointerEventData eventData) => Follow(eventData);

    public void OnEndDrag(PointerEventData eventData)
    {
        if (_ghost != null) Destroy(_ghost.gameObject);
        _ghost = null;
        string tag = null;
        var results = new List<RaycastResult>();
        EventSystem.current?.RaycastAll(eventData, results);
        foreach (var result in results)
        {
            var target = result.gameObject.GetComponentInParent<ArenaDropTarget>();
            if (target != null) { tag = target.Tag; break; }
        }
        try { _onDrop?.Invoke(tag); }
        catch (Exception ex) { Plugin.ModLogger.LogWarning("Arena drag drop failed: " + ex.Message); }
    }

    private void Follow(PointerEventData eventData)
    {
        if (_ghost == null) return;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, eventData.position, eventData.pressEventCamera, out var local);
        _ghost.anchoredPosition = local;
    }

    private void OnDestroy()
    {
        if (_ghost != null) Destroy(_ghost.gameObject);
    }
}
