namespace Blackglass.Accounts.Persistence.Migrations;

/// <summary>One migration file. <see cref="Sql"/> is normalized (no BOM, LF line endings), and <see cref="Checksum"/>
/// is the SHA-256 of its UTF-8 bytes, so a CRLF checkout on Windows and an LF copy on Linux agree.</summary>
public sealed record MigrationScript(int Version, string Name, string Sql, byte[] Checksum)
{
    public string FileName => $"{Version:D4}_{Name}.sql";
}
