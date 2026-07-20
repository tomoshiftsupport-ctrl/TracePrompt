using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TracePrompt.Models;

namespace TracePrompt.Views;

/// <summary>
/// 仮想スクリーン全体を覆う半透明のオーバーレイを出し、ドラッグで矩形範囲を選ばせるウィンドウです。
/// 「自由クリップ」モードのキャプチャ範囲指定に使います。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private const double MinSelectionSizeDip = 10;

    private Point? _dragStartDip;

    public RegionSelectorWindow()
    {
        InitializeComponent();

        // WindowState="Maximized" は現在のモニターしか覆わないため、仮想スクリーン全体を明示的に指定します。
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    /// <summary>選択が確定した範囲（物理ピクセル、仮想スクリーン座標）。キャンセル時は null。</summary>
    public WindowBounds? SelectedRegion { get; private set; }

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartDip = e.GetPosition(RootCanvas);
        RootCanvas.CaptureMouse();

        SelectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRectangle, _dragStartDip.Value.X);
        Canvas.SetTop(SelectionRectangle, _dragStartDip.Value.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        SizeLabel.Visibility = Visibility.Visible;

        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartDip is not { } start) return;

        Point current = e.GetPosition(RootCanvas);
        double left = Math.Min(start.X, current.X);
        double top = Math.Min(start.Y, current.Y);
        double width = Math.Abs(current.X - start.X);
        double height = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;

        SizeLabel.Text = $"{(int)width} × {(int)height}";
        Canvas.SetLeft(SizeLabel, left);
        Canvas.SetTop(SizeLabel, Math.Max(0, top - 20));
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStartDip is not { } start)
        {
            return;
        }

        RootCanvas.ReleaseMouseCapture();
        _dragStartDip = null;

        Point current = e.GetPosition(RootCanvas);
        double leftDip = Math.Min(start.X, current.X);
        double topDip = Math.Min(start.Y, current.Y);
        double widthDip = Math.Abs(current.X - start.X);
        double heightDip = Math.Abs(current.Y - start.Y);

        if (widthDip < MinSelectionSizeDip || heightDip < MinSelectionSizeDip)
        {
            // 選択が小さすぎる（誤クリック等）: やり直せるよう、選択表示だけ消して継続します。
            SelectionRectangle.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        // Canvas はウィンドウ左上が原点なので、ウィンドウの画面上の位置を足して仮想スクリーン座標にします。
        SelectedRegion = ConvertDipRectToPixelRegion(Left + leftDip, Top + topDip, widthDip, heightDip);
        DialogResult = true;
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SelectedRegion = null;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// DIP（デバイス非依存単位）の矩形を、実際のキャプチャで使う物理ピクセル座標へ変換します。
    /// DPI スケーリングが100%でない環境でも、ドラッグで見た目どおりの範囲を撮れるようにするためです。
    /// </summary>
    private WindowBounds ConvertDipRectToPixelRegion(double leftDip, double topDip, double widthDip, double heightDip)
    {
        Matrix transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
            ?? Matrix.Identity;

        Point topLeft = transform.Transform(new Point(leftDip, topDip));
        Point bottomRight = transform.Transform(new Point(leftDip + widthDip, topDip + heightDip));

        int left = (int)Math.Round(topLeft.X);
        int top = (int)Math.Round(topLeft.Y);
        int width = (int)Math.Round(bottomRight.X - topLeft.X);
        int height = (int)Math.Round(bottomRight.Y - topLeft.Y);

        return new WindowBounds(left, top, width, height);
    }
}
