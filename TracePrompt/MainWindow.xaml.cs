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
    private bool _previewRequested;
    private bool _wasSessionActive;

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
    /// 「範囲モード」ドロップダウンを開きます。手作りの Popup + StaysOpen 管理は実クリックで
    /// 反応しないことがあったため、WPF 標準の ContextMenu に開閉・外側クリックでの自動ドロップ・
    /// マウスキャプチャを任せています（左クリックで開く一般的なやり方です）。
    /// </summary>
    private void OnRegionModeToggleButtonClick(object sender, RoutedEventArgs e)
    {
        RegionModeContextMenu.PlacementTarget = RegionModeToggleButton;
        RegionModeContextMenu.IsOpen = true;
    }

    /// <summary>
    /// 範囲モードメニューで「全画面表示」を選んだとき: モードを切り替えます（メニューは選択で自動的に閉じます）。
    /// 既に選択中でも ViewModel 側の PropertyChanged が飛ばないことがあるため、
    /// チェックマーク（IsChecked）は毎回ここで両方とも明示的に設定し直します。
    /// </summary>
    private void OnSelectFullScreenModeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsFullScreenRegionMode = true;
        FullScreenModeMenuItem.IsChecked = true;
        ClipModeMenuItem.IsChecked = false;
    }

    /// <summary>範囲モードメニューで「クリップ」を選んだとき（考え方は上と同じ）。</summary>
    private void OnSelectClipModeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsFreeClipRegionMode = true;
        ClipModeMenuItem.IsChecked = true;
        FullScreenModeMenuItem.IsChecked = false;
    }

    /// <summary>
    /// キャプチャモードの「時間で」アイコンを選んだとき。
    /// 既に選択中でも ViewModel 側の PropertyChanged が飛ばないことがあるため、
    /// 見た目（IsChecked）は毎回ここで両方とも明示的に設定し直します。
    /// </summary>
    private void OnSelectIntervalCaptureModeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsIntervalCaptureMode = true;
        IntervalModeToggleButton.IsChecked = true;
        OperationModeToggleButton.IsChecked = false;
    }

    /// <summary>キャプチャモードの「操作で」アイコンを選んだとき（考え方は上と同じ）。</summary>
    private void OnSelectOperationCaptureModeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsOnOperationCaptureMode = true;
        OperationModeToggleButton.IsChecked = true;
        IntervalModeToggleButton.IsChecked = false;
    }

    /// <summary>
    /// 「撮影するモニターを選ぶ」ボタン: ComboBox を自前 Popup に入れる形は「Popup の中に Popup」となり、
    /// ComboBox 自身のドロップダウンが正しく開閉できず2台目以降を選べないことがあったため、
    /// クリックのたびにモニター一覧から ContextMenu を動的に組み立てて開きます。
    /// クリックしてもチェックが付いたことをその場で確認できるよう、選択しても・一覧を更新しても
    /// メニューは閉じずに開いたままにします（StaysOpenOnClick）。
    /// </summary>
    private void OnMonitorToggleButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = MonitorToggleButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        var refreshItem = new MenuItem
        {
            Header = "一覧を更新",
            Style = (Style)FindResource("RegionModeMenuItemStyle"),
            // StaysOpenOnClick は「CheckBox / RadioButton 形式の MenuItem 用」の設定であり、
            // 普通の（IsCheckable=False の）MenuItem では効かず、クリックで必ず閉じてしまいます。
            // チェックマークは表示させない（IsChecked は常に false のまま）まま IsCheckable=True にして、
            // StaysOpenOnClick を効かせます。
            IsCheckable = true,
            StaysOpenOnClick = true
        };
        refreshItem.Click += (_, _) =>
        {
            _viewModel.RefreshMonitorsCommand.Execute(null);
            ReplaceMonitorEntries(menu);

            // IsCheckable="True" にした副作用でクリックのたびに自分自身の IsChecked が
            // 反転してしまう（そのままだとチェックマークが付いたり消えたりする）ため、
            // 「一覧を更新」は常にチェックなしへ戻します。
            refreshItem.IsChecked = false;
        };
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator());

        ReplaceMonitorEntries(menu);
        menu.IsOpen = true;
    }

    /// <summary>
    /// モニター一覧部分（先頭の「一覧を更新」とその下の区切り線より後ろ）だけを作り直します。
    /// 「一覧を更新」を押したときにその項目自身を含む Items 全体を Clear() すると、
    /// クリック処理の最中に ContextMenu が閉じてしまうため、先頭2件（更新ボタン・区切り線）には
    /// 一切触れず、モニター一覧の部分だけを削除・再追加します。
    /// </summary>
    private void ReplaceMonitorEntries(ContextMenu menu)
    {
        const int headerItemCount = 2; // 「一覧を更新」＋区切り線
        while (menu.Items.Count > headerItemCount)
        {
            menu.Items.RemoveAt(menu.Items.Count - 1);
        }

        var monitorItems = new List<MenuItem>();
        foreach (MonitorInfo monitor in _viewModel.Monitors)
        {
            var item = new MenuItem
            {
                Header = monitor.Label,
                Style = (Style)FindResource("RegionModeMenuItemStyle"),
                IsCheckable = true,
                IsChecked = ReferenceEquals(monitor, _viewModel.SelectedMonitor),
                StaysOpenOnClick = true
            };
            item.Click += (_, _) =>
            {
                _viewModel.SelectedMonitor = monitor;
                foreach (MenuItem monitorItem in monitorItems)
                {
                    monitorItem.IsChecked = ReferenceEquals(monitorItem, item);
                }
            };
            monitorItems.Add(item);
            menu.Items.Add(item);
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
    /// 「キャプチャする」ボタン: 全画面モードでは選んだモニターに枠を表示するだけですが、
    /// クリップモードでは表示を ON にするタイミングでドラッグ選択オーバーレイを先に開き、
    /// 選び終わった範囲にそのまま枠を表示します（キャンセルした場合は何も変えません）。
    /// 既に表示中のときは、選び直しなしでそのまま非表示に戻します。
    /// </summary>
    private void OnCaptureButtonClick(object sender, RoutedEventArgs e)
    {
        bool turningOn = !_previewRequested;

        if (turningOn && _viewModel.IsFreeClipRegionMode)
        {
            var overlay = new RegionSelectorWindow { Owner = this };
            bool? ok = overlay.ShowDialog();
            if (ok != true || overlay.SelectedRegion is not { } region)
            {
                return;
            }

            _viewModel.SetFreeClipRegion(region);
        }

        _previewRequested = turningOn;
        CaptureToggleButton.IsChecked = _previewRequested;
        UpdateCaptureRegionOverlay();
    }

    /// <summary>
    /// キャプチャ範囲の枠と、枠の左上に貼り付く操作パネルを、今の設定に合わせて表示・配置・非表示します。
    /// 起動直後や範囲を選んだだけでは表示せず、「キャプチャする」ボタンで明示的に表示を求めたとき、
    /// または記録中（一時停止中も含む）だけ表示します（枠は待機中は青、記録中はオレンジの点線）。
    /// 記録を終了した直後は両方とも非表示にし、再度ボタンを押すか記録を始めるまで出しません。
    /// </summary>
    private void UpdateCaptureRegionOverlay()
    {
        WindowBounds? region = _viewModel.EffectiveCaptureRegion;

        bool isSessionActive = _viewModel.IsSessionActive;
        if (_wasSessionActive && !isSessionActive)
        {
            // ちょうど記録を終了した直後: 次に明示的にボタンを押すまでプレビューは出しません。
            _previewRequested = false;
            CaptureToggleButton.IsChecked = false;
        }
        _wasSessionActive = isSessionActive;

        if (region is null)
        {
            _captureRegionBorderWindow?.Hide();
            _recordingControlToolbarWindow?.Hide();
            _previewRequested = false;
            CaptureToggleButton.IsChecked = false;
            return;
        }

        bool showOverlays = isSessionActive || _previewRequested;
        if (showOverlays)
        {
            _captureRegionBorderWindow ??= new CaptureRegionBorderWindow { DataContext = _viewModel };
            _captureRegionBorderWindow.ApplyRegion(region.Value);
            _captureRegionBorderWindow.Show();

            _recordingControlToolbarWindow ??= new RecordingControlToolbarWindow { DataContext = _viewModel };
            _recordingControlToolbarWindow.ApplyRegion(region.Value);
            _recordingControlToolbarWindow.Show();
        }
        else
        {
            _captureRegionBorderWindow?.Hide();
            _recordingControlToolbarWindow?.Hide();
        }
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
