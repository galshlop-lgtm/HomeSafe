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
        else if (Tracking)
        {
            Tracking = false;
            StopTracking();
        }
    }

    internal static async Task Report(Guid id, LocationFix fix)
    {
        if (!IsLinkedDevice) return;
        try
        {
            await ApiClient.Current.Restore();
            var receipt = await ApiClient.Current.Send<LocationReceipt>(HttpMethod.Post, "locations", new LocationReport(id, fix));
            if (!receipt.ContinueMonitoring) StopIfCurrent(id);
        }
        catch (RequestFailure ex) when (ex.Code is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            StopIfCurrent(id);
        }
        catch { }
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
                || request.Deadline > DateTimeOffset.UtcNow) return null;
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
