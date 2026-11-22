using HomeSafe.Shared;

namespace HomeSafe.Server;

public static class Monitoring
{
    internal static bool EmergencyActive(UserRow child, ReturnRow row, DateTimeOffset now) =>
        child.Sharing && child.Privacy.EmergencyLocation && row.Status == ReturnStatus.Overdue
        && !row.ParentAcknowledged && row.EmergencyUntil > now;

    public static void RequestEmergency(State state, ReturnRow row, DateTimeOffset now)
    {
        var child = state.Users.First(u => u.Account.Id == row.ChildId);
        if (EmergencyActive(child, row, now)) return;
        if (!child.Sharing || !child.Privacy.EmergencyLocation || row.Status != ReturnStatus.Overdue
            || row.ParentAcknowledged || now >= row.Deadline.AddHours(2))
            throw new ApiError(409, "מיקום חירום זמין רק באיחור מאומת ובהסכמת הילד/ה.");
        row.EmergencyUntil = now.AddMinutes(5);
        ReturnWork.Queue(state, row, row.ChildId, AlertKind.EmergencyLocation, "שיתוף מיקום חירום פעיל",
            "ההורה ביקש מיקום בגלל איחור. אפשר לכבות את השיתוף בהגדרות.", now);
    }

    public static LocationReceipt SaveEmergency(State state, UserRow child, LocationReport input, DateTimeOffset now)
    {
        var row = ReturnWork.RequireRequest(state, input.RequestId, child.Account.Id);
        if (!EmergencyActive(child, row, now))
            return new(ReturnRules.NeedsMonitoring(row.Status));
        var fix = input.Fix;
        if (fix is null || !ReturnRules.ValidCoordinates(fix.Latitude, fix.Longitude)
            || !double.IsFinite(fix.Accuracy) || fix.Accuracy is <= 0 or > 50
            || fix.ObservedAt < now.AddMinutes(-2) || fix.ObservedAt < row.EmergencyUntil!.Value.AddMinutes(-5)
            || fix.ObservedAt > now.AddSeconds(5))
            throw new ApiError(400, "נתון המיקום אינו עדכני או מדויק.");
        if (row.Latest is null || row.Latest.ObservedAt < fix.ObservedAt) row.Latest = fix;
        return new(true);
    }

    public static MonitoringReceipt Record(State state, UserRow child, MonitoringInput input, DateTimeOffset now)
    {
        var row = ReturnWork.RequireRequest(state, input.RequestId, child.Account.Id);
        if (!ReturnRules.NeedsMonitoring(row.Status) || now >= row.Deadline.AddHours(2)) return new(false, false);
        if (!Enum.IsDefined(input.Presence) || input.Battery is < 0 or > 100
            || input.ObservedAt < now.AddMinutes(-3) || input.ObservedAt > now.AddSeconds(10))
            throw new ApiError(400, "עדכון המכשיר אינו תקין.");

        row.LastCommunication = now;
        if (input.Battery is int level)
        {
            row.Battery = level;
            if (level < 15 && !row.BatteryWarningSent && now >= row.Deadline.AddHours(-1) && now < row.Deadline)
            {
                row.BatteryWarningSent = true;
                ReturnWork.Queue(state, row, row.ChildId, AlertKind.Battery, "סוללה חלשה", "כדאי לחבר את המכשיר למטען.", now);
                if (state.Users.First(u => u.Account.Id == row.ParentId).Privacy.LowBatteryToParent)
                    ReturnWork.Queue(state, row, row.ParentId, AlertKind.Battery, "סוללה חלשה",
                        $"רמת הסוללה של {child.Account.Name} היא {level}%.", now);
            }
        }

        var observed = input.ObservedAt > now ? now : input.ObservedAt;
        if (input.HasLocationSample && child.Sharing && observed >= row.CreatedAt && (row.ObservedAt is null || observed > row.ObservedAt))
        {
            if (input.Presence == Presence.Inside)
                ReturnWork.Arrive(state, row, observed, now);
            else
            {
                if (now >= row.Deadline && observed > row.Deadline) ReturnWork.AdvanceOne(state, row, now);
                row.ObservedAt = observed;
                row.ObservedPresence = input.Presence;
                row.Approaching = input.Presence == Presence.Outside && input.Approaching;
                if (row.Status == ReturnStatus.LocationUnavailable && input.Presence == Presence.Outside
                    && now >= row.Deadline && now < row.Deadline.AddHours(2))
                    ReturnWork.ConfirmOverdue(state, row, now);
            }
        }
        return new(ReturnRules.NeedsMonitoring(row.Status), EmergencyActive(child, row, now));
    }
}
