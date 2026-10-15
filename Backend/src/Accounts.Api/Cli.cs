using Blackglass.Accounts.Api.Admin;

namespace Blackglass.Accounts.Api;

public static class Cli
{
    public const string Usage = "usage: Accounts.Api [serve | migrate | admin …]";

    /// <param name="input">The operator's terminal for the admin CLI's confirmation prompt, or null when there is none.</param>
    public static async Task<int> RunAsync(string[] args, IReadOnlyDictionary<string, string?> env, TextWriter output, TextWriter error,
        CancellationToken ct = default, TextReader? input = null)
    {
        var command = args.Length == 0 ? "serve" : args[0];
        if (command == "admin") return await AdminCommand.RunAsync(args[1..], env, output, error, ct, input);
        // serve and migrate take no arguments. Nothing is forwarded to ASP.NET Core's configuration, so an argument can't
        // rebind Kestrel (the listen address comes from the environment via ApiHost.ResolveUrls, else the loopback default).
        if (args.Length > 1) { error.WriteLine(Usage); return ExitCodes.Usage; }
        switch (command)
        {
            case "migrate":
                return await MigrateCommand.RunAsync(env, output, error, ct);
            case "serve":
                var start = await ServeCommand.StartAsync(env, error, ct);
                if (start.App is null) return start.ExitCode;
                await using (start.App) await start.App.WaitForShutdownAsync(ct);
                return ExitCodes.Ok;
            default:
                error.WriteLine(Usage);
                return ExitCodes.Usage;
        }
    }
}
