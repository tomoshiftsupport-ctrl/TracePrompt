namespace TracePrompt.Models;

/// <summary>記録のキャプチャ範囲の決め方。</summary>
public enum CaptureRegionMode
{
    /// <summary>選択した1台のモニターの全画面を撮ります。</summary>
    FullScreen,

    /// <summary>ドラッグで選んだ固定の矩形範囲を撮ります。</summary>
    FreeClip,
}
