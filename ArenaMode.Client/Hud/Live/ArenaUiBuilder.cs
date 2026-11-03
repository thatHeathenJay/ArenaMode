using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMode.Hud.Live;

/// <summary>
/// Instantiates a ripped layout tree (tools/arena-ui-export.py JSON) as uGUI objects: RectTransform, Image, TMP text,
/// layout groups/elements, fitters, canvas groups and masks. Arena's own MonoBehaviours are skipped; the HUD binds
/// state onto the named nodes afterwards.
/// </summary>
internal sealed class ArenaUiBuilder
{
    private readonly ArenaUiPack _pack;
    private readonly Func<string, string> _localize;

    internal ArenaUiBuilder(ArenaUiPack pack, Func<string, string> localize)
    {
        _pack = pack;
        _localize = localize;
    }

    internal RectTransform Build(JObject node, Transform parent)
    {
        var go = new GameObject(node.Value<string>("name") ?? "Node", typeof(RectTransform));
        go.layer = 5;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        ApplyRect(rect, node["rect"] as JObject);

        string localizationKey = null;
        if (node["components"] is JArray components)
            foreach (var component in components)
                if (component is JObject c)
                {
                    string type = c.Value<string>("type") ?? "";
                    var f = c["fields"] as JObject ?? new JObject();
                    try { AddComponent(go, type, f, ref localizationKey); }
                    catch (Exception ex)
                    {
                        Plugin.ModLogger.LogWarning($"Arena UI: {type} on {go.name} skipped: {ex.Message}");
                    }
                }
        if (node["children"] is JArray children)
            foreach (var child in children)
                if (child is JObject c) Build(c, rect);

        // EFT.UI.LocalizedText sits on the text itself or on a button whose "Label" child carries the text.
        if (localizationKey != null)
        {
            var text = go.GetComponent<TextMeshProUGUI>() ?? go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null) text.text = _localize(localizationKey) ?? text.text;
        }

        go.SetActive(node.Value<bool?>("active") ?? true);
        return rect;
    }

    private static void ApplyRect(RectTransform rect, JObject r)
    {
        if (r == null) return;
        rect.anchorMin = Vec2(r["anchorMin"], Vector2.zero);
        rect.anchorMax = Vec2(r["anchorMax"], Vector2.one);
        rect.pivot = Vec2(r["pivot"], new Vector2(.5f, .5f));
        rect.anchoredPosition = Vec2(r["pos"], Vector2.zero);
        rect.sizeDelta = Vec2(r["size"], Vector2.zero);
        if (r["scale"] is JArray s && s.Count >= 3)
            rect.localScale = new Vector3((float)s[0], (float)s[1], (float)s[2]);
        float rot = r.Value<float?>("rot") ?? 0f;
        if (Mathf.Abs(rot) > 0.001f) rect.localRotation = Quaternion.Euler(0f, 0f, rot);
    }

    private void AddComponent(GameObject go, string type, JObject f, ref string localizationKey)
    {
        bool enabled = f.Value<bool?>("m_Enabled") ?? true;
        switch (type)
        {
            case "UnityEngine.UI.Image":
            {
                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                image.color = Color(f["m_Color"], UnityEngine.Color.white);
                image.sprite = _pack.Sprite(f["m_Sprite"] as JObject);
                image.type = (Image.Type)(f.Value<int?>("m_Type") ?? 0);
                image.preserveAspect = (f.Value<int?>("m_PreserveAspect") ?? 0) != 0;
                image.fillCenter = (f.Value<int?>("m_FillCenter") ?? 1) != 0;
                image.fillMethod = (Image.FillMethod)(f.Value<int?>("m_FillMethod") ?? 0);
                image.fillAmount = f.Value<float?>("m_FillAmount") ?? 1f;
                image.fillClockwise = (f.Value<int?>("m_FillClockwise") ?? 1) != 0;
                image.fillOrigin = f.Value<int?>("m_FillOrigin") ?? 0;
                image.pixelsPerUnitMultiplier = f.Value<float?>("m_PixelsPerUnitMultiplier") ?? 1f;
                image.maskable = (f.Value<int?>("m_Maskable") ?? 1) != 0;
                image.enabled = enabled;
                break;
            }
            case "TMPro.TextMeshProUGUI":
            case "CustomTextMeshProUGUI":
            {
                var text = go.AddComponent<TextMeshProUGUI>();
                text.font = HudStyle.Font;
                text.raycastTarget = false;
                text.text = f.Value<string>("m_text") ?? "";
                text.fontSize = f.Value<float?>("m_fontSize") ?? 16f;
                text.color = Color(f["m_fontColor"], UnityEngine.Color.white);
                text.fontStyle = (FontStyles)(f.Value<int?>("m_fontStyle") ?? 0);
                int h = f.Value<int?>("m_HorizontalAlignment") ?? 0;
                int v = f.Value<int?>("m_VerticalAlignment") ?? 0;
                text.alignment = h + v > 0 ? (TextAlignmentOptions)(h | v)
                    : (TextAlignmentOptions)(f.Value<int?>("m_textAlignment") ?? (int)TextAlignmentOptions.TopLeft);
                text.enableAutoSizing = (f.Value<int?>("m_enableAutoSizing") ?? 0) != 0;
                text.fontSizeMin = f.Value<float?>("m_fontSizeMin") ?? 18f;
                text.fontSizeMax = f.Value<float?>("m_fontSizeMax") ?? 72f;
                text.enableWordWrapping = (f.Value<int?>("m_TextWrappingMode") ?? 1) != 0;
                text.overflowMode = (TextOverflowModes)(f.Value<int?>("m_overflowMode") ?? 0);
                text.characterSpacing = f.Value<float?>("m_characterSpacing") ?? 0f;
                text.lineSpacing = f.Value<float?>("m_lineSpacing") ?? 0f;
                text.richText = (f.Value<int?>("m_isRichText") ?? 1) != 0;
                if (f["m_margin"] is JObject m)
                    text.margin = new Vector4(m.Value<float?>("x") ?? 0, m.Value<float?>("y") ?? 0,
                        m.Value<float?>("z") ?? 0, m.Value<float?>("w") ?? 0);
                text.enabled = enabled;
                break;
            }
            case "EFT.UI.LocalizedText":
                localizationKey = f.Value<string>("localizationKey");
                break;
            case "UnityEngine.UI.HorizontalLayoutGroup":
            case "UnityEngine.UI.VerticalLayoutGroup":
            {
                HorizontalOrVerticalLayoutGroup group = type.EndsWith("HorizontalLayoutGroup")
                    ? go.AddComponent<HorizontalLayoutGroup>()
                    : go.AddComponent<VerticalLayoutGroup>();
                if (f["m_Padding"] is JObject p)
                    group.padding = new RectOffset(p.Value<int?>("m_Left") ?? 0, p.Value<int?>("m_Right") ?? 0,
                        p.Value<int?>("m_Top") ?? 0, p.Value<int?>("m_Bottom") ?? 0);
                group.childAlignment = (TextAnchor)(f.Value<int?>("m_ChildAlignment") ?? 0);
                group.spacing = f.Value<float?>("m_Spacing") ?? 0f;
                group.childForceExpandWidth = (f.Value<int?>("m_ChildForceExpandWidth") ?? 1) != 0;
                group.childForceExpandHeight = (f.Value<int?>("m_ChildForceExpandHeight") ?? 1) != 0;
                group.childControlWidth = (f.Value<int?>("m_ChildControlWidth") ?? 0) != 0;
                group.childControlHeight = (f.Value<int?>("m_ChildControlHeight") ?? 0) != 0;
                group.childScaleWidth = (f.Value<int?>("m_ChildScaleWidth") ?? 0) != 0;
                group.childScaleHeight = (f.Value<int?>("m_ChildScaleHeight") ?? 0) != 0;
                group.enabled = enabled;
                break;
            }
            case "UnityEngine.UI.LayoutElement":
            {
                var element = go.AddComponent<LayoutElement>();
                element.ignoreLayout = (f.Value<int?>("m_IgnoreLayout") ?? 0) != 0;
                element.minWidth = f.Value<float?>("m_MinWidth") ?? -1f;
                element.minHeight = f.Value<float?>("m_MinHeight") ?? -1f;
                element.preferredWidth = f.Value<float?>("m_PreferredWidth") ?? -1f;
                element.preferredHeight = f.Value<float?>("m_PreferredHeight") ?? -1f;
                element.flexibleWidth = f.Value<float?>("m_FlexibleWidth") ?? -1f;
                element.flexibleHeight = f.Value<float?>("m_FlexibleHeight") ?? -1f;
                element.layoutPriority = f.Value<int?>("m_LayoutPriority") ?? 1;
                element.enabled = enabled;
                break;
            }
            case "UnityEngine.UI.ContentSizeFitter":
            {
                var fitter = go.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = (ContentSizeFitter.FitMode)(f.Value<int?>("m_HorizontalFit") ?? 0);
                fitter.verticalFit = (ContentSizeFitter.FitMode)(f.Value<int?>("m_VerticalFit") ?? 0);
                fitter.enabled = enabled;
                break;
            }
            case "CanvasGroup":
            {
                var group = go.AddComponent<CanvasGroup>();
                group.alpha = f.Value<float?>("m_Alpha") ?? 1f;
                group.interactable = false;
                group.blocksRaycasts = false;
                break;
            }
            case "UnityEngine.UI.Mask":
            {
                if (go.GetComponent<Graphic>() == null) break;
                var mask = go.AddComponent<Mask>();
                mask.showMaskGraphic = (f.Value<int?>("m_ShowMaskGraphic") ?? 0) != 0;
                mask.enabled = enabled;
                break;
            }
            case "UnityEngine.UI.RectMask2D":
                go.AddComponent<RectMask2D>().enabled = enabled;
                break;
        }
    }

    internal static Color Color(JToken token, Color fallback)
        => token is JObject c
            ? new Color(c.Value<float?>("r") ?? 1f, c.Value<float?>("g") ?? 1f, c.Value<float?>("b") ?? 1f, c.Value<float?>("a") ?? 1f)
            : fallback;

    private static Vector2 Vec2(JToken token, Vector2 fallback)
        => token is JArray a && a.Count >= 2 ? new Vector2((float)a[0], (float)a[1]) : fallback;

    /// <summary>A node by its slash path under <paramref name="root"/> (inactive nodes included); throws when missing.</summary>
    internal static RectTransform Find(RectTransform root, string path)
        => (RectTransform)(root.Find(path) ?? throw new InvalidOperationException($"Arena UI layout node missing: {root.name}/{path}"));

    internal static RectTransform TryFind(RectTransform root, string path) => root.Find(path) as RectTransform;

    internal static TextMeshProUGUI Text(RectTransform root, string path) => Find(root, path).GetComponent<TextMeshProUGUI>()
        ?? throw new InvalidOperationException($"Arena UI layout node has no text: {root.name}/{path}");

    internal static void SetActive(RectTransform root, string path, bool active)
    {
        var node = TryFind(root, path);
        if (node != null) node.gameObject.SetActive(active);
    }

    internal static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}
