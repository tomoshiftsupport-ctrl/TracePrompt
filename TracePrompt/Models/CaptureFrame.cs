namespace TracePrompt.Models;

/// <summary>
/// 定期（または補助）キャプチャ1枚分です。
/// 操作イベントはこのフレームに時刻近傍で紐づきます。
/// </summary>
public sealed class CaptureFrame
{
    public CaptureFrame(long id, DateTime recordedAt, string screenshotPath, bool isAuxiliary = false)
    {
        Id = id;
        RecordedAt = recordedAt;
        ScreenshotPath = screenshotPath ?? throw new ArgumentNullException(nameof(screenshotPath));
        IsAuxiliary = isAuxiliary;
    }

    /// <summary>バッファ内で一意な ID です。</summary>
    public long Id { get; }

    /// <summary>撮影時刻です。</summary>
    public DateTime RecordedAt { get; }

    /// <summary>PNG ファイルパスです。</summary>
    public string ScreenshotPath { get; }

    /// <summary>
    /// 操作時に近い定期キャプチャが無く、補助的に撮った1枚かどうかです。
    /// </summary>
    public bool IsAuxiliary { get; }

    public string ToHistoryText()
    {
        // 操作ログと並べたとき一目で分かるよう【画面】マークを付ける
        string kind = IsAuxiliary ? "操作時スクショ" : "定期スクショ";
        return $"{RecordedAt:HH:mm:ss.fff}  【画面】 {kind}";
    }
}

