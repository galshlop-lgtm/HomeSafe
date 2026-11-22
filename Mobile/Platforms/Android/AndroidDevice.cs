using System.Globalization;
using System.Security.Cryptography;
using Encoding = System.Text.Encoding;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using IOnCompleteListener = Android.Gms.Tasks.IOnCompleteListener;
using Android.Media;
using Android.OS;
using Android.Provider;
using Firebase;
using Firebase.Messaging;
using HomeSafe.Shared;
using Microsoft.Maui.Storage;
using Uri = Android.Net.Uri;
using AppContext = Android.App.Application;

namespace HomeSafe.Mobile;

internal static partial class DeviceBridge
{
    internal const string TrackingChannel = "homesafe.location";
    private const string UrgentChannel = "homesafe.alarm.v1";
    private const string NormalChannel = "homesafe.updates.quiet";
    private const string FallbackKey = "homesafe.scheduled";
    private const string SecretKey = "homesafe.intent";
    private static readonly object proofGate = new();
    private static Context Context => AppContext.Context;
    private static NotificationManager Notifications => (NotificationManager)Context.GetSystemService(Context.NotificationService)!;
    private static AlarmManager Alarms => (AlarmManager)Context.GetSystemService(Context.AlarmService)!;
    private static AudioAttributes AlarmAudio => new AudioAttributes.Builder()!
        .SetUsage(AudioUsageKind.Alarm)!.SetContentType(AudioContentType.Sonification)!.Build()!;
    private static Uri AlarmUri => Uri.Parse($"android.resource://{Context.PackageName}/{Resource.Raw.alarm}")!;
    private static MediaPlayer? player;
    private static Vibrator? vibrator;
    private static System.Threading.Timer? soundTimer;
    private static ReturnView? monitoring;
    internal static string PlatformName => "android";

    internal static void ConfigureAndroid()
    {
        Notifications.CreateNotificationChannel(new NotificationChannel(UrgentChannel, "אזעקות חזרה הביתה", NotificationImportance.High)
        {
            Description = "התרעות דחופות למועדי חזרה",
            LockscreenVisibility = NotificationVisibility.Private
        }.WithAlarmSound());
        Notifications.CreateNotificationChannel(new(NormalChannel, "עדכונים שקטים", NotificationImportance.Low));
        Notifications.CreateNotificationChannel(new(TrackingChannel, "אימות מיקום", NotificationImportance.Low));
    }

    private static NotificationChannel WithAlarmSound(this NotificationChannel channel)
    {
        channel.SetSound(AlarmUri, AlarmAudio);
        channel.EnableVibration(true);
        channel.SetVibrationPattern([0, 500, 250, 500, 250, 700]);
        return channel;
    }

    private static async Task<string?> AskForNotifications()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && await Permissions.RequestAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            throw new InvalidOperationException("יש לאפשר התראות בהגדרות המכשיר.");
        FirebaseApp? app = FirebaseApp.InitializeApp(Context);
        if (app is null) throw new InvalidOperationException("גרסת האפליקציה אינה מחוברת ל־Firebase.");
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new TokenListener(completion));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class TokenListener(TaskCompletionSource<string?> completion) : Java.Lang.Object, IOnCompleteListener
    {
        public void OnComplete(Android.Gms.Tasks.Task task) =>
            completion.TrySetResult(task.IsSuccessful ? task.Result?.ToString() : null);
    }

    internal static async Task<bool> RequestLocation() =>
        await Permissions.RequestAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted
        && Context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted;

    private static bool Monitor(ReturnView request)
    {
        if (Context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Permission.Granted)
        {
            StopTracking();
            return false;
        }
        if (monitoring?.Id == request.Id && monitoring.Deadline == request.Deadline && HomeLocationService.Running) return true;
        if (!Active) return HomeLocationService.Running;
        try
        {
            var intent = new Intent(Context, typeof(HomeLocationService))
                .PutExtra("requestId", request.Id.ToString())
                .PutExtra("until", request.Deadline.AddHours(2).ToUnixTimeMilliseconds());
            Context.StartForegroundService(intent);
            monitoring = request;
            return true;
        }
        catch (Java.Lang.IllegalStateException) { return false; }
        catch (Java.Lang.SecurityException) { return false; }
    }

    private static void StopTracking()
    {
        monitoring = null;
        Context.StopService(new Intent(Context, typeof(HomeLocationService)));
    }

    internal static void StartSound()
    {
        StopSound();
        try
        {
            player = new MediaPlayer();
            player.SetAudioAttributes(AlarmAudio);
            player.SetDataSource(Context, AlarmUri);
            player.Looping = true;
            player.Prepare();
            player.Start();
            vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? (Context.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator
#pragma warning disable CA1422
                : Context.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422
            if (vibrator?.HasVibrator == true)
                vibrator.Vibrate(VibrationEffect.CreateWaveform([0, 450, 275, 450, 1800], 0));
            soundTimer = new System.Threading.Timer(_ => MainThread.BeginInvokeOnMainThread(StopSound), null, 90_000, Timeout.Infinite);
        }
        catch { StopSound(); }
    }

    internal static void StopSound()
    {
        soundTimer?.Dispose();
        soundTimer = null;
        player?.Release();
        player?.Dispose();
        player = null;
        try { vibrator?.Cancel(); } catch { }
        vibrator = null;
    }

    internal static void OpenExactSettings()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            Context.StartActivity(new Intent(Settings.ActionRequestScheduleExactAlarm, Uri.Parse($"package:{Context.PackageName}"))
                .AddFlags(ActivityFlags.NewTask));
        else AppInfo.ShowSettingsUI();
    }

    internal static void OpenFullScreenSettings()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            Context.StartActivity(new Intent("android.settings.MANAGE_APP_USE_FULL_SCREEN_INTENT", Uri.Parse($"package:{Context.PackageName}"))
                .AddFlags(ActivityFlags.NewTask));
        else AppInfo.ShowSettingsUI();
    }

    internal static void ClearAlert(Guid id)
    {
        Notifications.Cancel(id.ToString(), 1);
        Notifications.Cancel(id.ToString(), 2);
        if (Platform.CurrentActivity is MainActivity activity && activity.LockedRequest == id)
            activity.DismissLockScreenMode();
    }

    private static void ClearAll() => Notifications.CancelAll();

    internal static void PublishAndroid(AlertMessage alert)
    {
        if (!IsLinkedDevice || alert.ExpiresAt <= DateTimeOffset.UtcNow || !Notifications.AreNotificationsEnabled()) return;
        ConfigureAndroid();
        var raw = JsonSerializer.Serialize(alert);
        using var intent = new Intent(Context, typeof(MainActivity))
            .SetAction(alert.Urgent ? "org.homesafe.ALARM" : "org.homesafe.UPDATE")!
            .SetData(Uri.Parse($"homesafe://alerts/{alert.RequestId}/{(alert.Urgent ? "alarm" : "update")}"))!
            .PutExtra("homesafe.alert", raw)
            .PutExtra("homesafe.proof", Proof(raw))
            .AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var pendingIntent = PendingIntent.GetActivity(Context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        using var builder = new Notification.Builder(Context, alert.Urgent ? UrgentChannel : NormalChannel)
            .SetContentTitle(alert.Title)!
            .SetContentText(alert.Body)!
            .SetStyle(new Notification.BigTextStyle().BigText(alert.Body))!
            .SetSmallIcon(Resource.Drawable.homesafe_status)!
            .SetVisibility(NotificationVisibility.Private)!
            .SetContentIntent(pendingIntent)!
            .SetAutoCancel(true)!
            .SetTimeoutAfter(Math.Max(1_000, (long)(alert.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds))!;
        if (alert.Urgent)
        {
            builder.SetCategory(Notification.CategoryAlarm);
            if (!OperatingSystem.IsAndroidVersionAtLeast(34) || Notifications.CanUseFullScreenIntent())
                builder.SetFullScreenIntent(pendingIntent, true);
        }
        using var notification = builder.Build();
        if (alert.Urgent) notification.Flags |= NotificationFlags.Insistent;
        if (alert.Kind == AlertKind.Arrival) Notifications.Cancel(alert.RequestId.ToString(), 1);
        Notifications.Notify(alert.RequestId.ToString(), alert.Urgent ? 1 : 2, notification);
    }

    private static string Proof(string raw)
    {
        string key;
        lock (proofGate)
        {
            key = Preferences.Default.Get(SecretKey, "");
            if (key.Length == 0)
            {
                key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                Preferences.Default.Set(SecretKey, key);
            }
        }
        return Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(key), Encoding.UTF8.GetBytes(raw)));
    }

    internal static bool ReceiveOpened(string raw, string? proof, out AlertMessage alert)
    {
        alert = null!;
        if (raw.Length > 5000 || proof?.Length != 64
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Proof(raw)), Encoding.UTF8.GetBytes(proof)))
            return false;
        alert = JsonSerializer.Deserialize<AlertMessage>(raw)!;
        return alert is not null;
    }

    private static void SyncDeadlineFallback(ReturnView[] items)
    {
        var desired = items.ToDictionary(x => x.Id, x => x.Deadline.AddSeconds(5).ToUnixTimeMilliseconds());
        var previous = ReadAlarms();
        foreach (var duplicate in previous.Where(x => !desired.ContainsKey(x.Key)))
        {
            using var pendingIntent = AlarmIntent(duplicate.Key);
            Alarms.Cancel(pendingIntent);
        }
        foreach (var item in desired.Where(x => !previous.TryGetValue(x.Key, out long deadline) || deadline != x.Value))
            Schedule(item.Key, item.Value);
        Preferences.Default.Set(FallbackKey, JsonSerializer.Serialize(desired));
    }

    private static Dictionary<Guid, long> ReadAlarms() =>
        JsonSerializer.Deserialize<Dictionary<Guid, long>>(Preferences.Default.Get(FallbackKey, "{}")) ?? [];

    internal static void RescheduleAfterBoot()
    {
        if (!IsLinkedDevice) return;
        foreach (var item in ReadAlarms()) Schedule(item.Key, item.Value);
    }

    private static void Schedule(Guid id, long deadline)
    {
        if (deadline <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return;
        using var pendingIntent = AlarmIntent(id);
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) || Alarms.CanScheduleExactAlarms())
        {
            try { Alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, deadline, pendingIntent); return; }
            catch (Java.Lang.SecurityException) { }
        }
        Alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, deadline, pendingIntent);
    }

    private static PendingIntent AlarmIntent(Guid id)
    {
        using var intent = new Intent(Context, typeof(DeadlineReceiver))
            .SetData(Uri.Parse($"homesafe://deadline/{id}"))!.PutExtra("homesafe.request", id.ToString());
        return PendingIntent.GetBroadcast(Context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }
}

[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class HomePushService : FirebaseMessagingService
{
    public override void OnNewToken(string token) => DeviceBridge.DeviceToken(token);

    public override void OnMessageReceived(RemoteMessage message)
    {
        var data = message.Data;
        string? Read(string key) => data.TryGetValue(key, out var value) ? value : null;
        if (!Guid.TryParse(Read("id"), out Guid id)
            || !Guid.TryParse(Read("requestId"), out Guid request)
            || !Enum.TryParse(Read("kind"), out AlertKind kind)
            || !long.TryParse(Read("expires"), CultureInfo.InvariantCulture, out long expires)
            || expires is < 0 or > 253402300799) return;
        var alert = new AlertMessage(id, request, kind, Read("title") ?? "HomeSafe",
            Read("body") ?? "", Read("urgent") == "1",
            DateTimeOffset.FromUnixTimeSeconds(expires));
        DeviceBridge.PublishAndroid(alert);
        if (GetSystemService(Context.NotificationService) is NotificationManager notifications && notifications.AreNotificationsEnabled())
            _ = DeviceBridge.ConfirmReceipt(alert);
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter([Intent.ActionBootCompleted, "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED"])]
public sealed class DeadlineReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is Intent.ActionBootCompleted or "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED")
        {
            DeviceBridge.RescheduleAfterBoot();
            return;
        }
        if (!DeviceBridge.IsLinkedDevice || !Guid.TryParse(intent?.GetStringExtra("homesafe.request"), out Guid id)) return;
        var work = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                if (await DeviceBridge.LocalCheck(id) is {} alert) DeviceBridge.PublishAndroid(alert);
            }
            finally { work?.Finish(); }
        });
    }
}
