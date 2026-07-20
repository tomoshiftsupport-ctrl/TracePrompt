namespace TracePrompt.Models;

/// <summary>
/// 相談用データの1コマです。1枚のキャプチャと、そこに紐づく操作群です。
/// </summary>
public sealed class ConsultationTimelineEntry
{
    public ConsultationTimelineEntry(CaptureFrame frame, IReadOnlyList<RecordedAction> linkedActions)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        LinkedActions = linkedActions ?? Array.Empty<RecordedAction>();
    }

    public CaptureFrame Frame { get; }

    /// <summary>このフレームに紐づく操作（時刻近傍でリンクされたもの）。</summary>
    public IReadOnlyList<RecordedAction> LinkedActions { get; }

    public bool HasActions => LinkedActions.Count > 0;
}
