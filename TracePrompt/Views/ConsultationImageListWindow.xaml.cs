using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TracePrompt.ViewModels;

namespace TracePrompt.Views;

/// <summary>
/// 「選択画像を一覧化」用の別窓です。サムネイル一覧から任意でコマを選び、
/// 結合してコピー、または結合せずコピーします（選択はオプション。既定は全コマ対象）。
/// 上部のサムネイル一覧をクリックすると拡大ビューアーが開き、前後の画像への移動・選択チェックに加えて、
/// 別窓を挟まずこの画面のまま赤ペン記入・拡大縮小・保存ができます。
/// 下部の「コピー結果の確認」欄は選択が不要なため、拡大ビューアーを経由せず
/// 直接「赤ペン記入」画面（ImageAnnotatorWindow）を開きます。複数画像がコピーされていた場合は、
/// その画面内で ← / → キーや前後ボタンによりコピーされた画像間を移動できます。
/// </summary>
public partial class ConsultationImageListWindow : Window
{
    private const double PenThicknessOnFullImage = 3.5;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 0.25;

    /// <summary>拡大ビューアーの1件分。サムネイル一覧由来のときだけ選択チェックを表示します。</summary>
    private sealed class ViewerItem
    {
        public required string Path { get; init; }
        public required string Label { get; init; }
        public CaptureFrameThumbnailViewModel? SelectionSource { get; init; }
    }

    private List<ViewerItem> _viewerItems = new();
    private int _viewerIndex = -1;
    private bool _suppressViewerCheckBoxEvent;

    // ---- 拡大ビューアー内の赤ペン・拡大縮小状態 ----
    private readonly Stack<Stroke> _viewerRedoStack = new();
    private Stroke[]? _viewerClearedStrokesForUndo;
    private BitmapSource? _viewerSourceBitmap;
    private double _viewerImageDipW;
    private double _viewerImageDipH;
    private double _viewerFitZoom = 1.0;
    private double _viewerViewZoom = 1.0;
    private bool _viewerUserChangedZoom;
    private bool _viewerIsDirty;
    private bool _viewerSuppressStrokeEvents;
    private bool _viewerLayoutReady;

    public ConsultationImageListWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ConfigureViewerRedPen();
        Closing += OnWindowClosing;
    }

    private void ConfigureViewerRedPen()
    {
        ViewerInkSurface.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromRgb(0xDC, 0x26, 0x26),
            Width = PenThicknessOnFullImage,
            Height = PenThicknessOnFullImage,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse,
            IsHighlighter = false
        };
        ViewerInkSurface.EditingMode = InkCanvasEditingMode.Ink;
        ViewerInkSurface.UseCustomCursor = true;
        ViewerInkSurface.Cursor = Cursors.Pen;
    }

    private void OnThumbnailImageClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not CaptureFrameThumbnailViewModel vm)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!ConfirmDiscardOrSaveViewerChanges())
        {
            return;
        }

        _viewerItems = viewModel.ThumbnailFrames
            .Select(t => new ViewerItem { Path = t.Frame.ScreenshotPath, Label = t.DisplayLabel, SelectionSource = t })
            .ToList();
        _viewerIndex = viewModel.ThumbnailFrames.IndexOf(vm);
        ShowViewer();
        e.Handled = true;
    }

    /// <summary>コピー結果の確認欄: 単一画像をクリックしたとき（実ファイルが分かる場合のみ開けます）。</summary>
    private void OnClipboardImageClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        string? path = viewModel.ClipboardPreviewImageSourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        OpenAnnotatorForClipboardResult(new[] { path }, 0);
        e.Handled = true;
    }

    /// <summary>
    /// コピー結果の確認欄: 複数ファイルのサムネイルをクリックしたとき。直接「赤ペン記入」画面を開きます。
    /// コピーされた全ファイルを渡すため、開いた画面内で ← / → キーによりコピーされた画像間を移動できます。
    /// </summary>
    private void OnClipboardFileThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ClipboardPreviewFileEntry entry)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        List<string> paths = viewModel.ClipboardPreviewFileThumbnails
            .Select(t => t.Path)
            .Where(File.Exists)
            .ToList();

        int initialIndex = paths.IndexOf(entry.Path);
        if (initialIndex < 0)
        {
            paths = new List<string> { entry.Path };
            initialIndex = 0;
        }

        OpenAnnotatorForClipboardResult(paths, initialIndex);
        e.Handled = true;
    }

    /// <summary>
    /// コピー結果の画像を、間に拡大ビューアーを挟まず直接「赤ペン記入」画面で開きます。
    /// 複数枚渡した場合は、その画面内で前後の画像へ移動できます（ImageAnnotatorWindow 側の機能）。
    /// </summary>
    private void OpenAnnotatorForClipboardResult(IReadOnlyList<string> paths, int initialIndex)
    {
        var window = new ImageAnnotatorWindow(paths, initialIndex, "コピーした画像 — 赤ペン記入")
        {
            Owner = this
        };

        window.ShowDialog();

        if (window.Saved && DataContext is MainViewModel viewModel)
        {
            viewModel.RefreshClipboardPreviewCommand.Execute(null);
        }
    }

    private void ShowViewer()
    {
        if (_viewerIndex < 0 || _viewerIndex >= _viewerItems.Count)
        {
            return;
        }

        ViewerItem current = _viewerItems[_viewerIndex];

        bool supportsSelection = current.SelectionSource is not null;
        ViewerCheckBox.Visibility = supportsSelection ? Visibility.Visible : Visibility.Collapsed;
        if (supportsSelection)
        {
            _suppressViewerCheckBoxEvent = true;
            ViewerCheckBox.IsChecked = current.SelectionSource!.IsSelected;
            _suppressViewerCheckBoxEvent = false;
        }

        ViewerLabel.Text = $"{_viewerIndex + 1} / {_viewerItems.Count}　{current.Label}";
        LoadViewerImage(current.Path);
        ViewerPrevButton.IsEnabled = _viewerIndex > 0;
        ViewerNextButton.IsEnabled = _viewerIndex < _viewerItems.Count - 1;
        ViewerOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>画像を読み込み、赤ペン・拡大縮小の状態を初期化します（サムネイル一覧の LoadThumbnail と同じくファイル非ロック）。</summary>
    private void LoadViewerImage(string path)
    {
        _viewerLayoutReady = false;
        _viewerRedoStack.Clear();
        _viewerClearedStrokesForUndo = null;
        _viewerIsDirty = false;
        _viewerSuppressStrokeEvents = true;
        ViewerInkSurface.Strokes.Clear();
        _viewerSuppressStrokeEvents = false;

        BitmapImage? image = LoadFullImage(path);
        _viewerSourceBitmap = image;
        ViewerBackgroundImage.Source = image;

        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            _viewerImageDipW = 0;
            _viewerImageDipH = 0;
            UpdateViewerUndoRedoButtons();
            return;
        }

        _viewerImageDipW = image.PixelWidth * 96.0 / image.DpiX;
        _viewerImageDipH = image.PixelHeight * 96.0 / image.DpiY;

        ViewerComposeRoot.Width = _viewerImageDipW;
        ViewerComposeRoot.Height = _viewerImageDipH;
        ViewerBackgroundImage.Width = _viewerImageDipW;
        ViewerBackgroundImage.Height = _viewerImageDipH;
        ViewerInkSurface.Width = _viewerImageDipW;
        ViewerInkSurface.Height = _viewerImageDipH;
        ViewerInkSurface.DefaultDrawingAttributes.Width = PenThicknessOnFullImage;
        ViewerInkSurface.DefaultDrawingAttributes.Height = PenThicknessOnFullImage;

        _viewerLayoutReady = true;
        ComputeViewerFitZoom();
        _viewerUserChangedZoom = false;
        SetViewerZoom(_viewerFitZoom, markUserChanged: false);
        UpdateViewerUndoRedoButtons();
    }

    private void OnViewerCheckBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressViewerCheckBoxEvent) return;
        if (_viewerIndex < 0 || _viewerIndex >= _viewerItems.Count) return;

        CaptureFrameThumbnailViewModel? source = _viewerItems[_viewerIndex].SelectionSource;
        if (source is null) return;

        source.IsSelected = ViewerCheckBox.IsChecked == true;
    }

    private void OnViewerPrevClick(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex <= 0) return;
        if (!ConfirmDiscardOrSaveViewerChanges()) return;
        _viewerIndex--;
        ShowViewer();
    }

    private void OnViewerNextClick(object sender, RoutedEventArgs e)
    {
        if (_viewerIndex >= _viewerItems.Count - 1) return;
        if (!ConfirmDiscardOrSaveViewerChanges()) return;
        _viewerIndex++;
        ShowViewer();
    }

    private void OnViewerCloseClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardOrSaveViewerChanges()) return;
        CloseViewer();
    }

    private void CloseViewer()
    {
        ViewerOverlay.Visibility = Visibility.Collapsed;
        ViewerBackgroundImage.Source = null;
        _viewerSuppressStrokeEvents = true;
        ViewerInkSurface.Strokes.Clear();
        _viewerSuppressStrokeEvents = false;
        _viewerSourceBitmap = null;
        _viewerIsDirty = false;
        _viewerIndex = -1;
    }

    /// <summary>未保存の赤ペン記入があれば、保存するか確認します。Cancel が選ばれたら false（処理を中止）。</summary>
    private bool ConfirmDiscardOrSaveViewerChanges()
    {
        if (!_viewerIsDirty)
        {
            return true;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            "変更が保存されていません。保存しますか？",
            "AIヘルプキャプチャ",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            return TrySaveViewerImage();
        }

        // No: 破棄して続行
        _viewerIsDirty = false;
        return true;
    }

    // ---- 拡大縮小 ----

    private void ComputeViewerFitZoom()
    {
        if (_viewerImageDipW < 1 || _viewerImageDipH < 1)
        {
            _viewerFitZoom = 1.0;
            return;
        }

        double availW = ViewerCanvasScrollViewer.ViewportWidth;
        double availH = ViewerCanvasScrollViewer.ViewportHeight;
        if (availW < 8 || availH < 8)
        {
            availW = Math.Max(8, ViewerCanvasScrollViewer.ActualWidth - 20);
            availH = Math.Max(8, ViewerCanvasScrollViewer.ActualHeight - 20);
        }

        const double padding = 8;
        availW = Math.Max(8, availW - padding);
        availH = Math.Max(8, availH - padding);

        double scale = Math.Min(availW / _viewerImageDipW, availH / _viewerImageDipH);
        _viewerFitZoom = Math.Clamp(scale, MinZoom, 1.0);
    }

    private void SetViewerZoom(double zoom, bool markUserChanged)
    {
        double clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (markUserChanged)
        {
            clamped = Math.Round(clamped / ZoomStep) * ZoomStep;
            clamped = Math.Clamp(clamped, MinZoom, MaxZoom);
        }

        _viewerViewZoom = clamped;
        if (markUserChanged)
        {
            _viewerUserChangedZoom = true;
        }

        ViewerScaleTransform.ScaleX = _viewerViewZoom;
        ViewerScaleTransform.ScaleY = _viewerViewZoom;
        ViewerZoomPercentButton.Content = $"{_viewerViewZoom * 100:0}%";
    }

    private void ZoomViewerBySteps(int steps) => SetViewerZoom(_viewerViewZoom + steps * ZoomStep, markUserChanged: true);

    private void OnViewerZoomInClick(object sender, RoutedEventArgs e) => ZoomViewerBySteps(+1);

    private void OnViewerZoomOutClick(object sender, RoutedEventArgs e) => ZoomViewerBySteps(-1);

    private void OnViewerZoomResetClick(object sender, RoutedEventArgs e)
    {
        ComputeViewerFitZoom();
        _viewerUserChangedZoom = false;
        SetViewerZoom(_viewerFitZoom, markUserChanged: false);
    }

    private void OnViewerCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_viewerLayoutReady || _viewerSourceBitmap is null)
        {
            return;
        }

        ComputeViewerFitZoom();
        if (!_viewerUserChangedZoom)
        {
            SetViewerZoom(_viewerFitZoom, markUserChanged: false);
        }
    }

    private void OnViewerCanvasPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        ZoomViewerBySteps(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    // ---- 赤ペン: 描画・元に戻す・やり直す・消す ----

    private void OnViewerStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_viewerSuppressStrokeEvents) return;
        _viewerRedoStack.Clear();
        _viewerClearedStrokesForUndo = null;
        _viewerIsDirty = true;
        UpdateViewerUndoRedoButtons();
    }

    private void OnViewerStrokeErasing(object sender, InkCanvasStrokeErasingEventArgs e)
    {
        _viewerIsDirty = true;
    }

    private void OnViewerUndoClick(object sender, RoutedEventArgs e) => UndoViewerStroke();

    private void OnViewerRedoClick(object sender, RoutedEventArgs e) => RedoViewerStroke();

    private void OnViewerClearClick(object sender, RoutedEventArgs e)
    {
        if (ViewerInkSurface.Strokes.Count == 0) return;

        _viewerClearedStrokesForUndo = ViewerInkSurface.Strokes.ToArray();
        _viewerRedoStack.Clear();

        _viewerSuppressStrokeEvents = true;
        ViewerInkSurface.Strokes.Clear();
        _viewerSuppressStrokeEvents = false;
        _viewerIsDirty = true;
        UpdateViewerUndoRedoButtons();
    }

    private void UndoViewerStroke()
    {
        if (ViewerInkSurface.Strokes.Count == 0 && _viewerClearedStrokesForUndo is { Length: > 0 } cleared)
        {
            _viewerSuppressStrokeEvents = true;
            foreach (Stroke stroke in cleared)
            {
                ViewerInkSurface.Strokes.Add(stroke);
            }
            _viewerSuppressStrokeEvents = false;
            _viewerClearedStrokesForUndo = null;
            _viewerIsDirty = true;
            UpdateViewerUndoRedoButtons();
            return;
        }

        if (ViewerInkSurface.Strokes.Count == 0) return;

        Stroke last = ViewerInkSurface.Strokes[^1];
        _viewerSuppressStrokeEvents = true;
        ViewerInkSurface.Strokes.Remove(last);
        _viewerSuppressStrokeEvents = false;
        _viewerRedoStack.Push(last);
        _viewerClearedStrokesForUndo = null;
        _viewerIsDirty = true;
        UpdateViewerUndoRedoButtons();
    }

    private void RedoViewerStroke()
    {
        if (_viewerRedoStack.Count == 0) return;

        Stroke stroke = _viewerRedoStack.Pop();
        _viewerSuppressStrokeEvents = true;
        ViewerInkSurface.Strokes.Add(stroke);
        _viewerSuppressStrokeEvents = false;
        _viewerClearedStrokesForUndo = null;
        _viewerIsDirty = true;
        UpdateViewerUndoRedoButtons();
    }

    private void UpdateViewerUndoRedoButtons()
    {
        ViewerUndoButton.IsEnabled = ViewerInkSurface.Strokes.Count > 0
                                      || (_viewerClearedStrokesForUndo is { Length: > 0 });
        ViewerRedoButton.IsEnabled = _viewerRedoStack.Count > 0;
    }

    // ---- 保存・コピー ----

    private void OnViewerSaveClick(object sender, RoutedEventArgs e) => TrySaveViewerImage();

    /// <summary>背景画像に赤ペン記入（あれば）を焼き込んだ合成ビットマップを作ります。ファイルには触れません。</summary>
    private BitmapSource? BuildComposedViewerBitmap()
    {
        if (_viewerSourceBitmap is null)
        {
            return null;
        }

        int pixelW = _viewerSourceBitmap.PixelWidth;
        int pixelH = _viewerSourceBitmap.PixelHeight;
        if (pixelW <= 0 || pixelH <= 0)
        {
            return null;
        }

        double dipW = _viewerImageDipW > 1 ? _viewerImageDipW : ViewerComposeRoot.Width;
        double dipH = _viewerImageDipH > 1 ? _viewerImageDipH : ViewerComposeRoot.Height;

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawImage(_viewerSourceBitmap, new Rect(0, 0, pixelW, pixelH));
            if (ViewerInkSurface.Strokes.Count > 0)
            {
                double scaleX = pixelW / dipW;
                double scaleY = pixelH / dipH;
                dc.PushTransform(new ScaleTransform(scaleX, scaleY));
                ViewerInkSurface.Strokes.Draw(dc);
                dc.Pop();
            }
        }

        var rtb = new RenderTargetBitmap(pixelW, pixelH, _viewerSourceBitmap.DpiX, _viewerSourceBitmap.DpiY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>今表示中の画像（赤ペン記入込み）を、元のファイルへ上書き保存します。</summary>
    private bool TrySaveViewerImage()
    {
        if (_viewerIndex < 0 || _viewerIndex >= _viewerItems.Count)
        {
            return false;
        }

        string path = _viewerItems[_viewerIndex].Path;
        RenderTargetBitmap? rtb = BuildComposedViewerBitmap() as RenderTargetBitmap;
        if (rtb is null)
        {
            return false;
        }

        try
        {
            BitmapEncoder encoder = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 90 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = path + ".annot.tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(fs);
            }

            File.Copy(tempPath, path, overwrite: true);
            TryDeleteFile(tempPath);

            _viewerIsDirty = false;
            _viewerSourceBitmap = rtb;
            _viewerSuppressStrokeEvents = true;
            ViewerInkSurface.Strokes.Clear();
            _viewerSuppressStrokeEvents = false;
            _viewerRedoStack.Clear();
            _viewerClearedStrokesForUndo = null;
            ViewerBackgroundImage.Source = rtb;
            UpdateViewerUndoRedoButtons();

            // 保存した内容を、元の一覧（サムネイル／コピー結果）にも反映します。
            ViewerItem current = _viewerItems[_viewerIndex];
            current.SelectionSource?.ReloadThumbnail();
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.RefreshClipboardPreviewCommand.Execute(null);
            }

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

    private void OnViewerCopyImageClick(object sender, RoutedEventArgs e) => CopyViewerImageToClipboard();

    /// <summary>拡大表示中の画像（赤ペン記入込み）をそのままクリップボードへコピーします。</summary>
    private void CopyViewerImageToClipboard()
    {
        BitmapSource? source = BuildComposedViewerBitmap();
        if (source is null)
        {
            return;
        }

        try
        {
            Clipboard.SetImage(source);
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.RefreshClipboardPreviewCommand.Execute(null);
            }
        }
        catch
        {
            // クリップボード操作は他アプリと競合することがあるため、失敗しても無視します。
        }
    }

    private static void TryDeleteFile(string path)
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

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (ViewerOverlay.Visibility == Visibility.Visible && !ConfirmDiscardOrSaveViewerChanges())
        {
            e.Cancel = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewerOverlay.Visibility != Visibility.Visible) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.C)
            {
                CopyViewerImageToClipboard();
                e.Handled = true;
                return;
            }

            if (key == Key.Y || (key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
            {
                RedoViewerStroke();
                e.Handled = true;
                return;
            }

            if (key == Key.Z)
            {
                UndoViewerStroke();
                e.Handled = true;
                return;
            }

            if (key == Key.S)
            {
                TrySaveViewerImage();
                e.Handled = true;
                return;
            }

            if (key is Key.OemPlus or Key.Add)
            {
                ZoomViewerBySteps(+1);
                e.Handled = true;
                return;
            }

            if (key is Key.OemMinus or Key.Subtract)
            {
                ZoomViewerBySteps(-1);
                e.Handled = true;
                return;
            }

            if (key is Key.D0 or Key.NumPad0)
            {
                OnViewerZoomResetClick(sender, e);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (ConfirmDiscardOrSaveViewerChanges())
                {
                    CloseViewer();
                }

                e.Handled = true;
                break;
            case Key.Left:
                OnViewerPrevClick(sender, e);
                e.Handled = true;
                break;
            case Key.Right:
                OnViewerNextClick(sender, e);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// サムネイル一覧の上でマウスホイールを回したとき、一覧全体を包む外側のスクロールへ橋渡しします。
    /// ListBox 自身のスクロールは無効化しているため、そのままではホイールが効きません。
    /// </summary>
    private void OnThumbnailListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ContentScrollViewer.ScrollToVerticalOffset(ContentScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // ---- 1枚あたりのコマ数 / 並び方（列数）: ホイール・上下矢印・直接入力対応のスピナー ----

    private const int MinFramesPerImage = 1;
    private const int MaxFramesPerImage = 200;
    private const int MinLayoutColumns = 1;
    private const int MaxLayoutColumns = 12;

    private void OnFramesPerImageSpinnerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SpinnerInteraction.OnPreviewMouseWheel(e, MinFramesPerImage, MaxFramesPerImage, () => vm.FramesPerImageDisplay, v => vm.FramesPerImageDisplay = v);
    }

    private void OnFramesPerImageTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not TextBox tb) return;
        SpinnerInteraction.OnPreviewKeyDown(e, tb, MinFramesPerImage, MaxFramesPerImage, () => vm.FramesPerImageDisplay, v => vm.FramesPerImageDisplay = v);
    }

    private void OnFramesPerImageTextInput(object sender, TextCompositionEventArgs e) => SpinnerInteraction.OnPreviewTextInput(e);

    private void OnFramesPerImageIncrementClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SpinnerInteraction.Nudge(1, MinFramesPerImage, MaxFramesPerImage, () => vm.FramesPerImageDisplay, v => vm.FramesPerImageDisplay = v);
    }

    private void OnFramesPerImageDecrementClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SpinnerInteraction.Nudge(-1, MinFramesPerImage, MaxFramesPerImage, () => vm.FramesPerImageDisplay, v => vm.FramesPerImageDisplay = v);
    }

    private void OnLayoutColumnsSpinnerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SpinnerInteraction.OnPreviewMouseWheel(e, MinLayoutColumns, MaxLayoutColumns, () => vm.LayoutColumns, v => vm.LayoutColumns = v);
    }

    private void OnLayoutColumnsTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not TextBox tb) return;
        SpinnerInteraction.OnPreviewKeyDown(e, tb, MinLayoutColumns, MaxLayoutColumns, () => vm.LayoutColumns, v => vm.LayoutColumns = v);
    }

    private void OnLayoutColumnsTextInput(object sender, TextCompositionEventArgs e) => SpinnerInteraction.OnPreviewTextInput(e);

    private void OnLayoutColumnsIncrementClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SpinnerInteraction.Nudge(1, MinLayoutColumns, MaxLayoutColumns, () => vm.LayoutColumns, v => vm.LayoutColumns = v);
    }

    private void OnLayoutColumnsDecrementClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SpinnerInteraction.Nudge(-1, MinLayoutColumns, MaxLayoutColumns, () => vm.LayoutColumns, v => vm.LayoutColumns = v);
    }

    /// <summary>ファイルをロックしないよう、メモリに読み込んでから表示用サイズで BitmapImage を作ります。</summary>
    private static BitmapImage? LoadFullImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
