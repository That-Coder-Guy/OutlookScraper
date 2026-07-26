using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OutlookScraper.App.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base. Hand-rolled rather than pulling in an MVVM
/// framework for the three view models this app has.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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

/// <summary>An ICommand over an async handler, with re-entrancy guarded.</summary>
public sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    : ICommand
{
    private readonly Func<object?, Task> _execute = execute;
    private readonly Func<object?, bool>? _canExecute = canExecute;
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
