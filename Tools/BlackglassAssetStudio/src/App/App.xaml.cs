using System.Windows;
using System.Windows.Threading;
using Blackglass.AssetStudio.App.ViewModels;

namespace Blackglass.AssetStudio.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;
    }

    /// <summary>Last resort: tell the user and keep the app (and any running import's state) alive instead of crashing.</summary>
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel vm) vm.ReportError(e.Exception);
        MessageBox.Show(e.Exception.Message + "\n\nThe details are in the Result tab.", "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
