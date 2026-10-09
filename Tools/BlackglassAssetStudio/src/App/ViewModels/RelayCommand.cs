using System.Windows.Input;

namespace Blackglass.AssetStudio.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    readonly Func<Task> execute;
    readonly Func<bool>? canExecute;
    readonly Action<Exception>? onError;
    bool running;

    /// <param name="onError">Receives any exception the command throws. Without it an exception in this async-void handler would crash the app.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute, onError) { }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
        this.onError = onError;
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
        catch (Exception e) when (onError != null)
        {
            try { onError(e); } catch { /* the error handler must never take the app down */ }
        }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
