using Blackglass.Accounts.Domain.Attempts;

namespace Blackglass.Accounts.Domain.Tests.Attempts;

public class AttemptSecretsTests
{
    [Fact]
    public void Secrets_seed_is_31_bit_non_negative_and_salt_32_bytes()
    {
        IAttemptSecrets secrets = new CryptoAttemptSecrets();

        var seeds = Enumerable.Range(0, 2000).Select(_ => secrets.NextSeed()).ToList();
        Assert.All(seeds, seed => Assert.InRange(seed, 0, int.MaxValue - 1));
        Assert.True(seeds.Distinct().Count() > 1990, "2000 draws from 2^31 values should almost never repeat");

        var salts = Enumerable.Range(0, 100).Select(_ => secrets.NextRetentionSalt()).ToList();
        Assert.All(salts, salt => Assert.Equal(32, salt.Length));
        Assert.Equal(100, salts.Select(Convert.ToHexString).Distinct().Count());
        // Each call returns its own array: a caller can't alter another attempt's salt.
        Assert.NotSame(salts[0], salts[1]);

        // ToString is the type name only: no value can leak through it.
        Assert.Equal(typeof(CryptoAttemptSecrets).FullName, secrets.ToString());
    }

    [Fact]
    public void Production_secrets_use_RandomNumberGenerator()
    {
        var source = File.ReadAllText(SourcePath("Attempts", "AttemptSecrets.cs"));
        Assert.Contains("RandomNumberGenerator.GetInt32(0, int.MaxValue)", source);
        Assert.Contains("RandomNumberGenerator.GetBytes(", source);
        Assert.DoesNotContain("System.Random", source);
        Assert.DoesNotContain("new Random(", source);
        Assert.DoesNotContain("Random.Shared", source);
        Assert.DoesNotContain("override string ToString", source);

        // Nothing else in the Stage 7 domain draws from System.Random either.
        foreach (var folder in new[] { "Attempts", "Insurance" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(SourcePath(folder, "x"))!, "*.cs"))
            {
                var text = File.ReadAllText(file);
                Assert.False(text.Contains("System.Random") || text.Contains("new Random(") || text.Contains("Random.Shared"), file);
            }
        }
    }

    /// <summary>The domain source file, found from the test binaries (Backend/tests/Accounts.Domain.Tests/bin/...). Fails, never skips, when absent.</summary>
    static string SourcePath(string folder, string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var domain = Path.Combine(dir.FullName, "src", "Accounts.Domain");
            if (Directory.Exists(domain)) return Path.Combine(domain, folder, file);
        }
        throw new InvalidOperationException("Backend/src/Accounts.Domain was not found above " + AppContext.BaseDirectory);
    }
}
