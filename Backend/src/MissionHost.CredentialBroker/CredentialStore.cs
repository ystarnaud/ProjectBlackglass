using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Blackglass.MissionHost.CredentialBroker;

/// <summary>Where the store is and who must own it: production is <see cref="Production"/>, root's own directory.</summary>
public sealed record BrokerOptions(string Root, uint Owner)
{
    public static readonly BrokerOptions Production = new("/run/blackglass/credentials", 0);
}

public enum BrokerCode { Ok, Stale, Conflict, UnsafeStore, WriteFailed, BadAttempt }

/// <summary>A fault injected at a named write step (tests only): "process:write", "process:rename", "generation:write",
/// "generation:rename". It throws there, or runs <paramref name="pause"/> instead (to hold a request inside the store's lock).</summary>
public sealed class Faults(string point, Action? pause = null)
{
    public void Hit(string name)
    {
        if (name != point) return;
        if (pause is not null) pause();
        else throw new IOException($"injected failure at {name}");
    }
}

/// <summary>
/// The root-only credential store the mission template's LoadCredential reads (security correction I3):
/// Root (root:root 0700) / &lt;attempt&gt; (0700) / process (0400, the credential bytes only) and generation (0400, its decimal).
/// - The root must be a real directory of the owner with no group or other bit; the attempt directory and both files likewise;
///   a link, another file type, a second hard link, another owner or a wider mode anywhere fails closed (unsafe_store) and
///   nothing is read or written through it.
/// - Every request holds an exclusive flock on the root, so concurrent preparations are serialized.
/// - A higher generation replaces the credential; the same generation is idempotent with the same bytes and a conflict with
///   others; a lower one is stale. The credential is written before its generation, so a failure in between only lets the newer
///   request through again.
/// - Writes are new files (O_CREAT|O_EXCL|O_NOFOLLOW, exact mode), fsynced, renamed into place, the directory fsynced. Any
///   failure answers write_failed and the agent never starts the unit.
/// The log names the attempt, the generation and the result; never the credential.
/// </summary>
public sealed class CredentialStore(BrokerOptions options, TextWriter log, Faults? faults = null)
{
    const uint DirectoryMode = 0x1C0, FileMode = 0x100; // 0700, 0400
    const string TempPrefix = ".tmp-";
    static readonly string[] Known = ["process", "generation"];

    public BrokerCode Prepare(string attempt, int generation, string credential)
    {
        if (!BrokerRequest.IsAttempt(attempt) || generation < 1 || !BrokerRequest.IsCredential(credential)) return Log("prepare", attempt, generation, BrokerCode.BadAttempt);
        using var root = Native.OpenDirectory(options.Root, out var rootEntry);
        if (root is null || !Safe(rootEntry, directory: true)) return Log("prepare", attempt, generation, BrokerCode.UnsafeStore);
        Native.LockExclusive(root);
        var directory = Path.Combine(options.Root, attempt);
        var entry = Native.LStat(directory, out var errno);
        if (entry is null)
        {
            if (errno != Native.ENoEnt || !Native.MakeDirectory(directory, DirectoryMode, out _)) return Log("prepare", attempt, generation, BrokerCode.WriteFailed);
            entry = Native.LStat(directory);
        }
        if (entry is not { } dir || !Safe(dir, directory: true) || !CleanEntries(directory)) return Log("prepare", attempt, generation, BrokerCode.UnsafeStore);

        var processPath = Path.Combine(directory, "process");
        var generationPath = Path.Combine(directory, "generation");
        if (!Inspect(processPath, out var current) || !Inspect(generationPath, out var currentGeneration)) return Log("prepare", attempt, generation, BrokerCode.UnsafeStore);
        var existing = 0;
        if (currentGeneration is not null && !int.TryParse(Encoding.ASCII.GetString(currentGeneration), NumberStyles.None, CultureInfo.InvariantCulture, out existing))
            return Log("prepare", attempt, generation, BrokerCode.UnsafeStore);
        var bytes = Encoding.ASCII.GetBytes(credential);
        if (existing > generation) return Log("prepare", attempt, generation, BrokerCode.Stale);
        if (existing == generation && current is not null)
            return Log("prepare", attempt, generation, CryptographicOperations.FixedTimeEquals(current, bytes) ? BrokerCode.Ok : BrokerCode.Conflict);
        try
        {
            Replace(directory, "process", bytes);
            Replace(directory, "generation", Encoding.ASCII.GetBytes(generation.ToString(CultureInfo.InvariantCulture)));
            return Log("prepare", attempt, generation, BrokerCode.Ok);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RemoveTemporaries(directory);
            return Log("prepare", attempt, generation, BrokerCode.WriteFailed);
        }
    }

    public BrokerCode Discard(string attempt)
    {
        if (!BrokerRequest.IsAttempt(attempt)) return Log("discard", attempt, null, BrokerCode.BadAttempt);
        using var root = Native.OpenDirectory(options.Root, out var rootEntry);
        if (root is null || !Safe(rootEntry, directory: true)) return Log("discard", attempt, null, BrokerCode.UnsafeStore);
        Native.LockExclusive(root);
        var directory = Path.Combine(options.Root, attempt);
        var entry = Native.LStat(directory, out var errno);
        if (entry is null) return Log("discard", attempt, null, errno == Native.ENoEnt ? BrokerCode.Ok : BrokerCode.UnsafeStore);
        if (!Safe(entry.Value, directory: true) || !CleanEntries(directory)) return Log("discard", attempt, null, BrokerCode.UnsafeStore);
        try
        {
            foreach (var name in Known)
            {
                var path = Path.Combine(directory, name);
                if (Native.LStat(path) is { } f && !f.IsDirectory) File.Delete(path); // unlink: the name itself, never a target
            }
            Directory.Delete(directory);
            Native.Sync(root);
            return Log("discard", attempt, null, BrokerCode.Ok);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Log("discard", attempt, null, BrokerCode.WriteFailed); }
    }

    bool Safe(Native.Entry entry, bool directory) =>
        (directory ? entry.IsDirectory : entry.IsRegular && entry.Nlink == 1) && entry.Uid == options.Owner && entry.Private;

    /// <summary>True when the file is absent (<paramref name="bytes"/> null) or a safe one whose bytes were read; false otherwise.</summary>
    bool Inspect(string path, out byte[]? bytes)
    {
        bytes = null;
        var entry = Native.LStat(path, out var errno);
        if (entry is null) return errno == Native.ENoEnt;
        if (!Safe(entry.Value, directory: false) || entry.Value.Size > BrokerRequest.MaxBytes) return false;
        bytes = Native.ReadPrivate(path, options.Owner, BrokerRequest.MaxBytes);
        return bytes is not null;
    }

    /// <summary>Only the two credential files and this store's own crash leftovers may be in an attempt directory; a leftover is
    /// removed, anything else makes the store unsafe.</summary>
    bool CleanEntries(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(path);
            if (Known.Contains(name)) continue;
            if (!name.StartsWith(TempPrefix, StringComparison.Ordinal) || Native.LStat(path) is not { IsDirectory: false }) return false;
            File.Delete(path);
        }
        return true;
    }

    void Replace(string directory, string name, byte[] bytes)
    {
        var temp = Path.Combine(directory, TempPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        Native.WriteNew(temp, bytes, FileMode, () => faults?.Hit(name + ":write"));
        faults?.Hit(name + ":rename");
        File.Move(temp, Path.Combine(directory, name), overwrite: true); // rename(2): replaces the name itself
        using var dir = Native.OpenDirectory(directory, out _) ?? throw new IOException("The attempt directory could not be reopened.");
        Native.Sync(dir);
    }

    static void RemoveTemporaries(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, TempPrefix + "*"))
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    BrokerCode Log(string verb, string attempt, int? generation, BrokerCode code)
    {
        var shown = BrokerRequest.IsAttempt(attempt) ? attempt : "invalid";
        log.WriteLine($"credential_{verb} attempt={shown}{(generation is { } g ? $" generation={g}" : "")} result={Broker.Wire(code)}");
        return code;
    }
}
