using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Blackglass.AssetStudio.App.ViewModels;
using Microsoft.Win32;

namespace Blackglass.AssetStudio.App;

public partial class MainWindow : Window
{
    readonly MainViewModel vm;

    public MainWindow()
    {
        InitializeComponent();
        vm = new MainViewModel(new SettingsStore(AppPaths.SettingsPath))
        {
            PickFiles = () =>
            {
                var dialog = new OpenFileDialog { Multiselect = true, Filter = "3D models (*.fbx;*.obj;*.glb;*.gltf)|*.fbx;*.obj;*.glb;*.gltf|All files|*.*" };
                return dialog.ShowDialog(this) == true ? dialog.FileNames : Array.Empty<string>();
            },
            ShowSettings = settings => new SettingsWindow(settings) { Owner = this }.ShowDialog() == true,
            OpenPath = path =>
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" });
            },
        };
        DataContext = vm;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) vm.AddDropped(paths);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!vm.IsBusy) return;
        MessageBox.Show(this, "Unity is still importing. Closing now could leave project assets half-written, so please wait for it to finish.",
            "Import running", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Cancel = true;
    }
}
