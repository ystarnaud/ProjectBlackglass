namespace Blackglass.AssetStudio;

/// <summary>Finds importable model files inside a dropped folder.</summary>
public static class ModelFileScanner
{
    public static readonly string[] ModelExtensions = { ".fbx", ".obj", ".glb", ".gltf" };

    /// <summary>Model files below <paramref name="folder"/>, recursively. Folders that cannot be read are skipped, not fatal.</summary>
    public static IReadOnlyList<string> Find(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "*.*", options)
            .Where(f => ModelExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
