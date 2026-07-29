using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TracePrompt.Models;

namespace TracePrompt.Views;

/// <summary>
/// クリップモードのキャプチャ範囲を、中央のつまみだけをドラッグして移動させるための小さなウィンドウです。
/// 枠線本体（CaptureRegionBorderWindow）は常にクリックスルーのままにし、
/// このつまみだけが唯一のドラッグ開始点になります。
/// 自分自身は BitBlt によるキャプチャに映り込まないよう WDA_EXCLUDEFROMCAPTURE を設定します。
/// </summary>
public partial class CaptureRegionMoveHandleWindow : Window
{
    private const int HandleSize = 32;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopMost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    private readonly nint _hwnd;
    private WindowBounds _currentRegion;
    private bool _isDragging;
    private NativePoint _dragStartCursor;
    private WindowBounds _dragStartRegion;

    /// <summary>
    /// ドラッグ中、マウスが動くたびに呼ばれます。枠線側（CaptureRegionBorderWindow.ApplyRegion）を
    /// 同じ範囲へ同時に動かしてもらうためのコールバックです。MainWindow.xaml.cs が設定します。
    /// </summary>
    public Action<WindowBounds>? LiveRegionUpdate { get; set; }

    /// <summary>ドラッグが終わったとき、範囲のサイズは保ったまま最終的な位置（物理ピクセル）を通知します。</summary>
    public event EventHandler<WindowBounds>? RegionMoved;

    public CaptureRegionMoveHandleWindow()
    {
        InitializeComponent();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Windows 10 2004 未満では失敗しますが、その場合は諦めて表示だけ続けます（例外は投げません）。
        SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
    }

    /// <summary>キャプチャ範囲の中央につまみを配置します（ドラッグ中は代わりに ApplyRegion が直接呼ばれます）。</summary>
    public void ApplyRegion(WindowBounds region)
    {
        _currentRegion = region;

        int centerX = region.Left + region.Width / 2;
        int centerY = region.Top + region.Height / 2;
        SetWindowPos(_hwnd, HwndTopMost, centerX - HandleSize / 2, centerY - HandleSize / 2, 0, 0, SwpNoSize | SwpNoActivate);
    }

    private void OnHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out _dragStartCursor);
        _dragStartRegion = _currentRegion;
        _isDragging = true;
        HandleBorder.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// マウス移動のたびに、カーソルの物理ピクセル移動量ぶんだけ範囲全体をずらし、
    /// 自分自身（つまみ）と枠線側（LiveRegionUpdate 経由）を同時に追従させます。
    /// WPF の相対座標ではなく GetCursorPos（スクリーン座標）を使うのは、このアプリの座標系が
    /// 一貫して物理ピクセルであるため（DPI 変換をここでも避けられます）。
    /// </summary>
    private void OnHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        GetCursorPos(out NativePoint cursor);
        int deltaX = cursor.X - _dragStartCursor.X;
        int deltaY = cursor.Y - _dragStartCursor.Y;

        var newRegion = new WindowBounds(
            _dragStartRegion.Left + deltaX,
            _dragStartRegion.Top + deltaY,
            _dragStartRegion.Width,
            _dragStartRegion.Height);

        ApplyRegion(newRegion);
        LiveRegionUpdate?.Invoke(newRegion);
    }

    private void OnHandleMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        HandleBorder.ReleaseMouseCapture();
        RegionMoved?.Invoke(this, _currentRegion);
    }
}
