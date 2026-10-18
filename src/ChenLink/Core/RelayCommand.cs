using System.Windows.Input;

namespace ChenLink.Core;

/// <summary>同步命令。可执行性由 CanExecute 委托决定，VM 主动 RaiseCanExecuteChanged 刷新按钮状态。</summary>
public sealed class RelayCommand : ICommand
{
    readonly Action _execute;
    readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令：内部保证不重入（加载中再次点击会被忽略），异常统一交给 onError，避免 async void 吞异常。
/// 按钮的 IsEnabled 交给 VM 的布尔属性控制，命令本身只做防重入。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    readonly Func<Task> _execute;
    readonly Func<bool>? _canExecute;
    readonly Action<Exception>? _onError;
    bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_running || !CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
