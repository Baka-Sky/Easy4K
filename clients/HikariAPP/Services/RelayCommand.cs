using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace HikariAPP2.Services;

/// <summary>最小 ICommand 实现：支持同步/异步执行，异常不会把界面拖崩。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => { execute(); return Task.CompletedTask; }, canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        : this(p => { execute(p); return Task.CompletedTask; }, canExecute)
    {
    }

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        try { await _execute(parameter); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Command failed: {ex}"); }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>可选中的小胶囊（倍率 / 档位 / 精度）。</summary>
public sealed class ChoiceItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Label { get; }
    public ICommand Command { get; }

    public ChoiceItem(string label, Action onPick, bool selected = false)
    {
        Label = label;
        _isSelected = selected;
        Command = new RelayCommand(onPick);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
