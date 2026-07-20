using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TracePrompt.Services;

/// <summary>
/// クリップボードへ文字・画像をコピーするサービスです。
/// 失敗しても例外を外へ投げず、呼び出し側でメッセージ表示できるようにします。
/// </summary>
public sealed class ClipboardService
{
    /// <summary>
    /// 文字列をクリップボードへコピーします。
    /// </summary>
    public bool TryCopyText(string text, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrEmpty(text))
        {
            errorMessage = "コピーする文章がありません。";
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"説明文のコピーに失敗しました: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 画像ファイルを読み込み、画像データとしてクリップボードへコピーします。
    /// パス文字列ではなく、画像そのものをコピーします。
    /// </summary>
    public bool TryCopyImageFromFile(string imagePath, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            errorMessage = "コピーする画像ファイルが見つかりません。先に相談用データを生成してください。";
            return false;
        }

        try
        {
            BitmapSource? source = LoadBitmapWithoutLock(imagePath);
            if (source is null)
            {
                errorMessage = "画像の読み込みに失敗しました。";
                return false;
            }

            Clipboard.SetImage(source);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"画像のコピーに失敗しました: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 複数の画像ファイルを、1枚のビットマップではなく「複数ファイル」としてクリップボードへコピーします。
    /// 貼り付け先（対応しているチャットUI等）で個別の添付として扱われます。
    /// Explorer のファイルコピーと同じく「Preferred DropEffect（コピー）」も併せて設定します。
    /// これが無いと Excel など一部の Office アプリで貼り付けが失敗することがあります。
    /// </summary>
    public bool TryCopyFilesAsFileList(IReadOnlyList<string> filePaths, out string? errorMessage)
    {
        errorMessage = null;

        List<string> existing = (filePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .ToList();

        if (existing.Count == 0)
        {
            errorMessage = "コピーする画像ファイルが見つかりません。";
            return false;
        }

        try
        {
            var dropList = new StringCollection();
            dropList.AddRange(existing.ToArray());

            var data = new DataObject();
            data.SetFileDropList(dropList);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"ファイルのコピーに失敗しました: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 現在クリップボードに実際に入っている内容を読み取ります。
    /// 「コピーした（つもりの）ものが本当に貼り付けられる状態か」をアプリ内で確認するために使います
    /// （Excel 等の外部アプリで試さなくても、この結果を見れば分かります）。
    /// </summary>
    public ClipboardPreview GetPreview()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                BitmapSource? image = Clipboard.GetImage();
                if (image is not null)
                {
                    return new ClipboardPreview { Image = image };
                }
            }

            if (Clipboard.ContainsFileDropList())
            {
                StringCollection files = Clipboard.GetFileDropList();
                List<string> paths = files
                    .Cast<string>()
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();
                if (paths.Count > 0)
                {
                    return new ClipboardPreview { FilePaths = paths };
                }
            }

            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    return new ClipboardPreview { Text = text };
                }
            }
        }
        catch
        {
            // クリップボードは他アプリと共有のリソースのため、読み取りに失敗することがあります。空の結果を返します。
        }

        return new ClipboardPreview();
    }

    /// <summary>
    /// ファイルをロックしないよう、メモリに読み込んでから BitmapSource を作ります。
    /// </summary>
    private static BitmapSource? LoadBitmapWithoutLock(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// <summary>
/// <see cref="ClipboardService.GetPreview"/> の結果です。画像・ファイル一覧・テキストのうち
/// クリップボードに実際に入っていたものだけが設定されます（すべて null/空なら中身なし）。
/// </summary>
public sealed class ClipboardPreview
{
    public BitmapSource? Image { get; init; }
    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
    public string? Text { get; init; }

    public bool HasContent => Image is not null || FilePaths.Count > 0 || !string.IsNullOrEmpty(Text);
}
