using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using TracePrompt.Models;
using Loc = TracePrompt.Localization.LocalizationManager;

namespace TracePrompt.ViewModels;

/// <summary>
/// サムネイル一覧の1行分（1キャプチャ分）です。選択状態とサムネイル画像を持ちます。
/// </summary>
public sealed class CaptureFrameThumbnailViewModel : INotifyPropertyChanged
{
    private const int ThumbnailDecodeWidth = 180;

    private bool _isSelected;
    private BitmapImage? _thumbnailImage;

    public CaptureFrameThumbnailViewModel(
        CaptureFrame frame,
        IReadOnlyList<RecordedAction> linkedActions,
        bool isSelected)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        LinkedActions = linkedActions ?? Array.Empty<RecordedAction>();
        _isSelected = isSelected;
        _thumbnailImage = LoadThumbnail(Frame.ScreenshotPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureFrame Frame { get; }

    public IReadOnlyList<RecordedAction> LinkedActions { get; }

    public bool HasActions => LinkedActions.Count > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public BitmapImage? ThumbnailImage
    {
        get => _thumbnailImage;
        private set
        {
            _thumbnailImage = value;
            OnPropertyChanged();
        }
    }

    public string DisplayLabel => LinkedActions.Count switch
    {
        0 => $"{Frame.RecordedAt:HH:mm:ss.fff}",
        1 => $"{Frame.RecordedAt:HH:mm:ss.fff}　{LinkedActions[0].ToConsultationDetail()}",
        _ => Loc.Instance.Format("Thumbnail_Label_Format", Frame.RecordedAt, LinkedActions.Count),
    };

    /// <summary>編集（赤ペン記入）保存後に、ディスク上の最新画像でサムネイルを読み直します。</summary>
    public void ReloadThumbnail()
    {
        ThumbnailImage = LoadThumbnail(Frame.ScreenshotPath);
    }

    private static BitmapImage? LoadThumbnail(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = ThumbnailDecodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
