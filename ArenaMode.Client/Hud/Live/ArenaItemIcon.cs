using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using System;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// EFT's own rendered item icon on an Image: the icon cache hands back a sprite straight away when it has one, or
/// renders in the background and raises Changed. The binding dies with the Image.
/// </summary>
internal sealed class ArenaItemIcon : MonoBehaviour
{
    private Action _unbind;
    private ItemIcon _icon;
    private Image _image;
    private float _maxWidth, _maxHeight;

    internal static void Bind(Image image, Item item, float maxWidth, float maxHeight)
    {
        if (image == null || item == null) return;
        var holder = image.GetComponent<ArenaItemIcon>() ?? image.gameObject.AddComponent<ArenaItemIcon>();
        holder._image = image;
        holder._maxWidth = maxWidth;
        holder._maxHeight = maxHeight;
        holder.Load(item);
    }

    private void Load(Item item)
    {
        _unbind?.Invoke();
        _unbind = null;
        try
        {
            _icon = ItemViewFactory.LoadItemIcon(item, 1, false);
            Apply();
            _unbind = _icon.Changed.Subscribe(Apply);
        }
        catch (Exception ex)
        {
            Plugin.ModLogger.LogWarning($"Arena item icon for {item.TemplateId} unavailable: {ex.Message}");
        }
    }

    private void Apply()
    {
        if (_image == null || _icon == null) return;
        var sprite = _icon.Sprite;
        _image.enabled = sprite != null;
        if (sprite == null) return;
        _image.sprite = sprite;
        _image.preserveAspect = true;
        // Fit inside the cell at the icon's own aspect.
        float w = sprite.rect.width, h = sprite.rect.height;
        float scale = Mathf.Min(_maxWidth / Mathf.Max(1f, w), _maxHeight / Mathf.Max(1f, h), 1f);
        _image.rectTransform.sizeDelta = new Vector2(w * scale, h * scale);
    }

    private void OnDestroy()
    {
        _unbind?.Invoke();
        _unbind = null;
    }
}
