using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChenLink.Core;

/// <summary>最小 MVVM 基类：属性变更通知。不引入第三方 MVVM 框架，保持依赖精简。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>批量刷新多个计算属性（如状态文案、指标、按钮可用性）。</summary>
    protected void Raise(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
