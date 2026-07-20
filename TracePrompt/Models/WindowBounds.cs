namespace TracePrompt.Models;

/// <summary>
/// 画面上の矩形範囲（位置とサイズ）を表します。
/// 記録セッション中は固定のキャプチャ範囲としても使います。
/// </summary>
public readonly struct WindowBounds
{
    public WindowBounds(int left, int top, int width, int height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>ウィンドウ左端の X 座標（画面全体基準）。</summary>
    public int Left { get; }

    /// <summary>ウィンドウ上端の Y 座標（画面全体基準）。</summary>
    public int Top { get; }

    /// <summary>ウィンドウの幅（ピクセル）。</summary>
    public int Width { get; }

    /// <summary>ウィンドウの高さ（ピクセル）。</summary>
    public int Height { get; }

    /// <summary>右端の X 座標（Left + Width）。</summary>
    public int Right => Left + Width;

    /// <summary>下端の Y 座標（Top + Height）。</summary>
    public int Bottom => Top + Height;

    /// <summary>
    /// 画面座標の点が、このウィンドウの範囲内かどうかを判定します。
    /// </summary>
    public bool Contains(int screenX, int screenY)
    {
        return screenX >= Left
               && screenX < Right
               && screenY >= Top
               && screenY < Bottom;
    }
}
