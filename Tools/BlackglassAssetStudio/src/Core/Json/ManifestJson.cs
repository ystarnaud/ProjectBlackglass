using System.Text.Json;

namespace Blackglass.AssetStudio;

/// <summary>JSON settings for the manifest and result: public fields, camelCase names exactly as declared in the contract.</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The JSON document is empty.");

    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
}
