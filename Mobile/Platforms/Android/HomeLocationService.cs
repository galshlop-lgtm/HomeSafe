using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using HomeSafe.Shared;
using AndroidLocation = Android.Locations.Location;

namespace HomeSafe.Mobile;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class HomeLocationService : Service, ILocationListener
{
    internal static bool Running { get; private set; }
    private LocationManager? manager;
    private Guid request;
    private long until;
    private long lastUpload;
    private System.Threading.Timer? expiry;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (!Guid.TryParse(intent?.GetStringExtra("requestId"), out request))
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        until = intent!.GetLongExtra("until", 0);
        long remaining = until - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining <= 0) { StopSelf(); return StartCommandResult.NotSticky; }
        expiry?.Dispose();
        expiry = new System.Threading.Timer(_ => StopSelf(), null, remaining, Timeout.Infinite);
        DeviceBridge.ConfigureAndroid();
        using var open = PendingIntent.GetActivity(this, 1, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
        using var notification = new Notification.Builder(this, DeviceBridge.TrackingChannel)
            .SetSmallIcon(Resource.Drawable.homesafe_status)!
            .SetContentTitle("HomeSafe · שיתוף מיקום פעיל")!
            .SetContentText("המיקום נבדק בזמן בקשת חזרה פעילה.")!
            .SetOngoing(true)!
            .SetContentIntent(open)!.Build();
        try
        {
            StartForeground(19871, notification);
            manager = (LocationManager)GetSystemService(LocationService)!;
            manager.RemoveUpdates(this);
            manager.RequestLocationUpdates(LocationManager.GpsProvider, 25_000, 0f, this, Looper.MainLooper);
            if (manager.IsProviderEnabled(LocationManager.NetworkProvider))
                manager.RequestLocationUpdates(LocationManager.NetworkProvider, 70_000, 0f, this, Looper.MainLooper);
            Running = true;
        }
        catch (Java.Lang.SecurityException) { StopSelf(); }
        catch (Java.Lang.IllegalArgumentException) { StopSelf(); }
        return StartCommandResult.NotSticky;
    }

    public void OnLocationChanged(AndroidLocation location)
    {
        var now = DateTimeOffset.UtcNow;
        if (now.ToUnixTimeMilliseconds() >= until) { StopSelf(); return; }
#pragma warning disable CS0618, CA1422
        bool mocked = location.IsFromMockProvider;
#pragma warning restore CS0618, CA1422
        if (!location.HasAccuracy || location.Accuracy <= 0 || location.Accuracy > 50 || mocked) return;
        if (now.ToUnixTimeMilliseconds() - lastUpload < 15_000) return;
        lastUpload = now.ToUnixTimeMilliseconds();
        _ = DeviceBridge.Report(request, new LocationFix(location.Latitude, location.Longitude,
            location.Accuracy, DateTimeOffset.FromUnixTimeMilliseconds(location.Time)));
    }

    public void OnProviderEnabled(string provider) { }
    public void OnProviderDisabled(string provider) { }
    public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { }

    public override void OnDestroy()
    {
        Running = false;
        expiry?.Dispose();
        manager?.RemoveUpdates(this);
        base.OnDestroy();
    }
}
