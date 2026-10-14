using Blackglass.Accounts.Api;
using Blackglass.Accounts.Domain.Catalogue;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class ServeCatalogueTests
{
    [Fact]
    public async Task Serve_refuses_an_invalid_catalogue_before_reading_its_settings()
    {
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync(new Dictionary<string, string?>(), err, CancellationToken.None,
            () => GameCatalogue.Load("{}\n"u8.ToArray()));
        Assert.Equal(ExitCodes.InvalidCatalogue, start.ExitCode);
        Assert.Null(start.App);
        Assert.Contains("Refusing to start: Invalid catalogue", err.ToString());
        Assert.DoesNotContain("Configuration error", err.ToString());
    }

    [Fact]
    public async Task The_embedded_catalogue_passes_so_serve_goes_on_to_read_its_settings()
    {
        var err = new StringWriter();
        var start = await ServeCommand.StartAsync(new Dictionary<string, string?>(), err, CancellationToken.None);
        Assert.Equal(ExitCodes.Failure, start.ExitCode);
        Assert.Contains("Configuration error", err.ToString());
        Assert.DoesNotContain("catalogue", err.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
