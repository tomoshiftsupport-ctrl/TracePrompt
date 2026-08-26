using System.Configuration;
using System.Data;
using System.Windows;
using TracePrompt.Localization;
using TracePrompt.Services;

namespace TracePrompt;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // MainWindow の InitializeComponent が走る前に表示言語を確定させ、
        // 起動直後に「日本語→設定言語」へ切り替わって見えるちらつきを防ぎます。
        AppLanguage savedLanguage = AppLanguageExtensions.ParseCultureCode(new SettingsService().Load().Language);
        LocalizationManager.Instance.SetLanguage(savedLanguage);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
