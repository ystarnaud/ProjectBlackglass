using System.Windows.Input;

namespace Blackglass.AssetStudio.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    readonly Func<Task> execute;
    readonly Func<bool>? canExecute;
    bool running;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute) { }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
