using AgentWeb.Shared.Workspace;
using Microsoft.JSInterop;

namespace AgentWeb.Shared.Services;

// Le notifiche push di QUESTO dispositivo: Web Push nel browser, Pushy su Android, APNs su iPhone.
public interface IDevicePush
{
    string Kind { get; } // "web" | "pushy" | "apns": deve essere tra i PushConfigDto.Kinds del server
    Task<bool> SupportedAsync();
    Task<PushDeviceRequest?> CurrentAsync();                        // iscrizione già attiva qui, se c'è
    Task<PushDeviceRequest?> SubscribeAsync(PushConfigDto config);   // null = permesso negato
    Task UnsubscribeAsync();
}

// Browser: Web Push tramite push.js e il service worker push-sw.js.
public sealed class BrowserPush(IJSRuntime js) : IDevicePush, IAsyncDisposable
{
    IJSObjectReference? module;
    async Task<IJSObjectReference> Module() => module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/AgentWeb.Shared/push.js");

    public string Kind => "web";
    public async Task<bool> SupportedAsync() => await (await Module()).InvokeAsync<bool>("supported");
    public async Task<PushDeviceRequest?> CurrentAsync() => await (await Module()).InvokeAsync<PushDeviceRequest?>("current");
    public async Task<PushDeviceRequest?> SubscribeAsync(PushConfigDto config) => await (await Module()).InvokeAsync<PushDeviceRequest?>("subscribe", config.VapidPublicKey);
    public async Task UnsubscribeAsync() => await (await Module()).InvokeVoidAsync("unsubscribe");

    public async ValueTask DisposeAsync()
    {
        try { if (module != null) await module.DisposeAsync(); } catch (JSDisconnectedException) { }
    }
}
