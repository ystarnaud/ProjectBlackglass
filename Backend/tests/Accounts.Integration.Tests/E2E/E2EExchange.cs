using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Blackglass.Accounts.IntegrationTests.E2E;

/// <summary>A fact that runs only when BLACKGLASS_E2E=1 (Stage 13 CP4, run by hand after the owner's approval); skipped otherwise.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BLACKGLASS_E2E") != "1") Skip = "Stage 13 CP4 end-to-end: set BLACKGLASS_E2E=1 (owner-approved runs only).";
    }
}

/// <summary>
/// The private directory the CP4 host and the Unity Editor test share (BLACKGLASS_E2E_DIR, outside the repository): created by
/// the host (it must not exist yet), readable by the current user only. Every file is written whole (a temporary file, then a
/// rename), so a reader never sees half of one. Per scenario S: the host writes S/start.json, the credential S/credentials/process,
/// S/ticket, S/start-dropped.json, S/api-stopped.json; Unity writes S/unity-waiting.json, S/holding.json, S/unity-result.json and
/// its outbox S/outbox. evidence/ holds the redacted record of the run.
/// </summary>
public sealed class E2EExchange
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root { get; }

    E2EExchange(string root) => Root = root;

    public string Record => Path.Combine(Root, "cp4-database.json");

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static E2EExchange Create(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root)) throw new InvalidOperationException("BLACKGLASS_E2E_DIR must be an absolute path.");
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException($"{root} already exists: CP4 starts from a new directory.");
        CreatePrivateDirectory(root);
        var exchange = new E2EExchange(root);
        Directory.CreateDirectory(exchange.PathOf("evidence"));
        return exchange;
    }

    public void CreateDirectory(string relative) => Directory.CreateDirectory(PathOf(relative));

    public void WriteJson(string relative, object value) => WriteBytes(relative, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public void WriteBytes(string relative, byte[] bytes)
    {
        var path = PathOf(relative);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: false);
    }

    public void Evidence(string name, object value) => WriteJson("evidence/" + name, value);

    public async Task<JsonElement> WaitForJsonAsync(string relative, TimeSpan timeout)
    {
        var path = PathOf(relative);
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{relative} did not appear within {timeout}.");
            await Task.Delay(200);
        }
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        return doc.RootElement.Clone();
    }

    /// <summary>Every evidence file's text, for the final secrecy scan.</summary>
    public IEnumerable<(string File, string Text)> EvidenceTexts() =>
        Directory.GetFiles(PathOf("evidence")).Select(f => (Path.GetFileName(f), File.ReadAllText(f, Encoding.UTF8)));

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
