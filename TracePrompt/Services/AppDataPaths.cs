using System.IO;

namespace TracePrompt.Services;

/// <summary>
/// アプリのデータ保存先（%LOCALAPPDATA%\TracePrompt）を解決します。
/// </summary>
internal static class AppDataPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\TracePrompt のフルパス（常に絶対パス）を返します。
    /// 環境によっては Environment.GetFolderPath が空文字を返すことがあり（ユーザープロファイルが
    /// まだ実体化していない場合など）、それをそのまま Path.Combine に渡すと相対パスになってしまい、
    /// 「実際の保存先」と「エクスプローラーで開いた場所」がズレる不具合の原因になります。
    /// SpecialFolderOption.Create でフォルダの実体作成を促しつつ、それでも空ならユーザープロファイル
    /// 配下へ明示的にフォールバックし、常に絶対パスであることを保証します。
    /// </summary>
    public static string GetTracePromptRootDirectory()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            localAppData = string.IsNullOrWhiteSpace(userProfile)
                ? Path.Combine(Path.GetTempPath(), "TracePromptFallback")
                : Path.Combine(userProfile, "AppData", "Local");
        }

        return Path.Combine(localAppData, "TracePrompt");
    }
}
