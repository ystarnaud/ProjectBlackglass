using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ProjectPathsTests
{
    const string Root = @"C:\Games\Blackglass";

    [Theory]
    [InlineData(@"C:\Games\Blackglass\Assets\Art\Characters\EnemyUnit\Models\EnemyUnit.fbx", "Assets/Art/Characters/EnemyUnit/Models/EnemyUnit.fbx")]
    [InlineData(@"c:\games\BLACKGLASS\assets\Art", "Assets/Art")]
    [InlineData(@"C:\Games\Blackglass\Assets", "Assets")]
    [InlineData(@"C:\Games\Blackglass\Assets\Art\..\Props\Crate.fbx", "Assets/Props/Crate.fbx")]
    [InlineData(@"C:\Games\Blackglass\Assets\", "Assets")]
    public void A_path_inside_Assets_becomes_a_project_path(string absolute, string expected)
    {
        Assert.True(ProjectPaths.TryToProjectPath(Root, absolute, out var project, out var error), error);
        Assert.Equal(expected, project);
    }

    [Theory]
    [InlineData(@"D:\Elsewhere\Model.fbx")]
    [InlineData(@"C:\Games\Blackglass")]
    [InlineData(@"C:\Games\Blackglass\Library\x.fbx")]
    [InlineData(@"C:\Games\Blackglass\AssetsExtra\x.fbx")]
    [InlineData(@"C:\Games\Blackglass\Assets\..\ProjectSettings\x.asset")]
    [InlineData("")]
    public void A_path_outside_Assets_is_refused_with_a_reason(string absolute)
    {
        Assert.False(ProjectPaths.TryToProjectPath(Root, absolute, out var project, out var error));
        Assert.Equal("", project);
        Assert.Contains("Assets", error);
    }

    [Fact]
    public void ToAbsolute_joins_under_the_project_root() =>
        Assert.Equal(@"C:\Games\Blackglass\Assets\Art\X", ProjectPaths.ToAbsolute(Root, "Assets/Art/X"));

    [Fact]
    public void ToAbsolute_of_an_empty_path_is_the_Assets_folder() =>
        Assert.Equal(@"C:\Games\Blackglass\Assets", ProjectPaths.ToAbsolute(Root, ""));
}
