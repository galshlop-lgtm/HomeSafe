using AVFoundation;
using CoreLocation;
using Foundation;
using HomeSafe.Shared;
using UIKit;
using UserNotifications;

namespace HomeSafe.Mobile;

internal static partial class DeviceBridge
{
    private static readonly UNUserNotificationCenter Center = UNUserNotificationCenter.Current;
    private static readonly NotificationEvents notificationEvents = new();
    private static CLLocationManager? location;
    private static ReturnView? monitoring;
    private static AVAudioPlayer? player;
    private static System.Threading.Timer? soundTimer;
    private static DateTimeOffset lastUpload;
    private static DateTimeOffset lastNotificationSync;
    private static Dictionary<Guid, DateTimeOffset> scheduled = [];
    internal static string PlatformName => "ios";

    internal static void ConfigureIos()
    {
        Center.Delegate = notificationEvents;
        if (!IsLinkedDevice) return;
        Center.GetNotificationSettings(s =>
        {
            if (s.AuthorizationStatus is UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional)
                MainThread.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.RegisterForRemoteNotifications());
        });
    }

    private static async Task<string?> AskForNotifications()
    {
        var options = UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge;
#if HOMESAFE_CRITICAL_ALERTS
        options |= UNAuthorizationOptions.CriticalAlert;
#endif
        var (granted, _) = await Center.RequestAuthorizationAsync(options);
        if (!granted) throw new InvalidOperationException("יש לאפשר התראות בהגדרות המכשיר.");
        UIApplication.SharedApplication.RegisterForRemoteNotifications();
        return Registration?.Token;
    }

    internal static async Task BackgroundSync()
    {
        if (!IsLinkedDevice || DateTimeOffset.UtcNow - lastNotificationSync < TimeSpan.FromSeconds(2)) return;
        lastNotificationSync = DateTimeOffset.UtcNow;
        await ApiClient.Current.Restore();
        var model = await ApiClient.Current.Family();
        await MainThread.InvokeOnMainThreadAsync(() => Apply(model));
    }

    internal static async Task<bool> RequestLocation()
    {
        if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted) return false;
        await Permissions.RequestAsync<Permissions.LocationAlways>();
        return Manager.AccuracyAuthorization == CLAccuracyAuthorization.FullAccuracy;
    }

    private static CLLocationManager Manager
    {
        get
        {
            if (location is not null) return location;
            location = new CLLocationManager
            {
                DesiredAccuracy = CLLocation.AccuracyNearestTenMeters,
                DistanceFilter = -1,
                PausesLocationUpdatesAutomatically = false,
                ActivityType = CLActivityType.Other
            };
            location.LocationsUpdated += (_, e) =>
            {
                if (e.Locations.LastOrDefault() is {} fix) ReceiveFix(fix);
            };
            location.RegionEntered += (_, _) => location.RequestLocation();
            location.RegionLeft += (_, _) => location.RequestLocation();
            return location;
        }
    }

    private static bool Monitor(ReturnView request)
    {
        var manager = Manager;
        bool always = manager.AuthorizationStatus == CLAuthorizationStatus.AuthorizedAlways;
        if ((!always && (!Active || manager.AuthorizationStatus != CLAuthorizationStatus.AuthorizedWhenInUse))
            || manager.AccuracyAuthorization != CLAccuracyAuthorization.FullAccuracy)
        {
            StopTracking();
            return false;
        }
        if (monitoring?.Id == request.Id && monitoring.Deadline == request.Deadline
            && manager.AllowsBackgroundLocationUpdates == always) return true;
        StopTracking();
        monitoring = request;
        manager.AllowsBackgroundLocationUpdates = always;
        manager.ShowsBackgroundLocationIndicator = always;
        manager.StartUpdatingLocation();
        if (always && CLLocationManager.IsMonitoringAvailable(typeof(CLCircularRegion)))
        {
            var region = new CLCircularRegion(new(request.Home.Latitude, request.Home.Longitude),
                request.Home.Radius, "homesafe-" + request.Id) { NotifyOnEntry = true, NotifyOnExit = true };
            manager.StartMonitoring(region);
        }
        return true;
    }

    private static void ReceiveFix(CLLocation raw)
    {
        if (monitoring is not {} row) return;
        var now = DateTimeOffset.UtcNow;
        if (now > row.Deadline.AddHours(2)) { StopTracking(); return; }
        if (raw.HorizontalAccuracy is <= 0 or > 50 || now - lastUpload < TimeSpan.FromSeconds(15)) return;
        if (raw.SourceInformation?.IsSimulatedBySoftware == true) return;
        lastUpload = now;
        _ = Report(row.Id, new(raw.Coordinate.Latitude, raw.Coordinate.Longitude,
            raw.HorizontalAccuracy, DateTimeOffset.UnixEpoch.AddSeconds(raw.Timestamp.SecondsSince1970)));
    }

    private static void StopTracking()
    {
        monitoring = null;
        if (location is null && !IsLinkedDevice) return;
        var manager = Manager;
        manager.StopUpdatingLocation();
        foreach (var region in manager.MonitoredRegions.OfType<CLRegion>())
            if (region.Identifier.StartsWith("homesafe-", StringComparison.Ordinal)) manager.StopMonitoring(region);
    }

    internal static void StartSound()
    {
        StopSound();
        try
        {
            AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback,
                AVAudioSessionCategoryOptions.DuckOthers);
            AVAudioSession.SharedInstance().SetActive(true);
            if (NSBundle.MainBundle.GetUrlForResource("alarm", "wav") is not {} path) return;
            player = AVAudioPlayer.FromUrl(path);
            if (player is null) return;
            player.NumberOfLoops = -1;
            player.Play();
            Vibration.Default.Vibrate();
            soundTimer = new System.Threading.Timer(_ => MainThread.BeginInvokeOnMainThread(StopSound), null, 90_000, Timeout.Infinite);
        }
        catch { StopSound(); }
    }

    internal static void StopSound()
    {
        soundTimer?.Dispose();
        soundTimer = null;
        if (player is null) return;
        player.Stop();
        player.Dispose();
        player = null;
        try { AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation); }
        catch { }
    }

    private static void SyncDeadlineFallback(ReturnView[] items)
    {
        var desired = items.Where(r => r.Deadline > DateTimeOffset.UtcNow)
            .ToDictionary(r => r.Id, r => r.Deadline);
        if (scheduled.Count == desired.Count && scheduled.All(x => desired.GetValueOrDefault(x.Key) == x.Value)) return;
        Center.RemovePendingNotificationRequests(scheduled.Keys.Where(id => !desired.ContainsKey(id))
            .Select(FallbackId).ToArray());
        foreach (var item in desired.Where(x => scheduled.GetValueOrDefault(x.Key) != x.Value))
        {
            var message = Unavailable(item.Key);
            var content = new UNMutableNotificationContent
            {
                Title = message.Title, Body = message.Body, Sound = UNNotificationSound.Default,
                InterruptionLevel = UNNotificationInterruptionLevel.TimeSensitive2,
                UserInfo = NSDictionary.FromObjectsAndKeys(
                    [new NSString(item.Key.ToString()), new NSString("local")],
                    [new NSString("requestId"), new NSString("kind")])
            };
            double seconds = Math.Max(1, (item.Value.AddSeconds(30) - DateTimeOffset.UtcNow).TotalSeconds);
            var request = UNNotificationRequest.FromIdentifier(FallbackId(item.Key),
                content, UNTimeIntervalNotificationTrigger.CreateTrigger(seconds, false));
            Center.AddNotificationRequest(request, _ => { });
        }
        scheduled = desired;
    }

    private static string FallbackId(Guid id) => "fallback-" + id;

    internal static void ClearAlert(Guid id)
    {
        Center.RemovePendingNotificationRequests([FallbackId(id)]);
        Center.GetDeliveredNotifications(notifications =>
        {
            var matched = notifications.Where(n => n.Request.Identifier == FallbackId(id)
                || Read(n.Request.Content.UserInfo, "requestId") == id.ToString()).Select(n => n.Request.Identifier).ToArray();
            if (matched.Length > 0) Center.RemoveDeliveredNotifications(matched);
        });
    }

    private static void ClearAll()
    {
        Center.RemoveAllPendingNotificationRequests();
        Center.RemoveAllDeliveredNotifications();
        scheduled.Clear();
    }

    private static string? Read(NSDictionary values, string key) => values[new NSString(key)]?.ToString();

    private sealed class NotificationEvents : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification,
            Action<UNNotificationPresentationOptions> completionHandler)
        {
            var content = notification.Request.Content;
            bool urgent = Read(content.UserInfo, "urgent") == "1";
            if (urgent && Parse(content) is {} alert) Opened(alert);
            completionHandler(urgent ? UNNotificationPresentationOptions.List
                : UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);
        }

        public override async void DidReceiveNotificationResponse(UNUserNotificationCenter center,
            UNNotificationResponse response, Action completionHandler)
        {
            try
            {
                var content = response.Notification.Request.Content;
                if (Parse(content) is {} alert)
                {
                    if (Read(content.UserInfo, "kind") == "local")
                    {
                        if (await LocalCheck(alert.RequestId) is {} local) Opened(local);
                    }
                    else Opened(alert);
                }
            }
            finally { completionHandler(); }
        }

        private static AlertMessage? Parse(UNNotificationContent content)
        {
            if (!Guid.TryParse(Read(content.UserInfo, "requestId"), out var request)) return null;
            if (Read(content.UserInfo, "kind") == "local") return Unavailable(request);
            if (!Enum.TryParse(Read(content.UserInfo, "kind"), out AlertKind kind)) return null;
            return new(Guid.NewGuid(), request, kind, content.Title, content.Body,
                Read(content.UserInfo, "urgent") == "1", DateTimeOffset.UtcNow.AddMinutes(12));
        }
    }
}
