namespace AgentWeb.Shared.Workspace;

// Daemon (`agentweb daemon`): processo sul computer di un utente che esegue gli agenti legati a lui con runtime locali
// (claude, codex, qualsiasi comando che legge il prompt da stdin e scrive la risposta su stdout).
// Il server non esegue nulla e non dà credenziali: il daemon prende il lavoro con il login dell'utente, come la CLI.
public sealed record DaemonPollRequest(string Machine, List<string> Runtimes);

public sealed record DaemonDelivery(string Id, string Runtime, string Prompt);

public sealed record DaemonResult(string? Text, string? Error);

public sealed record MachineDto(string Machine, List<string> Runtimes, DateTimeOffset LastSeen, int Running);
