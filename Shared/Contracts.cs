namespace HomeSafe.Shared;

public enum FamilyRole { Parent, Child }

public enum ReturnStatus { Pending, Accepted, Arrived, Overdue, LocationUnavailable }

public enum AlertKind { Request, Reminder, Arrival, Overdue, LocationUnavailable, Escalation }

public enum Presence { Unknown, Inside, Outside }

public sealed record Account(Guid Id, string Name, FamilyRole Role, string? Phone);
public sealed record RegisterInput(string Name, string Email, string Password, FamilyRole Role, string? Phone);
public sealed record LoginInput(string Email, string Password);
public sealed record Session(string Token, Account Account);
public sealed record HomeArea(string Name, double Latitude, double Longitude, int Radius);
public sealed record LocationFix(double Latitude, double Longitude, double Accuracy, DateTimeOffset ObservedAt);
public sealed record LocationReport(Guid RequestId, LocationFix Fix);
public sealed record LocationReceipt(bool ContinueMonitoring);
public sealed record Invitation(string Code, DateTimeOffset ExpiresAt);
public sealed record PairInput(string Code);
public sealed record SharingInput(bool Enabled);
public sealed record DeviceInput(string Platform, string Token);
public sealed record RemindersInput(int[] Minutes);
public sealed record PhoneInput(string? Phone);
public sealed record ReturnInput(Guid ChildId, DateTimeOffset Deadline, Guid? RequestId, int? Revision);
public sealed record CheckInInput(int Revision);

public sealed record ReturnView(
    Guid Id, Guid ChildId, string ChildName, DateTimeOffset Deadline, HomeArea Home, ReturnStatus Status,
    int Revision, DateTimeOffset? ArrivedAt, bool LateArrival, bool Acknowledged,
    LocationFix? LastKnown, bool PushAccepted);

public sealed record FamilyView(
    Account Me, Account? Parent, Account[] Children, HomeArea? Home, bool SharingEnabled,
    int[] ReminderMinutes, ReturnView[] Requests, bool PushConfigured, bool DeviceRegistered);

public sealed record AlertMessage(
    Guid Id, Guid RequestId, AlertKind Kind, string Title, string Body, bool Urgent,
    DateTimeOffset ExpiresAt);

public static class ReturnRules
{
    public static bool NeedsMonitoring(ReturnStatus status) =>
        status is ReturnStatus.Pending or ReturnStatus.Accepted or ReturnStatus.Overdue
            or ReturnStatus.LocationUnavailable;

    public static bool IsUrgent(ReturnStatus status) =>
        status is ReturnStatus.Overdue or ReturnStatus.LocationUnavailable;

    public static bool ValidCoordinates(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude)
        && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    public static Presence Locate(HomeArea home, LocationFix? fix, DateTimeOffset asOf)
    {
        if (fix is null || !ValidCoordinates(fix.Latitude, fix.Longitude)
            || !double.IsFinite(fix.Accuracy) || fix.Accuracy <= 0
            || fix.Accuracy > Math.Min(50, home.Radius / 2.0)
            || fix.ObservedAt > asOf || fix.ObservedAt < asOf.AddMinutes(-3))
            return Presence.Unknown;

        const double earthRadius = 6_371_000;
        const double radians = Math.PI / 180;
        double latitudeDelta = (fix.Latitude - home.Latitude) * radians;
        double longitudeDelta = (fix.Longitude - home.Longitude) * radians;
        double a = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
            + Math.Cos(home.Latitude * radians) * Math.Cos(fix.Latitude * radians)
            * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        double distance = 2 * earthRadius * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));

        if (distance + fix.Accuracy <= home.Radius) return Presence.Inside;
        if (distance - fix.Accuracy > home.Radius) return Presence.Outside;
        return Presence.Unknown;
    }
}
