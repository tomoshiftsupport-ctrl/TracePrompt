using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TracePrompt.Models;
using TracePrompt.Services;

namespace TracePrompt.ViewModels;

/// <summary>
/// 連続キャプチャ＋操作イベント紐づけを中心としたメイン ViewModel です。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan WheelCoalesceInterval = TimeSpan.FromMilliseconds(350);

    public const int MinCapturesPerSecond = 1;
    public const int MaxCapturesPerSecond = 10;
    public const int MinRetentionSeconds = 1;
    public const int MaxRetentionSeconds = 120;
    public const int MinKeepCaptureCount = 1;
    public const int MaxKeepCaptureCount = 200;
    private const int AbsoluteMaxFrames = 200;

    private readonly MonitorEnumerationService _monitorEnumerationService;
    private readonly MouseHookService _mouseHookService;
    private readonly KeyboardHookService _keyboardHookService;
    private readonly ScreenshotService _screenshotService;
    private readonly TempStorageService _tempStorageService;
    private readonly ConsultationImageService _consultationImageService;
    private readonly ClipboardService _clipboardService;
    private readonly SettingsService _settingsService;
    private readonly Dispatcher _dispatcher;

    private readonly DispatcherTimer _captureTimer;
    private readonly DispatcherTimer _leftClickCommitTimer;
    private readonly DispatcherTimer _rightClickCommitTimer;
    private readonly DispatcherTimer _wheelFlushTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _recordingOverlayTimer;

    /// <summary>連続キャプチャのリングバッファ（ソース・オブ・トゥルース）。</summary>
    private readonly List<CaptureFrame> _captureBuffer = new();
    private long _nextCaptureId = 1;

    /// <summary>
    /// 秒でキャプチャモードで、操作した瞬間にはまだ存在しない「次の定期キャプチャ」を待っている操作です。
    /// 次のフレームが実際に撮れたら <see cref="ResolvePendingIntervalLinks"/> が遡って紐づけ直し、
    /// 記録が終わるまでどのフレームも来なければ <see cref="FlushPendingIntervalLinks"/> が
    /// 「キャプチャなしの操作」として確定させます（<see cref="AddActionWithCaptureLink"/> 参照）。
    /// </summary>
    private readonly List<(Func<CaptureFrame?, RecordedAction> BuildAction, HistoryItem Item)> _pendingIntervalLinks = new();

    /// <summary>
    /// 操作キャプチャモードで、設定したトリガーには一致しなかったがキャプチャ範囲内で起きたマウス操作
    /// （左右クリック・ホイール・ドラッグ）です。位置情報があるため、次にトリガーが一致して実際に
    /// 1枚撮れた瞬間にその画像へ遡って紐づけ直し、位置マークを描けるようにします。
    /// キーボードには位置の概念が無いためここには積みません（従来どおり「キャプチャなしの操作」）。
    /// </summary>
    private readonly List<(Func<CaptureFrame?, RecordedAction> BuildAction, HistoryItem Item)> _pendingOperationCaptureLinks = new();

    private RecordingState _state = RecordingState.Stopped;
    private MonitorInfo? _selectedMonitor;
    private CaptureRegionMode _captureRegionMode = CaptureRegionMode.FullScreen;
    private WindowBounds? _freeClipRegion;
    /// <summary>設定から読み込んだモニター識別子。<see cref="RefreshMonitors"/> で一覧と突き合わせて解決します。</summary>
    private string? _savedMonitorDeviceId;
    private string _statusMessage =
        "準備完了です。①キャプチャ範囲を選ぶ → ②記録開始 → ③操作 → ④相談用データ生成";
    private HistoryItem? _selectedHistoryItem;
    private BitmapImage? _previewImage;
    private BitmapImage? _consultationPreviewImage;
    private bool _isGeneratingConsultation;

    private const int MinFramesPerImage = 1;
    private const int MaxFramesPerImage = 200;
    private const int MinLayoutColumns = 1;
    private const int MaxLayoutColumns = 12;

    private int _framesPerImage = 6;
    private int _lastExplicitFramesPerImage = 6;
    private int _layoutColumns = 2;
    private bool _isLayoutAuto;
    private ConsultationQualityPreset _qualityPreset = ConsultationQualityPreset.Standard;
    private bool _useCustomAutoCombineSettings;
    private IReadOnlyList<string> _generatedMontagePaths = Array.Empty<string>();
    private bool _isGeneratingSelectedCombined;

    private BitmapSource? _clipboardPreviewImage;
    private string? _lastCopiedSingleImagePath;
    private IReadOnlyList<ClipboardPreviewFileEntry> _clipboardPreviewFileThumbnails = Array.Empty<ClipboardPreviewFileEntry>();
    private string _clipboardPreviewSummary = "まだ何もコピーしていません。";

    private int _capturesPerSecond = 1;
    // 既定値を10→6に: コマ数が増えるほど相談用画像の1コマあたりの解像度が下がるため、既定は控えめにする
    private int _retentionSeconds = 6;
    private int _keepCaptureCount = 20;
    private ScreenshotCaptureMode _captureMode = ScreenshotCaptureMode.Interval;

    private bool _recordMouseOperations = true;
    private bool _recordKeyboard;

    private DateTime? _recordingSessionStartedAt;
    private DateTime? _recordingPausedAt;
    private TimeSpan _recordingPausedTotal = TimeSpan.Zero;
    private DateTime? _lastCaptureRecordedAt;
    private string _recordingOverlayNoticeText = string.Empty;

    private CapturedInputBinding? _screenshotTrigger = CapturedInputBinding.DefaultLeftClick();
    private bool _isScreenshotTriggerListening;

    /// <summary>記録セッション中は固定される、実際のキャプチャ範囲（画面座標）。</summary>
    private WindowBounds? _captureRegion;

    private bool _leftTracking;
    private DateTime _leftDownAt;
    private int _leftDownX;
    private int _leftDownY;
    private bool _leftIsDragging;
    private WindowBounds _leftDownBounds;
    private PendingClick? _pendingLeftClick;

    private bool _rightTracking;
    private DateTime _rightDownAt;
    private int _rightDownX;
    private int _rightDownY;
    private WindowBounds _rightDownBounds;
    private PendingClick? _pendingRightClick;

    private int _wheelAccumDelta;
    private int _wheelScreenX;
    private int _wheelScreenY;
    private WindowBounds _wheelBounds;
    private bool _wheelHasPending;
    private bool _wheelIsOutOfRegion;

    private bool _disposed;

    private sealed class PendingClick
    {
        public required DateTime At { get; init; }
        public required int ScreenX { get; init; }
        public required int ScreenY { get; init; }
        public required WindowBounds Bounds { get; init; }
        public required bool IsLeft { get; init; }
    }

    public MainViewModel(
        MonitorEnumerationService monitorEnumerationService,
        MouseHookService mouseHookService,
        KeyboardHookService keyboardHookService,
        ScreenshotService screenshotService,
        TempStorageService tempStorageService,
        ConsultationImageService consultationImageService,
        ClipboardService clipboardService,
        SettingsService settingsService)
    {
        _monitorEnumerationService = monitorEnumerationService ?? throw new ArgumentNullException(nameof(monitorEnumerationService));
        _mouseHookService = mouseHookService ?? throw new ArgumentNullException(nameof(mouseHookService));
        _keyboardHookService = keyboardHookService ?? throw new ArgumentNullException(nameof(keyboardHookService));
        _screenshotService = screenshotService ?? throw new ArgumentNullException(nameof(screenshotService));
        _tempStorageService = tempStorageService ?? throw new ArgumentNullException(nameof(tempStorageService));
        _consultationImageService = consultationImageService ?? throw new ArgumentNullException(nameof(consultationImageService));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _captureTimer = new DispatcherTimer();
        _captureTimer.Tick += OnCaptureTimerTick;

        _leftClickCommitTimer = new DispatcherTimer();
        _leftClickCommitTimer.Tick += (_, _) =>
        {
            _leftClickCommitTimer.Stop();
            CommitPendingLeftClickIfAny();
        };

        _rightClickCommitTimer = new DispatcherTimer();
        _rightClickCommitTimer.Tick += (_, _) =>
        {
            _rightClickCommitTimer.Stop();
            CommitPendingRightClickIfAny();
        };

        _wheelFlushTimer = new DispatcherTimer { Interval = WheelCoalesceInterval };
        _wheelFlushTimer.Tick += (_, _) =>
        {
            _wheelFlushTimer.Stop();
            FlushWheelAccumulated();
        };

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            PersistSettings();
        };

        _recordingOverlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingOverlayTimer.Tick += (_, _) => RaiseRecordingOverlayStatusProperties();

        ClearScreenshotTriggerCommand = new RelayCommand(() =>
        {
            ScreenshotTrigger = null;
            IsScreenshotTriggerListening = false;
        });

        LoadSettings();
        ApplyCaptureTimerInterval();
        if (ScreenshotTrigger is null)
        {
            ScreenshotTrigger = CapturedInputBinding.DefaultLeftClick();
        }

        OperationHistory.CollectionChanged += OnOperationHistoryCollectionChanged;

        _mouseHookService.LeftButtonDown += (x, y) => Dispatch(() => ProcessLeftButtonDown(x, y));
        _mouseHookService.LeftButtonUp += (x, y) => Dispatch(() => ProcessLeftButtonUp(x, y));
        _mouseHookService.RightButtonDown += (x, y) => Dispatch(() => ProcessRightButtonDown(x, y));
        _mouseHookService.RightButtonUp += (x, y) => Dispatch(() => ProcessRightButtonUp(x, y));
        _mouseHookService.MouseMoveWhileLeftDown += (x, y) => Dispatch(() => ProcessMouseMoveWhileLeftDown(x, y));
        _mouseHookService.MouseWheel += (x, y, d) => Dispatch(() => ProcessMouseWheel(x, y, d));
        _keyboardHookService.KeyDown += (vk, display, ctrl, alt, shift, win) =>
            Dispatch(() => ProcessKeyDown(vk, display, ctrl, alt, shift, win));

        RefreshMonitorsCommand = new RelayCommand(RefreshMonitors, () => IsIdle);
        StartRecordingCommand = new RelayCommand(StartRecording, () => IsIdle);
        StopRecordingCommand = new RelayCommand(StopRecording, () => IsSessionActive);
        PauseRecordingCommand = new RelayCommand(PauseRecording, () => State == RecordingState.Recording);
        ResumeRecordingCommand = new RelayCommand(ResumeRecording, () => IsPaused);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        OpenTempFolderCommand = new RelayCommand(OpenTempFolder);
        CopyConsultationImageCommand = new RelayCommand(
            CopyConsultationImage,
            () => HasConsultationPreview && GeneratedMontagePaths.Count > 0);

        SelectAllThumbnailsCommand = new RelayCommand(SelectAllThumbnails, () => ThumbnailFrames.Count > 0);
        DeselectAllThumbnailsCommand = new RelayCommand(DeselectAllThumbnails, () => ThumbnailFrames.Count > 0);
        GenerateAndCopySelectedCombinedCommand = new RelayCommand(
            GenerateAndCopySelectedCombined,
            () => !_isGeneratingSelectedCombined && HasAnyThumbnailSelected);
        CopySelectedAsFilesCommand = new RelayCommand(CopySelectedAsFiles, () => HasAnyThumbnailSelected);
        RefreshClipboardPreviewCommand = new RelayCommand(RefreshClipboardPreview);

        _tempStorageService.ClearAll();
        RefreshMonitors();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AppName => "AIヘルプキャプチャ";

    public RecordingState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsSessionActive));
            OnPropertyChanged(nameof(IsHistoryPanelVisible));
            OnPropertyChanged(nameof(HasRecordingOverlayStatus));
            OnPropertyChanged(nameof(HasRecordingOverlayNotice));
            OnPropertyChanged(nameof(RecordingOverlayElapsedText));
            OnPropertyChanged(nameof(RecordingOverlayActivityText));
            OnPropertyChanged(nameof(RecordingOverlayNoticeText));
            RaiseCommandStates();
            SyncCaptureTimerWithState();
            SyncKeyboardHookWithSettings();
            SyncRecordingOverlayTimer();
        }
    }

    public string StateText => State switch
    {
        RecordingState.Recording => "記録中",
        RecordingState.Paused => "一時停止中",
        _ => "停止中"
    };

    public bool IsRecording => State == RecordingState.Recording;
    public bool IsPaused => State == RecordingState.Paused;
    public bool IsIdle => State == RecordingState.Stopped;
    public bool IsSessionActive => State is RecordingState.Recording or RecordingState.Paused;
    public bool HasRecordingOverlayStatus => IsSessionActive || HasRecordingOverlayNotice;

    public bool HasRecordingOverlayNotice => !string.IsNullOrWhiteSpace(RecordingOverlayNoticeText);

    public string RecordingOverlayNoticeText
    {
        get => _recordingOverlayNoticeText;
        private set
        {
            if (_recordingOverlayNoticeText == value) return;
            _recordingOverlayNoticeText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRecordingOverlayNotice));
            OnPropertyChanged(nameof(HasRecordingOverlayStatus));
        }
    }

    public string RecordingOverlayElapsedText => !HasRecordingOverlayStatus || _recordingSessionStartedAt is null
        ? string.Empty
        : $"経過 {FormatRecordingOverlayElapsed(GetRecordingOverlayElapsed())}";

    /// <summary>小型の操作ウィンドウ用: 接頭辞なし・常に時:分:秒（未開始時は "00:00:00"）で表示します。</summary>
    public string RecordingElapsedCompactText => _recordingSessionStartedAt is null
        ? "00:00:00"
        : FormatRecordingElapsedCompact(GetRecordingOverlayElapsed());

    /// <summary>小型の操作ウィンドウ用: 今バッファに保持しているキャプチャ枚数（"N 枚"）。キャプチャのたびに更新されます。</summary>
    public string CaptureCountCompactText => $"{_captureBuffer.Count} 枚";

    public string RecordingOverlayActivityText
    {
        get
        {
            if (!HasRecordingOverlayStatus)
            {
                return string.Empty;
            }

            if (IsPaused)
            {
                return $"一時停止中 / 累計 {_captureBuffer.Count} 枚";
            }

            if (_lastCaptureRecordedAt is DateTime lastCapture)
            {
                return $"最新 {lastCapture:HH:mm:ss} / 累計 {_captureBuffer.Count} 枚";
            }

            return CaptureMode == ScreenshotCaptureMode.Interval
                ? "初回キャプチャ待機中"
                : "操作待機中";
        }
    }

    /// <summary>
    /// 操作履歴パネルを表示するか。起動直後は非表示、記録開始後または履歴があるときに表示します。
    /// </summary>
    public bool IsHistoryPanelVisible => IsSessionActive || OperationHistory.Count > 0;

    /// <summary>キャプチャの取り方（秒間隔 / 操作ごと）。</summary>
    public ScreenshotCaptureMode CaptureMode
    {
        get => _captureMode;
        set
        {
            if (_captureMode == value) return;
            _captureMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIntervalCaptureMode));
            OnPropertyChanged(nameof(IsOnOperationCaptureMode));
            OnPropertyChanged(nameof(IsCapturesPerSecondEnabled));
            OnPropertyChanged(nameof(IsScreenshotTriggerEditorEnabled));
            OnPropertyChanged(nameof(CaptureModeHelpText));
            if (value != ScreenshotCaptureMode.OnOperation)
            {
                IsScreenshotTriggerListening = false;
            }

            ScheduleSettingsSave();
            SyncCaptureTimerWithState();
        }
    }

    public bool IsIntervalCaptureMode
    {
        get => CaptureMode == ScreenshotCaptureMode.Interval;
        set
        {
            if (value) CaptureMode = ScreenshotCaptureMode.Interval;
        }
    }

    public bool IsOnOperationCaptureMode
    {
        get => CaptureMode == ScreenshotCaptureMode.OnOperation;
        set
        {
            if (value) CaptureMode = ScreenshotCaptureMode.OnOperation;
        }
    }

    /// <summary>枚/秒の入力は「秒でキャプチャ」モードのときだけ有効です。</summary>
    public bool IsCapturesPerSecondEnabled => CaptureMode == ScreenshotCaptureMode.Interval;

    public string CaptureModeHelpText => CaptureMode switch
    {
        ScreenshotCaptureMode.OnOperation =>
            "操作キャプチャモード: 下の枠で指定したキー／マウス操作のときだけスクリーンショットを撮ります。",
        _ =>
            "秒でキャプチャモード: 設定した枚/秒で連続撮影し、操作は最寄りのキャプチャに紐づけます。"
    };

    /// <summary>スクショ・トリガー枠は「操作でキャプチャ」のときだけ編集できます。</summary>
    public bool IsScreenshotTriggerEditorEnabled => CaptureMode == ScreenshotCaptureMode.OnOperation;

    /// <summary>操作でキャプチャ時にスクショを取る単一入力です。</summary>
    public CapturedInputBinding? ScreenshotTrigger
    {
        get => _screenshotTrigger;
        private set
        {
            _screenshotTrigger = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScreenshotTriggerDisplayText));
            OnPropertyChanged(nameof(HasScreenshotTrigger));
            ScheduleSettingsSave();
        }
    }

    public bool IsScreenshotTriggerListening
    {
        get => _isScreenshotTriggerListening;
        private set
        {
            if (_isScreenshotTriggerListening == value) return;
            _isScreenshotTriggerListening = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScreenshotTriggerDisplayText));
        }
    }

    public bool HasScreenshotTrigger => ScreenshotTrigger is not null;

    public string ScreenshotTriggerDisplayText
    {
        get
        {
            if (IsScreenshotTriggerListening) return "キーまたはマウスをクリック…";
            if (ScreenshotTrigger is not null) return ScreenshotTrigger.DisplayText;
            return "（未設定・クリックして入力）";
        }
    }

    public ICommand ClearScreenshotTriggerCommand { get; private set; } = null!;

    public int CapturesPerSecond
    {
        get => _capturesPerSecond;
        set
        {
            int clamped = Math.Clamp(value, MinCapturesPerSecond, MaxCapturesPerSecond);
            if (_capturesPerSecond == clamped) return;
            _capturesPerSecond = clamped;
            OnPropertyChanged();
            ApplyCaptureTimerInterval();
            ScheduleSettingsSave();
            TrimCaptureBuffer();
        }
    }

    /// <summary>秒でキャプチャ時: バッファ容量の目安（秒）。</summary>
    public int RetentionSeconds
    {
        get => _retentionSeconds;
        set
        {
            int clamped = Math.Clamp(value, MinRetentionSeconds, MaxRetentionSeconds);
            if (_retentionSeconds == clamped) return;
            _retentionSeconds = clamped;
            OnPropertyChanged();
            ScheduleSettingsSave();
            if (IsSessionActive)
            {
                TrimCaptureBuffer();
            }
        }
    }

    /// <summary>操作でキャプチャ時: 維持する最大枚数。</summary>
    public int KeepCaptureCount
    {
        get => _keepCaptureCount;
        set
        {
            int clamped = Math.Clamp(value, MinKeepCaptureCount, MaxKeepCaptureCount);
            if (_keepCaptureCount == clamped) return;
            _keepCaptureCount = clamped;
            OnPropertyChanged();
            ScheduleSettingsSave();
            if (IsSessionActive)
            {
                TrimCaptureBuffer();
            }
        }
    }

    /// <summary>
    /// 左/右クリック・ダブルクリック・ドラッグ・ホイールなど、マウス操作全般を記録するかです。
    /// </summary>
    public bool RecordMouseOperations
    {
        get => _recordMouseOperations;
        set => SetBoolSetting(ref _recordMouseOperations, value);
    }

    public bool RecordKeyboard
    {
        get => _recordKeyboard;
        set
        {
            if (_recordKeyboard == value) return;
            _recordKeyboard = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowKeyboardPrivacyNote));
            ScheduleSettingsSave();
            SyncKeyboardHookWithSettings();
        }
    }

    public bool ShowKeyboardPrivacyNote => RecordKeyboard;

    public string KeyboardPrivacyNote =>
        "キー入力は押した物理キー名を記録します（IME変換後ではありません）。パスワード等重要な情報を入力する際はキーボード記録をOFFにしてください。";

    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (ReferenceEquals(_selectedMonitor, value)) return;
            _selectedMonitor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CaptureRegionSummaryText));
            OnPropertyChanged(nameof(EffectiveCaptureRegion));
            ScheduleSettingsSave();
        }
    }

    /// <summary>キャプチャ範囲の決め方（全画面 / 自由クリップ）。</summary>
    public CaptureRegionMode CaptureRegionMode
    {
        get => _captureRegionMode;
        set
        {
            if (_captureRegionMode == value) return;
            _captureRegionMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFullScreenRegionMode));
            OnPropertyChanged(nameof(IsFreeClipRegionMode));
            OnPropertyChanged(nameof(CaptureRegionSummaryText));
            OnPropertyChanged(nameof(EffectiveCaptureRegion));
            ScheduleSettingsSave();
        }
    }

    public bool IsFullScreenRegionMode
    {
        get => CaptureRegionMode == Models.CaptureRegionMode.FullScreen;
        set { if (value) CaptureRegionMode = Models.CaptureRegionMode.FullScreen; }
    }

    public bool IsFreeClipRegionMode
    {
        get => CaptureRegionMode == Models.CaptureRegionMode.FreeClip;
        set { if (value) CaptureRegionMode = Models.CaptureRegionMode.FreeClip; }
    }

    /// <summary>「自由クリップ」で選んだ固定範囲（画面座標）。</summary>
    public WindowBounds? FreeClipRegion
    {
        get => _freeClipRegion;
        private set
        {
            _freeClipRegion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFreeClipRegion));
            OnPropertyChanged(nameof(CaptureRegionSummaryText));
            OnPropertyChanged(nameof(EffectiveCaptureRegion));
            RaiseCommandStates();
        }
    }

    public bool HasFreeClipRegion => FreeClipRegion is not null;

    /// <summary>Step1カードに表示する、現在選択中のキャプチャ範囲の説明文。</summary>
    public string CaptureRegionSummaryText => CaptureRegionMode switch
    {
        Models.CaptureRegionMode.FullScreen => SelectedMonitor is null
            ? "モニターが選択されていません。"
            : $"{SelectedMonitor.Label} を撮ります。",
        _ => FreeClipRegion is { } r
            ? $"{r.Width} × {r.Height} @ ({r.Left}, {r.Top}) を撮ります。"
            : "範囲が未選択です。「自由クリップ」をクリックして範囲を選んでください。"
    };

    /// <summary>
    /// 今の設定から解決される実効キャプチャ範囲です。枠オーバーレイの表示・位置決めに使います。
    /// 記録中は <see cref="StartRecording"/> 時点で確定した範囲と一致します（Step1の操作は記録中ロックされるため）。
    /// </summary>
    public WindowBounds? EffectiveCaptureRegion => ResolveEffectiveCaptureRegion();

    public ObservableCollection<HistoryItem> OperationHistory { get; } = new();

    /// <summary>手動選択パネル用のサムネイル一覧（記録停止時に <see cref="RefreshThumbnailFrames"/> で更新）。</summary>
    public ObservableCollection<CaptureFrameThumbnailViewModel> ThumbnailFrames { get; } = new();

    public int ThumbnailFrameCount => ThumbnailFrames.Count;

    public bool HasAnyThumbnailSelected => ThumbnailFrames.Any(t => t.IsSelected);

    public int SelectedThumbnailCount => ThumbnailFrames.Count(t => t.IsSelected);

    public HistoryItem? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set
        {
            if (ReferenceEquals(_selectedHistoryItem, value)) return;
            _selectedHistoryItem = value;
            OnPropertyChanged();
            UpdatePreviewImage();
        }
    }

    public BitmapImage? PreviewImage
    {
        get => _previewImage;
        private set
        {
            _previewImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreviewImage));
            OnPropertyChanged(nameof(PreviewPlaceholderText));
        }
    }

    public bool HasPreviewImage => PreviewImage is not null;

    public string PreviewPlaceholderText
    {
        get
        {
            if (SelectedHistoryItem is null)
            {
                return "履歴を選択すると、紐づいたキャプチャをプレビューできます。";
            }

            if (SelectedHistoryItem.Action is null && SelectedHistoryItem.Frame is null)
            {
                return "この行は状態メッセージです。キャプチャ行または操作行を選んでください。";
            }

            string? path = ResolvePreviewPath(SelectedHistoryItem);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "紐づくキャプチャがまだありません（連続キャプチャ開始直後など）。";
            }

            if (!File.Exists(path))
            {
                return "画像ファイルが見つかりません。";
            }

            return "プレビューを読み込めませんでした。";
        }
    }

    public BitmapImage? ConsultationPreviewImage
    {
        get => _consultationPreviewImage;
        private set
        {
            _consultationPreviewImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasConsultationPreview));
            OnPropertyChanged(nameof(HasMultipleGeneratedMontages));
            RaiseCommandStates();
        }
    }

    public bool HasConsultationPreview => ConsultationPreviewImage is not null;

    public bool HasMultipleGeneratedMontages => GeneratedMontagePaths.Count > 1;

    public string ConsultationPreviewPlaceholder =>
        HasConsultationPreview
            ? string.Empty
            : "プレビューがここに表示されます。";

    // ---- 相談用データ: 別窓「結合方法を変える」で使う設定 ----

    /// <summary>「結合方法を変える」時の1枚あたりのコマ数。「すべて」は int.MaxValue。</summary>
    public int FramesPerImage
    {
        get => _framesPerImage;
        set
        {
            int normalized = value == int.MaxValue
                ? int.MaxValue
                : Math.Clamp(value, MinFramesPerImage, MaxFramesPerImage);
            if (_framesPerImage == normalized) return;
            _framesPerImage = normalized;
            if (normalized != int.MaxValue)
            {
                _lastExplicitFramesPerImage = normalized;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFramesPerImageAll));
            OnPropertyChanged(nameof(FramesPerImageDisplay));
            ScheduleSettingsSave();
        }
    }

    /// <summary>「すべて」トグル。オンで <see cref="FramesPerImage"/> を int.MaxValue に、オフで直前の実数値に戻します。</summary>
    public bool IsFramesPerImageAll
    {
        get => FramesPerImage == int.MaxValue;
        set => FramesPerImage = value ? int.MaxValue : _lastExplicitFramesPerImage;
    }

    /// <summary>
    /// 数値入力欄（スピナー）の表示・編集用です。「すべて」がオンの間も直前の実数値を保持したまま表示し、
    /// 数値を変えるとその値がそのまま <see cref="FramesPerImage"/> に反映されます（1〜200）。
    /// </summary>
    public int FramesPerImageDisplay
    {
        get => FramesPerImage == int.MaxValue ? _lastExplicitFramesPerImage : FramesPerImage;
        set
        {
            int normalized = Math.Clamp(value, MinFramesPerImage, MaxFramesPerImage);
            _lastExplicitFramesPerImage = normalized;
            if (IsFramesPerImageAll)
            {
                OnPropertyChanged();
            }
            else
            {
                FramesPerImage = normalized;
            }
        }
    }

    /// <summary>「結合方法を変える」時の並び方（列数）。1〜12列を自由に指定できます。</summary>
    public int LayoutColumns
    {
        get => _layoutColumns;
        set
        {
            int normalized = Math.Clamp(value, MinLayoutColumns, MaxLayoutColumns);
            if (_layoutColumns == normalized) return;
            _layoutColumns = normalized;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    /// <summary>オンのときはコマの縦横比から列数を自動判定し、<see cref="LayoutColumns"/> は無視します。</summary>
    public bool IsLayoutAuto
    {
        get => _isLayoutAuto;
        set
        {
            if (_isLayoutAuto == value) return;
            _isLayoutAuto = value;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public ConsultationQualityPreset QualityPreset
    {
        get => _qualityPreset;
        set
        {
            if (_qualityPreset == value) return;
            _qualityPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsQualityTextPriority));
            OnPropertyChanged(nameof(IsQualityStandard));
            OnPropertyChanged(nameof(IsQualitySizePriority));
            ScheduleSettingsSave();
        }
    }

    public bool IsQualityTextPriority
    {
        get => QualityPreset == ConsultationQualityPreset.TextPriority;
        set { if (value) QualityPreset = ConsultationQualityPreset.TextPriority; }
    }

    public bool IsQualityStandard
    {
        get => QualityPreset == ConsultationQualityPreset.Standard;
        set { if (value) QualityPreset = ConsultationQualityPreset.Standard; }
    }

    public bool IsQualitySizePriority
    {
        get => QualityPreset == ConsultationQualityPreset.SizePriority;
        set { if (value) QualityPreset = ConsultationQualityPreset.SizePriority; }
    }

    /// <summary>
    /// true にすると、記録停止時の自動結合（「相談用データ生成」）にも
    /// 1枚あたりのコマ数・並び方・画質の設定を適用します（複数枚に分割されることがあります）。
    /// false（既定）のときは、従来どおり全コマを1枚に結合します。
    /// </summary>
    public bool UseCustomAutoCombineSettings
    {
        get => _useCustomAutoCombineSettings;
        set
        {
            if (_useCustomAutoCombineSettings == value) return;
            _useCustomAutoCombineSettings = value;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    /// <summary>直近の「選択画像を結合してコピー」で生成されたファイル群。</summary>
    public IReadOnlyList<string> GeneratedMontagePaths
    {
        get => _generatedMontagePaths;
        private set
        {
            _generatedMontagePaths = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMultipleGeneratedMontages));
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// 今クリップボードに実際に入っている画像（あれば）。「コピーした（つもり）」ではなく、
    /// OS のクリップボードから読み戻した実体を表示するため、貼り付け先アプリを開かなくても確認できます。
    /// </summary>
    public BitmapSource? ClipboardPreviewImage
    {
        get => _clipboardPreviewImage;
        private set
        {
            _clipboardPreviewImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasClipboardPreviewImage));
            OnPropertyChanged(nameof(HasAnyClipboardPreviewVisual));
        }
    }

    public bool HasClipboardPreviewImage => ClipboardPreviewImage is not null;

    /// <summary>
    /// <see cref="ClipboardPreviewImage"/> の元になった実ファイルのパス（分かる場合のみ）。
    /// クリップボードの生ビットマップにはパス情報が無いため、コピー時にアプリ側で記録したものです。
    /// 拡大ビューアーでの赤ペン編集など、実ファイルを開く操作に使います。
    /// </summary>
    public string? ClipboardPreviewImageSourcePath => _lastCopiedSingleImagePath;

    /// <summary>
    /// 今クリップボードにファイルとして入っている画像のサムネイル一覧です（複数ファイルコピー時）。
    /// ファイル名だけでなく中身も見て確認でき、クリックすれば拡大・赤ペン編集もできます。
    /// </summary>
    public IReadOnlyList<ClipboardPreviewFileEntry> ClipboardPreviewFileThumbnails
    {
        get => _clipboardPreviewFileThumbnails;
        private set
        {
            _clipboardPreviewFileThumbnails = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasClipboardPreviewFileThumbnails));
            OnPropertyChanged(nameof(HasAnyClipboardPreviewVisual));
        }
    }

    public bool HasClipboardPreviewFileThumbnails => ClipboardPreviewFileThumbnails.Count > 0;

    public bool HasAnyClipboardPreviewVisual => HasClipboardPreviewImage || HasClipboardPreviewFileThumbnails;

    public string ClipboardPreviewSummary
    {
        get => _clipboardPreviewSummary;
        private set
        {
            if (_clipboardPreviewSummary == value) return;
            _clipboardPreviewSummary = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string TempDirectory => _tempStorageService.TempDirectory;
    public string ConsultationImagePath => _tempStorageService.ConsultationImagePath;
    public bool IsMouseHookRunning => _mouseHookService.IsRunning;

    public ICommand RefreshMonitorsCommand { get; }
    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }
    /// <summary>記録中のみ有効。ユーザーが明示的に一時停止します（自動再開しません）。</summary>
    public ICommand PauseRecordingCommand { get; }
    /// <summary>一時停止中のみ有効。一時停止を解除します。</summary>
    public ICommand ResumeRecordingCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    /// <summary>スクショ一時フォルダをエクスプローラーで開きます。</summary>
    public ICommand OpenTempFolderCommand { get; }
    public ICommand CopyConsultationImageCommand { get; }

    public ICommand SelectAllThumbnailsCommand { get; }
    public ICommand DeselectAllThumbnailsCommand { get; }
    /// <summary>「選択画像を結合してコピー」: チェック済みサムネイルをコマ数ごとに分割結合し、そのままクリップボードへコピーします。</summary>
    public ICommand GenerateAndCopySelectedCombinedCommand { get; }
    /// <summary>「選択画像を結合せずコピー」: チェック済みの元画像を複数ファイルのままコピーします。</summary>
    public ICommand CopySelectedAsFilesCommand { get; }
    /// <summary>今クリップボードに実際に入っている内容を読み直し、確認パネルへ反映します。</summary>
    public ICommand RefreshClipboardPreviewCommand { get; }

    private void SetBoolSetting(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        ScheduleSettingsSave();
    }

    private void LoadSettings()
    {
        AppSettings s = _settingsService.Load();
        _capturesPerSecond = Math.Clamp(s.CapturesPerSecond, MinCapturesPerSecond, MaxCapturesPerSecond);
        _retentionSeconds = Math.Clamp(s.RetentionSeconds, MinRetentionSeconds, MaxRetentionSeconds);
        _keepCaptureCount = Math.Clamp(
            s.KeepCaptureCount <= 0 ? 20 : s.KeepCaptureCount,
            MinKeepCaptureCount,
            MaxKeepCaptureCount);
        _captureMode = ResolveCaptureMode(s);
        _recordMouseOperations = ResolveRecordMouseOperations(s);
        _recordKeyboard = s.RecordKeyboard;
        _screenshotTrigger = s.ScreenshotTrigger is { DisplayText: { Length: > 0 } }
            ? s.ScreenshotTrigger
            : CapturedInputBinding.DefaultLeftClick();

        _framesPerImage = s.ConsultationFramesPerImage <= 0 ? 6 : s.ConsultationFramesPerImage;
        _lastExplicitFramesPerImage = _framesPerImage == int.MaxValue ? 6 : _framesPerImage;
        _layoutColumns = Math.Clamp(
            s.ConsultationLayoutColumns <= 0 ? 2 : s.ConsultationLayoutColumns,
            MinLayoutColumns,
            MaxLayoutColumns);
        _isLayoutAuto = s.ConsultationLayoutIsAuto;
        _qualityPreset = ParseEnumOrDefault(s.ConsultationQualityPreset, ConsultationQualityPreset.Standard);
        _useCustomAutoCombineSettings = s.UseCustomAutoCombineSettings;

        _captureRegionMode = ParseEnumOrDefault(s.CaptureRegionMode, Models.CaptureRegionMode.FullScreen);
        _freeClipRegion = s is { FreeClipWidth: > 0, FreeClipHeight: > 0 }
            ? new WindowBounds(s.FreeClipLeft ?? 0, s.FreeClipTop ?? 0, s.FreeClipWidth!.Value, s.FreeClipHeight!.Value)
            : null;
        _savedMonitorDeviceId = s.SelectedMonitorDeviceId;
    }

    private static TEnum ParseEnumOrDefault<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static ScreenshotCaptureMode ResolveCaptureMode(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.CaptureMode)
            && Enum.TryParse(s.CaptureMode, ignoreCase: true, out ScreenshotCaptureMode mode)
            && Enum.IsDefined(mode))
        {
            return mode;
        }

        // 旧設定: 連続キャプチャ OFF なら操作キャプチャに近い扱い
        if (s.RecordPeriodicCapture == false)
        {
            return ScreenshotCaptureMode.OnOperation;
        }

        return ScreenshotCaptureMode.Interval;
    }

    /// <summary>
    /// 新設定を優先し、旧個別フラグだけの設定ファイルからは OR で復元します。
    /// </summary>
    private static bool ResolveRecordMouseOperations(AppSettings s)
    {
        bool hasLegacy =
            s.RecordLeftClick is not null
            || s.RecordRightClick is not null
            || s.RecordDoubleClick is not null
            || s.RecordMouseWheel is not null
            || s.RecordDrag is not null;

        if (!hasLegacy)
        {
            return s.RecordMouseOperations;
        }

        // 旧形式: いずれかが ON ならマウス操作記録 ON（全部 OFF なら OFF）
        return s.RecordLeftClick == true
               || s.RecordRightClick == true
               || s.RecordDoubleClick == true
               || s.RecordMouseWheel == true
               || s.RecordDrag == true;
    }

    private void PersistSettings()
    {
        if (_disposed) return;
        _settingsService.Save(new AppSettings
        {
            CapturesPerSecond = CapturesPerSecond,
            RetentionSeconds = RetentionSeconds,
            KeepCaptureCount = KeepCaptureCount,
            CaptureMode = CaptureMode.ToString(),
            RecordMouseOperations = RecordMouseOperations,
            RecordKeyboard = RecordKeyboard,
            ScreenshotTrigger = ScreenshotTrigger,
            ConsultationFramesPerImage = FramesPerImage,
            ConsultationLayoutIsAuto = IsLayoutAuto,
            ConsultationLayoutColumns = LayoutColumns,
            ConsultationQualityPreset = QualityPreset.ToString(),
            UseCustomAutoCombineSettings = UseCustomAutoCombineSettings,
            CaptureRegionMode = CaptureRegionMode.ToString(),
            SelectedMonitorDeviceId = SelectedMonitor?.DeviceId,
            FreeClipLeft = FreeClipRegion?.Left,
            FreeClipTop = FreeClipRegion?.Top,
            FreeClipWidth = FreeClipRegion?.Width,
            FreeClipHeight = FreeClipRegion?.Height
        });
    }

    /// <summary>入力待ちを開始します（枠クリック時）。</summary>
    public void BeginScreenshotTriggerListen()
    {
        if (!IsScreenshotTriggerEditorEnabled) return;
        IsScreenshotTriggerListening = true;
    }

    /// <summary>入力待ち中の枠へ、単一のキー／マウス操作を割り当てます。</summary>
    public bool TryAssignScreenshotTrigger(CapturedInputBinding binding)
    {
        if (!IsScreenshotTriggerListening || binding is null) return false;
        ScreenshotTrigger = binding;
        IsScreenshotTriggerListening = false;
        return true;
    }

    public void CancelScreenshotTriggerListen()
    {
        IsScreenshotTriggerListening = false;
    }

    private void ScheduleSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void RefreshMonitors()
    {
        if (!IsIdle)
        {
            StatusMessage = "記録中または一時停止中はモニター一覧を更新できません。";
            return;
        }

        string? prevDeviceId = SelectedMonitor?.DeviceId ?? _savedMonitorDeviceId;
        Monitors.Clear();
        foreach (MonitorInfo m in _monitorEnumerationService.GetMonitors())
        {
            Monitors.Add(m);
        }

        SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceId == prevDeviceId)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();
        _savedMonitorDeviceId = null;

        StatusMessage = Monitors.Count == 0
            ? "モニターが見つかりませんでした。"
            : $"モニター一覧を更新しました（{Monitors.Count} 件）。";
    }

    /// <summary>「自由クリップ」のドラッグ選択で選ばれた矩形を確定します。</summary>
    public void SetFreeClipRegion(WindowBounds region)
    {
        FreeClipRegion = region;
        ScheduleSettingsSave();
        StatusMessage = $"キャプチャ範囲を設定しました（{region.Width} × {region.Height}）。";
    }

    /// <summary>現在の設定（モード・選択モニター・自由クリップ矩形）から実効キャプチャ範囲を解決します。</summary>
    private WindowBounds? ResolveEffectiveCaptureRegion() => CaptureRegionMode switch
    {
        Models.CaptureRegionMode.FullScreen => SelectedMonitor?.Bounds,
        _ => FreeClipRegion
    };

    private void StartRecording()
    {
        if (IsSessionActive) return;

        WindowBounds? region = ResolveEffectiveCaptureRegion();
        if (region is null)
        {
            StatusMessage = "エラー: キャプチャ範囲が未設定です。";
            return;
        }

        // 「停止」からの新規開始はまっさらな状態から始める（前回のスクショ・履歴・相談用データを破棄）
        ClearAllRecordedData();

        _recordingSessionStartedAt = DateTime.Now;
        _recordingPausedAt = null;
        _recordingPausedTotal = TimeSpan.Zero;
        _lastCaptureRecordedAt = null;
        _captureRegion = region;
        RecordingOverlayNoticeText = string.Empty;
        State = RecordingState.Recording;
        RaiseRecordingOverlayStatusProperties();

        AddMessageHistory("記録を開始しました");
        StatusMessage = CaptureMode == ScreenshotCaptureMode.Interval
            ? $"記録中です。【秒でキャプチャ】{CapturesPerSecond} 枚/秒（最大おおよそ {MaxBufferCapacity} 枚）。操作は最寄りフレームに紐づけます。"
            : $"記録中です。【操作キャプチャ】操作のたびに撮影します（保持 {KeepCaptureCount} 枚まで）。";

        StartMouseHook();
        SyncKeyboardHookWithSettings();
        SyncCaptureTimerWithState();
    }

    /// <summary>記録中のみ実行可能。ユーザー操作による明示的な一時停止です。</summary>
    private void PauseRecording()
    {
        if (State != RecordingState.Recording) return;
        _recordingPausedAt ??= DateTime.Now;
        State = RecordingState.Paused;
        RaiseRecordingOverlayStatusProperties();
        AddMessageHistory("記録を一時停止しました");
        StatusMessage = "一時停止中です。「再開」を押すと記録を再開します。";
    }

    /// <summary>一時停止中のみ実行可能。</summary>
    private void ResumeRecording()
    {
        if (State != RecordingState.Paused) return;
        if (_recordingPausedAt is DateTime pausedAt)
        {
            _recordingPausedTotal += DateTime.Now - pausedAt;
            _recordingPausedAt = null;
        }

        State = RecordingState.Recording;
        RaiseRecordingOverlayStatusProperties();
        AddMessageHistory("記録を再開しました");
        StatusMessage = CaptureMode == ScreenshotCaptureMode.Interval
            ? $"記録中です（秒でキャプチャ {CapturesPerSecond} 枚/秒）。"
            : "記録中です（操作キャプチャモード）。";
    }

    private void StopRecording()
    {
        if (!IsSessionActive) return;
        FlushPendingInputs();
        FlushPendingIntervalLinks();
        FlushPendingOperationCaptureLinks();
        EndSessionCore(flushPending: false);
        AddMessageHistory("記録を停止しました");
        RaiseCommandStates();
        AutoGenerateConsultationAfterStop();
    }

    /// <summary>
    /// 記録停止直後、操作が1件以上あれば相談用データ（説明文＋結合画像）をシームレスに自動生成します。
    /// コメントを追記して作り直したいときは、同じボタンで再生成できます。
    /// 特定のコマだけを選びたい・結合せずコピーしたいときは、任意で「結合方法を変える」を使います。
    /// </summary>
    private void AutoGenerateConsultationAfterStop()
    {
        RefreshThumbnailFrames();

        if (_captureBuffer.Count > 0 || CountActionRecords() > 0)
        {
            GenerateConsultation();
            TryAutoCopyConsultationImageAfterStop();
        }
        else
        {
            StatusMessage = "\u8a18\u9332\u3092\u505c\u6b62\u3057\u307e\u3057\u305f\u3002\u64cd\u4f5c\u304c\u8a18\u9332\u3055\u308c\u3066\u3044\u306a\u3044\u305f\u3081\u3001\u76f8\u8ac7\u7528\u30c7\u30fc\u30bf\u306f\u4f5c\u6210\u3055\u308c\u307e\u305b\u3093\u3002";
        }
    }

    private void TryAutoCopyConsultationImageAfterStop()
    {
        if (!TryCopyGeneratedMontages(out _)) return;

        const string copiedSummary = "\u30b3\u30d4\u30fc\u3057\u307e\u3057\u305f\u3002\u305d\u306e\u307e\u307eAI\u306b\u8cbc\u308a\u4ed8\u3051\u53ef\u80fd\u3067\u3059";

        AddMessageHistory(copiedSummary, isHighlighted: true);
        RecordingOverlayNoticeText = copiedSummary;
        StatusMessage = copiedSummary;
    }

    /// <summary>
    /// 「記録データを今すぐ削除」。隣に「スクショ保存フォルダを開く」があり誤操作の懸念があるため、
    /// 実際に削除するものがあるときだけ確認ダイアログを挟みます。
    /// </summary>
    private void ClearHistory()
    {
        bool hasAnyData = OperationHistory.Count > 0 || _captureBuffer.Count > 0 || HasConsultationPreview;
        if (!hasAnyData)
        {
            StatusMessage = "削除する記録データがありません。";
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            Application.Current.MainWindow,
            "記録した操作履歴・キャプチャ画像・相談用データをすべて削除します。この操作は取り消せません。よろしいですか？",
            "記録データを今すぐ削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        ClearAllRecordedData();
        RaiseCommandStates();
        StatusMessage = "記録データと相談用データを削除しました。";
    }

    /// <summary>履歴・キャプチャ・相談用データを全消去します（「記録データ削除」および新規記録開始時に使用）。</summary>
    private void ClearAllRecordedData()
    {
        CancelPendingClickTimers();
        SelectedHistoryItem = null;
        PreviewImage = null;
        OperationHistory.Clear();
        ClearCaptureBuffer(deleteFiles: true);
        ClearConsultationResult();
        _tempStorageService.ClearAll();
        GeneratedMontagePaths = Array.Empty<string>();
        RefreshThumbnailFrames();
    }

    /// <summary>
    /// スクショ保存先（%LOCALAPPDATA%\TracePrompt\Temp）をエクスプローラーで開きます。
    /// </summary>
    private void OpenTempFolder()
    {
        try
        {
            _tempStorageService.EnsureDirectory();
            string path = _tempStorageService.TempDirectory;

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });

            StatusMessage = $"一時フォルダを開きました: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: 一時フォルダを開けませんでした。{ex.Message}";
        }
    }

    private void GenerateConsultation()
    {
        if (_isGeneratingConsultation) return;

        List<ConsultationTimelineEntry> timeline = BuildConsultationTimeline();
        List<RecordedAction> outOfRegionActions = CollectOutOfRegionActions();
        if (timeline.Count == 0 && outOfRegionActions.Count == 0)
        {
            StatusMessage =
                "エラー: キャプチャが0件です。記録中に対象を最前面にし、連続キャプチャを有効にしてから操作してください。";
            return;
        }

        _isGeneratingConsultation = true;
        RaiseCommandStates();
        try
        {
            if (UseCustomAutoCombineSettings)
            {
                _tempStorageService.TryDeleteConsultationImage();
                if (!TryBuildSplitConsultationImages(timeline, outOfRegionActions, out List<string> outputPaths, out string? splitError))
                {
                    GeneratedMontagePaths = outputPaths;
                    ConsultationPreviewImage = outputPaths.Count > 0 ? LoadBitmapImageWithoutLock(outputPaths[0]) : null;
                    StatusMessage = $"エラー: 相談用画像を作れませんでした。{splitError}";
                    return;
                }

                GeneratedMontagePaths = outputPaths;
                BitmapImage? splitPreview = LoadBitmapImageWithoutLock(outputPaths[0]);
                ConsultationPreviewImage = splitPreview;
                StatusMessage = splitPreview is null
                    ? "画像は生成しましたがプレビューに失敗しました。コピーは試せます。"
                    : (outputPaths.Count > 1
                        ? $"相談用データを生成しました（{outputPaths.Count} 枚に分割 / コマ {timeline.Count} / 操作 {CountActionRecords()} 件）。"
                        : $"相談用データを生成しました（コマ {timeline.Count} / 操作 {CountActionRecords()} 件）。");
                return;
            }

            _tempStorageService.TryDeleteSplitConsultationImages();
            string outputPath = _tempStorageService.ConsultationImagePath;
            List<RecordedAction> collapsedOutOfRegionActions = CollapseOutOfRegionActionsToSingleCard(outOfRegionActions);
            if (!_consultationImageService.TryBuildConsultationImage(timeline, collapsedOutOfRegionActions, outputPath, out string? imageError))
            {
                GeneratedMontagePaths = Array.Empty<string>();
                ConsultationPreviewImage = null;
                StatusMessage = $"エラー: 相談用画像を作れませんでした。{imageError}";
                return;
            }

            GeneratedMontagePaths = new[] { outputPath };
            BitmapImage? preview = LoadBitmapImageWithoutLock(outputPath);
            ConsultationPreviewImage = preview;
            StatusMessage = preview is null
                ? "画像は生成しましたがプレビューに失敗しました。コピーは試せます。"
                : $"相談用データを生成しました（コマ {timeline.Count} / 操作 {CountActionRecords()} 件）。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: 相談用データの生成中に問題が発生しました。{ex.Message}";
        }
        finally
        {
            _isGeneratingConsultation = false;
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// バッファ内キャプチャを時系列で並べ、各コマに紐づく操作を付けます。
    /// 操作付きコマとその前後コマを優先し、多すぎる場合は間の空コマを間引きます。
    /// </summary>
    private List<ConsultationTimelineEntry> BuildConsultationTimeline()
    {
        List<CaptureFrame> frames = _captureBuffer
            .Where(f => File.Exists(f.ScreenshotPath))
            .OrderBy(f => f.RecordedAt)
            .ThenBy(f => f.Id)
            .ToList();

        if (frames.Count == 0)
        {
            return new List<ConsultationTimelineEntry>();
        }

        Dictionary<long, List<RecordedAction>> byId = BuildActionsByCaptureId();

        // 多すぎる場合: 操作付き＋前後を優先
        const int softMaxPanels = 40;
        HashSet<long> includeIds;
        if (frames.Count <= softMaxPanels)
        {
            includeIds = frames.Select(f => f.Id).ToHashSet();
        }
        else
        {
            includeIds = new HashSet<long>();
            for (int i = 0; i < frames.Count; i++)
            {
                CaptureFrame f = frames[i];
                bool hasOp = byId.ContainsKey(f.Id);
                if (hasOp || i == 0 || i == frames.Count - 1)
                {
                    includeIds.Add(f.Id);
                    if (i > 0) includeIds.Add(frames[i - 1].Id);
                    if (i < frames.Count - 1) includeIds.Add(frames[i + 1].Id);
                }
            }

            // まだ多い場合は均等サンプル
            if (includeIds.Count > softMaxPanels)
            {
                includeIds = frames
                    .Where(f => byId.ContainsKey(f.Id))
                    .Select(f => f.Id)
                    .Concat(new[] { frames[0].Id, frames[^1].Id })
                    .Take(softMaxPanels)
                    .ToHashSet();
            }
        }

        return frames
            .Where(f => includeIds.Contains(f.Id))
            .Select(f => new ConsultationTimelineEntry(
                f,
                byId.TryGetValue(f.Id, out List<RecordedAction>? ops)
                    ? ops.OrderBy(a => a.RecordedAt).ToList()
                    : Array.Empty<RecordedAction>()))
            .ToList();
    }

    /// <summary>
    /// OperationHistory 中の操作を、紐づく CaptureFrame.Id ごとにグループ化します（時系列順）。
    /// 自動結合（<see cref="BuildConsultationTimeline"/>）と手動サムネイル一覧の両方から使う共通ロジックです。
    /// </summary>
    private Dictionary<long, List<RecordedAction>> BuildActionsByCaptureId()
    {
        List<RecordedAction> actions = OperationHistory
            .Where(h => h.Action is not null)
            .Select(h => h.Action!)
            .OrderBy(a => a.RecordedAt)
            .ToList();

        var byId = new Dictionary<long, List<RecordedAction>>();
        foreach (RecordedAction a in actions)
        {
            if (a.LinkedCaptureId is not long id) continue;
            if (!byId.TryGetValue(id, out List<RecordedAction>? list))
            {
                list = new List<RecordedAction>();
                byId[id] = list;
            }

            list.Add(a);
        }

        return byId;
    }

    /// <summary>
    /// キャプチャ範囲の外で起きた操作を時系列順に集めます。撮れる画像が無いため、
    /// 相談用画像にはテキストのみのカードとして挟み込みます。
    /// </summary>
    private List<RecordedAction> CollectOutOfRegionActions() => OperationHistory
        .Where(h => h.Action is { IsOutOfRegion: true })
        .Select(h => h.Action!)
        .OrderBy(a => a.RecordedAt)
        .ToList();

    /// <summary>
    /// 画像なしカード（<see cref="CollectOutOfRegionActions"/> の結果、または各グループへの割り振り後の
    /// 一部）を、1つの画像につき1枚のカードにまとめます。カードごとに何件あっても省略はしません。
    /// 種類をまたいでも行を分けず、1件ずつ「→」で横につないだ1行にします
    /// （例:「左クリック：(526, 545) → a×13 → Backspace×14」）。
    /// 同じ内容が連続するときは「値×件数」に畳み、連続しない場合（a→b→a 等）はそのまま並べます。
    /// この結果を渡された側（ConsultationImageService）は、各項目に既にラベルが入っているので
    /// TypeLabel を重ねて付けません。アプリ本体の操作履歴一覧（OperationHistory）には影響せず、
    /// 相談用画像の生成時にだけ使います。
    /// </summary>
    private static List<RecordedAction> CollapseOutOfRegionActionsToSingleCard(IReadOnlyList<RecordedAction> actions)
    {
        if (actions.Count == 0)
        {
            return new List<RecordedAction>();
        }

        var segments = new List<string>();
        int i = 0;
        while (i < actions.Count)
        {
            string label = DescribeForMergedCard(actions[i]);
            int count = 1;
            int j = i + 1;
            while (j < actions.Count && DescribeForMergedCard(actions[j]) == label)
            {
                count++;
                j++;
            }

            segments.Add(count > 1 ? $"{label}×{count}" : label);
            i = j;
        }

        string joined = string.Join(" → ", segments);

        RecordedAction first = actions[0];
        RecordedAction last = actions[^1];
        var merged = new RecordedAction(
            actionType: first.ActionType,
            recordedAt: first.RecordedAt,
            summary: joined,
            endedAt: last.RecordedAt,
            isOutOfRegion: true,
            isGeometricallyOutOfRegion: actions.Any(a => a.IsGeometricallyOutOfRegion));

        return new List<RecordedAction> { merged };
    }

    /// <summary>
    /// 結合カード内の1件分の表示文言です。キーボードは Summary 自体が押されたキー表示
    /// （例: Ctrl + S）で見出しとして十分なため、冗長な「キーボード：」は付けません。
    /// </summary>
    private static string DescribeForMergedCard(RecordedAction a) =>
        a.ActionType == RecordedActionType.Keyboard
            ? (a.KeyboardDisplay ?? a.Summary)
            : $"{a.TypeLabel}：{a.Summary}";

    private void CopyConsultationImage()
    {
        if (!TryCopyGeneratedMontages(out string? errorMessage))
        {
            StatusMessage = errorMessage!;
            return;
        }

        StatusMessage = GeneratedMontagePaths.Count > 1
            ? $"\u7d50\u5408\u753b\u50cf {GeneratedMontagePaths.Count} \u679a\u3092\u30b3\u30d4\u30fc\u3057\u307e\u3057\u305f\u3002\u540c\u3058 AI \u5165\u529b\u6b04\u3078\u7d9a\u3051\u3066\u8cbc\u308a\u4ed8\u3051\u3066\u304f\u3060\u3055\u3044\u3002"
            : "\u753b\u50cf\u3092\u30b3\u30d4\u30fc\u3057\u307e\u3057\u305f\u3002\u540c\u3058 AI \u5165\u529b\u6b04\u3078\u7d9a\u3051\u3066\u8cbc\u308a\u4ed8\u3051\u3066\u304f\u3060\u3055\u3044\u3002";
    }

    private bool TryCopyGeneratedMontages(out string? errorMessage)
    {
        errorMessage = null;
        if (GeneratedMontagePaths.Count == 0)
        {
            errorMessage = "\u30a8\u30e9\u30fc: \u30b3\u30d4\u30fc\u3067\u304d\u308b\u753b\u50cf\u304c\u3042\u308a\u307e\u305b\u3093\u3002";
            return false;
        }

        bool copyOk = GeneratedMontagePaths.Count == 1
            ? _clipboardService.TryCopyImageFromFile(GeneratedMontagePaths[0], out string? copyError)
            : _clipboardService.TryCopyFilesAsFileList(GeneratedMontagePaths, out copyError);

        if (!copyOk)
        {
            errorMessage = $"\u30a8\u30e9\u30fc: \u753b\u50cf\u306e\u30b3\u30d4\u30fc\u306b\u5931\u6557\u3057\u307e\u3057\u305f\u3002{copyError}";
            return false;
        }

        _lastCopiedSingleImagePath = GeneratedMontagePaths.Count == 1 ? GeneratedMontagePaths[0] : null;
        RefreshClipboardPreview();
        return true;
    }

    /// <summary>
    /// 今クリップボードに実際に入っている内容を読み直し、確認パネルへ反映します。
    /// 「コピーした（つもり）」で終わらせず、貼り付け先アプリを開かなくても中身を確認できるようにします。
    /// </summary>
    private void RefreshClipboardPreview()
    {
        ClipboardPreview preview = _clipboardService.GetPreview();

        if (preview.Image is not null)
        {
            ClipboardPreviewImage = preview.Image;
            ClipboardPreviewFileThumbnails = Array.Empty<ClipboardPreviewFileEntry>();
            ClipboardPreviewSummary = $"画像 1枚（{preview.Image.PixelWidth}×{preview.Image.PixelHeight}px）がコピーされています。";
            OnPropertyChanged(nameof(ClipboardPreviewImageSourcePath));
            return;
        }

        ClipboardPreviewImage = null;
        _lastCopiedSingleImagePath = null;
        OnPropertyChanged(nameof(ClipboardPreviewImageSourcePath));

        if (preview.FilePaths.Count > 0)
        {
            ClipboardPreviewFileThumbnails = preview.FilePaths
                .Select(p => new ClipboardPreviewFileEntry(p, LoadClipboardPreviewThumbnail(p)))
                .ToList();
            ClipboardPreviewSummary = preview.FilePaths.Count == 1
                ? $"ファイル 1個がコピーされています: {Path.GetFileName(preview.FilePaths[0])}"
                : $"ファイル {preview.FilePaths.Count}個がコピーされています。";
            return;
        }

        ClipboardPreviewFileThumbnails = Array.Empty<ClipboardPreviewFileEntry>();

        if (!string.IsNullOrEmpty(preview.Text))
        {
            ClipboardPreviewSummary = $"テキスト（{preview.Text!.Length}文字）がコピーされています。";
            return;
        }

        ClipboardPreviewSummary = "クリップボードに画像・ファイルがありません。";
    }

    /// <summary>クリップボード確認パネル用の小さなサムネイルを、ファイルをロックせずに読み込みます。</summary>
    private static BitmapImage? LoadClipboardPreviewThumbnail(string path)
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
            image.DecodePixelWidth = 220;
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

    // ---- 手動選択（サムネイル一覧）まわり ----

    /// <summary>
    /// _captureBuffer から手動選択パネル用のサムネイル一覧を作り直します。
    /// 既存の Id と一致するものは選択状態を引き継ぎ、新規フレームは選択済み（true）で追加します。
    /// </summary>
    private void RefreshThumbnailFrames()
    {
        Dictionary<long, bool> previousSelection = ThumbnailFrames.ToDictionary(t => t.Frame.Id, t => t.IsSelected);

        foreach (CaptureFrameThumbnailViewModel old in ThumbnailFrames)
        {
            old.PropertyChanged -= OnThumbnailSelectionChanged;
        }

        ThumbnailFrames.Clear();

        List<CaptureFrame> frames = _captureBuffer
            .Where(f => File.Exists(f.ScreenshotPath))
            .OrderBy(f => f.RecordedAt)
            .ThenBy(f => f.Id)
            .ToList();

        if (frames.Count > 0)
        {
            Dictionary<long, List<RecordedAction>> byId = BuildActionsByCaptureId();
            foreach (CaptureFrame frame in frames)
            {
                bool isSelected = true;
                if (previousSelection.TryGetValue(frame.Id, out bool prev))
                {
                    isSelected = prev;
                }

                IReadOnlyList<RecordedAction> linked = byId.TryGetValue(frame.Id, out List<RecordedAction>? ops)
                    ? ops.OrderBy(a => a.RecordedAt).ToList()
                    : Array.Empty<RecordedAction>();

                var vm = new CaptureFrameThumbnailViewModel(frame, linked, isSelected);
                vm.PropertyChanged += OnThumbnailSelectionChanged;
                ThumbnailFrames.Add(vm);
            }
        }

        OnPropertyChanged(nameof(ThumbnailFrameCount));
        OnPropertyChanged(nameof(HasAnyThumbnailSelected));
        OnPropertyChanged(nameof(SelectedThumbnailCount));
        RaiseCommandStates();
    }

    private void OnThumbnailSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CaptureFrameThumbnailViewModel.IsSelected)) return;
        OnPropertyChanged(nameof(HasAnyThumbnailSelected));
        OnPropertyChanged(nameof(SelectedThumbnailCount));
        RaiseCommandStates();
    }

    private void SelectAllThumbnails()
    {
        foreach (CaptureFrameThumbnailViewModel t in ThumbnailFrames)
        {
            t.IsSelected = true;
        }
    }

    private void DeselectAllThumbnails()
    {
        foreach (CaptureFrameThumbnailViewModel t in ThumbnailFrames)
        {
            t.IsSelected = false;
        }
    }

    /// <summary>
    /// 「選択画像を結合してコピー」。チェック済みサムネイルを、現在の表示順のまま
    /// FramesPerImage 枚ごと・高さ上限ごとに分割して複数の結合画像を作り、そのままクリップボードへコピーします。
    /// 高さ上限を超えた場合はキャンバスを再縮小せず、次の画像へ回します。
    /// </summary>
    private void GenerateAndCopySelectedCombined()
    {
        if (_isGeneratingSelectedCombined) return;

        List<CaptureFrameThumbnailViewModel> selected = ThumbnailFrames.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "エラー: 画像が選択されていません。";
            return;
        }

        _isGeneratingSelectedCombined = true;
        RaiseCommandStates();
        try
        {
            List<ConsultationTimelineEntry> timeline = selected
                .Select(t => new ConsultationTimelineEntry(t.Frame, t.LinkedActions))
                .ToList();

            if (!TryBuildSplitConsultationImages(timeline, Array.Empty<RecordedAction>(), out List<string> outputPaths, out string? buildError))
            {
                GeneratedMontagePaths = outputPaths;
                StatusMessage = $"エラー: 結合画像を作れませんでした。{buildError}";
                return;
            }

            GeneratedMontagePaths = outputPaths;

            ConsultationPreviewImage = LoadBitmapImageWithoutLock(outputPaths[0]);

            bool copyOk = outputPaths.Count == 1
                ? _clipboardService.TryCopyImageFromFile(outputPaths[0], out string? copyError)
                : _clipboardService.TryCopyFilesAsFileList(outputPaths, out copyError);

            StatusMessage = copyOk
                ? (outputPaths.Count > 1
                    ? $"選択した {selected.Count} 枚を {outputPaths.Count} 枚の結合画像に分割してコピーしました。AI 入力欄へ貼り付けてください。"
                    : $"選択した {selected.Count} 枚を1枚の結合画像にまとめてコピーしました。AI 入力欄へ貼り付けてください。")
                : $"結合はできましたが、コピーに失敗しました。{copyError}";
            if (copyOk)
            {
                _lastCopiedSingleImagePath = outputPaths.Count == 1 ? outputPaths[0] : null;
                RefreshClipboardPreview();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: 結合中に問題が発生しました。{ex.Message}";
        }
        finally
        {
            _isGeneratingSelectedCombined = false;
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// timeline を FramesPerImage 枚ごと・高さ上限ごとに分割し、複数の結合画像ファイルを書き出します
    /// （「選択画像を結合してコピー」と、設定を適用した自動結合の両方から使う共通処理です）。
    /// outOfRegionActions は、タイムスタンプが最も近いグループへ振り分けて挟み込みます。
    /// </summary>
    private bool TryBuildSplitConsultationImages(
        List<ConsultationTimelineEntry> timeline,
        IReadOnlyList<RecordedAction> outOfRegionActions,
        out List<string> outputPaths,
        out string? errorMessage)
    {
        outputPaths = new List<string>();
        errorMessage = null;

        int resolvedColumns = IsLayoutAuto
            ? ConsultationSplitService.ResolveAutoColumns(
                timeline,
                e => _consultationImageService.TryGetAspectRatio(e.Frame.ScreenshotPath))
            : LayoutColumns;

        List<List<ConsultationTimelineEntry>> groups = ConsultationSplitService.SplitIntoGroups(
            timeline,
            FramesPerImage,
            e => _consultationImageService.EstimatePanelHeight(e, ConsultationImageService.DefaultMaxScreenshotWidth),
            ConsultationImageService.SoftMaxTotalHeight);

        _tempStorageService.TryDeleteSplitConsultationImages();

        // 実キャプチャが0件（画面外操作のみ）のときは、テキストのみの1枚にまとめます。
        if (groups.Count == 0 && outOfRegionActions.Count > 0)
        {
            string singlePath = _tempStorageService.GetSplitConsultationImagePath(1);
            List<RecordedAction> collapsed = CollapseOutOfRegionActionsToSingleCard(outOfRegionActions);
            if (!_consultationImageService.TryBuildConsultationImage(
                    new List<ConsultationTimelineEntry>(), collapsed, singlePath, resolvedColumns, QualityPreset, out errorMessage))
            {
                return false;
            }

            outputPaths.Add(singlePath);
            return true;
        }

        List<RecordedAction>[] outOfRegionByGroup = AssignOutOfRegionActionsToGroups(groups, outOfRegionActions);

        for (int i = 0; i < groups.Count; i++)
        {
            string outputPath = _tempStorageService.GetSplitConsultationImagePath(i + 1);
            List<RecordedAction> collapsedGroup = CollapseOutOfRegionActionsToSingleCard(outOfRegionByGroup[i]);
            if (!_consultationImageService.TryBuildConsultationImage(
                    groups[i], collapsedGroup, outputPath, resolvedColumns, QualityPreset, out string? groupError))
            {
                errorMessage = $"{i + 1} 枚目の結合画像を作れませんでした。{groupError}";
                return false;
            }

            outputPaths.Add(outputPath);
        }

        return true;
    }

    /// <summary>
    /// 画面外での操作を、時系列上いちばん近いグループへ振り分けます
    /// （グループの時間帯に含まれれば距離0、外れていれば端との差を距離とします）。
    /// </summary>
    private static List<RecordedAction>[] AssignOutOfRegionActionsToGroups(
        List<List<ConsultationTimelineEntry>> groups,
        IReadOnlyList<RecordedAction> outOfRegionActions)
    {
        var buckets = new List<RecordedAction>[groups.Count];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new List<RecordedAction>();
        }

        if (groups.Count == 0)
        {
            return buckets;
        }

        foreach (RecordedAction action in outOfRegionActions)
        {
            int bestIndex = 0;
            double bestDistanceMs = double.MaxValue;

            for (int i = 0; i < groups.Count; i++)
            {
                List<ConsultationTimelineEntry> group = groups[i];
                if (group.Count == 0) continue;

                DateTime start = group[0].Frame.RecordedAt;
                DateTime end = group[^1].Frame.RecordedAt;
                double distanceMs = action.RecordedAt < start
                    ? (start - action.RecordedAt).TotalMilliseconds
                    : action.RecordedAt > end
                        ? (action.RecordedAt - end).TotalMilliseconds
                        : 0;

                if (distanceMs < bestDistanceMs)
                {
                    bestDistanceMs = distanceMs;
                    bestIndex = i;
                }
            }

            buckets[bestIndex].Add(action);
        }

        return buckets;
    }

    /// <summary>「選択画像を結合せずコピー」。チェック済みの元画像を、表示順のまま複数ファイルでコピーします。</summary>
    private void CopySelectedAsFiles()
    {
        List<string> paths = ThumbnailFrames
            .Where(t => t.IsSelected)
            .Select(t => t.Frame.ScreenshotPath)
            .ToList();

        if (paths.Count == 0)
        {
            StatusMessage = "エラー: 画像が選択されていません。";
            return;
        }

        bool copyOk = paths.Count == 1
            ? _clipboardService.TryCopyImageFromFile(paths[0], out string? error)
            : _clipboardService.TryCopyFilesAsFileList(paths, out error);

        if (!copyOk)
        {
            StatusMessage = $"エラー: 画像のコピーに失敗しました。{error}";
            return;
        }

        _lastCopiedSingleImagePath = paths.Count == 1 ? paths[0] : null;
        StatusMessage = $"選択した {paths.Count} 枚を個別ファイルとしてコピーしました。AI 入力欄へ貼り付けてください。";
        RefreshClipboardPreview();
    }

    private void ClearConsultationResult()
    {
        ConsultationPreviewImage = null;
        GeneratedMontagePaths = Array.Empty<string>();
        _tempStorageService.TryDeleteConsultationImage();
        _tempStorageService.TryDeleteSplitConsultationImages();
    }

    // ---- タイマー / フック ----

    private void StartMouseHook()
    {
        try
        {
            if (!_mouseHookService.IsRunning) _mouseHookService.Start();
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: マウス監視を開始できませんでした。{ex.Message}";
        }
    }

    private void StopMouseHook()
    {
        try { _mouseHookService.Stop(); }
        catch { /* ignore */ }
    }

    private void SyncKeyboardHookWithSettings()
    {
        if (_disposed) return;
        try
        {
            if (IsSessionActive && RecordKeyboard)
            {
                if (!_keyboardHookService.IsRunning) _keyboardHookService.Start();
            }
            else
            {
                _keyboardHookService.Stop();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: キーボード監視を切り替えられませんでした。{ex.Message}";
        }
    }

    private void EndSessionCore(bool flushPending = true)
    {
        if (flushPending) FlushPendingInputs();
        CancelPendingClickTimers();
        StopCaptureTimer();
        StopMouseHook();
        try { _keyboardHookService.Stop(); } catch { /* ignore */ }
        ResetPointerTracking();
        _captureRegion = null;
        _recordingSessionStartedAt = null;
        _recordingPausedAt = null;
        _recordingPausedTotal = TimeSpan.Zero;
        _lastCaptureRecordedAt = null;
        State = RecordingState.Stopped;
        RaiseRecordingOverlayStatusProperties();
    }

    private void ResetPointerTracking()
    {
        _leftTracking = false;
        _leftIsDragging = false;
        _rightTracking = false;
        _pendingLeftClick = null;
        _pendingRightClick = null;
        _wheelHasPending = false;
        _wheelAccumDelta = 0;
        _wheelFlushTimer.Stop();
    }

    private void FlushPendingInputs()
    {
        if (_leftTracking)
        {
            if (_leftIsDragging && RecordMouseOperations)
            {
                CommitDrag(_leftDownAt, DateTime.Now, _leftDownX, _leftDownY, _leftDownX, _leftDownY, _leftDownBounds);
            }
            else if (RecordMouseOperations)
            {
                CommitClickAction(RecordedActionType.LeftClick, _leftDownAt, _leftDownX, _leftDownY, _leftDownBounds);
            }

            _leftTracking = false;
            _leftIsDragging = false;
        }

        if (_rightTracking)
        {
            if (RecordMouseOperations)
            {
                CommitClickAction(RecordedActionType.RightClick, _rightDownAt, _rightDownX, _rightDownY, _rightDownBounds);
            }

            _rightTracking = false;
        }

        CommitPendingLeftClickIfAny();
        CommitPendingRightClickIfAny();
        FlushWheelAccumulated();
    }

    private void OnCaptureTimerTick(object? sender, EventArgs e) => CapturePeriodicFrame();

    // ---- 連続キャプチャ ----

    private int MaxBufferCapacity =>
        CaptureMode == ScreenshotCaptureMode.OnOperation
            ? Math.Clamp(KeepCaptureCount, MinKeepCaptureCount, AbsoluteMaxFrames)
            : Math.Clamp(CapturesPerSecond * RetentionSeconds + 8, 10, AbsoluteMaxFrames);

    private void CapturePeriodicFrame()
    {
        if (_disposed || CaptureMode != ScreenshotCaptureMode.Interval || State != RecordingState.Recording) return;

        if (!TrySaveScreenshotPng(out string? path, out string? error) || path is null)
        {
            StatusMessage = $"警告: 定期キャプチャに失敗しました。{error}";
            return;
        }

        // 秒モードでは履歴にもキャプチャ行を出す
        AddCaptureFrame(DateTime.Now, path, isAuxiliary: false, addToHistory: true);
    }

    private CaptureFrame AddCaptureFrame(DateTime at, string path, bool isAuxiliary, bool addToHistory)
    {
        long id = _nextCaptureId++;
        var frame = new CaptureFrame(id, at, path, isAuxiliary);
        _captureBuffer.Add(frame);
        if (addToHistory)
        {
            OperationHistory.Insert(0, HistoryItem.FromFrame(frame));
        }

        // 保護対象（TrimCaptureBuffer が見る LinkedCaptureId）を確定させてからトリムします。
        ResolvePendingIntervalLinks(frame);
        ResolvePendingOperationCaptureLinks(frame);
        TrimCaptureBuffer();
        _lastCaptureRecordedAt = at;
        RaiseRecordingOverlayStatusProperties();
        RaiseCommandStates();
        return frame;
    }

    /// <summary>
    /// 操作にキャプチャを紐づけつつ履歴へ追加します。
    /// ・操作キャプチャモード: 設定したトリガーに一致するときは、その場で1枚撮って即確定します。
    ///   一致しないマウス操作（左右クリック・ホイール・ドラッグ）は範囲内で起きている前提のため、
    ///   <see cref="_pendingOperationCaptureLinks"/> に積んで次にトリガーが一致し実際に撮れた瞬間
    ///   （<see cref="ResolvePendingOperationCaptureLinks"/>）に遡って紐づけ直し、位置マークを描けるように
    ///   します。キーボードには位置の概念が無いため、一致しなければ即「キャプチャなしの操作」にします。
    /// ・秒でキャプチャモード: 操作した瞬間には「次に撮られる定期キャプチャ」がまだ存在しないため、
    ///   いったん未紐づけのまま追加し、<see cref="_pendingIntervalLinks"/> に積んで
    ///   次の定期キャプチャが実際に撮れた瞬間（<see cref="ResolvePendingIntervalLinks"/>）に
    ///   遡って紐づけ直します。記録終了までどの定期キャプチャも来なければ
    ///   <see cref="FlushPendingIntervalLinks"/> が「キャプチャなしの操作」として確定させます。
    /// </summary>
    private RecordedAction AddActionWithCaptureLink(
        DateTime moment,
        RecordedActionType actionType,
        Func<CaptureFrame?, RecordedAction> buildAction,
        int? keyboardVk = null,
        bool keyboardCtrl = false,
        bool keyboardAlt = false,
        bool keyboardShift = false,
        bool keyboardWin = false)
    {
        if (CaptureMode == ScreenshotCaptureMode.OnOperation)
        {
            CaptureFrame? frame = ResolveOperationCapture(moment, actionType, keyboardVk, keyboardCtrl, keyboardAlt, keyboardShift, keyboardWin);
            if (frame is null && actionType != RecordedActionType.Keyboard)
            {
                // ここに来る呼び出し元（CommitClickAction/CommitDrag/ホイールの範囲内ブランチ）は
                // すでに範囲内判定を終えているため、位置マークを次のキャプチャへ持ち越せます。
                RecordedAction pendingMouse = buildAction(null);
                AddActionHistory(pendingMouse);
                _pendingOperationCaptureLinks.Add((buildAction, OperationHistory[0]));
                return pendingMouse;
            }

            RecordedAction resolved = buildAction(frame);
            AddActionHistory(resolved);
            return resolved;
        }

        RecordedAction pending = buildAction(null);
        AddActionHistory(pending);
        _pendingIntervalLinks.Add((buildAction, OperationHistory[0]));
        return pending;
    }

    /// <summary>秒でキャプチャモードで保留中の操作を、いま撮れた定期キャプチャへ遡って紐づけます。</summary>
    private void ResolvePendingIntervalLinks(CaptureFrame newFrame) => ResolvePendingLinks(_pendingIntervalLinks, newFrame);

    /// <summary>
    /// 記録停止時、次の定期キャプチャが来ないまま残った保留中の操作を、
    /// 「キャプチャなしの操作」として確定させます。StopRecording から FlushPendingInputs の後に呼びます。
    /// </summary>
    private void FlushPendingIntervalLinks() => ResolvePendingLinks(_pendingIntervalLinks, null);

    /// <summary>操作キャプチャモードで保留中のマウス操作を、いま撮れたキャプチャへ遡って紐づけます。</summary>
    private void ResolvePendingOperationCaptureLinks(CaptureFrame newFrame) => ResolvePendingLinks(_pendingOperationCaptureLinks, newFrame);

    /// <summary>
    /// 記録停止時、次のトリガーが来ないまま残った保留中のマウス操作を、
    /// 「キャプチャなしの操作」として確定させます。
    /// </summary>
    private void FlushPendingOperationCaptureLinks() => ResolvePendingLinks(_pendingOperationCaptureLinks, null);

    private void ResolvePendingLinks(
        List<(Func<CaptureFrame?, RecordedAction> BuildAction, HistoryItem Item)> pendingLinks,
        CaptureFrame? frame)
    {
        if (pendingLinks.Count == 0) return;

        foreach (var pending in pendingLinks)
        {
            RecordedAction resolved = pending.BuildAction(frame);
            int index = OperationHistory.IndexOf(pending.Item);
            if (index >= 0)
            {
                OperationHistory[index] = HistoryItem.FromAction(resolved);
            }
        }

        pendingLinks.Clear();
    }

    /// <summary>操作キャプチャモード専用: 設定した単一トリガーに一致するときだけ1枚撮影します。</summary>
    private CaptureFrame? ResolveOperationCapture(
        DateTime moment,
        RecordedActionType actionType,
        int? keyboardVk = null,
        bool keyboardCtrl = false,
        bool keyboardAlt = false,
        bool keyboardShift = false,
        bool keyboardWin = false)
    {
        if (!IsScreenshotTriggerMatch(actionType, keyboardVk, keyboardCtrl, keyboardAlt, keyboardShift, keyboardWin))
        {
            return null;
        }

        return TryCaptureForOperation(moment);
    }

    /// <summary>
    /// キーボードの修飾キー状態（ctrl/alt/shift/win）は、呼び出し元（KeyboardHookService）が
    /// GetAsyncKeyState で取得したグローバルな値をそのまま受け取ります。
    /// System.Windows.Input.Keyboard.Modifiers / Keyboard.IsKeyDown は WPF の入力管理が追跡する
    /// フォーカス依存の状態のため、記録対象（自アプリの外の別ウィンドウ）にフォーカスがあるとき
    /// 正しく更新されず、操作キャプチャのトリガー判定が常に不一致になるバグの原因になっていました。
    /// </summary>
    private bool IsScreenshotTriggerMatch(
        RecordedActionType actionType,
        int? keyboardVk,
        bool keyboardCtrl = false,
        bool keyboardAlt = false,
        bool keyboardShift = false,
        bool keyboardWin = false)
    {
        CapturedInputBinding? t = ScreenshotTrigger;
        // 未設定なら操作キャプチャでは撮らない（必ず枠で指定）
        if (t is null) return false;

        if (t.Kind == CapturedInputKind.Mouse)
        {
            return t.MouseButton switch
            {
                "Left" => actionType is RecordedActionType.LeftClick
                    or RecordedActionType.LeftDoubleClick
                    or RecordedActionType.Drag,
                "Right" => actionType is RecordedActionType.RightClick
                    or RecordedActionType.RightDoubleClick,
                "Wheel" => actionType == RecordedActionType.MouseWheel,
                _ => false
            };
        }

        if (t.Kind == CapturedInputKind.Keyboard
            && actionType == RecordedActionType.Keyboard
            && keyboardVk is int vk
            && t.VirtualKey == vk)
        {
            return t.Ctrl == keyboardCtrl && t.Alt == keyboardAlt && t.Shift == keyboardShift && t.Win == keyboardWin;
        }

        return false;
    }

    /// <summary>操作タイミングで1枚撮影しバッファへ入れます（履歴行は操作側に任せる）。</summary>
    private CaptureFrame? TryCaptureForOperation(DateTime moment)
    {
        if (State != RecordingState.Recording)
        {
            return null;
        }

        if (!TrySaveScreenshotPng(out string? path, out _) || path is null)
        {
            return null;
        }

        // 操作キャプチャは履歴に「キャプチャ行」を増やさず、操作行のプレビューに使う
        return AddCaptureFrame(moment, path, isAuxiliary: true, addToHistory: false);
    }

    private bool TrySaveScreenshotPng(out string? path, out string? error)
    {
        path = _tempStorageService.CreateScreenshotFilePath();
        error = null;

        if (_captureRegion is not { } region)
        {
            error = "キャプチャ範囲が未設定です。";
            _tempStorageService.TryDeleteFile(path);
            path = null;
            return false;
        }

        if (_screenshotService.TryCaptureRegionToPng(region, path, out error))
        {
            return true;
        }

        _tempStorageService.TryDeleteFile(path);
        path = null;
        return false;
    }

    /// <summary>
    /// リングバッファ: 容量超過時、操作と無関係な古いフレームから削除。
    /// 操作紐づけフレームとその直前・直後は優先保持。記録停止後は自動削除しない。
    /// </summary>
    private void TrimCaptureBuffer()
    {
        if (!IsSessionActive) return;

        int capacity = MaxBufferCapacity;
        if (_captureBuffer.Count <= capacity) return;

        HashSet<long> linkedIds = OperationHistory
            .Where(h => h.Action?.LinkedCaptureId is not null)
            .Select(h => h.Action!.LinkedCaptureId!.Value)
            .ToHashSet();

        List<CaptureFrame> ordered = _captureBuffer.OrderBy(f => f.RecordedAt).ThenBy(f => f.Id).ToList();
        var protectedIds = new HashSet<long>(linkedIds);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!linkedIds.Contains(ordered[i].Id)) continue;
            if (i > 0) protectedIds.Add(ordered[i - 1].Id);
            if (i < ordered.Count - 1) protectedIds.Add(ordered[i + 1].Id);
        }

        // 古い順に、保護されていないものから削除
        var removable = ordered.Where(f => !protectedIds.Contains(f.Id)).ToList();
        int needRemove = _captureBuffer.Count - capacity;
        foreach (CaptureFrame frame in removable)
        {
            if (needRemove <= 0) break;
            RemoveCaptureFrame(frame);
            needRemove--;
        }

        // まだ多い場合は保護付きでも最古から（操作は残す）
        while (_captureBuffer.Count > capacity && _captureBuffer.Count > 0)
        {
            CaptureFrame? victim = _captureBuffer
                .OrderBy(f => protectedIds.Contains(f.Id) ? 1 : 0)
                .ThenBy(f => f.RecordedAt)
                .FirstOrDefault();
            if (victim is null) break;
            // 操作に直接リンクされたものは最後まで残す
            if (linkedIds.Contains(victim.Id) && _captureBuffer.Count(f => linkedIds.Contains(f.Id)) <= linkedIds.Count)
            {
                // リンク済みが全部だと削れない → 容量超過を許容
                if (_captureBuffer.All(f => linkedIds.Contains(f.Id))) break;
            }

            if (linkedIds.Contains(victim.Id)) break;
            RemoveCaptureFrame(victim);
        }
    }

    private void RemoveCaptureFrame(CaptureFrame frame)
    {
        _captureBuffer.RemoveAll(f => f.Id == frame.Id);
        for (int i = OperationHistory.Count - 1; i >= 0; i--)
        {
            if (OperationHistory[i].Frame?.Id == frame.Id)
            {
                if (ReferenceEquals(SelectedHistoryItem, OperationHistory[i]))
                {
                    SelectedHistoryItem = null;
                }

                OperationHistory.RemoveAt(i);
            }
        }

        _tempStorageService.TryDeleteFile(frame.ScreenshotPath);
    }

    private void ClearCaptureBuffer(bool deleteFiles)
    {
        if (deleteFiles)
        {
            foreach (CaptureFrame f in _captureBuffer)
            {
                _tempStorageService.TryDeleteFile(f.ScreenshotPath);
            }
        }

        _captureBuffer.Clear();
        _lastCaptureRecordedAt = null;
        RaiseRecordingOverlayStatusProperties();
    }

    private void ApplyCaptureTimerInterval()
    {
        double ms = 1000.0 / Math.Max(1, _capturesPerSecond);
        _captureTimer.Interval = TimeSpan.FromMilliseconds(ms);
    }

    private void SyncCaptureTimerWithState()
    {
        if (_disposed) return;
        if (State == RecordingState.Recording && CaptureMode == ScreenshotCaptureMode.Interval)
        {
            ApplyCaptureTimerInterval();
            if (!_captureTimer.IsEnabled) _captureTimer.Start();
        }
        else
        {
            StopCaptureTimer();
        }
    }

    private void SyncRecordingOverlayTimer()
    {
        if (_disposed) return;
        if (State == RecordingState.Recording)
        {
            if (!_recordingOverlayTimer.IsEnabled)
            {
                _recordingOverlayTimer.Start();
            }
        }
        else
        {
            _recordingOverlayTimer.Stop();
        }
    }

    private TimeSpan GetRecordingOverlayElapsed()
    {
        if (_recordingSessionStartedAt is not DateTime startedAt)
        {
            return TimeSpan.Zero;
        }

        DateTime now = _recordingPausedAt ?? DateTime.Now;
        TimeSpan elapsed = now - startedAt - _recordingPausedTotal;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private static string FormatRecordingOverlayElapsed(TimeSpan elapsed)
    {
        int hours = (int)elapsed.TotalHours;
        return hours > 0
            ? $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static string FormatRecordingElapsedCompact(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    private void RaiseRecordingOverlayStatusProperties()
    {
        OnPropertyChanged(nameof(HasRecordingOverlayStatus));
        OnPropertyChanged(nameof(RecordingOverlayElapsedText));
        OnPropertyChanged(nameof(RecordingOverlayActivityText));
        OnPropertyChanged(nameof(RecordingElapsedCompactText));
        OnPropertyChanged(nameof(CaptureCountCompactText));
    }

    private void StopCaptureTimer()
    {
        if (_captureTimer.IsEnabled) _captureTimer.Stop();
    }

    // ---- マウス / キーボード（操作のみ。画像は紐づけ） ----

    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _ = _dispatcher.BeginInvoke(action);
    }

    private bool CanRecordNow() => !_disposed && State == RecordingState.Recording;

    private bool CanRecordPointerInRegion(out WindowBounds bounds)
    {
        bounds = default;
        if (!CanRecordNow() || _captureRegion is not { } region) return false;
        bounds = region;
        return true;
    }

    private static void RelativeFromBounds(
        WindowBounds bounds, int screenX, int screenY,
        out int windowX, out int windowY, out double relativeX, out double relativeY)
    {
        windowX = screenX - bounds.Left;
        windowY = screenY - bounds.Top;
        relativeX = bounds.Width > 0 ? Clamp01((double)windowX / bounds.Width) : 0;
        relativeY = bounds.Height > 0 ? Clamp01((double)windowY / bounds.Height) : 0;
    }

    private static bool IsWithinDoubleClick(int x1, int y1, DateTime t1, int x2, int y2, DateTime t2)
    {
        int timeMs = MouseHookService.GetOsDoubleClickTimeMs();
        if ((t2 - t1).TotalMilliseconds > timeMs) return false;
        (int w, int h) = MouseHookService.GetOsDoubleClickSize();
        return Math.Abs(x2 - x1) <= w / 2 && Math.Abs(y2 - y1) <= h / 2;
    }

    private static bool ExceedsDragThreshold(int x1, int y1, int x2, int y2)
    {
        (int w, int h) = MouseHookService.GetOsDragThreshold();
        return Math.Abs(x2 - x1) >= Math.Max(4, w) || Math.Abs(y2 - y1) >= Math.Max(4, h);
    }

    private void ProcessLeftButtonDown(int screenX, int screenY)
    {
        if (OwnWindowHitTest.IsPointOverOwnProcessWindow(screenX, screenY)) return;
        if (!CanRecordPointerInRegion(out WindowBounds bounds)) return;
        if (!bounds.Contains(screenX, screenY))
        {
            if (RecordMouseOperations)
            {
                CommitOutOfRegionClickAction(RecordedActionType.LeftClick, DateTime.Now, screenX, screenY);
            }

            return;
        }

        DateTime now = DateTime.Now;

        if (_pendingLeftClick is not null
            && IsWithinDoubleClick(_pendingLeftClick.ScreenX, _pendingLeftClick.ScreenY, _pendingLeftClick.At, screenX, screenY, now))
        {
            _leftClickCommitTimer.Stop();
            PendingClick first = _pendingLeftClick;
            _pendingLeftClick = null;

            if (RecordMouseOperations)
            {
                // ダブルクリックとして1件記録（シングルは出さない）
                CommitClickAction(RecordedActionType.LeftDoubleClick, now, screenX, screenY, first.Bounds);
            }

            return;
        }

        CommitPendingLeftClickIfAny();
        if (RecordMouseOperations)
        {
            BeginLeftTracking(now, screenX, screenY, bounds);
        }
    }

    private void BeginLeftTracking(DateTime now, int screenX, int screenY, WindowBounds bounds)
    {
        _leftTracking = true;
        _leftIsDragging = false;
        _leftDownAt = now;
        _leftDownX = screenX;
        _leftDownY = screenY;
        _leftDownBounds = bounds;
    }

    private void ProcessMouseMoveWhileLeftDown(int screenX, int screenY)
    {
        if (!_leftTracking || _leftIsDragging) return;
        if (ExceedsDragThreshold(_leftDownX, _leftDownY, screenX, screenY))
        {
            _leftIsDragging = true;
            if (_pendingLeftClick is not null)
            {
                _leftClickCommitTimer.Stop();
                _pendingLeftClick = null;
            }
        }
    }

    private void ProcessLeftButtonUp(int screenX, int screenY)
    {
        if (!_leftTracking) return;
        _leftTracking = false;
        DateTime now = DateTime.Now;

        if (_leftIsDragging)
        {
            _leftIsDragging = false;
            if (RecordMouseOperations && CanRecordNow())
            {
                CommitDrag(_leftDownAt, now, _leftDownX, _leftDownY, screenX, screenY, _leftDownBounds);
            }

            return;
        }

        if (!RecordMouseOperations) return;

        _pendingLeftClick = new PendingClick
        {
            At = _leftDownAt,
            ScreenX = _leftDownX,
            ScreenY = _leftDownY,
            Bounds = _leftDownBounds,
            IsLeft = true
        };
        _leftClickCommitTimer.Stop();
        _leftClickCommitTimer.Interval = TimeSpan.FromMilliseconds(MouseHookService.GetOsDoubleClickTimeMs());
        _leftClickCommitTimer.Start();
    }

    private void CommitPendingLeftClickIfAny()
    {
        if (_pendingLeftClick is null) return;
        PendingClick pending = _pendingLeftClick;
        _pendingLeftClick = null;
        _leftClickCommitTimer.Stop();
        if (RecordMouseOperations)
        {
            CommitClickAction(RecordedActionType.LeftClick, pending.At, pending.ScreenX, pending.ScreenY, pending.Bounds);
        }
    }

    private void ProcessRightButtonDown(int screenX, int screenY)
    {
        if (OwnWindowHitTest.IsPointOverOwnProcessWindow(screenX, screenY)) return;
        if (!CanRecordPointerInRegion(out WindowBounds bounds)) return;
        if (!bounds.Contains(screenX, screenY))
        {
            if (RecordMouseOperations)
            {
                CommitOutOfRegionClickAction(RecordedActionType.RightClick, DateTime.Now, screenX, screenY);
            }

            return;
        }

        DateTime now = DateTime.Now;

        if (_pendingRightClick is not null
            && IsWithinDoubleClick(_pendingRightClick.ScreenX, _pendingRightClick.ScreenY, _pendingRightClick.At, screenX, screenY, now))
        {
            _rightClickCommitTimer.Stop();
            PendingClick first = _pendingRightClick;
            _pendingRightClick = null;
            if (RecordMouseOperations)
            {
                CommitClickAction(RecordedActionType.RightDoubleClick, now, screenX, screenY, first.Bounds);
            }

            return;
        }

        CommitPendingRightClickIfAny();
        if (!RecordMouseOperations) return;

        _rightTracking = true;
        _rightDownAt = now;
        _rightDownX = screenX;
        _rightDownY = screenY;
        _rightDownBounds = bounds;
    }

    private void ProcessRightButtonUp(int screenX, int screenY)
    {
        if (!_rightTracking) return;
        _rightTracking = false;
        if (!RecordMouseOperations) return;

        _pendingRightClick = new PendingClick
        {
            At = _rightDownAt,
            ScreenX = _rightDownX,
            ScreenY = _rightDownY,
            Bounds = _rightDownBounds,
            IsLeft = false
        };
        _rightClickCommitTimer.Stop();
        _rightClickCommitTimer.Interval = TimeSpan.FromMilliseconds(MouseHookService.GetOsDoubleClickTimeMs());
        _rightClickCommitTimer.Start();
    }

    private void CommitPendingRightClickIfAny()
    {
        if (_pendingRightClick is null) return;
        PendingClick pending = _pendingRightClick;
        _pendingRightClick = null;
        _rightClickCommitTimer.Stop();
        if (RecordMouseOperations)
        {
            CommitClickAction(RecordedActionType.RightClick, pending.At, pending.ScreenX, pending.ScreenY, pending.Bounds);
        }
    }

    private void CommitClickAction(
        RecordedActionType type,
        DateTime at,
        int screenX,
        int screenY,
        WindowBounds bounds)
    {
        RelativeFromBounds(bounds, screenX, screenY, out int wx, out int wy, out double rx, out double ry);

        RecordedAction BuildAction(CaptureFrame? frame) => new(
            actionType: type,
            recordedAt: at,
            summary: $"画面座標({screenX}, {screenY}) / 相対({rx * 100:0.0}%, {ry * 100:0.0}%)",
            linkedCaptureId: frame?.Id,
            screenshotPath: frame?.ScreenshotPath,
            screenX: screenX,
            screenY: screenY,
            windowX: wx,
            windowY: wy,
            relativeX: rx,
            relativeY: ry,
            windowBounds: bounds,
            isOutOfRegion: frame is null);

        RecordedAction action = AddActionWithCaptureLink(at, type, BuildAction);
        StatusMessage = action.LinkedCaptureId is not null
            ? $"{action.TypeLabel}を記録（キャプチャ紐づけ / 操作 {CountActionRecords()} 件）。"
            : CaptureMode == ScreenshotCaptureMode.Interval
                ? $"{action.TypeLabel}を記録（次のキャプチャに紐づけ予定 / 操作 {CountActionRecords()} 件）。"
                : $"{action.TypeLabel}を記録（画像なし / 操作 {CountActionRecords()} 件）。";
    }

    /// <summary>
    /// キャプチャ範囲の外でのクリックを記録します。撮れる画像が無いため、紐づけ・相対位置計算はしません。
    /// ダブルクリック判定やドラッグ追跡もせず、押した瞬間に単発の記録として確定します。
    /// </summary>
    private void CommitOutOfRegionClickAction(RecordedActionType type, DateTime at, int screenX, int screenY)
    {
        var action = new RecordedAction(
            actionType: type,
            recordedAt: at,
            summary: $"画面座標({screenX}, {screenY})",
            screenX: screenX,
            screenY: screenY,
            isOutOfRegion: true,
            isGeometricallyOutOfRegion: true);

        AddActionHistory(action);
        StatusMessage = $"画面外での{action.TypeLabel}を記録（操作 {CountActionRecords()} 件）。";
    }

    private void CommitDrag(
        DateTime start,
        DateTime end,
        int x1, int y1, int x2, int y2,
        WindowBounds bounds)
    {
        RelativeFromBounds(bounds, x1, y1, out _, out _, out double rx1, out double ry1);
        RelativeFromBounds(bounds, x2, y2, out _, out _, out double rx2, out double ry2);
        double dist = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        string dir = DescribeDirection(x2 - x1, y2 - y1);

        RecordedAction BuildAction(CaptureFrame? frame) => new(
            actionType: RecordedActionType.Drag,
            recordedAt: start,
            summary: $"{dir}へ移動（距離 {dist:0}px）",
            linkedCaptureId: frame?.Id,
            screenshotPath: frame?.ScreenshotPath,
            endedAt: end,
            screenX: x1,
            screenY: y1,
            endScreenX: x2,
            endScreenY: y2,
            relativeX: rx1,
            relativeY: ry1,
            endRelativeX: rx2,
            endRelativeY: ry2,
            windowBounds: bounds,
            dragDistance: dist,
            dragDirection: dir,
            isOutOfRegion: frame is null);

        AddActionWithCaptureLink(start, RecordedActionType.Drag, BuildAction);
        StatusMessage = $"ドラッグを記録（操作 {CountActionRecords()} 件）。";
    }

    private static string DescribeDirection(int dx, int dy)
    {
        if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return "ほぼその場";
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? "左から右" : "右から左";
        return dy >= 0 ? "上から下" : "下から上";
    }

    private void ProcessMouseWheel(int screenX, int screenY, int delta)
    {
        if (!RecordMouseOperations || !CanRecordPointerInRegion(out WindowBounds bounds))
        {
            return;
        }

        if (OwnWindowHitTest.IsPointOverOwnProcessWindow(screenX, screenY))
        {
            return;
        }

        if (!_wheelHasPending)
        {
            _wheelHasPending = true;
            _wheelAccumDelta = 0;
        }

        _wheelAccumDelta += delta;
        _wheelScreenX = screenX;
        _wheelScreenY = screenY;
        _wheelBounds = bounds;
        _wheelIsOutOfRegion = !bounds.Contains(screenX, screenY);
        _wheelFlushTimer.Stop();
        _wheelFlushTimer.Start();
    }

    private void FlushWheelAccumulated()
    {
        if (!_wheelHasPending || _wheelAccumDelta == 0)
        {
            _wheelHasPending = false;
            _wheelAccumDelta = 0;
            return;
        }

        if (!RecordMouseOperations || !CanRecordNow())
        {
            _wheelHasPending = false;
            _wheelAccumDelta = 0;
            return;
        }

        int delta = _wheelAccumDelta;
        int sx = _wheelScreenX;
        int sy = _wheelScreenY;
        WindowBounds bounds = _wheelBounds;
        bool isOutOfRegion = _wheelIsOutOfRegion;
        _wheelHasPending = false;
        _wheelAccumDelta = 0;

        DateTime now = DateTime.Now;
        bool up = delta > 0;
        int notches = Math.Max(1, (int)Math.Round(Math.Abs(delta) / 120.0));
        string dir = up ? "上方向" : "下方向";

        RecordedAction action;
        if (isOutOfRegion)
        {
            action = new RecordedAction(
                actionType: RecordedActionType.MouseWheel,
                recordedAt: now,
                summary: $"{dir}に{notches}回スクロール（画面座標 {sx}, {sy}）",
                screenX: sx,
                screenY: sy,
                wheelDelta: delta,
                wheelNotchCount: notches,
                wheelUp: up,
                isOutOfRegion: true,
                isGeometricallyOutOfRegion: true);
            AddActionHistory(action);
        }
        else
        {
            RelativeFromBounds(bounds, sx, sy, out _, out _, out double rx, out double ry);

            RecordedAction BuildAction(CaptureFrame? frame) => new(
                actionType: RecordedActionType.MouseWheel,
                recordedAt: now,
                summary: $"{dir}に{notches}回スクロール",
                linkedCaptureId: frame?.Id,
                screenshotPath: frame?.ScreenshotPath,
                screenX: sx,
                screenY: sy,
                relativeX: rx,
                relativeY: ry,
                windowBounds: bounds,
                wheelDelta: delta,
                wheelNotchCount: notches,
                wheelUp: up,
                isOutOfRegion: frame is null);

            action = AddActionWithCaptureLink(now, RecordedActionType.MouseWheel, BuildAction);
        }

        StatusMessage = $"ホイールを記録（{action.Summary}）。";
    }

    private void ProcessKeyDown(int virtualKey, string displayLabel, bool ctrl, bool alt, bool shift, bool win)
    {
        if (!RecordKeyboard || !CanRecordNow()) return;
        if (OwnWindowHitTest.IsForegroundWindowOwnProcess()) return;
        DateTime now = DateTime.Now;

        // キャプチャに紐づかなかった（操作キャプチャのトリガー不一致・秒キャプチャで次の定期フレームが
        // まだ来ていない等）キーボード操作は、IsOutOfRegion=true にしてテキストのみのカードとして
        // 相談用画像に含めます（表示文言は RecordedAction.NoImageReasonLabel が「画面外」ではなく
        // 中立的な言い方にします）。これを付けないと、BuildActionsByCaptureId（LinkedCaptureId 必須）にも
        // CollectOutOfRegionActions（IsOutOfRegion 必須）にも拾われず、画像から完全に抜け落ちていました。
        RecordedAction BuildAction(CaptureFrame? frame) => new(
            actionType: RecordedActionType.Keyboard,
            recordedAt: now,
            summary: displayLabel,
            linkedCaptureId: frame?.Id,
            screenshotPath: frame?.ScreenshotPath,
            keyboardDisplay: displayLabel,
            isOutOfRegion: frame is null);

        AddActionWithCaptureLink(now, RecordedActionType.Keyboard, BuildAction, virtualKey, ctrl, alt, shift, win);
        StatusMessage = $"キーボードを記録（{displayLabel}）。";
    }

    private void CancelPendingClickTimers()
    {
        _leftClickCommitTimer.Stop();
        _rightClickCommitTimer.Stop();
        _pendingLeftClick = null;
        _pendingRightClick = null;
    }

    public void ProcessLeftClickForTest(int screenX, int screenY)
    {
        if (!CanRecordPointerInRegion(out WindowBounds bounds) || !bounds.Contains(screenX, screenY)) return;
        CommitClickAction(RecordedActionType.LeftClick, DateTime.Now, screenX, screenY, bounds);
    }

    public void GenerateConsultationForTest() => GenerateConsultation();

    private void OnOperationHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsHistoryPanelVisible));
    }

    private void AddMessageHistory(string message, bool isHighlighted = false) =>
        OperationHistory.Insert(0, HistoryItem.FromMessage(message, isHighlighted));

    private void AddActionHistory(RecordedAction action)
    {
        OperationHistory.Insert(0, HistoryItem.FromAction(action));
        RaiseCommandStates();
    }

    private int CountActionRecords() => OperationHistory.Count(h => h.Action is not null);

    private static string? ResolvePreviewPath(HistoryItem item)
    {
        if (item.Frame is not null) return item.Frame.ScreenshotPath;
        if (item.Action?.ScreenshotPath is { } p) return p;
        return null;
    }

    /// <summary>
    /// 履歴プレビューに表示中の画像ファイルパスを取得します。
    /// </summary>
    public bool TryGetHistoryPreviewPath(out string path)
    {
        path = string.Empty;
        if (SelectedHistoryItem is null) return false;
        string? resolved = ResolvePreviewPath(SelectedHistoryItem);
        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved)) return false;
        path = resolved;
        return true;
    }

    /// <summary>
    /// 相談用結合画像のパスを取得します。
    /// </summary>
    public bool TryGetConsultationPreviewPath(out string path)
    {
        path = string.Empty;
        string? resolved = GeneratedMontagePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(resolved) || !HasConsultationPreview || !File.Exists(resolved)) return false;
        path = resolved;
        return true;
    }

    /// <summary>履歴プレビュー画像をファイルから読み直します（注釈保存後など）。</summary>
    public void ReloadHistoryPreview()
    {
        UpdatePreviewImage();
        StatusMessage = "履歴プレビューの画像を更新しました（赤ペン記入を保存済み）。";
    }

    /// <summary>相談用画像プレビューをファイルから読み直します（注釈保存後など）。</summary>
    public void ReloadConsultationPreview()
    {
        string? path = GeneratedMontagePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ConsultationPreviewImage = null;
            return;
        }

        ConsultationPreviewImage = LoadBitmapImageWithoutLock(path);
        RaiseCommandStates();
        StatusMessage = "相談用画像を更新しました（赤ペン記入を保存済み）。";
    }

    private void UpdatePreviewImage()
    {
        PreviewImage = null;
        string? path = SelectedHistoryItem is null ? null : ResolvePreviewPath(SelectedHistoryItem);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            OnPropertyChanged(nameof(PreviewPlaceholderText));
            return;
        }

        PreviewImage = LoadBitmapImageWithoutLock(path);
        OnPropertyChanged(nameof(PreviewPlaceholderText));
    }

    public bool TryGetConsultationPreviewPathFromEntry(object? entry, out string path)
    {
        path = string.Empty;
        if (entry is not string imagePath || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        path = imagePath;
        return true;
    }

    private static BitmapImage? LoadBitmapImageWithoutLock(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
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

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private void RaiseCommandStates()
    {
        (RefreshMonitorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyConsultationImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllThumbnailsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeselectAllThumbnailsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GenerateAndCopySelectedCombinedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopySelectedAsFilesCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PersistSettings();

        _captureTimer.Tick -= OnCaptureTimerTick;
        _settingsSaveTimer.Stop();
        _recordingOverlayTimer.Stop();

        CancelPendingClickTimers();
        StopCaptureTimer();
        StopMouseHook();
        try { _keyboardHookService.Stop(); } catch { /* ignore */ }
        _mouseHookService.Dispose();
        _keyboardHookService.Dispose();
        _captureRegion = null;

        SelectedHistoryItem = null;
        PreviewImage = null;
        ConsultationPreviewImage = null;
        OperationHistory.CollectionChanged -= OnOperationHistoryCollectionChanged;
        OperationHistory.Clear();
        foreach (CaptureFrameThumbnailViewModel t in ThumbnailFrames)
        {
            t.PropertyChanged -= OnThumbnailSelectionChanged;
        }

        ThumbnailFrames.Clear();
        // アプリ終了時に一時ファイルを削除する仕様は廃止しました。次回起動時も残しておきます。
        ClearCaptureBuffer(deleteFiles: false);
    }
}

/// <summary>
/// クリップボード確認パネルの複数ファイル欄に表示する1件分です。
/// サムネイルだけでなく元のファイルパスも保持し、クリックしたときに拡大・赤ペン編集できるようにします。
/// </summary>
public sealed class ClipboardPreviewFileEntry
{
    public ClipboardPreviewFileEntry(string path, BitmapImage? thumbnail)
    {
        Path = path;
        Thumbnail = thumbnail;
    }

    public string Path { get; }
    public BitmapImage? Thumbnail { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
}





