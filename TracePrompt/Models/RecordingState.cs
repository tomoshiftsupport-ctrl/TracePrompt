namespace TracePrompt.Models;

/// <summary>
/// 記録の状態を表す列挙型です。
/// 停止・記録中・一時停止の3状態を明確に管理します。
/// </summary>
public enum RecordingState
{
    /// <summary>記録していない状態です。</summary>
    Stopped,

    /// <summary>記録中の状態です。</summary>
    Recording,

    /// <summary>
    /// 記録セッション中だが、ユーザーが一時停止した状態です。
    /// </summary>
    Paused
}
