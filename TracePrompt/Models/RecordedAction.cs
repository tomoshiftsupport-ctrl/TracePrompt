namespace TracePrompt.Models;

/// <summary>
/// マウス／キーボード操作1件分です。
/// 画像は操作ごとに撮らず、最も近い定期キャプチャ（LinkedCaptureId）に紐づけます。
/// </summary>
public sealed class RecordedAction
{
    public RecordedAction(
        RecordedActionType actionType,
        DateTime recordedAt,
        string summary,
        long? linkedCaptureId = null,
        string? screenshotPath = null,
        DateTime? endedAt = null,
        int? screenX = null,
        int? screenY = null,
        int? endScreenX = null,
        int? endScreenY = null,
        int? windowX = null,
        int? windowY = null,
        double? relativeX = null,
        double? relativeY = null,
        double? endRelativeX = null,
        double? endRelativeY = null,
        WindowBounds? windowBounds = null,
        string? keyboardDisplay = null,
        int? wheelDelta = null,
        int? wheelNotchCount = null,
        bool? wheelUp = null,
        double? dragDistance = null,
        string? dragDirection = null,
        bool isOutOfRegion = false)
    {
        ActionType = actionType;
        RecordedAt = recordedAt;
        Summary = summary ?? string.Empty;
        LinkedCaptureId = linkedCaptureId;
        ScreenshotPath = screenshotPath;
        EndedAt = endedAt;
        ScreenX = screenX;
        ScreenY = screenY;
        EndScreenX = endScreenX;
        EndScreenY = endScreenY;
        WindowX = windowX;
        WindowY = windowY;
        RelativeX = relativeX;
        RelativeY = relativeY;
        EndRelativeX = endRelativeX;
        EndRelativeY = endRelativeY;
        WindowBounds = windowBounds;
        KeyboardDisplay = keyboardDisplay;
        WheelDelta = wheelDelta;
        WheelNotchCount = wheelNotchCount;
        WheelUp = wheelUp;
        DragDistance = dragDistance;
        DragDirection = dragDirection;
        IsOutOfRegion = isOutOfRegion;
    }

    public RecordedActionType ActionType { get; }
    public DateTime RecordedAt { get; }
    public DateTime? EndedAt { get; }
    public string Summary { get; }

    /// <summary>紐づいたキャプチャフレームの ID です。</summary>
    public long? LinkedCaptureId { get; }

    /// <summary>紐づいたキャプチャのパス（生成時点のスナップショット）です。</summary>
    public string? ScreenshotPath { get; }

    public int? ScreenX { get; }
    public int? ScreenY { get; }
    public int? EndScreenX { get; }
    public int? EndScreenY { get; }
    public int? WindowX { get; }
    public int? WindowY { get; }
    public double? RelativeX { get; }
    public double? RelativeY { get; }
    public double? EndRelativeX { get; }
    public double? EndRelativeY { get; }
    public WindowBounds? WindowBounds { get; }
    public string? KeyboardDisplay { get; }
    public int? WheelDelta { get; }
    public int? WheelNotchCount { get; }
    public bool? WheelUp { get; }
    public double? DragDistance { get; }
    public string? DragDirection { get; }

    /// <summary>キャプチャ範囲の外で起きた操作か。true の場合、画像には紐づかず「画面外での操作」として扱います。</summary>
    public bool IsOutOfRegion { get; }

    public string TypeLabel => ActionType switch
    {
        RecordedActionType.LeftClick => "左クリック",
        RecordedActionType.RightClick => "右クリック",
        RecordedActionType.LeftDoubleClick => "左ダブルクリック",
        RecordedActionType.RightDoubleClick => "右ダブルクリック",
        RecordedActionType.MouseWheel => "ホイール",
        RecordedActionType.Drag => "ドラッグ",
        RecordedActionType.Keyboard => "キーボード",
        _ => "操作"
    };

    public string ToHistoryText()
    {
        // 操作キャプチャで画像が付いた行は【画面】を付け、キャプチャ無し操作と区別する
        string captureMark = string.IsNullOrWhiteSpace(ScreenshotPath) ? string.Empty : " 【画面】";
        string label = IsOutOfRegion ? $"画面外での操作（{TypeLabel}）" : TypeLabel;
        return $"{RecordedAt:HH:mm:ss.fff}{captureMark} {label}：{Summary}";
    }

    public string ToConsultationDetail() =>
        IsOutOfRegion ? $"画面外での操作（{TypeLabel}）：{Summary}" : $"{TypeLabel}：{Summary}";
}
