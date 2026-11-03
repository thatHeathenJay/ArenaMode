using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Field order == Unity 6 (6000.3) serialized order, verified byte-exact against all 4,108 TextMeshProUGUI
// components in the Arena menu scene (see tools/Rip-ArenaUi.ps1 in the ArenaMode fork). Do not reorder.
namespace TMPro
{
    public enum ColorGradientMode { Single, HorizontalGradient, VerticalGradient, FourCornersGradient }
    public enum FontWeight { Thin = 100, ExtraLight = 200, Light = 300, Regular = 400, Medium = 500, SemiBold = 600, Bold = 700, Heavy = 800, Black = 900 }
    public enum FontStyles { Normal = 0, Bold = 1, Italic = 2, Underline = 4, LowerCase = 8, UpperCase = 16, SmallCaps = 32, Strikethrough = 64, Superscript = 128, Subscript = 256, Highlight = 512 }
    public enum HorizontalAlignmentOptions { Left = 1, Center = 2, Right = 4, Justified = 8, Flush = 16, Geometry = 32 }
    public enum VerticalAlignmentOptions { Top = 256, Middle = 512, Bottom = 1024, Baseline = 2048, Geometry = 4096, Capline = 8192 }
    public enum TextAlignmentOptions { TopLeft = 257, Converted = 65535 }
    public enum TextWrappingModes { NoWrap = 0, Normal = 1, PreserveWhitespace = 2, PreserveWhitespaceNoWrap = 3 }
    public enum TextOverflowModes { Overflow = 0, Ellipsis = 1, Masking = 2, Truncate = 3, ScrollRect = 4, Page = 5, Linked = 6 }
    public enum OTL_FeatureTag { kern = 0x6B65726E, liga = 0x6C696761, mark = 0x6D61726B, mkmk = 0x6D6B6D6B }
    public enum TextureMappingOptions { Character = 0, Line = 1, Paragraph = 2, MatchAspect = 3 }
    public enum VertexSortingOrder { Normal, Reverse }

    [Serializable]
    public struct VertexGradient { public Color topLeft; public Color topRight; public Color bottomLeft; public Color bottomRight; }

    public class FontAsset : ScriptableObject { }
    public class TextColorGradient : ScriptableObject { }
    public class SpriteAsset : ScriptableObject { }
    public class TextStyleSheet : ScriptableObject { }
    public class TMP_SubMeshUI : MaskableGraphic { }
    // Legacy names still referenced by the EFT Assembly-CSharp (CustomTextMeshProUGUI etc.).
    public class TMP_FontAsset : FontAsset { }
    public class TMP_SpriteAsset : SpriteAsset { }
    public class TMP_StyleSheet : TextStyleSheet { }
    public class TMP_ColorGradient : TextColorGradient { }

    public abstract class TMP_Text : MaskableGraphic
    {
        [SerializeField] protected string m_text;
        [SerializeField] protected bool m_isRightToLeft;
        [SerializeField] protected FontAsset m_fontAsset;
        [SerializeField] protected Material m_sharedMaterial;
        [SerializeField] protected Material[] m_fontSharedMaterials;
        [SerializeField] protected Material m_fontMaterial;
        [SerializeField] protected Material[] m_fontMaterials;
        [SerializeField] protected Color32 m_fontColor32;
        [SerializeField] protected Color m_fontColor;
        [SerializeField] protected bool m_enableVertexGradient;
        [SerializeField] protected ColorGradientMode m_colorMode;
        [SerializeField] protected VertexGradient m_fontColorGradient;
        [SerializeField] protected TextColorGradient m_fontColorGradientPreset;
        [SerializeField] protected SpriteAsset m_spriteAsset;
        [SerializeField] protected bool m_tintAllSprites;
        [SerializeField] protected TextStyleSheet m_StyleSheet;
        [SerializeField] protected int m_TextStyleHashCode;
        [SerializeField] protected bool m_overrideHtmlColors;
        [SerializeField] protected Color32 m_faceColor;
        [SerializeField] protected float m_fontSize;
        [SerializeField] protected float m_fontSizeBase;
        [SerializeField] protected FontWeight m_fontWeight;
        [SerializeField] protected bool m_enableAutoSizing;
        [SerializeField] protected float m_fontSizeMin;
        [SerializeField] protected float m_fontSizeMax;
        [SerializeField] protected FontStyles m_fontStyle;
        [SerializeField] protected HorizontalAlignmentOptions m_HorizontalAlignment;
        [SerializeField] protected VerticalAlignmentOptions m_VerticalAlignment;
        [SerializeField] protected TextAlignmentOptions m_textAlignment;
        [SerializeField] protected float m_characterSpacing;
        // Present in 6000.3 builds (1.0 in all but one Arena component), absent from the 3.0.0-exp.4 source; name unknown.
        [SerializeField] protected float m_Unity6SpacingScale;
        [SerializeField] protected float m_wordSpacing;
        [SerializeField] protected float m_lineSpacing;
        [SerializeField] protected float m_lineSpacingMax;
        [SerializeField] protected float m_paragraphSpacing;
        [SerializeField] protected float m_charWidthMaxAdj;
        [SerializeField] protected TextWrappingModes m_TextWrappingMode;
        [SerializeField] protected float m_wordWrappingRatios;
        [SerializeField] protected TextOverflowModes m_overflowMode;
        [SerializeField] protected TMP_Text m_linkedTextComponent;
        [SerializeField] protected TMP_Text parentLinkedComponent;
        [SerializeField] protected bool m_enableKerning;
        [SerializeField] protected List<OTL_FeatureTag> m_ActiveFontFeatures;
        [SerializeField] protected bool m_enableExtraPadding;
        [SerializeField] protected bool checkPaddingRequired;
        [SerializeField] protected bool m_isRichText;
        [SerializeField] protected bool m_EmojiFallbackSupport;
        [SerializeField] protected bool m_parseCtrlCharacters;
        [SerializeField] protected bool m_isOrthographic;
        [SerializeField] protected bool m_isCullingEnabled;
        [SerializeField] protected TextureMappingOptions m_horizontalMapping;
        [SerializeField] protected TextureMappingOptions m_verticalMapping;
        [SerializeField] protected float m_uvLineOffset;
        [SerializeField] protected VertexSortingOrder m_geometrySortingOrder;
        [SerializeField] protected bool m_IsTextObjectScaleStatic;
        [SerializeField] protected bool m_VertexBufferAutoSizeReduction;
        [SerializeField] protected bool m_useMaxVisibleDescender;
        [SerializeField] protected int m_pageToDisplay;
        [SerializeField] protected Vector4 m_margin;
        [SerializeField] protected bool m_isUsingLegacyAnimationComponent;
        [SerializeField] protected bool m_isVolumetricText;
    }

    public class TextMeshProUGUI : TMP_Text
    {
        [SerializeField] private bool m_hasFontAssetChanged;
        [SerializeField] protected Material m_baseMaterial;
        [SerializeField] private Vector4 m_maskOffset;
    }
}
