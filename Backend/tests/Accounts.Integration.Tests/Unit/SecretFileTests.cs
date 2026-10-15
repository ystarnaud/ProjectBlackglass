using System.Security.AccessControl;
using System.Security.Principal;
using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>Test secrets (admin keys, database passwords) live in files only the current user can read, and are gone
/// after the test: on success, on a failed assertion and on an exception.</summary>
public class SecretFileTests
{
    [Fact]
    public void A_secret_file_is_readable_only_by_the_current_user_and_dispose_deletes_it()
    {
        string path;
        using (var file = SecretFile.Write("not-a-real-secret"))
        {
            path = file.Path;
            Assert.Equal("not-a-real-secret", File.ReadAllText(path));
            Assert.StartsWith(SecretFile.Root, path, StringComparison.Ordinal);
            AssertOnlyTheCurrentUserCanRead(path);
            AssertOnlyTheCurrentUserCanRead(Path.GetDirectoryName(path)!);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_secret_file_is_deleted_when_the_code_using_it_throws()
    {
        string? path = null;
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var file = SecretFile.Write("not-a-real-secret");
            path = file.Path;
            throw new InvalidOperationException("an assertion or a bug");
        }));
        Assert.NotNull(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Directories_left_by_a_test_process_that_no_longer_runs_are_swept()
    {
        var stale = Path.Combine(SecretFile.Root, $"{int.MaxValue}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "leftover"), "x");
        using var own = SecretFile.Write("y");
        var live = Path.GetDirectoryName(own.Path)!;

        SecretFile.SweepStaleDirectories();

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(live)); // this process's own directory stays
    }

    [Fact]
    public void A_directory_named_for_a_process_this_user_cannot_inspect_is_kept_and_does_not_break_the_sweep()
    {
        var guarded = Path.Combine(SecretFile.Root, $"4-{Guid.NewGuid():N}"); // Windows: the System process
        Directory.CreateDirectory(guarded);
        try
        {
            SecretFile.SweepStaleDirectories(); // must not throw (Win32Exception: access denied)
            Assert.True(Directory.Exists(guarded) || !OperatingSystem.IsWindows());
        }
        finally { if (Directory.Exists(guarded)) Directory.Delete(guarded, recursive: true); }
    }

    [Fact]
    public void Test_admin_keys_are_random_per_run_never_a_constant_in_source()
    {
        var key = AccountTestKit.NewAdminKey();
        Assert.True(key.Length >= Blackglass.Accounts.Api.Auth.AuthSettings.MinAdminKeyLength);
        Assert.NotEqual(key, AccountTestKit.NewAdminKey());
    }

    static void AssertOnlyTheCurrentUserCanRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.NotEmpty(rules);
            foreach (var rule in rules) Assert.Equal(me, rule.IdentityReference);
        }
        else
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
        }
    }
}
