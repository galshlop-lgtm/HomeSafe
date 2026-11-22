using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace HomeSafe.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.LayoutDirection)]
public class MainActivity : MauiAppCompatActivity
{
    internal Guid? LockedRequest { get; private set; }

    protected override void AttachBaseContext(Context? context)
    {
        if (context?.Resources?.Configuration is not {} inherited)
        {
            base.AttachBaseContext(context);
            return;
        }
        var localized = new Android.Content.Res.Configuration(inherited);
        localized.SetLocale(Java.Util.Locale.ForLanguageTag("he-IL"));
        base.AttachBaseContext(context.CreateConfigurationContext(localized));
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        DeviceBridge.ConfigureAndroid();
        ReadAlert(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ReadAlert(intent);
    }

    private void ReadAlert(Intent? intent)
    {
        if (intent?.GetStringExtra("homesafe.alert") is not {} raw) return;
        intent.RemoveExtra("homesafe.alert");
        // Only internal notifications may deliver alert intents.
        if (!DeviceBridge.ReceiveOpened(raw, intent.GetStringExtra("homesafe.proof"), out var alert)) return;
        if (alert.Urgent)
        {
            LockedRequest = alert.RequestId;
            if (OperatingSystem.IsAndroidVersionAtLeast(27))
            {
                SetShowWhenLocked(true);
                SetTurnScreenOn(true);
            }
            else
            {
#pragma warning disable CS0618
                Window?.AddFlags(WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn);
#pragma warning restore CS0618
            }
        }
        else DismissLockScreenMode();
        DeviceBridge.Opened(alert);
    }

    internal void DismissLockScreenMode()
    {
        LockedRequest = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(27))
        {
            SetShowWhenLocked(false);
            SetTurnScreenOn(false);
        }
        else
        {
#pragma warning disable CS0618
            Window?.ClearFlags(WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn);
#pragma warning restore CS0618
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        DeviceBridge.Resume();
    }

    protected override void OnPause()
    {
        DeviceBridge.Pause();
        base.OnPause();
    }
}

[Application]
public class MainApplication(IntPtr handle, Android.Runtime.JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
