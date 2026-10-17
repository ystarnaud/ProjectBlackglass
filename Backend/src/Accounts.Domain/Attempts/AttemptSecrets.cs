using System.Security.Cryptography;

namespace Blackglass.Accounts.Domain.Attempts;

/// <summary>
/// The per-attempt secrets drawn at admission: the mission seed and the 32-byte retention salt (spec §6.2). They are
/// stored with the attempt and never appear in a client response, a log, an audit row, an anomaly, an error or admin
/// output. Implementations must not print a value through ToString.
/// </summary>
public interface IAttemptSecrets
{
    /// <summary>A seed from 0 to int.MaxValue − 1 (31 bits, never negative).</summary>
    int NextSeed();

    /// <summary>32 fresh bytes, in a new array each call.</summary>
    byte[] NextRetentionSalt();
}

/// <summary>The production secrets: the operating system's cryptographic generator. Holds no state, so it has nothing to print.</summary>
public sealed class CryptoAttemptSecrets : IAttemptSecrets
{
    public const int RetentionSaltBytes = 32;

    public int NextSeed() => RandomNumberGenerator.GetInt32(0, int.MaxValue);

    public byte[] NextRetentionSalt() => RandomNumberGenerator.GetBytes(RetentionSaltBytes);
}
