using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BlinkAlarm.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    readonly Action execute;
    readonly Func<bool>? canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>计数卡片上的一个圆点。</summary>
public sealed class BlinkDot : ObservableObject
{
    bool isOn;
    public bool IsOn { get => isOn; set => Set(ref isOn, value); }
}

/// <summary>"外设"页里可选的一个串口。</summary>
public sealed class RelayPortOption : ObservableObject
{
    readonly Action<string> select;
    bool isSelected;

    public RelayPortOption(string name, bool isSelected, Action<string> select)
    {
        Name = name;
        this.isSelected = isSelected;
        this.select = select;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => isSelected;
        set { if (Set(ref isSelected, value) && value) select(Name); }
    }
}
