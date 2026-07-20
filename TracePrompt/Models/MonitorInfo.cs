namespace TracePrompt.Models;

/// <summary>「全画面」モードで選ぶモニター1台分の情報です。</summary>
public sealed class MonitorInfo
{
    public MonitorInfo(string deviceId, string label, WindowBounds bounds, bool isPrimary)
    {
        DeviceId = deviceId;
        Label = label;
        Bounds = bounds;
        IsPrimary = isPrimary;
    }

    /// <summary>OS が割り当てるモニターの識別子（例: \\.\DISPLAY1）。設定の保存・復元に使います。</summary>
    public string DeviceId { get; }

    /// <summary>ComboBox 表示用の説明文（例: "モニター 1 (1920×1080)（メイン）"）。</summary>
    public string Label { get; }

    /// <summary>仮想スクリーン座標でのモニターの矩形範囲。</summary>
    public WindowBounds Bounds { get; }

    public bool IsPrimary { get; }

    public override string ToString() => Label;
}
