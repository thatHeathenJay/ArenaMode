using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaMode.Hud.Live;

/// <summary>
/// The live Arena UI rip: layout JSON (tools/arena-ui-export.py) plus the textures they reference, produced from the
/// player's OWN EFT: Arena install by tools/pack-arena-ui.py into &lt;plugin dir&gt;/arena-ui. BSG assets never ship with
/// the mod; when the folder is missing the stock HUD draws instead.
/// </summary>
internal sealed class ArenaUiPack : IDisposable
{
    internal static string Directory
        => Path.Combine(ArenaAssetPaths.FromPluginFile(Plugin.Instance.Info.Location), "arena-ui");

    internal static bool IsAvailable
        => File.Exists(Path.Combine(Directory, "layouts", "ArenaCheckPointWidget.json"));

    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JObject> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal JObject Layout(string name)
    {
        if (_layouts.TryGetValue(name, out var cached)) return cached;
        string file = Path.Combine(Directory, "layouts", name + ".json");
        var layout = JObject.Parse(File.ReadAllText(file));
        _layouts[name] = layout;
        return layout;
    }

    /// <summary>Texture by the path the layout names ("Texture2D/X.png"); null (cached) when missing or unreadable.</summary>
    internal Texture2D Texture(string relative)
    {
        if (_disposed || string.IsNullOrEmpty(relative)) return null;
        if (_textures.TryGetValue(relative, out var cached)) return cached;
        Texture2D texture = null;
        try
        {
            string file = Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file))
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(File.ReadAllBytes(file), false))
                    throw new InvalidDataException("PNG decode failed");
                texture.name = "Arena UI " + Path.GetFileName(file);
                texture.filterMode = FilterMode.Bilinear;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.Apply(false, true);
            }
        }
        catch (Exception ex)
        {
            if (texture != null) Object.Destroy(texture);
            texture = null;
            Plugin.ModLogger.LogWarning("Arena UI texture unavailable: " + relative + ": " + ex.Message);
        }
        _textures[relative] = texture;
        return texture;
    }

    /// <summary>A sprite from a layout sprite reference {sprite, rect, border, pivot, ppu}.</summary>
    internal Sprite Sprite(JObject reference)
    {
        if (_disposed || reference == null) return null;
        string file = reference.Value<string>("sprite");
        if (string.IsNullOrEmpty(file)) return null;
        var rect = reference["rect"] as JArray;
        string key = file + "|" + (rect?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
        if (_sprites.TryGetValue(key, out var cached)) return cached;
        Sprite sprite = null;
        var texture = Texture(file);
        if (texture != null)
        {
            try
            {
                var r = rect != null && rect.Count == 4
                    ? new Rect((float)rect[0], (float)rect[1], (float)rect[2], (float)rect[3])
                    : new Rect(0f, 0f, texture.width, texture.height);
                // Variant exports ("_0") can differ in size from the atlas the layout was authored on.
                r.xMin = Mathf.Clamp(r.xMin, 0f, texture.width);
                r.yMin = Mathf.Clamp(r.yMin, 0f, texture.height);
                r.xMax = Mathf.Clamp(r.xMax, r.xMin + 1f, texture.width);
                r.yMax = Mathf.Clamp(r.yMax, r.yMin + 1f, texture.height);
                var pivot = Vec2(reference["pivot"] as JArray, new Vector2(.5f, .5f));
                var border = reference["border"] as JArray;
                var b = border != null && border.Count == 4
                    ? new Vector4((float)border[0], (float)border[1], (float)border[2], (float)border[3])
                    : Vector4.zero;
                float ppu = reference.Value<float?>("ppu") ?? 100f;
                sprite = UnityEngine.Sprite.Create(texture, r, pivot, ppu > 0 ? ppu : 100f, 0,
                    SpriteMeshType.FullRect, b);
                sprite.name = reference.Value<string>("name") ?? file;
            }
            catch (Exception ex)
            {
                Plugin.ModLogger.LogWarning("Arena UI sprite unavailable: " + key + ": " + ex.Message);
            }
        }
        _sprites[key] = sprite;
        return sprite;
    }

    /// <summary>Whole-texture sprite, e.g. a weapon silhouette named by its short name; null when absent.</summary>
    internal Sprite WholeTexture(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        foreach (char c in Path.GetInvalidFileNameChars())
            if (relative.IndexOf(c) >= 0 && c != '/' && c != '\\') return null;
        return Sprite(new JObject { ["sprite"] = relative });
    }

    private static Vector2 Vec2(JArray array, Vector2 fallback)
        => array != null && array.Count >= 2 ? new Vector2((float)array[0], (float)array[1]) : fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sprite in _sprites.Values) if (sprite != null) Object.Destroy(sprite);
        foreach (var texture in _textures.Values) if (texture != null) Object.Destroy(texture);
        _sprites.Clear();
        _textures.Clear();
        _layouts.Clear();
    }
}
