using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace Blackglass.AssetStudio.Tests;

public class ModelFileScannerTests
{
    [Fact]
    public void Finds_model_files_recursively_and_ignores_other_files()
    {
        using var t = new TempDir();
        t.Write("a/Hero.FBX");
        t.Write("a/deep/er/Rock.obj");
        t.Write("a/notes.txt");
        t.Write("a/Crate.glb");
        var found = ModelFileScanner.Find(t.Combine("a")).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "Crate.glb", "Hero.FBX", "Rock.obj" }, found);
    }

    [Fact]
    public void Skips_inaccessible_subfolders_instead_of_throwing()
    {
        using var t = new TempDir();
        t.Write("root/Visible.fbx");
        t.Write("root/locked/Hidden.fbx");
        var locked = new DirectoryInfo(t.Combine("root", "locked"));
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = locked.GetAccessControl();
        acl.AddAccessRule(deny);
        locked.SetAccessControl(acl);
        try
        {
            var found = ModelFileScanner.Find(t.Combine("root")).Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "Visible.fbx" }, found);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            locked.SetAccessControl(acl);
        }
    }
}
