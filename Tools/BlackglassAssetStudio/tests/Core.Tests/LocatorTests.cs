using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class LocatorTests
{
    [Fact]
    public void Project_detection_needs_assets_and_version_file()
    {
        using var t = new TempDir();
        Assert.False(ProjectLocator.IsProject(t.Path));
        t.MakeProject();
        Assert.True(ProjectLocator.IsProject(t.Path));
        Assert.False(ProjectLocator.IsProject(null));
        Assert.False(ProjectLocator.IsProject(""));
    }

    [Fact]
    public void FindFrom_walks_up_to_the_project()
    {
        using var t = new TempDir();
        t.MakeProject();
        var deep = t.Combine("Tools", "X", "bin");
        Directory.CreateDirectory(deep);
        Assert.Equal(t.Path, ProjectLocator.FindFrom(deep));
    }

    [Fact]
    public void FindFrom_returns_null_outside_a_project()
    {
        using var t = new TempDir();
        Assert.Null(ProjectLocator.FindFrom(t.Path));
    }

    [Fact]
    public void Editor_version_is_read_from_the_project()
    {
        using var t = new TempDir();
        t.MakeProject("6000.3.25f1");
        Assert.Equal("6000.3.25f1", ProjectLocator.ReadEditorVersion(t.Path));
    }

    [Fact]
    public void Unity_is_found_under_the_hub_root_for_the_projects_version_only()
    {
        using var hub = new TempDir();
        hub.Write("6000.3.25f1/Editor/Unity.exe");
        hub.Write("2022.3.46f1/Editor/Unity.exe");
        var found = UnityLocator.FindExecutable("6000.3.25f1", new[] { hub.Path });
        Assert.EndsWith(Path.Combine("6000.3.25f1", "Editor", "Unity.exe"), found);
        Assert.Null(UnityLocator.FindExecutable("6000.9.9f1", new[] { hub.Path }));
    }

    [Fact]
    public void Resolve_accepts_an_explicit_path_whose_hub_folder_matches_the_project_version()
    {
        using var t = new TempDir();
        var project = t.MakeProject("6000.3.25f1");
        var exe = t.Write("hub/6000.3.25f1/Editor/Unity.exe");
        var r = UnityLocator.Resolve(new AppSettings { UnityExePath = exe }, project);
        Assert.Equal(exe, r.Path);
        Assert.Contains("6000.3.25f1", r.Message);
    }

    [Fact]
    public void Resolve_refuses_an_explicit_path_of_a_different_editor_version()
    {
        using var t = new TempDir();
        var project = t.MakeProject("6000.3.25f1");
        var exe = t.Write("hub/2022.3.46f1/Editor/Unity.exe");
        var r = UnityLocator.Resolve(new AppSettings { UnityExePath = exe }, project);
        Assert.Null(r.Path);
        Assert.Contains("2022.3.46f1", r.Message);
        Assert.Contains("6000.3.25f1", r.Message);
        Assert.Contains("Settings", r.Message);
    }

    [Fact]
    public void Resolve_allows_an_explicit_path_with_an_unknown_version_but_says_it_could_not_verify()
    {
        using var t = new TempDir();
        var project = t.MakeProject("6000.3.25f1");
        var exe = t.Write("my unity/Unity.exe");
        var r = UnityLocator.Resolve(new AppSettings { UnityExePath = exe }, project);
        Assert.Equal(exe, r.Path);
        Assert.Contains("could not be verified", r.Message);
        Assert.Contains("6000.3.25f1", r.Message);
    }

    [Theory]
    [InlineData("6000.3.25f1", true)]
    [InlineData("2022.3.46f1", true)]
    [InlineData("6000.0.0b3", true)]
    [InlineData("my unity", false)]
    [InlineData("bgas-1234abcd", false)]
    public void Hub_layout_version_is_only_trusted_when_the_folder_looks_like_a_unity_version(string folder, bool known)
    {
        using var t = new TempDir();
        var exe = t.Write(folder + "/Editor/Unity.exe");
        var v = UnityLocator.DetectEditorVersion(exe);
        if (known) Assert.Equal(folder, v); else Assert.Null(v);
    }

    [Fact]
    public void Resolve_explains_a_missing_editor_and_names_the_version()
    {
        using var t = new TempDir();
        var project = t.MakeProject("6000.1.1f1");
        var r = UnityLocator.Resolve(new AppSettings(), project, new[] { t.Combine("nohub") });
        Assert.Null(r.Path);
        Assert.Contains("6000.1.1f1", r.Message);
    }

    [Fact]
    public void Resolve_rejects_a_bad_explicit_path_instead_of_silently_using_another()
    {
        using var t = new TempDir();
        var project = t.MakeProject();
        var r = UnityLocator.Resolve(new AppSettings { UnityExePath = t.Combine("missing.exe") }, project, new[] { t.Combine("nohub") });
        Assert.Null(r.Path);
        Assert.Contains("missing.exe", r.Message);
    }
}
