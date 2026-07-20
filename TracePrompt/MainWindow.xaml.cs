using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TracePrompt.Models;
using TracePrompt.Services;
using TracePrompt.ViewModels;
using TracePrompt.Views;

namespace TracePrompt;

/// <summary>
/// メイン画面のコードビハインドです。
/// 画面の初期化と DataContext の設定だけを行い、業務ロジックは ViewModel 側に任せます。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CaptureRegionBorderWindow? _captureRegionBorderWindow;
    private RecordingControlToolbarWindow? _recordingControlToolbarWindow;

    public MainWindow()
    {
        InitializeComponent();

        var monitorEnumerationService = new MonitorEnumerationService();
        var mouseHookService = new MouseHookService();
        var keyboardHookService = new KeyboardHookService();
        var screenshotService = new ScreenshotService();
        var tempStorageService = new TempStorageService();
        var consultationImageService = new ConsultationImageService();
        var clipboardService = new ClipboardService();
        var settingsService = new SettingsService();

        _viewModel = new MainViewModel(
            monitorEnumerationService,
            mouseHookService,
            keyboardHookService,
            screenshotService,
            tempStorageService,
            consultationImageService,
            clipboardService,
            settingsService);
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChangedForOverlay;
        Closed += OnClosed;

        UpdateCaptureRegionOverlay();
    }

    /// <summary>
    /// 「自由クリップ」ラジオボタンのクリック: 毎回ドラッグ選択オーバーレイを開きます。
    /// （既に自由クリップが選ばれている状態でクリックしても、選び直しとして再度開きます）。
    /// </summary>
    private void OnFreeClipRadioButtonClick(object sender, RoutedEventArgs e)
    {
        var overlay = new RegionSelectorWindow { Owner = this };
        bool? ok = overlay.ShowDialog();
        if (ok == true && overlay.SelectedRegion is { } region)
        {
            _viewModel.SetFreeClipRegion(region);
        }
    }

    private void OnViewModelPropertyChangedForOverlay(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MainViewModel.EffectiveCaptureRegion)
            or nameof(MainViewModel.IsIdle) or nameof(MainViewModel.IsRecording) or nameof(MainViewModel.IsPaused))
        {
            UpdateCaptureRegionOverlay();
        }
    }

    /// <summary>
    /// キャプチャ範囲の枠と操作パネルを、今の設定に合わせて表示・配置・非表示します。
    /// 「自由クリップ」「全画面」のどちらでも、範囲が決まっている間は常に薄い枠を表示し続けます。
    /// </summary>
    private void UpdateCaptureRegionOverlay()
    {
        WindowBounds? region = _viewModel.EffectiveCaptureRegion;
        if (region is null)
        {
            _captureRegionBorderWindow?.Hide();
            _recordingControlToolbarWindow?.Hide();
            return;
        }

        _captureRegionBorderWindow ??= new CaptureRegionBorderWindow();
        _captureRegionBorderWindow.ApplyRegion(region.Value);
        _captureRegionBorderWindow.Show();

        if (_recordingControlToolbarWindow is null)
        {
            _recordingControlToolbarWindow = new RecordingControlToolbarWindow { DataContext = _viewModel };
        }

        _recordingControlToolbarWindow.ApplyRegion(region.Value);
        _recordingControlToolbarWindow.Show();
    }

    /// <summary>
    /// 直近の操作履歴 ListBox 用。
    /// 履歴に項目がありスクロールできるときは一覧を動かし、
    /// 空・スクロール不要・端まで到達したときは画面全体（外側）をスクロールします。
    /// </summary>
    private void OnOperationHistoryPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        // 何も無い（＝スクロールできない）→ 画面全体をスクロール
        ScrollViewer? listScroll = FindDescendantScrollViewer(listBox);
        bool listCanScroll = listBox.Items.Count > 0
                             && listScroll is not null
                             && listScroll.ScrollableHeight > 0.5;

        if (!listCanScroll)
        {
            ScrollMainPage(e);
            return;
        }

        HandleNestedVerticalScroll(listScroll!, e);

        // 一覧の端まで行った場合も画面全体へ
        if (!e.Handled)
        {
            ScrollMainPage(e);
        }
    }

    /// <summary>メイン画面全体の ScrollViewer をホイール分だけ動かします。</summary>
    private void ScrollMainPage(MouseWheelEventArgs e)
    {
        if (MainPageScrollViewer is null)
        {
            return;
        }

        MainPageScrollViewer.ScrollToVerticalOffset(MainPageScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// プレビュー内 ScrollViewer のホイール処理です。
    /// 画像が無い・スクロール不要・端まで到達したときは、メイン画面全体をスクロールします。
    /// （内側 ScrollViewer がイベントを飲み込み外側が動かないのを防ぎます）。
    /// </summary>
    private void OnNestedScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        bool canScrollVertically = scrollViewer.ScrollableHeight > 0.5;
        bool canScrollHorizontally = scrollViewer.ScrollableWidth > 0.5;

        // 空欄・画像が枠に収まっている → 全体ページをスクロール
        if (!canScrollVertically && !canScrollHorizontally)
        {
            ScrollMainPage(e);
            return;
        }

        // Shift+ホイール → 横スクロール（画像が横に大きいとき）
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (canScrollHorizontally)
            {
                scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
            else
            {
                ScrollMainPage(e);
            }

            return;
        }

        HandleNestedVerticalScroll(scrollViewer, e);
        if (e.Handled)
        {
            return;
        }

        // 縦は端でも横だけ余白がある場合は横スクロールを試す
        bool scrollingDown = e.Delta < 0;
        bool canScrollRight = scrollViewer.HorizontalOffset < scrollViewer.ScrollableWidth - 0.5;
        bool canScrollLeft = scrollViewer.HorizontalOffset > 0.5;
        if ((scrollingDown && canScrollRight) || (!scrollingDown && canScrollLeft))
        {
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        // 内側で動けない（端に到達など） → 全体ページへ
        ScrollMainPage(e);
    }

    private static void HandleNestedVerticalScroll(ScrollViewer scrollViewer, MouseWheelEventArgs e)
    {
        bool scrollingDown = e.Delta < 0;
        bool canScrollDown = scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - 0.5;
        bool canScrollUp = scrollViewer.VerticalOffset > 0.5;

        if ((scrollingDown && canScrollDown) || (!scrollingDown && canScrollUp))
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            ScrollViewer? found = FindDescendantScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// スクショトリガー欄: クリックで入力待ち。入力待ち中はマウスボタン1つを確定。
    /// </summary>
    private void OnScreenshotTriggerBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsScreenshotTriggerEditorEnabled)
        {
            return;
        }

        if (!_viewModel.IsScreenshotTriggerListening)
        {
            _viewModel.BeginScreenshotTriggerListen();
            if (sender is UIElement el)
            {
                el.Focus();
            }

            e.Handled = true;
            return;
        }

        string? button = e.ChangedButton switch
        {
            MouseButton.Left => "Left",
            MouseButton.Right => "Right",
            MouseButton.Middle => "Middle",
            MouseButton.XButton1 => "XButton1",
            MouseButton.XButton2 => "XButton2",
            _ => null
        };
        if (button is null) return;

        string display = button switch
        {
            "Left" => "左クリック",
            "Right" => "右クリック",
            "Middle" => "中クリック",
            "XButton1" => "マウスボタン4",
            "XButton2" => "マウスボタン5",
            _ => button
        };

        _viewModel.TryAssignScreenshotTrigger(CapturedInputBinding.FromMouse(button, display));
        e.Handled = true;
    }

    private void OnScreenshotTriggerBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.IsScreenshotTriggerListening || !_viewModel.IsScreenshotTriggerEditorEnabled)
        {
            return;
        }

        _viewModel.TryAssignScreenshotTrigger(CapturedInputBinding.FromMouse("Wheel", "マウスホイール"));
        e.Handled = true;
    }

    private void OnScreenshotTriggerBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsScreenshotTriggerListening)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System)
        {
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        string display = KeyboardHookService.BuildDisplayLabel(vk, ctrl, alt, shift, win);
        if (string.IsNullOrWhiteSpace(display))
        {
            display = key.ToString();
        }

        _viewModel.TryAssignScreenshotTrigger(
            CapturedInputBinding.FromKeyboard(vk, ctrl, alt, shift, win, display));
        e.Handled = true;
    }

    private void OnHistoryPreviewImageClick(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.TryGetHistoryPreviewPath(out string path))
        {
            return;
        }

        OpenImageAnnotator(path, "履歴プレビュー — 赤ペン記入", reloadHistory: true);
        e.Handled = true;
    }

    private void OnConsultationPreviewImageClick(object sender, MouseButtonEventArgs e)
{
    if (!_viewModel.TryGetConsultationPreviewPath(out string path))
    {
        return;
    }

    OpenConsultationImageAnnotator(path);
    e.Handled = true;
}

private void OnConsultationPreviewThumbnailClick(object sender, MouseButtonEventArgs e)
{
    if (sender is not FrameworkElement element
        || !_viewModel.TryGetConsultationPreviewPathFromEntry(element.DataContext, out string path))
    {
        return;
    }

    OpenConsultationImageAnnotator(path);
    e.Handled = true;
}

private void OpenConsultationImageAnnotator(string imagePath)
{
    List<string> paths = _viewModel.GeneratedMontagePaths
        .Where(File.Exists)
        .ToList();

    int initialIndex = paths.IndexOf(imagePath);
    if (initialIndex < 0)
    {
        paths = new List<string> { imagePath };
        initialIndex = 0;
    }

    OpenImageAnnotator(paths, initialIndex, "相談用画像 — 赤ペン記入", reloadHistory: false);
}

    /// <summary>「選択画像を一覧化」ボタン: サムネイル選択用の別窓を開きます（選択はオプション）。</summary>
    private void OnOpenImageListClick(object sender, RoutedEventArgs e)
    {
        var window = new ConsultationImageListWindow(_viewModel)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    // ---- 1枚あたりのコマ数 / 並び方（列数） ホイール・上下矢印・直接入力対応のスピナー ----

    private const int MinFramesPerImage = 1;
    private const int MaxFramesPerImage = 200;
    private const int MinLayoutColumns = 1;
    private const int MaxLayoutColumns = 12;

    private void OnFramesPerImageSpinnerPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        SpinnerInteraction.OnPreviewMouseWheel(e, MinFramesPerImage, MaxFramesPerImage, () => _viewModel.FramesPerImageDisplay, v => _viewModel.FramesPerImageDisplay = v);

    private void OnFramesPerImageTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        SpinnerInteraction.OnPreviewKeyDown(e, tb, MinFramesPerImage, MaxFramesPerImage, () => _viewModel.FramesPerImageDisplay, v => _viewModel.FramesPerImageDisplay = v);
    }

    private void OnFramesPerImageTextInput(object sender, TextCompositionEventArgs e) => SpinnerInteraction.OnPreviewTextInput(e);

    private void OnFramesPerImageIncrementClick(object sender, RoutedEventArgs e) =>
        SpinnerInteraction.Nudge(1, MinFramesPerImage, MaxFramesPerImage, () => _viewModel.FramesPerImageDisplay, v => _viewModel.FramesPerImageDisplay = v);

    private void OnFramesPerImageDecrementClick(object sender, RoutedEventArgs e) =>
        SpinnerInteraction.Nudge(-1, MinFramesPerImage, MaxFramesPerImage, () => _viewModel.FramesPerImageDisplay, v => _viewModel.FramesPerImageDisplay = v);

    private void OnLayoutColumnsSpinnerPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        SpinnerInteraction.OnPreviewMouseWheel(e, MinLayoutColumns, MaxLayoutColumns, () => _viewModel.LayoutColumns, v => _viewModel.LayoutColumns = v);

    private void OnLayoutColumnsTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        SpinnerInteraction.OnPreviewKeyDown(e, tb, MinLayoutColumns, MaxLayoutColumns, () => _viewModel.LayoutColumns, v => _viewModel.LayoutColumns = v);
    }

    private void OnLayoutColumnsTextInput(object sender, TextCompositionEventArgs e) => SpinnerInteraction.OnPreviewTextInput(e);

    private void OnLayoutColumnsIncrementClick(object sender, RoutedEventArgs e) =>
        SpinnerInteraction.Nudge(1, MinLayoutColumns, MaxLayoutColumns, () => _viewModel.LayoutColumns, v => _viewModel.LayoutColumns = v);

    private void OnLayoutColumnsDecrementClick(object sender, RoutedEventArgs e) =>
        SpinnerInteraction.Nudge(-1, MinLayoutColumns, MaxLayoutColumns, () => _viewModel.LayoutColumns, v => _viewModel.LayoutColumns = v);

    private void OpenImageAnnotator(string imagePath, string title, bool reloadHistory)
{
    OpenImageAnnotator(new[] { imagePath }, 0, title, reloadHistory);
}

private void OpenImageAnnotator(IReadOnlyList<string> imagePaths, int initialIndex, string title, bool reloadHistory)
{
    var window = new ImageAnnotatorWindow(imagePaths, initialIndex, title)
    {
        Owner = this
    };

    window.ShowDialog();

    if (!window.Saved)
    {
        return;
    }

    if (reloadHistory)
    {
        _viewModel.ReloadHistoryPreview();
    }
    else
    {
        _viewModel.ReloadConsultationPreview();
    }
}

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChangedForOverlay;
        _captureRegionBorderWindow?.Close();
        _recordingControlToolbarWindow?.Close();
        _viewModel.Dispose();
    }
}
