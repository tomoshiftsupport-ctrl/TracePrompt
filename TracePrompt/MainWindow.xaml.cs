using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private CaptureRegionMoveHandleWindow? _captureRegionMoveHandleWindow;
    private CompactRecordingWindow? _compactRecordingWindow;
    private bool _previewRequested;
    private bool _wasSessionActive;
    private bool _returnedToMainWindowByUser;

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
        LocationChanged += OnWindowLocationOrStateChangedForPopups;
        StateChanged += OnWindowLocationOrStateChangedForPopups;

        UpdateCaptureRegionOverlay();
    }

    /// <summary>
    /// 範囲モード／モニターの Popup は WPF の既定動作だと、本体ウィンドウを動かしたり
    /// 最小化したりしても追従・非表示にならず、開いた位置にそのまま浮いて残ってしまいます。
    /// 外側クリックで閉じるのと同じ考え方で、ウィンドウが動いた・状態が変わった瞬間に閉じます。
    /// </summary>
    private void OnWindowLocationOrStateChangedForPopups(object? sender, EventArgs e)
    {
        RegionModePopup.IsOpen = false;
        MonitorPopup.IsOpen = false;
    }

    /// <summary>
    /// 「範囲モード」ドロップダウンを開閉します。
    /// 開閉は「Click のたびに IsOpen を反転させる」だけの単純な経路一本に統一しています。
    /// Popup は StaysOpen="True" にしてあるため、WPF 側の「外側クリックで自動的に閉じる」機能自体が
    /// 無効になっており、このボタンをもう一度押したときに「自動クローズ→Click で開き直し」という
    /// 競合が起きようがありません（外側クリックでの見た目の自動クローズは、代わりに
    /// <see cref="OnWindowPreviewMouseDownForPopups"/> が自前で担当します）。
    /// </summary>
    private void OnRegionModeToggleButtonClick(object sender, RoutedEventArgs e)
    {
        RegionModePopup.IsOpen = !RegionModePopup.IsOpen;
    }

    /// <summary>
    /// 範囲モードメニューで「全画面表示」を選んだとき: モードを切り替えます（選んでもメニューは閉じません。
    /// トグルボタンをもう一度押すか、外側をクリックしたときだけ閉じます）。
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
    /// 「撮影するモニターを選ぶ」ボタン: 範囲モードボタンと同じ Popup + 明示トグル方式です。
    /// 開くときだけモニター一覧から中身を組み立て直します（閉じるときは組み立てません）。
    /// </summary>
    private void OnMonitorToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (MonitorPopup.IsOpen)
        {
            MonitorPopup.IsOpen = false;
            return;
        }

        RebuildMonitorPopupItems();
        MonitorPopup.IsOpen = true;
    }

    /// <summary>
    /// モニター一覧（「一覧を更新」＋区切り線＋各モニター）を MonitorPopupItemsPanel に作り直します。
    /// </summary>
    private void RebuildMonitorPopupItems()
    {
        MonitorPopupItemsPanel.Children.Clear();

        // RegionModeOptionButtonStyle は TargetType="ToggleButton" のため、
        // Button に適用すると型不一致で例外になる（クラッシュの原因だった）。
        // トグルの見た目・機能自体は使わないが、型を揃えるために ToggleButton にする。
        var refreshItem = new ToggleButton
        {
            Content = "一覧を更新",
            Style = (Style)FindResource("RegionModeOptionButtonStyle"),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        refreshItem.Click += (_, _) =>
        {
            _viewModel.RefreshMonitorsCommand.Execute(null);
            RebuildMonitorPopupItems();
        };
        MonitorPopupItemsPanel.Children.Add(refreshItem);

        MonitorPopupItemsPanel.Children.Add(new Separator
        {
            Margin = new Thickness(2, 4, 2, 4)
        });

        var monitorItems = new List<ToggleButton>();
        foreach (MonitorInfo monitor in _viewModel.Monitors)
        {
            var item = new ToggleButton
            {
                Content = monitor.Label,
                Style = (Style)FindResource("RegionModeOptionButtonStyle"),
                IsChecked = ReferenceEquals(monitor, _viewModel.SelectedMonitor)
            };
            item.Click += (_, _) =>
            {
                _viewModel.SelectedMonitor = monitor;
                foreach (ToggleButton monitorItem in monitorItems)
                {
                    monitorItem.IsChecked = ReferenceEquals(monitorItem, item);
                }
            };
            monitorItems.Add(item);
            MonitorPopupItemsPanel.Children.Add(item);
        }
    }

    /// <summary>
    /// 範囲モード／モニターのドロップダウンは Popup の StaysOpen="True" で外側クリックの自動クローズを
    /// 無効化しているため、代わりにここで「開いている状態でトグルボタンとポップアップの外側がクリックされたら閉じる」
    /// を自前で判定します。トグルボタン自身の上でのクリックは、そのボタンの Click ハンドラーが開閉を
    /// 一元管理するのでここでは何もしません（二重に反転させて開閉が食い違うのを避けるため）。
    /// </summary>
    private void OnWindowPreviewMouseDownForPopups(object sender, MouseButtonEventArgs e)
    {
        CloseDropdownPopupIfClickedOutside(RegionModePopup, RegionModeToggleButton, e);
        CloseDropdownPopupIfClickedOutside(MonitorPopup, MonitorToggleButton, e);
    }

    private static void CloseDropdownPopupIfClickedOutside(Popup popup, UIElement toggleButton, MouseButtonEventArgs e)
    {
        if (!popup.IsOpen)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject clicked)
        {
            return;
        }

        if (IsDescendantOrSelf(clicked, toggleButton) || (popup.Child is not null && IsDescendantOrSelf(clicked, popup.Child)))
        {
            return;
        }

        popup.IsOpen = false;
    }

    private static bool IsDescendantOrSelf(DependencyObject element, DependencyObject ancestor)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
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
    /// 「キャプチャする」ボタン: 押すたびに必ず枠を表示させます（選択・非選択の2状態は持たない、一方通行の操作です）。
    /// 全画面モードでは選んだモニターに枠を表示するだけですが、クリップモードでは
    /// ドラッグ選択オーバーレイを先に開き、選び終わった範囲にそのまま枠を表示します
    /// （キャンセルした場合は何も変えません）。
    /// </summary>
    private void OnCaptureButtonClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsFreeClipRegionMode)
        {
            var overlay = new RegionSelectorWindow { Owner = this };
            bool? ok = overlay.ShowDialog();
            if (ok != true || overlay.SelectedRegion is not { } region)
            {
                return;
            }

            _viewModel.SetFreeClipRegion(region);
        }

        _previewRequested = true;
        // 新しく範囲を確定する操作なので、以前「戻る」を押していても必ず小型ウィンドウ表示から始めます。
        _returnedToMainWindowByUser = false;
        UpdateCaptureRegionOverlay();
    }

    /// <summary>
    /// 小型ウィンドウの「戻る」ボタンが押されたとき: 記録は止めず、通常のメインウィンドウ表示に戻すだけです。
    /// 次に記録が始まったとき（IsIdle→IsRecording）は、自動でまた小型ウィンドウに切り替わります
    /// （<see cref="UpdateCaptureRegionOverlay"/> 内のリセット処理を参照）。
    /// </summary>
    private void OnCompactRecordingWindowReturnRequested(object? sender, EventArgs e)
    {
        _returnedToMainWindowByUser = true;
        UpdateCaptureRegionOverlay();
    }

    /// <summary>
    /// クリップモードの中央ハンドルをドラッグで動かし終えたとき: 新しい位置を ViewModel に反映します。
    /// これにより EffectiveCaptureRegion の変更が伝播し、以後の記録も新しい位置から行われます。
    /// </summary>
    private void OnCaptureRegionMoved(object? sender, WindowBounds newRegion)
    {
        if (_viewModel.IsFreeClipRegionMode)
        {
            _viewModel.SetFreeClipRegion(newRegion);
        }
    }

    /// <summary>
    /// キャプチャ範囲の枠・中央の移動ハンドルと、メインウィンドウ／小型操作ウィンドウのどちらを表示するかを、
    /// 今の設定に合わせてまとめて切り替えます。
    /// 枠と移動ハンドルは、小型ウィンドウ表示中（＝メインウィンドウが隠れている間）だけ出します。
    /// メインウィンドウに戻っている間（「戻る」操作後など）は、枠が画面に残って邪魔にならないよう非表示にします。
    /// 移動ハンドルはさらに「クリップモードかつ未記録」のときだけ出し、記録が始まったら動かせなくします。
    /// </summary>
    private void UpdateCaptureRegionOverlay()
    {
        WindowBounds? region = _viewModel.EffectiveCaptureRegion;

        bool isSessionActive = _viewModel.IsSessionActive;
        if (!_wasSessionActive && isSessionActive)
        {
            // ちょうど記録が始まった直後: 「戻る」でメインウィンドウを見ていても、小型ウィンドウ表示へ戻します。
            _returnedToMainWindowByUser = false;
        }
        if (_wasSessionActive && !isSessionActive)
        {
            // ちょうど記録を終了した直後: 次に明示的にボタンを押すまでプレビューは出しません。
            _previewRequested = false;
        }
        _wasSessionActive = isSessionActive;

        if (region is null)
        {
            _previewRequested = false;
        }

        bool wantsCompactMode = region is not null && (isSessionActive || _previewRequested) && !_returnedToMainWindowByUser;

        if (wantsCompactMode && region is not null)
        {
            _captureRegionBorderWindow ??= new CaptureRegionBorderWindow { DataContext = _viewModel };
            _captureRegionBorderWindow.ApplyRegion(region.Value);
            _captureRegionBorderWindow.Show();

            // 動かせるのは「クリップモードかつ未記録」のときだけ（記録中は背後のアプリ操作を邪魔しないよう出しません）。
            bool showMoveHandle = _viewModel.IsFreeClipRegionMode && !isSessionActive;
            if (showMoveHandle)
            {
                bool isNewHandleWindow = _captureRegionMoveHandleWindow is null;
                _captureRegionMoveHandleWindow ??= new CaptureRegionMoveHandleWindow();
                if (isNewHandleWindow)
                {
                    _captureRegionMoveHandleWindow.RegionMoved += OnCaptureRegionMoved;
                    // ドラッグ中、枠線側も毎フレーム同じ範囲へ動かして一体で動いているように見せます。
                    _captureRegionMoveHandleWindow.LiveRegionUpdate = liveRegion => _captureRegionBorderWindow?.ApplyRegion(liveRegion);
                }
                _captureRegionMoveHandleWindow.ApplyRegion(region.Value);
                _captureRegionMoveHandleWindow.Show();
            }
            else
            {
                _captureRegionMoveHandleWindow?.Hide();
            }
        }
        else
        {
            _captureRegionBorderWindow?.Hide();
            _captureRegionMoveHandleWindow?.Hide();
        }

        if (wantsCompactMode)
        {
            bool isFirstShow = _compactRecordingWindow is null;
            _compactRecordingWindow ??= new CompactRecordingWindow { DataContext = _viewModel };
            if (isFirstShow)
            {
                _compactRecordingWindow.ReturnRequested += OnCompactRecordingWindowReturnRequested;
            }
            if (isFirstShow && region is not null)
            {
                _compactRecordingWindow.ApplyInitialPosition(region.Value);
            }
            _compactRecordingWindow.Show();
            Hide();
        }
        else
        {
            _compactRecordingWindow?.Hide();
            if (!IsVisible)
            {
                Show();
            }
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
        _captureRegionMoveHandleWindow?.Close();
        _compactRecordingWindow?.Close();
        _viewModel.Dispose();
    }
}
