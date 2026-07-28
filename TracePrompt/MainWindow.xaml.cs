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

    /// <summary>⚙ 設定ボタン: 頻度・保持・記録対象などの詳細設定を別窓で開きます。</summary>
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel)
        {
            Owner = this
        };

        window.ShowDialog();
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

    /// <summary>「内容を確認・選び直す」ボタン: サムネイル選択・結合設定用の別窓を開きます（選択はオプション）。</summary>
    private void OnOpenImageListClick(object sender, RoutedEventArgs e)
    {
        var window = new ConsultationImageListWindow(_viewModel)
        {
            Owner = this
        };

        window.ShowDialog();
    }

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
