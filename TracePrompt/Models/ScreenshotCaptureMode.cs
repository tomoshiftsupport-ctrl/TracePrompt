namespace TracePrompt.Models;

/// <summary>
/// スクリーンショットの取り方です。
/// （WPF の System.Windows.Input.CaptureMode と区別するため Screenshot を付けています）
/// </summary>
public enum ScreenshotCaptureMode
{
    /// <summary>設定した枚/秒で連続キャプチャし、操作は最寄りフレームに紐づける。</summary>
    Interval = 0,

    /// <summary>左クリックやキー操作などの操作ごとにキャプチャする。</summary>
    OnOperation = 1
}
