using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Blackglass.Accounts.Persistence.Migrations;

/// <summary>The ordered, validated set of migration files (spec §15.4): NNNN_lower_snake_name.sql, contiguous from 0000.</summary>
public sealed class MigrationCatalog
{
    static readonly Regex FileNamePattern = new(@"^(\d{4})_([a-z0-9]+(?:_[a-z0-9]+)*)\.sql$", RegexOptions.CultureInvariant);
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public IReadOnlyList<MigrationScript> Scripts { get; }
    public int LatestVersion => Scripts[^1].Version;

    MigrationCatalog(IReadOnlyList<MigrationScript> scripts) => Scripts = scripts;

    public static MigrationCatalog Parse(IEnumerable<(string FileName, byte[] Content)> files)
    {
        var scripts = new List<MigrationScript>();
        foreach (var (fileName, content) in files)
        {
            var match = FileNamePattern.Match(fileName);
            if (!match.Success)
                throw new MigrationCatalogException($"'{fileName}' is not named NNNN_lower_snake_name.sql.");
            var sql = Normalize(fileName, content);
            if (string.IsNullOrWhiteSpace(sql))
                throw new MigrationCatalogException($"'{fileName}' is empty.");
            var version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            scripts.Add(new MigrationScript(version, match.Groups[2].Value, sql, SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        }

        if (scripts.Count == 0) throw new MigrationCatalogException("No migrations found.");
        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Version == i) continue;
            throw new MigrationCatalogException(i > 0 && scripts[i].Version == scripts[i - 1].Version
                ? $"Version {scripts[i].Version:D4} appears twice."
                : $"Expected version {i:D4} but found {scripts[i].FileName}; versions start at 0000 and have no gaps.");
        }
        return new MigrationCatalog(scripts);
    }

    static string Normalize(string fileName, byte[] content)
    {
        string text;
        try { text = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException) { throw new MigrationCatalogException($"'{fileName}' is not valid UTF-8."); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return text.Replace("\r\n", "\n");
    }
}
