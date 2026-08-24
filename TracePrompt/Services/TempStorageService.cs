using System.IO;

namespace TracePrompt.Services;

/// <summary>
/// TracePrompt 専用の一時フォルダへ画像を保存・削除するサービスです。
/// 保存先: %LOCALAPPDATA%\TracePrompt\Temp
/// </summary>
public sealed class TempStorageService
{
    private int _sequence;

    public TempStorageService()
    {
        TempDirectory = Path.Combine(AppDataPaths.GetTracePromptRootDirectory(), "Temp");
    }

    /// <summary>一時ファイルを置くフォルダのフルパスです。</summary>
    public string TempDirectory { get; }

    /// <summary>
    /// AI 相談用にまとめた縦長画像の保存パスです。JPEG（高画質圧縮）で保存します。
    /// 例: %LOCALAPPDATA%\TracePrompt\Temp\consultation.jpg
    /// </summary>
    public string ConsultationImagePath => Path.Combine(TempDirectory, "consultation.jpg");

    /// <summary>フォルダが無ければ作成します。</summary>
    public void EnsureDirectory()
    {
        Directory.CreateDirectory(TempDirectory);
    }

    /// <summary>consultation.jpg だけを削除します。</summary>
    public void TryDeleteConsultationImage()
    {
        TryDeleteFile(ConsultationImagePath);
    }

    /// <summary>
    /// 「選択画像を一覧化」で分割生成する結合画像のパスです（1始まり）。
    /// 例: %LOCALAPPDATA%\TracePrompt\Temp\consultation_1.jpg
    /// </summary>
    public string GetSplitConsultationImagePath(int index) =>
        Path.Combine(TempDirectory, $"consultation_{index}.jpg");

    /// <summary>前回の分割生成結果（consultation_*.jpg）をすべて削除します。</summary>
    public void TryDeleteSplitConsultationImages()
    {
        try
        {
            if (!Directory.Exists(TempDirectory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(TempDirectory, "consultation_*.jpg"))
            {
                TryDeleteFile(file);
            }
        }
        catch
        {
            // クリーンアップ失敗でも続行します。
        }
    }

    /// <summary>
    /// 新しい PNG 用のファイルパスを生成します（まだファイルは作りません）。
    /// ファイル名には日時と連番を入れます。
    /// </summary>
    public string CreateScreenshotFilePath()
    {
        EnsureDirectory();
        int seq = Interlocked.Increment(ref _sequence);
        string fileName = $"click_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{seq:D4}.png";
        return Path.Combine(TempDirectory, fileName);
    }

    /// <summary>指定ファイルを削除します（失敗しても例外は投げません）。</summary>
    public void TryDeleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // 一時ファイル削除の失敗でアプリを止めないようにします。
        }
    }

    /// <summary>
    /// 一時フォルダ内のファイルをすべて削除します。
    /// 記録データ削除ボタンやアプリ終了時に使います。
    /// </summary>
    public void ClearAll()
    {
        try
        {
            EnsureDirectory();
            foreach (string file in Directory.EnumerateFiles(TempDirectory))
            {
                TryDeleteFile(file);
            }
        }
        catch
        {
            // クリーンアップ失敗でも続行します。
        }
    }

    /// <summary>一時フォルダ内のファイル数を返します（確認用）。</summary>
    public int CountFiles()
    {
        try
        {
            if (!Directory.Exists(TempDirectory))
            {
                return 0;
            }

            return Directory.GetFiles(TempDirectory).Length;
        }
        catch
        {
            return 0;
        }
    }
}
