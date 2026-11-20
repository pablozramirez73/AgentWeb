using Android.App;
using Android.Content.PM;
using Android.OS;

namespace AgentWeb;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (ME.Pushy.Sdk.Pushy.IsRegistered(this)) ME.Pushy.Sdk.Pushy.Listen(this); // il servizio di Pushy riparte se Android l'ha fermato
    }
}
