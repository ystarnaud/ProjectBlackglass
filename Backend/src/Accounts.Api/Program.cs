using Blackglass.Accounts.Api;

// Only a terminal is asked for confirmation: a redirected stdin (a script, `docker compose run -T`) may never send a line.
return await Cli.RunAsync(args, ProcessEnvironment.Read(), Console.Out, Console.Error, input: Console.IsInputRedirected ? null : Console.In);
