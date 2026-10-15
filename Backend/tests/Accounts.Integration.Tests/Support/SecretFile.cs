using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>
/// A test secret (an admin key, a database password) in a file only the current user can read: one private directory
/// per test process under <see cref="Root"/> (Windows: a protected ACL granting only the current user; elsewhere 0700,
/// files 0600). Dispose deletes the file, so a `using` removes it on success, on a failed assertion and on an exception.
/// Directories left by a crashed test process are swept the first time a later run writes a secret.
/// </summary>
public sealed class SecretFile : IDisposable
{
    public static string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "blackglass-test-secrets");

    static readonly Lazy<string> ProcessDirectory = new(() =>
    {
        SweepStaleDirectories();
        var dir = System.IO.Path.Combine(Root, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        CreatePrivateDirectory(dir);
        return dir;
    });

    public string Path { get; }

    SecretFile(string path) => Path = path;

    public static SecretFile Write(string content)
    {
        var path = System.IO.Path.Combine(ProcessDirectory.Value, Guid.NewGuid().ToString("N"));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(new FileStream(path, options))) writer.Write(content); // inherits the directory's ACL on Windows
        return new SecretFile(path);
    }

    public void Dispose() => File.Delete(Path);

    /// <summary>Deletes the directories of test processes that are no longer running (named "&lt;pid&gt;-&lt;guid&gt;").</summary>
    public static void SweepStaleDirectories()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var name = System.IO.Path.GetFileName(dir);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0 || !int.TryParse(name[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || IsRunning(pid)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // another run is sweeping it too
        }
    }

    /// <summary>False only when no such process exists; a process this user can't inspect counts as running (its
    /// directory is kept, never a reason to fail every later secret write).</summary>
    static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
    }

    static void CreatePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).Create(security);
        }
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
