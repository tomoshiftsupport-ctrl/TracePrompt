using Loc = TracePrompt.Localization.LocalizationManager;

namespace TracePrompt.Models;

/// <summary>
/// スクショを取るトリガーとなる、単一の入力（キー1つ or マウスボタン1つ）です。
/// </summary>
public sealed class CapturedInputBinding
{
    public CapturedInputKind Kind { get; init; }

    /// <summary>マウスの場合: Left / Right / Middle / XButton1 / XButton2 / Wheel</summary>
    public string? MouseButton { get; init; }

    /// <summary>キーボードの仮想キーコードです。</summary>
    public int? VirtualKey { get; init; }

    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Win { get; init; }

    /// <summary>UI 表示用（例: 左クリック, Ctrl + S）。保存時点の言語のまま固定されるため、
    /// 表示には <see cref="ResolveDisplayText"/> を使ってください。</summary>
    public string DisplayText { get; init; } = string.Empty;

    /// <summary>
    /// 現在の表示言語でラベルを解決します。マウスボタンは言語ごとに単語が異なるため、
    /// 保存済みの <see cref="DisplayText"/> をそのまま使わず、都度 <see cref="MouseButton"/> から組み立て直します。
    /// キーボードのラベル（例: Ctrl + S）はキー名がもともと言語非依存のため、保存済みの値をそのまま使います。
    /// </summary>
    public string ResolveDisplayText() => Kind == CapturedInputKind.Mouse
        ? MouseButton switch
        {
            "Left" => Loc.Instance.Get("MouseButton_Left"),
            "Right" => Loc.Instance.Get("MouseButton_Right"),
            "Middle" => Loc.Instance.Get("MouseButton_Middle"),
            "XButton1" => Loc.Instance.Get("MouseButton_XButton1"),
            "XButton2" => Loc.Instance.Get("MouseButton_XButton2"),
            "Wheel" => Loc.Instance.Get("MouseButton_Wheel"),
            _ => DisplayText
        }
        : DisplayText;

    public static CapturedInputBinding FromMouse(string mouseButton, string displayText) => new()
    {
        Kind = CapturedInputKind.Mouse,
        MouseButton = mouseButton,
        DisplayText = displayText
    };

    public static CapturedInputBinding FromKeyboard(
        int virtualKey,
        bool ctrl,
        bool alt,
        bool shift,
        bool win,
        string displayText) => new()
    {
        Kind = CapturedInputKind.Keyboard,
        VirtualKey = virtualKey,
        Ctrl = ctrl,
        Alt = alt,
        Shift = shift,
        Win = win,
        DisplayText = displayText
    };

    public static CapturedInputBinding DefaultLeftClick() =>
        FromMouse("Left", Loc.Instance.Get("MouseButton_Left"));
}

public enum CapturedInputKind
{
    Mouse,
    Keyboard
}
