using System.Collections;

namespace Blackglass.Accounts.Api;

public static class ProcessEnvironment
{
    public static IReadOnlyDictionary<string, string?> Read() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
}
