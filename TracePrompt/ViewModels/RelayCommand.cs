using System.Windows.Input;

namespace TracePrompt.ViewModels;

/// <summary>
/// ボタン押下などを ViewModel から扱うための、シンプルなコマンド実装です。
/// 外部パッケージを使わず、.NET 標準の ICommand だけで動かします。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        // null のまま実行すると落ちるため、コンストラクタで必ずチェックします。
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// コマンドが実行できるかどうかが変わったときに UI へ通知します。
    /// </summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// いまボタンを押せるかどうかを返します。
    /// </summary>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <summary>
    /// ボタンが押されたときの処理を実行します。
    /// 押せない状態なら何もしません（連打や不正操作の防止）。
    /// </summary>
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _execute();
    }

    /// <summary>
    /// 状態が変わったあと、ボタンの有効/無効を再評価させます。
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
