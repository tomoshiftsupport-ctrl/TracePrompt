using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TracePrompt.Models;

namespace TracePrompt.Views;

/// <summary>
/// キャプチャ範囲の枠の左上に貼り付く、開始・一時停止・再開・停止の小さな操作パネルです。
/// 自分自身は BitBlt によるキャプチャに映り込まないよう WDA_EXCLUDEFROMCAPTURE を設定します
/// （クリック操作はできる必要があるため、枠オーバーレイと違いマウス透過にはしません）。
/// </summary>
public partial class RecordingControlToolbarWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopMost = new(-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly nint _hwnd;

    public RecordingControlToolbarWindow()
    {
        InitializeComponent();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Windows 10 2004 未満では失敗しますが、その場合は諦めて表示だけ続けます（例外は投げません）。
        SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
    }

    /// <summary>キャプチャ範囲の左上（物理ピクセル座標）へ、少し内側に余白を空けて配置します。</summary>
    public void ApplyRegion(WindowBounds region)
    {
        const int margin = 8;
        SetWindowPos(_hwnd, HwndTopMost, region.Left + margin, region.Top + margin, 0, 0, SwpNoSize | SwpNoActivate);
    }
}
