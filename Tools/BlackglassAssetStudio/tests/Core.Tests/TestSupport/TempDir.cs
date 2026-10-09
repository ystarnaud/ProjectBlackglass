namespace Blackglass.AssetStudio.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bgas-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string Write(string relative, string content = "x")
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Makes a folder that looks like a Unity project (Assets and ProjectSettings/ProjectVersion.txt).</summary>
    public string MakeProject(string version = "6000.3.25f1")
    {
        Directory.CreateDirectory(Combine("Assets"));
        Write("ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {version}\nm_EditorVersionWithRevision: {version} (abc)\n");
        return Path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}
