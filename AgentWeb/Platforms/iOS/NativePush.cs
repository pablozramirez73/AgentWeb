using AgentWeb.Shared.Services;
using AgentWeb.Shared.Workspace;
using UIKit;
using UserNotifications;

namespace AgentWeb;

// iPhone: notifiche push direttamente da Apple (APNs). Serve la capability "Push Notifications" nel provisioning profile.
sealed class NativePush : IDevicePush
{
    const string Token = "push-apns-token"; // salvato solo se l'utente le ha attivate
    static TaskCompletionSource<string>? pending;

    public string Kind => "apns";
    public Task<bool> SupportedAsync() => Task.FromResult(true);
    public Task<PushDeviceRequest?> CurrentAsync() => Task.FromResult(Preferences.Get(Token, null) is { } t ? new PushDeviceRequest(Kind, t) : null);

    public async Task<PushDeviceRequest?> SubscribeAsync(PushConfigDto config)
    {
        var (granted, _) = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge);
        if (!granted) return null;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await MainThread.InvokeOnMainThreadAsync(UIApplication.SharedApplication.RegisterForRemoteNotifications);
        var token = await pending.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Preferences.Set(Token, token);
        return new(Kind, token);
    }

    public Task UnsubscribeAsync() { Preferences.Remove(Token); return Task.CompletedTask; }

    // Chiamati da AppDelegate quando iOS risponde a RegisterForRemoteNotifications.
    internal static void Registered(Foundation.NSData deviceToken) =>
        pending?.TrySetResult(Convert.ToHexStringLower(deviceToken.ToArray()));
    internal static void Failed(Foundation.NSError error) =>
        pending?.TrySetException(new InvalidOperationException($"iOS non ha registrato le notifiche: {error.LocalizedDescription}"));
}
