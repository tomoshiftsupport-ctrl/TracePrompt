namespace TracePrompt.Localization;

/// <summary>
/// アプリが対応する表示言語です。将来ほかの言語を増やすときはここに追加します。
/// </summary>
public enum AppLanguage
{
    Japanese,
    English
}

public static class AppLanguageExtensions
{
    /// <summary>設定ファイルへ保存する文字列（BCP-47 に近い言語コード）に変換します。</summary>
    public static string ToCultureCode(this AppLanguage language) => language switch
    {
        AppLanguage.English => "en",
        _ => "ja"
    };

    /// <summary>設定ファイルから読み込んだ言語コードを <see cref="AppLanguage"/> に変換します。未知の値は日本語扱いです。</summary>
    public static AppLanguage ParseCultureCode(string? code) => code switch
    {
        "en" => AppLanguage.English,
        _ => AppLanguage.Japanese
    };
}
