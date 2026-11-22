using Microsoft.Maui.Devices;
using HomeSafe.Shared;
using Microsoft.Maui.Storage;

namespace HomeSafe.Mobile;

internal static partial class DeviceBridge
{
    private const string DeviceTokenKey = "homesafe.push";
    private const string AccountKey = "homesafe.active";
    private const string ConsentKey = "homesafe.consent";
    private const string RevokeKey = "homesafe.revocation";
    private static AlertMessage? pending;
    private static bool revoking;
    private static bool registering;
    private static string? registered;
    internal static bool Active { get; private set; }
    internal static bool Tracking { get; private set; }
    internal static bool IsLinkedDevice => Preferences.Default.Get(AccountKey, "").Length > 0;
    internal static event Action<AlertMessage>? AlertOpened;
    internal static event Action? StateChanged;

    internal static DeviceInput? Registration
    {
        get
        {
            string token = Preferences.Default.Get(DeviceTokenKey, "");
            return token.Length == 0 ? null : new(PlatformName, token);
        }
    }

    internal static void DeviceToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        Preferences.Default.Set(DeviceTokenKey, token);
        if (IsLinkedDevice) MainThread.BeginInvokeOnMainThread(async () => await RegisterQuietly(ApiClient.Current));
    }

    internal static async Task RegisterQuietly(ApiClient api)
    {
        if (!api.SignedIn || registering || Registration is not {} registration || registered == registration.Token) return;
        registering = true;
        try
        {
            await api.Send<object>(HttpMethod.Put, "devices", registration);
            registered = registration.Token;
        }
        catch { }
        finally { registering = false; }
    }

    internal static async Task RequestNotifications(ApiClient api)
    {
        var token = await AskForNotifications();
        if (!string.IsNullOrEmpty(token)) DeviceToken(token);
        registered = null;
        await RegisterQuietly(api);
    }

    internal static void Opened(AlertMessage alert)
    {
        if (!IsLinkedDevice || alert.ExpiresAt <= DateTimeOffset.UtcNow) return;
        pending = alert;
        MainThread.BeginInvokeOnMainThread(() => AlertOpened?.Invoke(alert));
    }

    internal static AlertMessage? ConsumePending()
    {
        var item = pending;
        pending = null;
        return item;
    }

    internal static void Resume()
    {
        Active = true;
        StateChanged?.Invoke();
    }

    internal static void Pause() => Active = false;

    internal static void SetLocalConsent(bool enabled)
    {
        Preferences.Default.Remove(RevokeKey);
        if (enabled) Preferences.Default.Set(ConsentKey, Preferences.Default.Get(AccountKey, ""));
        else Preferences.Default.Remove(ConsentKey);
    }

    internal static void RevokeLocalTracking()
    {
        Preferences.Default.Set(RevokeKey, Preferences.Default.Get(AccountKey, ""));
        Preferences.Default.Remove(ConsentKey);
        Tracking = false;
        StopTracking();
    }

    internal static async Task Apply(FamilyView family)
    {
        string account = family.Me.Id.ToString();
        Preferences.Default.Set(AccountKey, account);
        if (!family.DeviceRegistered && registered is not null) registered = null;
        _ = RegisterQuietly(ApiClient.Current);
        var now = DateTimeOffset.UtcNow;
        var requests = family.Requests.Where(r => r.Status is ReturnStatus.Pending or ReturnStatus.Accepted).Take(30).ToArray();
        SyncDeadlineFallback(requests);
        foreach (var row in family.Requests.Where(r => r.Status == ReturnStatus.Arrived || r.Acknowledged))
            ClearAlert(row.Id);

        bool blocked = Preferences.Default.Get(RevokeKey, "") == account;
        if (blocked && family.SharingEnabled && !revoking)
        {
            revoking = true;
            try
            {
                await ApiClient.Current.Send<object>(HttpMethod.Put, "settings/sharing", new SharingInput(false));
                Preferences.Default.Remove(RevokeKey);
            }
            catch { }
            finally { revoking = false; }
        }
        var request = family.Me.Role == FamilyRole.Child && family.SharingEnabled && !blocked
            && Preferences.Default.Get(ConsentKey, "") == account
            && family.Requests.FirstOrDefault() is {} latest && ReturnRules.NeedsMonitoring(latest.Status)
            && now < latest.Deadline.AddHours(2) ? latest : null;

        if (request is not null) Tracking = Monitor(request);
        else
        {
            approachSamples.Clear();
            if (monitoring is not null)
            {
                Tracking = false;
                StopTracking();
            }
        }
        if (family.Me.Role == FamilyRole.Child && family.Requests.FirstOrDefault() is {} heartbeat
            && ReturnRules.NeedsMonitoring(heartbeat.Status) && now < heartbeat.Deadline.AddHours(2)
            && now - lastHeartbeat >= TimeSpan.FromSeconds(60))
            _ = SendMonitoring(heartbeat, null);

    }

    private const string EmergencyKey = "homesafe.emergencyConsent";
    internal static bool EmergencyConsent => IsLinkedDevice
        && Preferences.Default.Get(EmergencyKey, "") == Preferences.Default.Get(AccountKey, "");
    internal static void SetEmergencyConsent(bool enabled)
    {
        if (enabled) Preferences.Default.Set(EmergencyKey, Preferences.Default.Get(AccountKey, ""));
        else Preferences.Default.Remove(EmergencyKey);
    }

    private static Guid sampleRequest;
    private static readonly Queue<LocationFix> approachSamples = new();
    private static DateTimeOffset lastHeartbeat;
    private static bool reporting;

    internal static Task Heartbeat(Guid id) =>
        monitoring is {} row && row.Id == id && DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(60)
            ? SendMonitoring(row, null) : Task.CompletedTask;

    internal static async Task ConfirmReceipt(AlertMessage alert)
    {
        if (!alert.Urgent || !IsLinkedDevice) return;
        try
        {
            await ApiClient.Current.Restore();
            if (ApiClient.Current.SignedIn)
                await ApiClient.Current.Send<object>(HttpMethod.Post, $"alerts/{alert.Id}/receipt");
        }
        catch { }
    }

    internal static Task Report(Guid id, LocationFix fix) =>
        monitoring is {} row && row.Id == id ? SendMonitoring(row, fix) : Task.CompletedTask;

    private static async Task SendMonitoring(ReturnView row, LocationFix? fix)
    {
        if (reporting || !IsLinkedDevice) return;
        reporting = true;
        lastHeartbeat = DateTimeOffset.UtcNow;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (sampleRequest != row.Id)
            {
                sampleRequest = row.Id;
                approachSamples.Clear();
            }
            var presence = ReturnRules.Locate(row.Home, fix, now);
            bool approaching = presence == Presence.Outside && Approaching(row.Home, fix!);
            if (fix is not null && presence == Presence.Unknown) approachSamples.Clear();

            await ApiClient.Current.Restore();
            if (!ApiClient.Current.SignedIn) return;
            var receipt = await ApiClient.Current.Send<MonitoringReceipt>(HttpMethod.Post, "monitoring",
                new MonitoringInput(row.Id, presence, presence == Presence.Unknown ? now : fix!.ObservedAt,
                    BatteryPercent(), approaching, HasLocationSample: fix is not null));
            if (!receipt.ContinueMonitoring) StopIfCurrent(row.Id);
            else if (receipt.EmergencyLocationRequested && fix is not null && presence != Presence.Unknown
                && EmergencyConsent && monitoring?.Id == row.Id && Preferences.Default.Get(ConsentKey, "") == Preferences.Default.Get(AccountKey, ""))
                await ApiClient.Current.Send<LocationReceipt>(HttpMethod.Post, "locations", new LocationReport(row.Id, fix));
        }
        catch (RequestFailure ex) when (ex.Code is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            StopIfCurrent(row.Id);
        }
        catch { }
        finally { reporting = false; }
    }

    private static int? BatteryPercent()
    {
        try
        {
            double charge = Battery.Default.ChargeLevel;
            return charge is >= 0 and <= 1 ? (int)Math.Round(charge * 100) : null;
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or FeatureNotEnabledException) { return null; }
    }

    private static bool Approaching(HomeArea home, LocationFix newest)
    {
        if (approachSamples.TryPeek(out var first) && newest.ObservedAt - first.ObservedAt > TimeSpan.FromMinutes(2))
            approachSamples.Clear();
        if (approachSamples.Count > 0 && newest.ObservedAt <= approachSamples.Last().ObservedAt) return false;
        approachSamples.Enqueue(newest);
        if (approachSamples.Count > 3) approachSamples.Dequeue();
        if (approachSamples.Count != 3) return false;
        var samples = approachSamples.ToArray();
        double slowest = double.PositiveInfinity;
        for (int i = 1; i < samples.Length; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];
            var seconds = (current.ObservedAt - previous.ObservedAt).TotalSeconds;
            if (seconds is < 8 or > 60) return false;
            double progress = ReturnRules.DistanceMetres(home, previous) - ReturnRules.DistanceMetres(home, current)
                - previous.Accuracy - current.Accuracy;
            slowest = Math.Min(slowest, progress / seconds);
        }
        return slowest > 0 && (ReturnRules.DistanceMetres(home, newest) + newest.Accuracy - home.Radius) / slowest <= 300;
    }

    private static void StopIfCurrent(Guid requestId) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (monitoring?.Id != requestId) return;
        Tracking = false;
        StopTracking();
    });

    internal static AlertMessage Unavailable(Guid id) => new(Guid.NewGuid(), id, AlertKind.LocationUnavailable,
        "נדרש אימות הגעה", "מועד החזרה הגיע. פתחו את HomeSafe כדי לבדוק את מצב ההגעה.", true,
        DateTimeOffset.UtcNow.AddMinutes(10));

    internal static async Task<AlertMessage?> LocalCheck(Guid id)
    {
        try
        {
            await ApiClient.Current.Restore();
            var family = await ApiClient.Current.Family();
            var request = family.Requests.FirstOrDefault(r => r.Id == id);
            if (request is null || request.Acknowledged || request.Status == ReturnStatus.Arrived
                || request.Deadline > DateTimeOffset.UtcNow || request.GraceUntil > DateTimeOffset.UtcNow) return null;
            if (request.Status == ReturnStatus.Overdue)
                return new(Guid.NewGuid(), id, AlertKind.Overdue,
                    family.Me.Role == FamilyRole.Parent ? "ילדך עדיין לא הגיע הביתה" : "שעת החזרה הביתה עברה",
                    $"מועד החזרה של {request.ChildName}: {request.Deadline.ToLocalTime():HH:mm}.", true,
                    DateTimeOffset.UtcNow.AddMinutes(10));
        }
        catch { }
        return Unavailable(id);
    }

    internal static void Reset()
    {
        SetEmergencyConsent(false);
        approachSamples.Clear();
        lastHeartbeat = default;
        Preferences.Default.Remove(AccountKey);
        Preferences.Default.Remove(ConsentKey);
        Preferences.Default.Remove(RevokeKey);
        registered = null;
        pending = null;
        Tracking = false;
        StopTracking();
        StopSound();
        SyncDeadlineFallback([]);
        ClearAll();
    }
}
