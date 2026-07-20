using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TracePrompt.Views;

/// <summary>
/// 1枚または複数枚の画像を開き、赤ペン（InkCanvas）で書き込みができるウィンドウです。
/// 元のファイルへ上書き保存（Ctrl+S）、合成結果のクリップボードコピー（Ctrl+C）、
/// 元に戻す・やり直し（Ctrl+Z / Ctrl+Y）、ズーム（Ctrl+ +/- で段階変更、Ctrl+0 でフィット表示に戻す、
/// Ctrl+ホイールでも変更可）に対応します。複数枚のときは ← / → キーで前後の画像へ移動でき、
/// 未保存の変更があれば移動前・終了前に保存するか確認します。Escape キーでウィンドウを閉じます。
/// </summary>
public partial class ImageAnnotatorWindow : Window
{
    private const double PenThicknessOnFullImage = 3.5;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 0.25;

    private readonly IReadOnlyList<string> _imagePaths;
    private string _imagePath;
    private int _imageIndex;
    private readonly string? _windowTitleBase;
    private readonly Stack<Stroke> _redoStack = new();
    private Stroke[]? _clearedStrokesForUndo;
    private BitmapSource? _sourceBitmap;
    private double _imageDipW;
    private double _imageDipH;
    /// <summary>キャンバスに収まる自動フィット倍率です。</summary>
    private double _fitZoom = 1.0;
    /// <summary>実際に表示へ適用されている倍率です。Ctrl+0 でフィット倍率に戻ります。</summary>
    private double _viewZoom = 1.0;
    private bool _userChangedZoom;
    private bool _isDirty;
    private bool _suppressStrokeEvents;
    private bool _saved;
    private bool _layoutReady;

    public ImageAnnotatorWindow(string imagePath, string? windowTitle = null)
    : this(new[] { imagePath }, 0, windowTitle)
{
}

public ImageAnnotatorWindow(IReadOnlyList<string> imagePaths, int initialIndex, string? windowTitle = null)
{
    InitializeComponent();

    if (imagePaths is null)
    {
        throw new ArgumentNullException(nameof(imagePaths));
    }

    string[] normalizedPaths = imagePaths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .ToArray();
    if (normalizedPaths.Length == 0)
    {
        throw new ArgumentException("At least one image path is required.", nameof(imagePaths));
    }

    _imagePaths = normalizedPaths;
    _imageIndex = Math.Clamp(initialIndex, 0, _imagePaths.Count - 1);
    _imagePath = _imagePaths[_imageIndex];
    _windowTitleBase = windowTitle;
    if (!string.IsNullOrWhiteSpace(windowTitle))
    {
        Title = windowTitle;
    }

    ConfigureRedPen();
    Loaded += OnLoaded;
    Closing += OnClosing;
}

public bool Saved => _saved;

private bool HasMultipleImages => _imagePaths.Count > 1;

private void UpdateNavigationUi()
{
    Visibility navigationVisibility = HasMultipleImages ? Visibility.Visible : Visibility.Collapsed;
    ImageNavigationPanel.Visibility = navigationVisibility;
    PrevImageButton.Visibility = navigationVisibility;
    NextImageButton.Visibility = navigationVisibility;
    PrevImageButton.IsEnabled = _imageIndex > 0;
    NextImageButton.IsEnabled = _imageIndex < _imagePaths.Count - 1;
    ImageIndexText.Text = HasMultipleImages ? $"{_imageIndex + 1} / {_imagePaths.Count}" : string.Empty;

    string title = _windowTitleBase ?? "Image Annotator";
    Title = HasMultipleImages ? $"{title} ({_imageIndex + 1}/{_imagePaths.Count})" : title;
}

private void ResetEditorState()
{
    _layoutReady = false;
    _redoStack.Clear();
    _clearedStrokesForUndo = null;
    _isDirty = false;
    _suppressStrokeEvents = true;
    InkSurface.Strokes.Clear();
    _suppressStrokeEvents = false;
    UpdateUndoRedoButtons();
}

private void ApplyLoadedImageState()
{
    ApplyCanvasNativeSize();
    ComputeFitZoom();
    _userChangedZoom = false;
    SetViewZoom(_fitZoom, markUserChanged: false);
    _layoutReady = true;
    UpdateNavigationUi();
    UpdateUndoRedoButtons();
}

private bool ConfirmDiscardOrSaveChanges()
{
    if (!_isDirty)
    {
        return true;
    }

    MessageBoxResult result = MessageBox.Show(
        this,
        "記入内容が保存されていません。保存しますか？",
        "AIヘルプキャプチャ",
        MessageBoxButton.YesNoCancel,
        MessageBoxImage.Question);

    if (result == MessageBoxResult.Cancel)
    {
        return false;
    }

    if (result == MessageBoxResult.Yes)
    {
        return TrySave();
    }

    _isDirty = false;
    return true;
}

private void NavigateToImage(int newIndex)
{
    if (!HasMultipleImages || newIndex < 0 || newIndex >= _imagePaths.Count || newIndex == _imageIndex)
    {
        return;
    }

    if (!ConfirmDiscardOrSaveChanges())
    {
        return;
    }

    _imageIndex = newIndex;
    _imagePath = _imagePaths[_imageIndex];
    ResetEditorState();
    if (!TryLoadImage())
    {
        MessageBox.Show(this, "画像を読み込めませんでした。", "AIヘルプキャプチャ", MessageBoxButton.OK, MessageBoxImage.Warning);
        Close();
        return;
    }

    ApplyLoadedImageState();
    StatusText.Text = "画像を切り替えました";
    Focus();
}

private void OnPrevImageClick(object sender, RoutedEventArgs e) => NavigateToImage(_imageIndex - 1);

private void OnNextImageClick(object sender, RoutedEventArgs e) => NavigateToImage(_imageIndex + 1);

    private void ConfigureRedPen()
    {
        InkSurface.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromRgb(0xDC, 0x26, 0x26),
            Width = PenThicknessOnFullImage,
            Height = PenThicknessOnFullImage,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse,
            IsHighlighter = false
        };
        InkSurface.EditingMode = InkCanvasEditingMode.Ink;
        InkSurface.UseCustomCursor = true;
        InkSurface.Cursor = Cursors.Pen;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
{
    Loaded -= OnLoaded;
    ResetEditorState();
    if (!TryLoadImage())
    {
        MessageBox.Show(
            this,
            "画像を読み込めませんでした。",
            "AIヘルプキャプチャ",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        DialogResult = false;
        Close();
        return;
    }

    ExpandWindowToWorkArea();
    UpdateLayout();
    ApplyLoadedImageState();
    Focus();
}

    private bool TryLoadImage()
    {
        try
        {
            if (!File.Exists(_imagePath))
            {
                return false;
            }

            byte[] bytes = File.ReadAllBytes(_imagePath);
            if (bytes.Length == 0)
            {
                return false;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            _sourceBitmap = image;

            int pixelW = image.PixelWidth;
            int pixelH = image.PixelHeight;
            if (pixelW <= 0 || pixelH <= 0)
            {
                return false;
            }

            _imageDipW = pixelW * 96.0 / image.DpiX;
            _imageDipH = pixelH * 96.0 / image.DpiY;
            if (_imageDipW < 1 || _imageDipH < 1)
            {
                return false;
            }

            BackgroundImage.Source = image;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ExpandWindowToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        double targetW = work.Width * 0.96;
        double targetH = work.Height * 0.94;
        Width = Math.Max(MinWidth, targetW);
        Height = Math.Max(MinHeight, targetH);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    /// <summary>キャンバス・背景画像・インク面のサイズを、画像の実ピクセルサイズ（DIP換算）に合わせます。</summary>
    private void ApplyCanvasNativeSize()
    {
        ComposeRoot.Width = _imageDipW;
        ComposeRoot.Height = _imageDipH;
        BackgroundImage.Width = _imageDipW;
        BackgroundImage.Height = _imageDipH;
        InkSurface.Width = _imageDipW;
        InkSurface.Height = _imageDipH;
        InkSurface.DefaultDrawingAttributes.Width = PenThicknessOnFullImage;
        InkSurface.DefaultDrawingAttributes.Height = PenThicknessOnFullImage;
    }

    private void OnCanvasViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_layoutReady || _sourceBitmap is null)
        {
            return;
        }

        ComputeFitZoom();
        // ユーザーが手動でズームを変更していなければ、リサイズのたびに自動でフィット倍率へ合わせ直します。
        if (!_userChangedZoom)
        {
            SetViewZoom(_fitZoom, markUserChanged: false);
        }
        else
        {
            UpdateZoomPercentLabel();
        }
    }

    private void ComputeFitZoom()
    {
        if (_imageDipW < 1 || _imageDipH < 1)
        {
            _fitZoom = 1.0;
            return;
        }

        double availW = CanvasScrollViewer.ViewportWidth;
        double availH = CanvasScrollViewer.ViewportHeight;
        if (availW < 8 || availH < 8)
        {
            availW = Math.Max(8, ActualWidth - 40);
            availH = Math.Max(8, ActualHeight - 150);
        }

        const double padding = 8;
        availW = Math.Max(8, availW - padding);
        availH = Math.Max(8, availH - padding);

        double scale = Math.Min(availW / _imageDipW, availH / _imageDipH);
        _fitZoom = Math.Clamp(scale, MinZoom, 1.0);
    }

    private void SetViewZoom(double zoom, bool markUserChanged)
    {
        double clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        // ユーザー操作によるズームはステップ単位に丸めて範囲内に収めます。
        if (markUserChanged)
        {
            clamped = Math.Round(clamped / ZoomStep) * ZoomStep;
            clamped = Math.Clamp(clamped, MinZoom, MaxZoom);
        }

        _viewZoom = clamped;
        if (markUserChanged)
        {
            _userChangedZoom = true;
        }

        ViewScaleTransform.ScaleX = _viewZoom;
        ViewScaleTransform.ScaleY = _viewZoom;
        UpdateZoomPercentLabel();
    }

    private void UpdateZoomPercentLabel()
    {
        ZoomPercentButton.Content = $"{_viewZoom * 100:0}%";
    }

    private void ZoomBySteps(int steps)
    {
        SetViewZoom(_viewZoom + steps * ZoomStep, markUserChanged: true);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomBySteps(+1);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomBySteps(-1);

    private void OnZoomResetClick(object sender, RoutedEventArgs e)
    {
        ComputeFitZoom();
        _userChangedZoom = false;
        SetViewZoom(_fitZoom, markUserChanged: false);
    }

    private void OnCanvasPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        ZoomBySteps(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_suppressStrokeEvents)
        {
            return;
        }

        _redoStack.Clear();
        _clearedStrokesForUndo = null;
        _isDirty = true;
        StatusText.Text = "未保存の変更があります";
        UpdateUndoRedoButtons();
    }

    private void OnStrokeErasing(object sender, InkCanvasStrokeErasingEventArgs e)
    {
        _isDirty = true;
        StatusText.Text = "未保存の変更があります";
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (InkSurface.Strokes.Count == 0)
        {
            return;
        }

        _clearedStrokesForUndo = InkSurface.Strokes.ToArray();
        _redoStack.Clear();

        _suppressStrokeEvents = true;
        InkSurface.Strokes.Clear();
        _suppressStrokeEvents = false;
        _isDirty = true;
        StatusText.Text = "線をすべて消しました（Ctrl+Z で復元）";
        UpdateUndoRedoButtons();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (TrySave())
        {
            StatusText.Text = "保存しました";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyImageClick(object sender, RoutedEventArgs e) => CopyImageToClipboard();

    /// <summary>合成した画像（赤ペン記入を含む）をクリップボードへコピーします。</summary>
    private void CopyImageToClipboard()
    {
        BitmapSource? composed = BuildComposedBitmap();
        if (composed is null)
        {
            StatusText.Text = "コピーできる画像がありません";
            return;
        }

        try
        {
            Clipboard.SetImage(composed);
            StatusText.Text = "画像をコピーしました（赤ペン記入を含む）";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"コピーに失敗しました: {ex.Message}";
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
{
    Key key = e.Key == Key.System ? e.SystemKey : e.Key;

    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
    {
        if (key == Key.Y || (key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (key == Key.S)
        {
            if (TrySave())
            {
                StatusText.Text = "保存しました";
            }

            e.Handled = true;
            return;
        }

        if (key == Key.C)
        {
            CopyImageToClipboard();
            e.Handled = true;
            return;
        }

        if (key is Key.OemPlus or Key.Add)
        {
            ZoomBySteps(+1);
            e.Handled = true;
            return;
        }

        if (key is Key.OemMinus or Key.Subtract)
        {
            ZoomBySteps(-1);
            e.Handled = true;
            return;
        }

        if (key is Key.D0 or Key.NumPad0)
        {
            OnZoomResetClick(sender, e);
            e.Handled = true;
        }

        return;
    }

    if (Keyboard.Modifiers == ModifierKeys.None)
    {
        if (key == Key.Left)
        {
            NavigateToImage(_imageIndex - 1);
            e.Handled = true;
            return;
        }

        if (key == Key.Right)
        {
            NavigateToImage(_imageIndex + 1);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}

    private void Undo()
    {
        if (InkSurface.Strokes.Count == 0 && _clearedStrokesForUndo is { Length: > 0 } cleared)
        {
            _suppressStrokeEvents = true;
            foreach (Stroke stroke in cleared)
            {
                InkSurface.Strokes.Add(stroke);
            }
            _suppressStrokeEvents = false;
            _clearedStrokesForUndo = null;
            _isDirty = true;
            StatusText.Text = "未保存の変更があります";
            UpdateUndoRedoButtons();
            return;
        }

        if (InkSurface.Strokes.Count == 0)
        {
            return;
        }

        Stroke last = InkSurface.Strokes[^1];
        _suppressStrokeEvents = true;
        InkSurface.Strokes.Remove(last);
        _suppressStrokeEvents = false;
        _redoStack.Push(last);
        _clearedStrokesForUndo = null;
        _isDirty = true;
        StatusText.Text = "未保存の変更があります";
        UpdateUndoRedoButtons();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        Stroke stroke = _redoStack.Pop();
        _suppressStrokeEvents = true;
        InkSurface.Strokes.Add(stroke);
        _suppressStrokeEvents = false;
        _clearedStrokesForUndo = null;
        _isDirty = true;
        StatusText.Text = "未保存の変更があります";
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        UndoButton.IsEnabled = InkSurface.Strokes.Count > 0
                               || (_clearedStrokesForUndo is { Length: > 0 });
        RedoButton.IsEnabled = _redoStack.Count > 0;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
{
    if (!ConfirmDiscardOrSaveChanges())
    {
        e.Cancel = true;
    }
}

    /// <summary>背景画像に赤ペン記入（あれば）を焼き込んだ合成ビットマップを作ります。ファイルには触れません。</summary>
    private RenderTargetBitmap? BuildComposedBitmap()
    {
        if (_sourceBitmap is null)
        {
            return null;
        }

        int pixelW = _sourceBitmap.PixelWidth;
        int pixelH = _sourceBitmap.PixelHeight;
        if (pixelW <= 0 || pixelH <= 0)
        {
            return null;
        }

        // レイアウトがまだ整っていない場合は ComposeRoot のサイズをフォールバックにし、画像のピクセルサイズとの比率でインクの座標をスケールします。
        double dipW = _imageDipW > 1 ? _imageDipW : ComposeRoot.Width;
        double dipH = _imageDipH > 1 ? _imageDipH : ComposeRoot.Height;

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawImage(_sourceBitmap, new Rect(0, 0, pixelW, pixelH));
            if (InkSurface.Strokes.Count > 0)
            {
                double scaleX = pixelW / dipW;
                double scaleY = pixelH / dipH;
                dc.PushTransform(new ScaleTransform(scaleX, scaleY));
                InkSurface.Strokes.Draw(dc);
                dc.Pop();
            }
        }

        var rtb = new RenderTargetBitmap(
            pixelW,
            pixelH,
            _sourceBitmap.DpiX,
            _sourceBitmap.DpiY,
            PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private bool TrySave()
    {
        RenderTargetBitmap? rtb = BuildComposedBitmap();
        if (rtb is null)
        {
            return false;
        }

        try
        {
            // 拡張子が .jpg/.jpeg のときは JPEG（高画質圧縮）、それ以外は PNG として保存します。
            // 一時ファイルに書き出してから元のファイルへ差し替えることで、保存中の破損を防ぎます。
            BitmapEncoder encoder = _imagePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || _imagePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 90 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            string? dir = Path.GetDirectoryName(_imagePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = _imagePath + ".annot.tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(fs);
            }

            File.Copy(tempPath, _imagePath, overwrite: true);
            TryDelete(tempPath);

            _isDirty = false;
            _saved = true;
            _sourceBitmap = rtb;

            _suppressStrokeEvents = true;
            InkSurface.Strokes.Clear();
            _suppressStrokeEvents = false;
            _redoStack.Clear();
            _clearedStrokesForUndo = null;
            BackgroundImage.Source = rtb;
            UpdateUndoRedoButtons();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"保存に失敗しました。\n{ex.Message}",
                "AIヘルプキャプチャ",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}
