using AgentWeb.Cli;

AgentWeb.Shared.Lang.Use(Environment.GetEnvironmentVariable("AGENTWEB_LANG"));
return await CliApp.RunAsync(args, CliEnvironment.FromConsole());
