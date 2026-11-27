using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// fetch_url per gli agenti: solo pagine pubbliche. Il controllo è sull'IP a cui ci si collega davvero (ConnectCallback),
// quindi né un DNS che cambia risposta né un redirect portano a localhost, rete privata, link-local o metadati cloud.
public sealed partial class SafeFetch(IConfiguration config)
{
    const int MaxBytes = 2 * 1024 * 1024, MaxChars = 20_000, MaxRedirects = 3;
    // Solo per i test (server finto su 127.0.0.1): mai in produzione.
    readonly bool allowPrivate = config.GetValue("Web:AllowPrivateHosts", false);

    HttpClient? client;
    HttpClient Client => client ??= new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // i redirect li seguiamo noi, ricontrollando ogni destinazione
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(a => allowPrivate || IsPublic(a)).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException(T("net.notPublic", ctx.DnsEndPoint.Host));
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return T("fetch.badUrl");
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.ParseAdd("AgentWeb-Agent/1.0 (+fetch_url)");
                req.Headers.Accept.ParseAdd("text/html,text/plain,application/json;q=0.9,*/*;q=0.5");
                using var r = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)r.StatusCode is >= 300 and < 400 && r.Headers.Location is { } next)
                {
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    if (uri.Scheme is not ("http" or "https")) return T("fetch.badRedirect");
                    continue;
                }
                if (!r.IsSuccessStatusCode) return T("fetch.http", (int)r.StatusCode);
                var type = r.Content.Headers.ContentType?.MediaType ?? "";
                if (!(type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") || type.Length == 0))
                    return T("fetch.notText", type);

                await using var stream = await r.Content.ReadAsStreamAsync(ct);
                var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct)) > 0 && buffer.Length < MaxBytes) buffer.Write(chunk, 0, read);
                var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)Math.Min(buffer.Length, MaxBytes));
                if (type.Contains("html")) text = HtmlToText(text);
                if (text.Length > MaxChars) text = text[..MaxChars] + "\n[…troncato]";
                // Il contenuto è DATO, non istruzioni: lo diciamo esplicitamente al modello.
                return T("fetch.content", uri, text);
            }
            return T("fetch.tooManyRedirects");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return T("fetch.failed", uri, ex.GetBaseException().Message);
        }
    }

    // Pubblico = non loopback, privato (RFC 1918 / ULA), link-local (incl. 169.254.169.254 dei metadati cloud), CGNAT, multicast, riservato.
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.None)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal || a.IsIPv6Multicast);
        var b = a.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
              || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
              || (b[0] == 192 && b[1] == 168)
              || (b[0] == 169 && b[1] == 254)
              || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
              || (b[0] == 192 && b[1] == 0 && b[2] == 0)
              || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
    }

    static string HtmlToText(string html)
    {
        html = ScriptOrStyle().Replace(html, " ");
        html = Breaks().Replace(html, "\n");
        html = Tags().Replace(html, " ");
        html = WebUtility.HtmlDecode(html);
        html = Spaces().Replace(html, " ");
        return BlankLines().Replace(html, "\n\n").Trim();
    }

    [GeneratedRegex(@"<(script|style|noscript|svg)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ScriptOrStyle();
    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Breaks();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\r\f\v]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n+")] private static partial Regex BlankLines();
}
