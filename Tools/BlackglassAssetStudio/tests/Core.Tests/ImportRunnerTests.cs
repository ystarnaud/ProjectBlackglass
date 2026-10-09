using Blackglass.AssetPipeline;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ImportRunnerTests
{
    sealed class FakeUnity : IUnityProcess
    {
        public readonly List<string[]> Calls = new();
        public Func<string[], int> Behaviour = _ => 0;

        public Task<int> RunAsync(string exe, IReadOnlyList<string> arguments, IProgress<string>? progress, string logPath)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            return Task.FromResult(Behaviour(args));
        }

        public static string Arg(string[] a, string name) => a[Array.IndexOf(a, name) + 1];
    }

    sealed class World : IDisposable
    {
        public readonly TempDir T = new();
        public readonly string Project;
        public readonly AppSettings Settings;
        public readonly FakeUnity Unity = new();

        public World(string? parentFolder = null)
        {
            if (parentFolder == null)
            {
                Project = T.MakeProject();
                Settings = new AppSettings { ProjectPath = Project, UnityExePath = T.Write("fake/Unity.exe"), StagingFolder = T.Combine("runs") };
            }
            else
            {
                // Project and runs root both live under a folder whose name has a space and a non-ASCII character.
                Project = T.Combine(parentFolder, "My Project");
                Directory.CreateDirectory(Path.Combine(Project, "Assets"));
                T.Write(parentFolder + "/My Project/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.3.25f1\n");
                Settings = new AppSettings { ProjectPath = Project, UnityExePath = T.Write("fake/Unity.exe"), StagingFolder = T.Combine(parentFolder, "runs") };
            }
        }

        /// <summary>A valid list entry for a file with a space in its name (classified as an environment piece; any valid entry will do).</summary>
        public AssetItem Item(string file = "Crate Box.fbx") => AssetItemFactory.Create(T.Write("src/" + file), null, Settings);

        public ImportRunner Runner(Func<string, bool>? open = null, Func<string, IReadOnlyDictionary<string, string>?>? git = null) =>
            new(Settings, Unity, open ?? (_ => false), git ?? (_ => null));

        /// <summary>A fake Unity that echoes every manifest item back, as successes or failures.</summary>
        public void Echo(bool success = true)
        {
            Unity.Behaviour = args =>
            {
                var manifest = ManifestJson.Deserialize<ImportManifest>(File.ReadAllText(FakeUnity.Arg(args, "-blackglassManifest")));
                foreach (var item in manifest.items) Assert.True(File.Exists(item.sourcePath), "staged source must exist: " + item.sourcePath);
                var result = new ImportResult { runId = manifest.runId, unityVersion = "6000.3.25f1", success = success };
                foreach (var item in manifest.items) result.items.Add(new ItemResult { id = item.id, success = success });
                File.WriteAllText(FakeUnity.Arg(args, "-blackglassResult"), ManifestJson.Serialize(result));
                return success ? 0 : 1;
            };
        }

        public void Dispose() => T.Dispose();
    }

    [Fact]
    public async Task Happy_path_stages_writes_the_manifest_launches_and_returns_the_result()
    {
        using var w = new World();
        w.Echo();
        var item = w.Item();
        var outcome = await w.Runner().RunAsync(new[] { item });

        Assert.Null(outcome.FatalError);
        Assert.True(outcome.Success);
        Assert.Single(w.Unity.Calls);
        Assert.True(File.Exists(Path.Combine(outcome.RunDir, "manifest.json")));
        Assert.Equal(item.Id, outcome.Result!.items[0].id);
        Assert.True(File.Exists(item.OriginalPath));
    }

    [Fact]
    public async Task Failed_items_come_back_as_a_result_not_a_fatal_error()
    {
        using var w = new World();
        w.Echo(success: false);
        var outcome = await w.Runner().RunAsync(new[] { w.Item() });
        Assert.Null(outcome.FatalError);
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Result);
        Assert.Equal(1, outcome.ExitCode);
    }

    [Fact]
    public async Task An_open_editor_stops_the_run_before_anything_is_staged()
    {
        using var w = new World();
        var outcome = await w.Runner(open: _ => true).RunAsync(new[] { w.Item() });
        Assert.Contains("Close it", outcome.FatalError);
        Assert.Empty(w.Unity.Calls);
        Assert.False(Directory.Exists(w.T.Combine("runs")));
    }

    [Fact]
    public async Task Validation_errors_stop_the_run_and_name_the_item()
    {
        using var w = new World();
        var item = w.Item();
        item.Import.destinationFolder = "Packages/nope";
        var outcome = await w.Runner().RunAsync(new[] { item });
        Assert.Contains("Crate_Box", outcome.FatalError);
        Assert.Contains("Assets", outcome.FatalError);
        Assert.Empty(w.Unity.Calls);
    }

    [Fact]
    public async Task Invalid_project_and_missing_unity_are_explained()
    {
        using var w = new World();
        var item = w.Item();
        w.Settings.ProjectPath = w.T.Combine("not-a-project");
        Assert.Contains("Unity project", (await w.Runner().RunAsync(new[] { item })).FatalError);

        w.Settings.ProjectPath = w.Project;
        w.Settings.UnityExePath = w.T.Combine("missing", "Unity.exe");
        Assert.Contains("Unity.exe", (await w.Runner().RunAsync(new[] { item })).FatalError);
    }

    [Fact]
    public async Task No_result_file_gives_the_exit_code_log_path_and_last_errors()
    {
        using var w = new World();
        w.Unity.Behaviour = args =>
        {
            File.WriteAllText(FakeUnity.Arg(args, "-logFile"), "booting\nAssets/Foo.cs(1,1): error CS1002: ; expected\n");
            return 1;
        };
        var outcome = await w.Runner().RunAsync(new[] { w.Item() });
        Assert.NotNull(outcome.FatalError);
        Assert.Contains("exit code 1", outcome.FatalError);
        Assert.Contains("unity.log", outcome.FatalError);
        Assert.Contains("error CS1002", outcome.FatalError);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task A_garbage_result_file_is_reported_not_thrown()
    {
        using var w = new World();
        w.Unity.Behaviour = args => { File.WriteAllText(FakeUnity.Arg(args, "-blackglassResult"), "{ not json"); return 0; };
        var outcome = await w.Runner().RunAsync(new[] { w.Item() });
        Assert.Contains("result", outcome.FatalError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_result_with_another_schema_version_is_rejected()
    {
        using var w = new World();
        w.Unity.Behaviour = args =>
        {
            File.WriteAllText(FakeUnity.Arg(args, "-blackglassResult"), "{\"schemaVersion\": 99, \"items\": []}");
            return 0;
        };
        Assert.Contains("schema", (await w.Runner().RunAsync(new[] { w.Item() })).FatalError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Changed_project_files_are_listed_from_the_git_snapshots()
    {
        using var w = new World();
        w.Echo();
        var calls = 0;
        IReadOnlyDictionary<string, string>? Snap(string _) =>
            ++calls == 1 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Assets/Crate_Box.fbx"] = "??|1" };
        var outcome = await w.Runner(git: Snap).RunAsync(new[] { w.Item() });
        Assert.Single(outcome.ChangedFiles);
        Assert.Contains("Crate_Box.fbx", outcome.ChangedFiles[0]);
    }

    [Fact]
    public async Task Unity_receives_every_path_as_one_argument_even_with_spaces_and_accents()
    {
        var folder = "Café Dir";
        using var w = new World(folder);
        w.Echo();
        var outcome = await w.Runner().RunAsync(new[] { w.Item("Café Prop.fbx") });
        Assert.Null(outcome.FatalError);
        var args = w.Unity.Calls.Single();
        foreach (var name in new[] { "-projectPath", "-blackglassManifest", "-blackglassResult", "-logFile" })
            Assert.Contains(folder, FakeUnity.Arg(args, name));
        Assert.Equal(w.Project, FakeUnity.Arg(args, "-projectPath"));
        Assert.True(File.Exists(FakeUnity.Arg(args, "-blackglassManifest")));
        Assert.Equal(1, args.Count(a => a == FakeUnity.Arg(args, "-blackglassManifest")));
    }

    [Fact]
    public async Task An_unreadable_result_file_is_reported_not_thrown()
    {
        using var w = new World();
        FileStream? held = null;
        w.Unity.Behaviour = args =>
        {
            held = new FileStream(FakeUnity.Arg(args, "-blackglassResult"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            return 0;
        };
        try
        {
            var outcome = await w.Runner().RunAsync(new[] { w.Item() });
            Assert.NotNull(outcome.FatalError);
            Assert.Contains("result", outcome.FatalError, StringComparison.OrdinalIgnoreCase);
            Assert.Null(outcome.Result);
        }
        finally { held?.Dispose(); }
    }
}
