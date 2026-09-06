using System.Text;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace QQMusic.Tui.UI;

/// <summary>
/// 现代化初音未来 (Hatsune Miku) 主题调色板
/// 严格遵循多层级视觉规范：
/// - 纯净皓白 (歌名、关键正文): #EDF6F6
/// - 初音葱绿 (边框、框架标题、状态指示): #39C5BB
/// - 发光青翠 (歌词高亮、聚焦条强调): #5DF8EB
/// - 冰晶淡青 (歌手、次要提示): #A2E8E2
/// - 静谧青灰 (专辑名、时间戳、弱化说明): #62828A
/// - 经典品红 (初音发饰标识、快捷键、激活按钮): #FF5277
/// - 冷夜深空黑 (界面背景底色): #12151A / #161B22
/// </summary>
public static class MikuTheme
{
    // 主题色彩定义
    public static readonly Color QqGreenPrimary  = new(0x31, 0xC2, 0x7C); // #31C27C 绿色
    public static readonly Color QqGreenLight    = new(0x5F, 0xE3, 0xA1); // #5FE3A1 浅绿色
    public static readonly Color QqGreenDark     = new(0x20, 0x58, 0x54); // #205854 聚焦背景色
    public static readonly Color QqGreenActive   = new(0x18, 0x46, 0x42); // #184642 未获焦选中底色
    public static readonly Color QqTextLyricDim  = new(0xB8, 0xCC, 0xD0); // #B8CCD0 歌词次要文字颜色

    // 点缀色
    public static readonly Color MikuPinkAccent  = new(0xFF, 0x52, 0x77); // #FF5277 品红
    public static readonly Color MikuPinkLight   = new(0xFF, 0x7A, 0x99); // #FF7A99 粉红

    // 背景底色：采用 Color.None 透传终端原生背景
    public static readonly Color MikuBgCanvas    = Color.None;
    public static readonly Color MikuBgSurface   = Color.None;
    public static readonly Color MikuBgDialog    = new(0x1A, 0x21, 0x2C); // 弹窗背景色
    public static readonly Color MikuBgFocus     = QqGreenDark;           // 列表聚焦高亮

    // 字体文本层级
    public static readonly Color MikuTextWhite   = new(0xED, 0xF6, 0xF6); // #EDF6F6 正文字体颜色
    public static readonly Color MikuTextSub     = new(0xA2, 0xE8, 0xE2); // #A2E8E2 次级文字颜色
    public static readonly Color MikuTextMuted   = new(0x62, 0x82, 0x8A); // #62828A 弱化文字颜色

    public static Scheme Base { get; } = CreateBaseScheme();
    public static Scheme Dialog { get; } = CreateDialogScheme();
    public static Scheme PlayerBar { get; } = CreatePlayerBarScheme();
    public static Scheme FavoriteActive { get; } = CreateFavoriteActiveScheme();
    public static Scheme Lyric { get; } = CreateLyricScheme();
    public static Scheme FrameBorder { get; } = CreateFrameBorderActiveScheme();
    public static Scheme FrameBorderDim { get; } = CreateFrameBorderDimScheme();
    public static Scheme FrameBorderActive { get; } = CreateFrameBorderActiveScheme();
    public static Scheme TitleHighlight { get; } = CreateTitleHighlightScheme();

    private static Scheme CreateBaseScheme()
    {
        return new Scheme
        {
            Normal    = new Attribute(MikuTextWhite,    MikuBgSurface),
            Focus     = new Attribute(Color.White,      QqGreenDark),   // 选中行白色文字与高亮背景
            HotNormal = new Attribute(MikuPinkAccent,   MikuBgSurface),
            HotFocus  = new Attribute(Color.White,      MikuPinkAccent),
            Disabled  = new Attribute(MikuTextMuted,    MikuBgSurface),
            Highlight = new Attribute(QqGreenPrimary,   MikuBgSurface),
            Active    = new Attribute(Color.White,      QqGreenActive), // 未获焦选中行
            ReadOnly  = new Attribute(MikuTextMuted,    MikuBgSurface),
            Editable  = new Attribute(Color.White,      Color.None)
        };
    }

    private static Scheme CreateDialogScheme()
    {
        return new Scheme
        {
            Normal    = new Attribute(MikuTextWhite,    MikuBgDialog),
            Focus     = new Attribute(Color.White,      QqGreenDark),
            HotNormal = new Attribute(MikuPinkAccent,   MikuBgDialog),
            HotFocus  = new Attribute(Color.White,      MikuPinkAccent),
            Disabled  = new Attribute(MikuTextMuted,    MikuBgDialog),
            Highlight = new Attribute(QqGreenPrimary,   MikuBgDialog),
            Active    = new Attribute(QqGreenLight,     QqGreenDark),
            ReadOnly  = new Attribute(MikuTextMuted,    MikuBgDialog),
            Editable  = new Attribute(Color.White,      MikuBgDialog)
        };
    }

    private static Scheme CreatePlayerBarScheme()
    {
        return new Scheme
        {
            // 底部控制栏
            Normal    = new Attribute(QqGreenPrimary,   MikuBgSurface),
            Focus     = new Attribute(Color.White,      QqGreenDark),
            HotNormal = new Attribute(MikuPinkAccent,   MikuBgSurface),
            HotFocus  = new Attribute(Color.White,      MikuPinkAccent),
            Disabled  = new Attribute(MikuTextMuted,    MikuBgSurface),
            Highlight = new Attribute(QqGreenLight,     MikuBgSurface),
            Active    = new Attribute(QqGreenPrimary,   MikuBgSurface),
            ReadOnly  = new Attribute(MikuTextMuted,    MikuBgSurface),
            Editable  = new Attribute(Color.White,      Color.None)
        };
    }

    private static Scheme CreateFavoriteActiveScheme()
    {
        return new Scheme
        {
            Normal    = new Attribute(QqGreenLight,     MikuBgSurface),
            Focus     = new Attribute(Color.White,      QqGreenDark),
            HotNormal = new Attribute(QqGreenPrimary,   MikuBgSurface),
            HotFocus  = new Attribute(Color.White,      QqGreenPrimary),
            Disabled  = new Attribute(MikuTextMuted,    MikuBgSurface),
            Highlight = new Attribute(QqGreenLight,     MikuBgSurface),
            Active    = new Attribute(QqGreenLight,     MikuBgSurface),
            ReadOnly  = new Attribute(MikuTextMuted,    MikuBgSurface),
            Editable  = new Attribute(Color.White,      Color.None)
        };
    }

    private static Scheme CreateLyricScheme()
    {
        return new Scheme
        {
            // 歌词配色：透明背景，当前播放行高亮
            Normal    = new Attribute(QqTextLyricDim,   Color.None),
            Focus     = new Attribute(QqGreenPrimary,   Color.None),
            HotNormal = new Attribute(QqGreenLight,     Color.None),
            HotFocus  = new Attribute(QqGreenLight,     Color.None),
            Disabled  = new Attribute(new Color(0x66, 0x77, 0x80), Color.None),
            Highlight = new Attribute(QqGreenPrimary,   Color.None),
            Active    = new Attribute(QqGreenPrimary,   Color.None),
            ReadOnly  = new Attribute(QqTextLyricDim,   Color.None),
            Editable  = new Attribute(QqGreenPrimary,   Color.None)
        };
    }

    private static Scheme CreateFrameBorderDimScheme()
    {
        return new Scheme
        {
            // 未获焦窗格边框
            Normal    = new Attribute(new Color(0x38, 0x58, 0x54), Color.None),
            Focus     = new Attribute(QqGreenPrimary,              Color.None),
            HotNormal = new Attribute(MikuPinkAccent,              Color.None),
            HotFocus  = new Attribute(Color.White,                 MikuPinkAccent),
            Disabled  = new Attribute(MikuTextMuted,               Color.None),
            Highlight = new Attribute(new Color(0x38, 0x58, 0x54), Color.None),
            Active    = new Attribute(new Color(0x38, 0x58, 0x54), Color.None),
            ReadOnly  = new Attribute(new Color(0x38, 0x58, 0x54), Color.None),
            Editable  = new Attribute(new Color(0x38, 0x58, 0x54), Color.None)
        };
    }

    private static Scheme CreateFrameBorderActiveScheme()
    {
        return new Scheme
        {
            // 获焦激活窗格边框
            Normal    = new Attribute(QqGreenPrimary, Color.None),
            Focus     = new Attribute(QqGreenPrimary, Color.None),
            HotNormal = new Attribute(MikuPinkAccent, Color.None),
            HotFocus  = new Attribute(Color.White,    MikuPinkAccent),
            Disabled  = new Attribute(MikuTextMuted,  Color.None),
            Highlight = new Attribute(QqGreenLight,   Color.None),
            Active    = new Attribute(QqGreenPrimary, Color.None),
            ReadOnly  = new Attribute(QqGreenPrimary, Color.None),
            Editable  = new Attribute(QqGreenPrimary, Color.None)
        };
    }

    private static Scheme CreateTitleHighlightScheme()
    {
        return new Scheme
        {
            // 窗格顶部标题配色
            Normal    = new Attribute(QqGreenPrimary, Color.None),
            Focus     = new Attribute(QqGreenLight,   Color.None),
            HotNormal = new Attribute(QqGreenLight,   Color.None),
            HotFocus  = new Attribute(Color.White,    MikuPinkAccent),
            Disabled  = new Attribute(MikuTextMuted,  Color.None),
            Highlight = new Attribute(QqGreenLight,   Color.None),
            Active    = new Attribute(QqGreenPrimary, Color.None),
            ReadOnly  = new Attribute(QqGreenPrimary, Color.None),
            Editable  = new Attribute(QqGreenPrimary, Color.None)
        };
    }

    public static void Apply()
    {
        // 将按钮外框括号统一为 ASCII 简洁半角中括号 [ ]
        Glyphs.LeftBracket = new Rune('[');
        Glyphs.RightBracket = new Rune(']');
        Terminal.Gui.Views.Button.DefaultShadow = ShadowStyles.None;
        Terminal.Gui.Views.Dialog.DefaultShadow = ShadowStyles.None;
        SchemeManager.AddScheme("Base", Base);
        SchemeManager.AddScheme("Dialog", Dialog);
        SchemeManager.AddScheme("PlayerBar", PlayerBar);
        SchemeManager.AddScheme("Lyric", Lyric);
        SchemeManager.AddScheme("FrameBorder", FrameBorder);
        SchemeManager.AddScheme("FrameBorderDim", FrameBorderDim);
        SchemeManager.AddScheme("FrameBorderActive", FrameBorderActive);
        SchemeManager.AddScheme("TitleHighlight", TitleHighlight);
    }

    public static void ApplyTo(View view, Scheme? scheme = null)
    {
        scheme ??= Base;
        view.SetScheme(scheme);
        if (view is Terminal.Gui.Views.Button btn)
        {
            btn.ShadowStyle = ShadowStyles.None;
        }
        foreach (var sub in view.SubViews)
        {
            ApplyTo(sub, scheme);
        }
    }
}
