using Blackglass.Accounts.Api;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class CliTests
{
    [Fact]
    public async Task Unknown_command_prints_usage_and_exits_64()
    {
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await Cli.RunAsync(["bogus"], new Dictionary<string, string?>(), TextWriter.Null, err));
        Assert.Contains("usage", err.ToString());
    }

    [Fact]
    public async Task Migrate_with_extra_arguments_is_a_usage_error()
    {
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await Cli.RunAsync(["migrate", "0003"], new Dictionary<string, string?>(), TextWriter.Null, err));
        Assert.Contains("usage", err.ToString());
    }

    [Theory]
    [InlineData("--urls=http://0.0.0.0:5080")]
    [InlineData("--Kestrel:Endpoints:Http:Url=http://0.0.0.0:5080")]
    [InlineData("--environment=Development")]
    public async Task Serve_with_any_argument_is_a_usage_error_before_reading_settings(string argument)
    {
        // No database settings are given: a usage error proves the argument was refused before anything else ran.
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await Cli.RunAsync(["serve", argument], new Dictionary<string, string?>(), TextWriter.Null, err));
        Assert.Contains("usage", err.ToString());
    }

    [Fact]
    public async Task Missing_database_settings_fail_with_the_variable_named()
    {
        var err = new StringWriter();
        Assert.Equal(ExitCodes.Failure, await Cli.RunAsync(["migrate"], new Dictionary<string, string?>(), TextWriter.Null, err));
        Assert.Contains("BLACKGLASS_DB_", err.ToString());
    }
}
