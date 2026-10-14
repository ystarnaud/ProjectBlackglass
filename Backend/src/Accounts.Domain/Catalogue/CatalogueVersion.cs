using System.Security.Cryptography;

namespace Blackglass.Accounts.Domain.Catalogue;

/// <summary>catalogueVersion: "sha256:" and the lowercase hex SHA-256 of the catalogue file's bytes (spec §4.6).</summary>
public static class CatalogueVersion
{
    public static string Of(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
