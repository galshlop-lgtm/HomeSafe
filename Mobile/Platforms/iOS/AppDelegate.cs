using Foundation;
using UIKit;

namespace HomeSafe.Mobile;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var launched = base.FinishedLaunching(application, launchOptions);
        DeviceBridge.ConfigureIos();
        if (launchOptions?.ContainsKey(UIApplication.LaunchOptionsLocationKey) == true)
            _ = DeviceBridge.BackgroundSync();
        return launched;
    }

    public override void OnActivated(UIApplication application) => DeviceBridge.Resume();
    public override void OnResignActivation(UIApplication application) => DeviceBridge.Pause();

    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void Registered(UIApplication application, NSData deviceToken) =>
        DeviceBridge.DeviceToken(Convert.ToHexString(deviceToken.ToArray()).ToLowerInvariant());

    [Export("application:didReceiveRemoteNotification:fetchCompletionHandler:")]
    public async void BackgroundPush(UIApplication application, NSDictionary userInfo,
        Action<UIBackgroundFetchResult> completion)
    {
        try { await DeviceBridge.BackgroundSync(); completion(UIBackgroundFetchResult.NewData); }
        catch { completion(UIBackgroundFetchResult.Failed); }
    }
}
