using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace Blackglass.AssetStudio.App;

public partial class SettingsWindow : Window
{
    readonly AppSettings settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        ProjectBox.Text = settings.ProjectPath;
        UnityBox.Text = settings.UnityExePath;
        StagingBox.Text = settings.StagingFolder;
        HeightBox.Text = settings.DefaultTargetHeight.ToString("0.###", CultureInfo.CurrentCulture);
        AvatarBox.Text = settings.DefaultSharedAvatarPath;
        Check(this, new RoutedEventArgs());
    }

    void BrowseProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Blackglass project folder" };
        if (dialog.ShowDialog(this) == true) { ProjectBox.Text = dialog.FolderName; Check(sender, e); }
    }

    void BrowseUnity(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Unity.exe|Unity.exe|Executables|*.exe" };
        if (dialog.ShowDialog(this) == true) { UnityBox.Text = dialog.FileName; Check(sender, e); }
    }

    void Check(object sender, RoutedEventArgs e)
    {
        var project = ProjectBox.Text.Trim();
        var isProject = ProjectLocator.IsProject(project);
        ProjectNote.Text = isProject ? $"OK. Unity version {ProjectLocator.ReadEditorVersion(project)}." : "Not a Unity project folder.";
        UnityNote.Text = isProject ? UnityLocator.Resolve(new AppSettings { UnityExePath = UnityBox.Text.Trim() }, project).Message : "";
    }

    void Save(object sender, RoutedEventArgs e)
    {
        if (!float.TryParse(HeightBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var height) || !(height > 0))
        {
            MessageBox.Show(this, "The target height must be a number greater than 0.", "Settings");
            return;
        }
        settings.ProjectPath = ProjectBox.Text.Trim();
        settings.UnityExePath = UnityBox.Text.Trim();
        settings.StagingFolder = StagingBox.Text.Trim();
        settings.DefaultTargetHeight = height;
        settings.DefaultSharedAvatarPath = AvatarBox.Text.Trim();
        DialogResult = true;
    }
}
