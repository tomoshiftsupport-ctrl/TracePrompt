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
        bool isOutOfRegion = false,
        bool isGeometricallyOutOfRegion = false)
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
        IsGeometricallyOutOfRegion = isGeometricallyOutOfRegion;
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

    /// <summary>
    /// 画像に紐づかず、テキストのみのカードとして扱うべき操作か。
    /// 実際にキャプチャ範囲の外で起きた場合（<see cref="IsGeometricallyOutOfRegion"/>）だけでなく、
    /// 範囲内で起きたのに紐づく画像が無かった場合（操作キャプチャのトリガー不一致など）も true にします
    /// （<see cref="NoImageReasonLabel"/> がこの違いに応じて表示文言を出し分けます）。
    /// </summary>
    public bool IsOutOfRegion { get; }

    /// <summary>
    /// 実際にキャプチャ範囲の外でマウス操作が起きたか。<see cref="IsOutOfRegion"/> と異なり、
    /// 「範囲内だが紐づく画像が無かっただけ」のケースは含みません。範囲座標という概念が無い
    /// キーボード操作では常に false です。
    /// </summary>
    public bool IsGeometricallyOutOfRegion { get; }

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

    /// <summary>
    /// 画像が紐づかないときの見出し文言です。実際に範囲外で起きたときだけ「画面外での操作」とし、
    /// 範囲内なのに紐づく画像が無かっただけのとき（キーボードに限らずマウスも含む）は、
    /// 誤解を招かないよう中立的な文言にします。
    /// </summary>
    public string NoImageReasonLabel =>
        IsGeometricallyOutOfRegion ? "画面外での操作" : "キャプチャなしの操作";

    /// <summary>
    /// 紐づいていた画像だけを外した複製を返します。保持枚数の上限を超えて古い画像が削除されたときに、
    /// 操作そのものの記録（種別・座標・時刻など）は残しつつ「画像なしの操作」として扱えるようにします。
    /// </summary>
    public RecordedAction WithoutImage()
    {
        if (LinkedCaptureId is null && ScreenshotPath is null) return this;

        return new RecordedAction(
            actionType: ActionType,
            recordedAt: RecordedAt,
            summary: Summary,
            linkedCaptureId: null,
            screenshotPath: null,
            endedAt: EndedAt,
            screenX: ScreenX,
            screenY: ScreenY,
            endScreenX: EndScreenX,
            endScreenY: EndScreenY,
            windowX: WindowX,
            windowY: WindowY,
            relativeX: RelativeX,
            relativeY: RelativeY,
            endRelativeX: EndRelativeX,
            endRelativeY: EndRelativeY,
            windowBounds: WindowBounds,
            keyboardDisplay: KeyboardDisplay,
            wheelDelta: WheelDelta,
            wheelNotchCount: WheelNotchCount,
            wheelUp: WheelUp,
            dragDistance: DragDistance,
            dragDirection: DragDirection,
            isOutOfRegion: true,
            isGeometricallyOutOfRegion: IsGeometricallyOutOfRegion);
    }

    public string ToHistoryText()
    {
        // 操作キャプチャで画像が付いた行は【画面】を付け、キャプチャ無し操作と区別する
        string captureMark = string.IsNullOrWhiteSpace(ScreenshotPath) ? string.Empty : " 【画面】";
        // キーボードは Summary 自体が押されたキー表示（例: Ctrl + S）で見出しとして十分なため、
        // 冗長な「キーボード：」は付けません。
        string body = ActionType == RecordedActionType.Keyboard && !IsOutOfRegion
            ? Summary
            : $"{(IsOutOfRegion ? $"{NoImageReasonLabel}（{TypeLabel}）" : TypeLabel)}：{Summary}";
        return $"{RecordedAt:HH:mm:ss.fff}{captureMark} {body}";
    }

    public string ToConsultationDetail()
    {
        if (IsOutOfRegion)
        {
            return $"{NoImageReasonLabel}（{TypeLabel}）：{Summary}";
        }

        return ActionType == RecordedActionType.Keyboard ? Summary : $"{TypeLabel}：{Summary}";
    }
}
