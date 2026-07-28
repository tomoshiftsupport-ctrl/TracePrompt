using System.Windows;
using System.Windows.Input;
using TracePrompt.Models;
using TracePrompt.Services;
using TracePrompt.ViewModels;

namespace TracePrompt.Views;

/// <summary>
/// 記録の詳細設定（キャプチャモード・頻度・保持・記録対象）をまとめた別窓です。
/// 本体窓はキャプチャの邪魔にならないよう常に小さく保ち、頻繁には変えない設定はここに集約します。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// スクショトリガー欄: クリックで入力待ち。入力待ち中はマウスボタン1つを確定。
    /// </summary>
    private void OnScreenshotTriggerBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsScreenshotTriggerEditorEnabled)
        {
            return;
        }

        if (!_viewModel.IsScreenshotTriggerListening)
        {
            _viewModel.BeginScreenshotTriggerListen();
            if (sender is UIElement el)
            {
                el.Focus();
            }

            e.Handled = true;
            return;
        }

        string? button = e.ChangedButton switch
        {
            MouseButton.Left => "Left",
            MouseButton.Right => "Right",
            MouseButton.Middle => "Middle",
            MouseButton.XButton1 => "XButton1",
            MouseButton.XButton2 => "XButton2",
            _ => null
        };
        if (button is null) return;

        string display = button switch
        {
            "Left" => "左クリック",
            "Right" => "右クリック",
            "Middle" => "中クリック",
            "XButton1" => "マウスボタン4",
            "XButton2" => "マウスボタン5",
            _ => button
        };

        _viewModel.TryAssignScreenshotTrigger(CapturedInputBinding.FromMouse(button, display));
        e.Handled = true;
    }

    private void OnScreenshotTriggerBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.IsScreenshotTriggerListening || !_viewModel.IsScreenshotTriggerEditorEnabled)
        {
            return;
        }

        _viewModel.TryAssignScreenshotTrigger(CapturedInputBinding.FromMouse("Wheel", "マウスホイール"));
        e.Handled = true;
    }

    private void OnScreenshotTriggerBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsScreenshotTriggerListening)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System)
        {
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        string display = KeyboardHookService.BuildDisplayLabel(vk, ctrl, alt, shift, win);
        if (string.IsNullOrWhiteSpace(display))
        {
            display = key.ToString();
        }

        _viewModel.TryAssignScreenshotTrigger(
            CapturedInputBinding.FromKeyboard(vk, ctrl, alt, shift, win, display));
        e.Handled = true;
    }
}
