namespace TracePrompt.Models;

/// <summary>
/// アプリ設定（永続化用）です。
/// </summary>
public sealed class AppSettings
{
    /// <summary>表示言語（"ja" / "en"）。未設定（既存ユーザーの旧設定など）は日本語として扱います。</summary>
    public string Language { get; set; } = "ja";

    public int CapturesPerSecond { get; set; } = 1;

    /// <summary>秒でキャプチャ時のリングバッファ目安（秒）。</summary>
    public int RetentionSeconds { get; set; } = 6;

    /// <summary>操作でキャプチャ時に維持する最大枚数。</summary>
    public int KeepCaptureCount { get; set; } = 20;

    /// <summary>
    /// キャプチャモード（Interval / OnOperation）。文字列で保存します。
    /// </summary>
    public string CaptureMode { get; set; } = nameof(ScreenshotCaptureMode.Interval);

    /// <summary>旧設定互換: true なら Interval、false なら OnOperation に近い扱い。</summary>
    public bool? RecordPeriodicCapture { get; set; }

    /// <summary>
    /// 左/右クリック・ダブルクリック・ドラッグ・ホイールなどマウス操作全般です。
    /// </summary>
    public bool RecordMouseOperations { get; set; } = true;

    public bool RecordKeyboard { get; set; }

    /// <summary>操作でキャプチャ時にスクショを取る単一入力（キー or マウス1つ）。</summary>
    public CapturedInputBinding? ScreenshotTrigger { get; set; }

    /// <summary>「選択画像を一覧化」時の1枚あたりのコマ数（すべて＝int.MaxValue）。</summary>
    public int ConsultationFramesPerImage { get; set; } = 6;

    /// <summary>結合画像の並び方: 自動判定するかどうか。</summary>
    public bool ConsultationLayoutIsAuto { get; set; }

    /// <summary>結合画像の並び方: 自動判定しないときの列数。</summary>
    public int ConsultationLayoutColumns { get; set; } = 2;

    /// <summary>結合画像の画質プリセット（TextPriority / Standard / SizePriority）。文字列で保存します。</summary>
    public string ConsultationQualityPreset { get; set; } = nameof(Models.ConsultationQualityPreset.Standard);

    /// <summary>
    /// true の場合、記録停止時の自動結合（「相談用データ生成」）にも
    /// ConsultationFramesPerImage/ConsultationLayoutIsAuto/ConsultationLayoutColumns/ConsultationQualityPreset を適用します。
    /// false（既定）のときは、従来どおり全コマを1枚（高さ超過時のみ自動縮小）に結合します。
    /// </summary>
    public bool UseCustomAutoCombineSettings { get; set; }

    /// <summary>キャプチャ範囲の決め方（FullScreen / FreeClip）。文字列で保存します。</summary>
    public string CaptureRegionMode { get; set; } = nameof(Models.CaptureRegionMode.FullScreen);

    /// <summary>「全画面」モードで選んだモニターの識別子（例: \\.\DISPLAY1）。</summary>
    public string? SelectedMonitorDeviceId { get; set; }

    /// <summary>「自由クリップ」モードの選択範囲（画面座標）。未選択時は null。</summary>
    public int? FreeClipLeft { get; set; }
    public int? FreeClipTop { get; set; }
    public int? FreeClipWidth { get; set; }
    public int? FreeClipHeight { get; set; }

    // ---- 旧設定（読み込み互換用。新規保存では書きません）----
    public bool? RecordLeftClick { get; set; }
    public bool? RecordRightClick { get; set; }
    public bool? RecordDoubleClick { get; set; }
    public bool? RecordMouseWheel { get; set; }
    public bool? RecordDrag { get; set; }
}
