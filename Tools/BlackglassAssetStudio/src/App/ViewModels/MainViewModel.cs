using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Blackglass.AssetStudio.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    readonly SettingsStore store;
    AssetItemViewModel? selected;
    string statusText = "Add files to begin.";
    string resultText = "";
    string logPath = "";
    bool isBusy;
    CharacterContext? character;
    string characterModelPath = "";
    string characterSummary = NoCharacterText;

    const string NoCharacterText = "Optional. Pick the character's model FBX and every animation you add is named, saved and linked to it automatically.";

    public MainViewModel(SettingsStore store)
    {
        this.store = store;
        Settings = store.Load();
        SettingsDefaults.Fill(Settings, AppContext.BaseDirectory);
        store.Save(Settings);

        AddFilesCommand = new RelayCommand(() => { if (PickFiles != null) AddFiles(PickFiles(), null); }, null, ReportError);
        AddFolderCommand = new RelayCommand(() =>
        {
            var folder = PickFolder?.Invoke();
            if (!string.IsNullOrEmpty(folder)) AddDropped(new[] { folder });
        }, () => !IsBusy, ReportError);
        BrowseCharacterCommand = new RelayCommand(() =>
        {
            var picked = PickProjectFile?.Invoke("Choose the character's model FBX", "Models (*.fbx)|*.fbx", character?.ModelPath ?? "Assets/Art/Characters");
            if (!string.IsNullOrEmpty(picked)) CharacterModelPath = picked;
        }, () => !IsBusy, ReportError);
        ClearCharacterCommand = new RelayCommand(() => CharacterModelPath = "", () => characterModelPath.Length > 0, ReportError);
        RemoveCommand = new RelayCommand(() => { if (Selected != null) Items.Remove(Selected); }, () => Selected != null && !IsBusy, ReportError);
        ClearCommand = new RelayCommand(() => Items.Clear(), () => Items.Count > 0 && !IsBusy, ReportError);
        ValidateCommand = new RelayCommand(Validate, () => Items.Count > 0 && !IsBusy, ReportError);
        ImportSelectedCommand = new RelayCommand(() => ImportAsync(Selected == null ? new List<AssetItemViewModel>() : new List<AssetItemViewModel> { Selected }),
            () => Selected != null && !IsBusy, ReportError);
        ImportAllCommand = new RelayCommand(() => ImportAsync(Items.ToList()), () => Items.Count > 0 && !IsBusy, ReportError);
        SettingsCommand = new RelayCommand(OpenSettings, () => !IsBusy, ReportError);
        OpenLogCommand = new RelayCommand(() => OpenPath?.Invoke(LogPath), () => LogPath.Length > 0, ReportError);
        Items.CollectionChanged += (_, _) => { Raise(nameof(HasItems)); CommandManager.InvalidateRequerySuggested(); };
    }

    public AppSettings Settings { get; }
    public ObservableCollection<AssetItemViewModel> Items { get; } = new();

    public AssetItemViewModel? Selected { get => selected; set { selected = value; Raise(); Raise(nameof(HasSelection)); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasSelection => Selected != null;
    public bool HasItems => Items.Count > 0;
    public string StatusText { get => statusText; private set { statusText = value; Raise(); } }
    public string ResultText { get => resultText; private set { resultText = value; Raise(); } }
    public string LogPath { get => logPath; private set { logPath = value; Raise(); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Raise(); CommandManager.InvalidateRequerySuggested(); } }

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand BrowseCharacterCommand { get; }
    public RelayCommand ClearCharacterCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand ImportSelectedCommand { get; }
    public RelayCommand ImportAllCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    /// <summary>Set by the window: a file picker, the settings dialog (true when saved) and "show this file in Explorer".</summary>
    public Func<IEnumerable<string>>? PickFiles { get; set; }
    /// <summary>A folder anywhere on disk, or null if cancelled.</summary>
    public Func<string?>? PickFolder { get; set; }
    /// <summary>Title, file filter, project path to start near; returns an Assets/... path, or null if cancelled or outside the project.</summary>
    public Func<string, string, string, string?>? PickProjectFile { get; set; }
    public Func<AppSettings, bool>? ShowSettings { get; set; }
    public Action<string>? OpenPath { get; set; }

    /// <summary>Project path of the character model the animations belong to; empty for none. Setting it names and routes every animation in the list.</summary>
    public string CharacterModelPath
    {
        get => characterModelPath;
        set => SetCharacter(value);
    }

    public string CharacterSummary { get => characterSummary; private set { characterSummary = value; Raise(); } }

    void SetCharacter(string? text)
    {
        characterModelPath = (text ?? "").Trim().Replace('\\', '/');
        Raise(nameof(CharacterModelPath));
        CommandManager.InvalidateRequerySuggested();
        if (characterModelPath.Length == 0) { character = null; CharacterSummary = NoCharacterText; return; }

        var problem = CheckCharacterModel(characterModelPath);
        if (problem != null) { character = null; CharacterSummary = problem; return; }

        character = CharacterContext.FromModel(characterModelPath);
        foreach (var vm in Items) vm.ApplyCharacter(character);
        CharacterSummary = $"Character: {character.Name}. Animations are named \"{character.Name} <Action>\", saved into {character.AnimationsFolder} and use this model's skeleton. Fields you edit by hand are kept.";
    }

    string? CheckCharacterModel(string path)
    {
        if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            return "Pick the character's model FBX inside the project's Assets folder, for example Assets/Art/Characters/EnemyUnit/Models/EnemyUnit.fbx.";
        if (ProjectLocator.IsProject(Settings.ProjectPath) && !File.Exists(ProjectPaths.ToAbsolute(Settings.ProjectPath, path)))
            return $"Not found in the project: {path}";
        return null;
    }

    void ApplyCharacterTo(AssetItemViewModel vm)
    {
        if (character != null) vm.ApplyCharacter(character);
    }

    /// <summary>Files, or folders (searched recursively for model files; the dropped folder is the classification context).</summary>
    public void AddDropped(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                AddFiles(ModelFileScanner.Find(path), path);
            else
                AddFiles(new[] { path }, Path.GetDirectoryName(path));
        }
    }

    public void AddFiles(IEnumerable<string> paths, string? dropFolder)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (Items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var vm = new AssetItemViewModel(AssetItemFactory.Create(path, dropFolder, Settings), Settings) { AfterProfileChange = ApplyCharacterTo };
            ApplyCharacterTo(vm);
            Items.Add(vm);
            added++;
        }
        if (added > 0)
        {
            Selected ??= Items[^1];
            if (dropFolder != null) { Settings.LastDropFolder = dropFolder; store.Save(Settings); }
        }
        StatusText = added == 0 ? "No new files were added." : $"Added {added} file(s). Check each profile, then Validate or Import.";
    }

    /// <summary>Shows an unexpected exception in the status line and Result tab instead of letting it crash the app.</summary>
    public void ReportError(Exception e)
    {
        StatusText = "Unexpected error: " + e.Message;
        ResultText = string.Join(Environment.NewLine + Environment.NewLine, "Unexpected error: " + e.Message, e.ToString());
    }

    void Validate()
    {
        if (!ProjectLocator.IsProject(Settings.ProjectPath)) { StatusText = "Set the Blackglass project path in Settings first."; return; }
        var results = new ItemValidator(Settings.ProjectPath).ValidateAll(Items.Select(i => i.Item).ToList());
        foreach (var vm in Items) vm.ApplyValidation(results[vm.Item.Id]);
        var errors = results.Values.Sum(v => v.Count(m => m.Severity == Severity.Error));
        StatusText = errors == 0 ? "Validation passed." : $"Validation found {errors} error(s). Select an entry to read them.";
    }

    void OpenSettings()
    {
        if (ShowSettings != null && ShowSettings(Settings)) { store.Save(Settings); StatusText = "Settings saved."; }
    }

    async Task ImportAsync(IReadOnlyList<AssetItemViewModel> subset)
    {
        if (subset.Count == 0 || IsBusy) return;
        IsBusy = true;
        // A new run starts clean: after a pre-run failure there is no log, so Open log must not open the previous run's.
        LogPath = "";
        ResultText = "";
        try
        {
            foreach (var vm in subset) vm.SetStatus("Queued");
            var progress = new Progress<string>(text => StatusText = text);
            var runner = new ImportRunner(Settings, new UnityProcess());
            var outcome = await runner.RunAsync(subset.Select(i => i.Item).ToList(), progress);
            if (outcome.LogPath.Length > 0) LogPath = outcome.LogPath;
            var names = Items.ToDictionary(i => i.Item.Id, i => i.Item.Import.name);
            if (outcome.FatalError != null)
            {
                // ExitCode stays -1 when Unity never started (validation, staging or launch failure).
                var unityRan = outcome.ExitCode != -1 || outcome.ChangedFiles.Count > 0;
                ResultText = unityRan ? FormatAfterRunFailure(outcome) : outcome.FatalError;
                foreach (var vm in subset) vm.SetStatus(unityRan ? "No result" : "Not run");
                StatusText = unityRan
                    ? "Unity ran but returned no usable result; project files may have changed. See the Result tab."
                    : "Import did not run. See the Result tab.";
                return;
            }
            foreach (var r in outcome.Result!.items)
                Items.FirstOrDefault(i => i.Item.Id == r.id)?.ApplyResult(r);
            ResultText = ResultFormatter.Format(outcome.Result, id => names.GetValueOrDefault(id, id), outcome.ChangedFiles, outcome.LogPath);
            StatusText = outcome.Success ? "Import finished." : "Import finished with errors. See the Result tab.";
        }
        catch (Exception e)
        {
            // Anything unexpected (for example while reading results back): never leave entries stuck on "Queued".
            foreach (var vm in subset) vm.SetStatus("Error");
            ReportError(e);
        }
        finally { IsBusy = false; }
    }

    static string FormatAfterRunFailure(RunOutcome outcome)
    {
        var nl = Environment.NewLine;
        var text = outcome.FatalError ?? "";
        if (outcome.ChangedFiles.Count > 0)
            text += nl + nl + "Project files changed during the run (check with git status):" + nl + "  " + string.Join(nl + "  ", outcome.ChangedFiles);
        else
            text += nl + nl + "No project file changes were detected (or git was unavailable).";
        return text;
    }
}
