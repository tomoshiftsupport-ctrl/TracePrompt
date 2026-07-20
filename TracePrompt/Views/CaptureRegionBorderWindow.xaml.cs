using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TracePrompt.Models;

namespace TracePrompt.Views;

/// <summary>
/// キャプチャ範囲を薄い枠線で常時プレビュー表示する、クリックスルーのオーバーレイです。
/// 自分自身は BitBlt によるキャプチャに映り込まないよう、
/// WS_EX_TRANSPARENT（マウス透過）と WDA_EXCLUDEFROMCAPTURE（キャプチャ除外）を設定します。
/// </summary>
public partial class CaptureRegionBorderWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopMost = new(-1);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly nint _hwnd;

    public CaptureRegionBorderWindow()
    {
        InitializeComponent();

        // Show() 前に HWND を確定させ、クリックスルー化とキャプチャ除外を先に済ませます。
        _hwnd = new WindowInteropHelper(this).EnsureHandle();

        int exStyle = GetWindowLong(_hwnd, GwlExStyle);
        SetWindowLong(_hwnd, GwlExStyle, exStyle | WsExTransparent | WsExLayered | WsExToolWindow);

        // Windows 10 2004 未満では失敗しますが、その場合は諦めて表示だけ続けます（例外は投げません）。
        SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
    }

    /// <summary>物理ピクセル座標で、枠線がちょうどキャプチャ範囲を縁取るように配置します。</summary>
    public void ApplyRegion(WindowBounds region)
    {
        SetWindowPos(_hwnd, HwndTopMost, region.Left, region.Top, region.Width, region.Height, SwpNoActivate);
    }
}
