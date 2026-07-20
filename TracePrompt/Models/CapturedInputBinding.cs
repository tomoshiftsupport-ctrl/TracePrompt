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

    /// <summary>UI 表示用（例: 左クリック, Ctrl + S）</summary>
    public string DisplayText { get; init; } = string.Empty;

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
        FromMouse("Left", "左クリック");
}

public enum CapturedInputKind
{
    Mouse,
    Keyboard
}
