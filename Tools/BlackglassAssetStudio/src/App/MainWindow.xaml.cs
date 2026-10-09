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
            PickFolder = () =>
            {
                var dialog = new OpenFolderDialog { Title = "Choose a folder of FBX / OBJ files" };
                return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
            },
            PickProjectFile = (title, filter, current) => PickInProject(false, title, filter, current),
            ShowSettings = settings => new SettingsWindow(settings) { Owner = this }.ShowDialog() == true,
            OpenPath = path =>
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" });
            },
        };
        DataContext = vm;
    }

    /// <summary>A file or folder picked inside the project, as an Assets/... path; null if cancelled or outside the project.</summary>
    string? PickInProject(bool folder, string title, string filter, string current)
    {
        var root = vm.Settings.ProjectPath;
        if (!ProjectLocator.IsProject(root))
        {
            MessageBox.Show(this, "Set the Blackglass project folder in Settings first.", "Project not set", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var start = ProjectPaths.ToAbsolute(root, current);
        if (!folder && Path.HasExtension(start)) start = Path.GetDirectoryName(start) ?? start;
        if (!Directory.Exists(start)) start = ProjectPaths.ToAbsolute(root, "");

        string? picked;
        if (folder)
        {
            var dialog = new OpenFolderDialog { Title = title, InitialDirectory = start };
            picked = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        else
        {
            var dialog = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = start };
            picked = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (picked == null) return null;
        if (ProjectPaths.TryToProjectPath(root, picked, out var projectPath, out var error)) return projectPath;
        MessageBox.Show(this, error, "Outside the project", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        vm.SetSelection(ItemList.SelectedItems.Cast<AssetItemViewModel>().ToList());

    void BrowseBatchDestination(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BatchSelectionViewModel batch) return;
        var picked = PickInProject(true, "Choose the folder to save into", "", batch.Destination);
        if (picked != null) batch.Destination = picked;
    }

    void BrowseBatchSkeleton(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BatchSelectionViewModel batch) return;
        var picked = PickInProject(false, "Choose the character's model FBX", "Models (*.fbx)|*.fbx", batch.Skeleton);
        if (picked != null) batch.Skeleton = picked;
    }

    static AssetItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as AssetItemViewModel;

    void BrowseDestination(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        var picked = item == null ? null : PickInProject(true, "Choose the folder to save into", "", item.Destination);
        if (item != null && picked != null) item.Destination = picked;
    }

    void BrowseAvatar(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        var picked = item == null ? null : PickInProject(false, "Choose the character's model FBX", "Models (*.fbx)|*.fbx", item.SharedAvatarPath);
        if (item != null && picked != null) item.SharedAvatarPath = picked;
    }

    void BrowseTheme(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        var picked = item == null ? null : PickInProject(false, "Choose the environment theme asset", "Assets (*.asset)|*.asset", item.ThemePath);
        if (item != null && picked != null) item.ThemePath = picked;
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
