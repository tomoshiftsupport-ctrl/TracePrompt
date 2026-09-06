using System.IO;
using System.Runtime.InteropServices;

namespace TracePrompt.Services;

/// <summary>
/// アプリのデータ保存先（通常は %LOCALAPPDATA%\TracePrompt）を解決します。
/// </summary>
internal static class AppDataPaths
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    /// <summary>
    /// アプリのデータ保存先ルートのフルパス（常に絶対パス）を返します。
    ///
    /// Microsoft Store（MSIX）版で実行している場合、Win32 の生パス（%LOCALAPPDATA%\TracePrompt）へ
    /// 書き込んでも、OS のファイルシステム仮想化により実体は
    /// %LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;\LocalCache\Local\TracePrompt へ
    /// 静かにリダイレクトされる（＝保存自体は成功するが、Environment.GetFolderPath が返す表示上のパスと
    /// 「エクスプローラーで開く」パスが一致しなくなる）。この場合は
    /// <see cref="Windows.Storage.ApplicationData.Current"/> が返す、OS 公式のパッケージ専用パスを
    /// そのまま使うことで、保存先と「フォルダを開く」の参照先を常に一致させる。
    /// Packages\&lt;PackageFamilyName&gt;\LocalCache\Local という内部構造はここではハードコードしない。
    ///
    /// 非パッケージ（exe）実行時は、環境によっては Environment.GetFolderPath が空文字を返すことがあり
    /// （ユーザープロファイルがまだ実体化していない場合など）、それをそのまま Path.Combine に渡すと
    /// 相対パスになってしまうため、ユーザープロファイル配下へ明示的にフォールバックする。
    /// </summary>
    public static string GetTracePromptRootDirectory()
    {
        if (TryGetPackagedRootDirectory(out string? packagedRoot))
        {
            return packagedRoot!;
        }

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

    /// <summary>
    /// MSIX パッケージとして実行中の場合のみ、OS 公式 API（ApplicationData）から
    /// パッケージ専用の保存先ルートを取得します。非パッケージ実行時、または
    /// 何らかの理由で ApplicationData の取得に失敗した場合は false を返し、
    /// 呼び出し元で従来の非パッケージ用ロジックにフォールバックさせます。
    /// </summary>
    private static bool TryGetPackagedRootDirectory(out string? path)
    {
        path = null;

        if (!IsRunningInsidePackage())
        {
            return false;
        }

        try
        {
            // Packages\<PackageFamilyName>\LocalCache\Local の内部構造は組み立てず、
            // OS にパッケージ専用のキャッシュ領域を問い合わせる。
            string cacheRoot = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
            path = Path.Combine(cacheRoot, "TracePrompt");
            return true;
        }
        catch
        {
            // パッケージ判定は真でも ApplicationData の取得に失敗するケースに備え、
            // 呼び出し元で非パッケージ用ロジックへフォールバックできるようにする。
            return false;
        }
    }

    /// <summary>
    /// MSIX パッケージの中で実行されているかどうかを判定します。
    /// GetCurrentPackageFullName は非パッケージ実行時に APPMODEL_ERROR_NO_PACKAGE を返すため、
    /// その戻り値だけを見る（パッケージ名自体は使わない、パス構造もここでは組み立てない）。
    /// </summary>
    private static bool IsRunningInsidePackage()
    {
        int length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }
}
