namespace TracePrompt.Models;

/// <summary>結合画像の画質プリセット。</summary>
public enum ConsultationQualityPreset
{
    /// <summary>文字優先: 解像度を高めに保ちます。</summary>
    TextPriority,

    /// <summary>標準。</summary>
    Standard,

    /// <summary>容量優先: 解像度を抑えてファイルサイズを小さくします。</summary>
    SizePriority,
}
