using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipAssist.Infrastructure;

/// <summary>
/// 所有 ViewModel 的基类，实现 INotifyPropertyChanged。
/// 作用：属性值变化时通知界面刷新（这是 MVVM 数据绑定的基础）。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知，调用处不用手写属性名字符串。</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 给字段赋值；只有值真的变了才通知界面，并返回是否发生变化。
    /// 用法：if (SetProperty(ref _name, value)) { /* 额外逻辑 */ }
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}