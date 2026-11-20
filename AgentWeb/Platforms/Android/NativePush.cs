using AgentWeb.Shared.Services;
using AgentWeb.Shared.Workspace;
using Android.App;
using Android.Content;
using Android.Media;
using ME.Pushy.Sdk;

namespace AgentWeb;

// Android: notifiche push tramite Pushy (niente Firebase). L'app va registrata su pushy.me con lo stesso package name.
sealed class NativePush : IDevicePush
{
    const string Enabled = "push-enabled"; // l'utente le ha attivate da Impostazioni → Notifiche
    static Context Ctx => Platform.CurrentActivity ?? Android.App.Application.Context;

    public string Kind => "pushy";
    public Task<bool> SupportedAsync() => Task.FromResult(true);

    public Task<PushDeviceRequest?> CurrentAsync() => Task.FromResult(
        Preferences.Get(Enabled, false) && Pushy.IsRegistered(Ctx) ? new PushDeviceRequest(Kind, Pushy.GetDeviceCredentials(Ctx).Token) : null);

    public async Task<PushDeviceRequest?> SubscribeAsync(PushConfigDto config)
    {
        // Android 13+: il permesso per le notifiche si chiede a runtime.
        if (await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PostNotifications>) != PermissionStatus.Granted) return null;
        var token = await Task.Run(() => Pushy.Register(Ctx)); // bloccante: mai sul thread dell'interfaccia
        Pushy.Listen(Ctx);
        Preferences.Set(Enabled, true);
        return new(Kind, token);
    }

    public Task UnsubscribeAsync() { Preferences.Set(Enabled, false); return Task.CompletedTask; }
}

// Arriva una notifica Pushy (anche ad app chiusa): la si mostra; toccandola si apre l'app.
[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter(["pushy.me"])]
public sealed class PushReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null || !Preferences.Get("push-enabled", false)) return;
        var open = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = new Notification.Builder(context)
            .SetAutoCancel(true)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle(intent.GetStringExtra("title") ?? "AgentWeb")
            .SetContentText(intent.GetStringExtra("body") ?? "")
            .SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Notification))
            .SetContentIntent(open);
        Pushy.SetNotificationChannel(builder, context); // Android 8+: canale di notifica
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Notify(Random.Shared.Next(), builder.Build());
    }
}
