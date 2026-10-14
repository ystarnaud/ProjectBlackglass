using Blackglass.Accounts.Persistence;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class DbSettingsTests : IDisposable
{
    const string Secret = "s3cret-Value-Never-Printed";
    readonly string passwordFile = Path.Combine(Path.GetTempPath(), $"bg-pw-{Guid.NewGuid():N}");

    public DbSettingsTests() => File.WriteAllText(passwordFile, Secret + "\n");
    public void Dispose() => File.Delete(passwordFile);

    Dictionary<string, string?> Env() => new()
    {
        [DbSettings.HostVar] = "postgres", [DbSettings.NameVar] = "accounts",
        [DbSettings.UserVar] = "accounts_app", [DbSettings.PasswordFileVar] = passwordFile,
    };

    [Fact]
    public void Reads_settings_and_trims_the_trailing_newline_of_the_password_file()
    {
        var s = DbSettings.FromEnvironment(Env());
        Assert.Equal(("postgres", 5432, "accounts", "accounts_app"), (s.Host, s.Port, s.Database, s.Username));
        Assert.Equal("accounts_app@postgres:5432/accounts", s.ToString());
    }

    [Theory]
    [InlineData(DbSettings.HostVar)]
    [InlineData(DbSettings.NameVar)]
    [InlineData(DbSettings.UserVar)]
    [InlineData(DbSettings.PasswordFileVar)]
    public void Missing_variable_is_named(string name)
    {
        var env = Env();
        env.Remove(name);
        Assert.Contains(name, Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(env)).Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("54x")]
    public void Bad_port_is_refused(string port)
    {
        var env = Env();
        env[DbSettings.PortVar] = port;
        Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(env));
    }

    [Fact]
    public void Unreadable_or_empty_password_file_is_refused()
    {
        var env = Env();
        env[DbSettings.PasswordFileVar] = passwordFile + ".missing";
        Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(env));
        File.WriteAllText(passwordFile, "\n");
        Assert.Throws<DbSettingsException>(() => DbSettings.FromEnvironment(Env()));
    }

    [Fact]
    public void The_password_never_appears_in_text_output()
    {
        var s = DbSettings.FromEnvironment(Env());
        Assert.DoesNotContain(Secret, s.ToString());
        using var ds = s.CreateDataSource("test");
        Assert.DoesNotContain(Secret, ds.ConnectionString);
    }
}
