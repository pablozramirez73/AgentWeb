using System.Text;

namespace AgentWeb.Cli;

// Tutto ciò che la CLI usa del mondo esterno: nei test si sostituisce con stringhe e il server in memoria.
public sealed class CliEnvironment
{
    public required TextReader In { get; init; }
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }
    public required string ConfigDir { get; init; }
    public bool Interactive { get; init; }
    public Func<string?>? ReadSecret { get; init; } // prompt senza eco; null se non c'è un terminale
    public Func<HttpMessageHandler> CreateHandler { get; init; } = () => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = System.Net.DecompressionMethods.All }; // zstd, br, gzip
    public string? DefaultServer { get; init; }
    public CancellationToken Cancel { get; init; } // Ctrl+C / SIGTERM: ferma il daemon e i suoi processi
    public TimeSpan DaemonHeartbeat { get; init; } = TimeSpan.FromSeconds(10);
    public string ExecutablePath { get; init; } = Environment.ProcessPath ?? "agentweb";

    public static CliEnvironment FromConsole() => new()
    {
        Cancel = StopSignal(),
        In = Console.In,
        Out = Console.Out,
        Err = Console.Error,
        Interactive = !Console.IsInputRedirected,
        ReadSecret = Console.IsInputRedirected ? null : ReadHidden,
        DefaultServer = Environment.GetEnvironmentVariable("AGENTWEB_SERVER"),
        // ~/.config/agentweb (o $XDG_CONFIG_HOME/agentweb); AGENTWEB_CONFIG_DIR per profili separati (es. un agente).
        ConfigDir = Environment.GetEnvironmentVariable("AGENTWEB_CONFIG_DIR")
            ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "agentweb"),
    };

    static System.Runtime.InteropServices.PosixSignalRegistration? sigterm; // tenuta viva: altrimenti il GC toglie il gestore

    static CancellationToken StopSignal()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
        return cts.Token;
    }

    static string? ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return sb.ToString(); }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
    }
}
