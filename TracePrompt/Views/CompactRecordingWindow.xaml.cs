using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TracePrompt.Models;
using TracePrompt.ViewModels;

namespace TracePrompt.Views;

/// <summary>
/// キャプチャ範囲確定後に表示する、開始／一時停止／再開／停止・経過時間・設定・戻るだけの小型ウィンドウです。
/// メインウィンドウはこの間 Hide() されており、ここが唯一の操作口になります（MainWindow.xaml.cs 側で制御）。
/// 自分自身は BitBlt によるキャプチャに映り込まないよう WDA_EXCLUDEFROMCAPTURE を設定します
/// （クリック操作はできる必要があるため、キャプチャ枠と違いマウス透過にはしません）。
/// タイトルバーが無いので、ボタン以外の余白をドラッグして自由に移動できるようにしています。
/// </summary>
public partial class CompactRecordingWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopMost = new(-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly nint _hwnd;

    /// <summary>「戻る」ボタンが押されたことを呼び出し元（MainWindow）へ伝えます。表示・非表示の判断は持ちません。</summary>
    public event EventHandler? ReturnRequested;

    public CompactRecordingWindow()
    {
        InitializeComponent();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Windows 10 2004 未満では失敗しますが、その場合は諦めて表示だけ続けます（例外は投げません）。
        SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
    }

    /// <summary>
    /// 初回表示のときにだけ呼び出し、キャプチャ範囲の左上少し内側に配置します。
    /// 物理ピクセル座標で直接 SetWindowPos するのは、CaptureRegionBorderWindow / 旧 RecordingControlToolbarWindow と
    /// 同じ理由（WPF の Left/Top は DIP のため、モニターの DPI 設定によって座標がずれるのを避けるため）です。
    /// これ以降はユーザーの DragMove() による移動を尊重し、二度と呼び出しません。
    /// </summary>
    public void ApplyInitialPosition(WindowBounds region)
    {
        const int margin = 16;
        SetWindowPos(_hwnd, HwndTopMost, region.Left + margin, region.Top + margin, 0, 0, SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// ルート Border 上でのマウス押下: クリック元がボタンならドラッグ扱いにせずそのままボタンへ渡し、
    /// それ以外（余白）なら DragMove() でウィンドウ移動を開始します。
    /// </summary>
    private void OnRootBorderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsWithinButton(source))
        {
            return;
        }

        DragMove();
    }

    private void OnReturnButtonClick(object sender, RoutedEventArgs e)
    {
        ReturnRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Esc キーは「戻る」ボタンと同じ扱いにします。ただし「戻る」ボタン自体がキャプチャ開始後は
    /// 隠れる（IsIdle のときだけ出す）仕様なので、Esc もそれに合わせて未開始のときだけ効かせます。
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel { IsIdle: true })
        {
            ReturnRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    /// <summary>MainWindow.xaml.cs の OnOpenSettingsClick と同内容。Owner だけこのウィンドウにします。</summary>
    private void OnSettingsButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var window = new SettingsWindow(viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private static bool IsWithinButton(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is ButtonBase)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
