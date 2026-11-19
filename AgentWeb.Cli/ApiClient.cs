using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentWeb.Cli;

// Errore con codice di uscita: 1 = errore del server/richiesta, 2 = uso sbagliato, 3 = non autenticato.
public sealed class CliException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public static class ExitCodes
{
    public const int Ok = 0, Error = 1, Usage = 2, Unauthenticated = 3;
}

// Credenziali salvate dopo il login: refresh token incluso, file leggibile solo dal proprietario (0600).
public sealed record Credentials(string Server, string UserName, string AccessToken, string RefreshToken);

public sealed class CredentialStore(string dir)
{
    string FilePath => Path.Combine(dir, "credentials.json");

    public Credentials? Load() =>
        File.Exists(FilePath) ? JsonSerializer.Deserialize<Credentials>(File.ReadAllText(FilePath), CliJson.Options) : null;

    public void Save(Credentials c)
    {
        Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, CliJson.Options));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Delete() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}

public static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

// Client delle API del server: stesse rotte e stessa autorizzazione delle app (nessuna rotta riservata alla CLI).
// Su 401 prova una volta il refresh del token; se la sessione è revocata -> CliException con codice 3.
public sealed class ApiClient(HttpClient http, CredentialStore store, Credentials creds)
{
    sealed record TokenResponse(string AccessToken, string RefreshToken);

    Credentials current = creds;

    public string UserName => current.UserName;
    public string Server => http.BaseAddress!.ToString();

    public async Task<T> GetAsync<T>(string path) => await ReadAsync<T>(await SendAsync(() => new(HttpMethod.Get, path)));

    public async Task<T> PostAsync<T>(string path, object? body) =>
        await ReadAsync<T>(await SendAsync(() => new(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body, options: CliJson.Options) }));

    public async Task PostAsync(string path, object? body) =>
        (await SendAsync(() => new(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body, options: CliJson.Options) })).Dispose();

    // Come le altre chiamate (refresh, 401 -> codice 3), ma restituisce la risposta qualunque sia lo stato: 204, 404… li gestisce chi chiama.
    public Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendCoreAsync(() => new(method, path) { Content = body is null ? null : JsonContent.Create(body, options: CliJson.Options) }, ct);

    async Task<HttpResponseMessage> SendCoreAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var r = await SendOnceAsync(make(), ct);
        if (r.StatusCode == HttpStatusCode.Unauthorized && await TryRefreshAsync())
        {
            r.Dispose();
            r = await SendOnceAsync(make(), ct);
        }
        if (r.StatusCode == HttpStatusCode.Unauthorized)
        {
            r.Dispose();
            throw new CliException("Sessione scaduta o revocata: esegui di nuovo `agentweb login`.", ExitCodes.Unauthenticated);
        }
        return r;
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make)
    {
        var r = await SendCoreAsync(make, default);
        if (!r.IsSuccessStatusCode)
        {
            var text = await r.Content.ReadAsStringAsync();
            throw new CliException(text.Length > 0 && text.Length < 500 ? text : $"Server: HTTP {(int)r.StatusCode}");
        }
        return r;
    }

    Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        return http.SendAsync(req, ct);
    }

    readonly SemaphoreSlim refreshGate = new(1, 1); // il daemon usa il client da più worker: un refresh alla volta

    async Task<bool> TryRefreshAsync()
    {
        await refreshGate.WaitAsync();
        try { return await RefreshOnceAsync(); }
        finally { refreshGate.Release(); }
    }

    async Task<bool> RefreshOnceAsync()
    {
        using var r = await http.PostAsJsonAsync("identity/refresh", new { refreshToken = current.RefreshToken });
        if (!r.IsSuccessStatusCode) return false;
        var t = (await r.Content.ReadFromJsonAsync<TokenResponse>())!;
        current = current with { AccessToken = t.AccessToken, RefreshToken = t.RefreshToken };
        store.Save(current);
        return true;
    }

    static async Task<T> ReadAsync<T>(HttpResponseMessage r)
    {
        using (r) return (await r.Content.ReadFromJsonAsync<T>(CliJson.Options))!;
    }
}
