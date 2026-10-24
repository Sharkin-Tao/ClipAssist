using System.Windows.Input;

namespace ClipAssist.Infrastructure;

/// <summary>
/// 最简 ICommand 实现：把“按钮点击”转发到一个方法上。
/// 这样 XAML 里只需要 Command="{Binding XxxCommand}"，不用写事件处理器。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>无参数版本，方便写 () => ... 的短方法。</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>
    /// 交给 CommandManager 统一管理“按钮是否可用”的重新查询，
    /// 省去手动调用 RaiseCanExecuteChanged。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}