using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace TracePrompt.Localization;

/// <summary>
/// アプリ全体の表示言語を1か所で管理します。
/// XAML からは <see cref="LocExtension"/>（内部的にはこのインスタンスのインデクサーへのバインド）経由で参照し、
/// <see cref="SetLanguage"/> で切り替えると、バインドされている文字列がその場ですべて更新されます。
/// コードビハインドや ViewModel からは <see cref="Get"/> / <see cref="Format"/>、または
/// <see cref="LanguageChanged"/> イベントの購読で追随できます。
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private readonly ResourceManager _resourceManager =
        new("TracePrompt.Resources.Strings", typeof(LocalizationManager).Assembly);

    private CultureInfo _culture = new(AppLanguage.Japanese.ToCultureCode());

    private LocalizationManager()
    {
    }

    public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Japanese;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>言語が切り替わったときに発火します（ViewModel 側の派生プロパティ再通知などに使います）。</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// XAML バインディング用のインデクサーです。キーが見つからない場合はキー自体を返すため、
    /// 未登録キーでもクラッシュせず、画面上で気づきやすくなります。
    /// </summary>
    public string this[string key] => Get(key);

    public string Get(string key) => _resourceManager.GetString(key, _culture) ?? key;

    public string Format(string key, params object?[] args) =>
        string.Format(_culture, Get(key), args);

    public void SetLanguage(AppLanguage language)
    {
        CurrentLanguage = language;
        _culture = new CultureInfo(language.ToCultureCode());
        CultureInfo.CurrentUICulture = _culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(System.Windows.Data.Binding.IndexerName));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
