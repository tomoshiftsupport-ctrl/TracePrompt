using System.Windows.Controls;
using System.Windows.Input;

namespace TracePrompt.Views;

/// <summary>
/// 数値入力欄に付随するスピナー入力（マウスホイール・上下矢印キー・直接入力）を、
/// 複数の画面（メイン画面・選択画像を一覧化画面）で同じ挙動にするための共通処理です。
/// 数値の上下限や呼び出し元の ViewModel プロパティは画面ごとに異なるため、ここでは入力の橋渡しのみ行います。
internal static class SpinnerInteraction
{
    public static void Nudge(int delta, int min, int max, Func<int> getValue, Action<int> setValue) =>
        setValue(Math.Clamp(getValue() + delta, min, max));

    public static void OnPreviewMouseWheel(MouseWheelEventArgs e, int min, int max, Func<int> getValue, Action<int> setValue)
    {
        Nudge(e.Delta > 0 ? 1 : -1, min, max, getValue, setValue);
        e.Handled = true;
    }

    public static void OnPreviewKeyDown(KeyEventArgs e, TextBox textBox, int min, int max, Func<int> getValue, Action<int> setValue)
    {
        switch (e.Key)
        {
            case Key.Up:
                Nudge(1, min, max, getValue, setValue);
                e.Handled = true;
                break;
            case Key.Down:
                Nudge(-1, min, max, getValue, setValue);
                e.Handled = true;
                break;
            case Key.Enter:
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
                break;
        }
    }

    public static void OnPreviewTextInput(TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(c => !char.IsDigit(c));
}
