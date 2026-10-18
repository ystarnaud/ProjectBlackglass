namespace Blackglass.MissionHost.Agent;

/// <summary>
/// The agent's only way to write a file in its own directories: a new temporary file (0600, created exclusively), fsynced,
/// renamed over the target (atomic), then the directory fsynced. A crash leaves the old content or the new, never part of
/// either; a leftover temporary file is ignored by every reader and removed by <see cref="RemoveTemporaries"/>.
/// </summary>
public static class AtomicFile
{
    const string TempMarker = ".agent-tmp-";

    public static bool IsTemporary(string path) => Path.GetFileName(path).Contains(TempMarker, StringComparison.Ordinal);

    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("A path with a directory is required.", nameof(path));
        var temp = path + TempMarker + Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        Unix.SyncDirectory(directory);
    }

    /// <summary>Deletes a file the agent owns and makes the deletion durable.</summary>
    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        Unix.SyncDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>Creates a directory the agent owns (0700) if it is missing.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Unix.SyncDirectory(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!);
    }

    /// <summary>Removes temporary files a crash left in one of the agent's directories.</summary>
    public static int RemoveTemporaries(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory).Where(IsTemporary))
        {
            File.Delete(file);
            removed++;
        }
        if (removed > 0) Unix.SyncDirectory(directory);
        return removed;
    }
}
