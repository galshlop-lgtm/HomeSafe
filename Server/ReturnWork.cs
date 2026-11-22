using HomeSafe.Shared;

namespace HomeSafe.Server;

public static class ReturnWork
{
    public static ReturnRow RequireRequest(State state, Guid id, Guid actor)
        => state.Requests.Find(r => r.Id == id && (r.ParentId == actor || r.ChildId == actor))
           ?? throw new ApiError(404, "הבקשה אינה זמינה.");

    public static void Queue(State state, ReturnRow request, Guid recipient, AlertKind kind,
        string title, string body, DateTimeOffset now, bool urgent = false)
    {
        state.Outbox.Add(new()
        {
            Recipient = recipient,
            NextTry = now,
            Message = new(Guid.NewGuid(), request.Id, kind, title, body, urgent,
                now.AddMinutes(urgent ? 12 : 10))
        });
    }

    public static void RequestChanged(State state, ReturnRow row, UserRow parent, DateTimeOffset now)
    {
        state.Outbox.RemoveAll(x => x.Message.RequestId == row.Id && !x.Accepted);
        Queue(state, row, row.ChildId, AlertKind.Request, "בקשת חזרה הביתה",
            $"מאת {parent.Account.Name}. פתחו את HomeSafe כדי לראות את שעת החזרה.", now);
    }

    public static void Arrive(State state, ReturnRow row, LocationFix fix, DateTimeOffset now)
    {
        row.Status = ReturnStatus.Arrived;
        row.ArrivedAt = fix.ObservedAt;
        row.Latest = null;
        state.Outbox.RemoveAll(x => x.Message.RequestId == row.Id && !x.Accepted);
        var child = state.Users.First(u => u.Account.Id == row.ChildId);
        Queue(state, row, row.ParentId, AlertKind.Arrival, "הגעה הביתה אומתה",
            $"{child.Account.Name} הגיע/ה לאזור הבית.", now);
        Queue(state, row, row.ChildId, AlertKind.Arrival, "הגעת הביתה", "ההגעה שלך אומתה.", now);
    }

    public static void AdvanceOne(State state, ReturnRow row, DateTimeOffset now)
    {
        var parent = state.Users.First(u => u.Account.Id == row.ParentId);
        if (row.Status is ReturnStatus.Pending or ReturnStatus.Accepted)
        {
            if (now >= row.Deadline)
            {
                var presence = ReturnRules.Locate(row.Home, row.Latest, row.Deadline);
                if (presence == Presence.Inside)
                {
                    Arrive(state, row, row.Latest!, now);
                    return;
                }

                row.Status = presence == Presence.Outside
                    ? ReturnStatus.Overdue : ReturnStatus.LocationUnavailable;
                row.AlarmAt = now;
                state.Outbox.RemoveAll(x => x.Message.RequestId == row.Id && !x.Accepted);
                SendAlarm(state, row, now, escalation: false);
                return;
            }

            var dueReminders = parent.Reminders
                .Where(m => row.Deadline.AddMinutes(-m) <= now && !row.SentReminders.Contains(m)).ToArray();
            if (dueReminders.Length > 0)
            {
                row.SentReminders = [.. row.SentReminders, .. dueReminders];
                var scheduledAt = row.Deadline.AddMinutes(-dueReminders.Min());
                if (scheduledAt >= row.CreatedAt && now - scheduledAt < TimeSpan.FromMinutes(3))
                {
                    int minutes = Math.Max(1, (int)Math.Ceiling((row.Deadline - now).TotalMinutes));
                    Queue(state, row, row.ChildId, AlertKind.Reminder, "תזכורת לחזרה הביתה",
                        $"נותרו כ־{minutes} דקות עד שעת החזרה.", now);
                }
            }
        }

        if (ReturnRules.IsUrgent(row.Status) && row.AlarmAt is {} triggered
            && row.EscalationStage < 2 && now >= triggered.AddMinutes((row.EscalationStage + 1) * 2))
        {
            row.EscalationStage++;
            SendAlarm(state, row, now, escalation: true);
        }
    }

    private static void SendAlarm(State state, ReturnRow row, DateTimeOffset now, bool escalation)
    {
        var child = state.Users.First(u => u.Account.Id == row.ChildId).Account;
        bool missed = row.Status == ReturnStatus.Overdue;
        var kind = escalation ? AlertKind.Escalation : missed ? AlertKind.Overdue : AlertKind.LocationUnavailable;
        // Avoid sending a backlog of alarms after a long outage.
        if (now > row.Deadline.AddHours(2)) return;
        if (!row.ParentAcknowledged)
            Queue(state, row, row.ParentId, kind, missed ? "ילדך עדיין לא הגיע הביתה" : "לא ניתן לאמת הגעה",
                missed ? $"{child.Name} היה/תה מחוץ לאזור הבית במועד החזרה."
                    : $"אין מיקום מספיק עדכני ומדויק עבור {child.Name}. כדאי ליצור קשר.", now, urgent: true);
        if (!row.ChildAcknowledged)
            Queue(state, row, row.ChildId, kind, missed ? "שעת החזרה הביתה עברה" : "לא הצלחנו לאמת את הגעתך",
                missed ? "המיקום שלך היה מחוץ לאזור הבית במועד שסוכם."
                    : "יש לבדוק את הרשאות המיקום והחיבור או להתקשר להורה.", now, urgent: true);
    }

    public static bool Advance(State state, DateTimeOffset now)
    {
        bool changed = false;
        foreach (var row in state.Requests)
        {
            var previous = (row.Status, row.EscalationStage, row.SentReminders.Length);
            AdvanceOne(state, row, now);
            changed |= previous != (row.Status, row.EscalationStage, row.SentReminders.Length);
            if (row.Latest is {} fix && now - fix.ObservedAt > TimeSpan.FromHours(24))
            {
                row.Latest = null;
                changed = true;
            }
        }
        changed |= state.Outbox.RemoveAll(d => d.Message.ExpiresAt < now.AddHours(-24)) > 0;
        changed |= state.Requests.RemoveAll(r => r.Deadline < now.AddDays(-30)) > 0;
        changed |= state.Invites.RemoveAll(i => i.ExpiresAt <= now) > 0;
        changed |= state.Sessions.RemoveAll(s => s.ExpiresAt <= now) > 0;
        return changed;
    }

    public static FamilyView View(State state, UserRow user, DateTimeOffset now, bool pushConfigured)
    {
        Guid id = user.Account.Id;
        var parent = state.Users.Find(u => u.Account.Id == user.ParentId);
        var children = state.Users.Where(u => u.ParentId == id).Select(u => u.Account).ToArray();
        var requests = state.Requests.Where(r => r.ParentId == id || r.ChildId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r =>
            {
                var child = state.Users.First(u => u.Account.Id == r.ChildId);
                bool parentView = r.ParentId == id;
                var lastKnown = parentView && child.Sharing && r.Latest is {} fix
                    && now - fix.ObservedAt <= TimeSpan.FromHours(24) ? fix : null;
                bool pushAccepted = state.Outbox.Any(x => x.Recipient == id && x.Message.RequestId == r.Id
                    && x.Message.Urgent && x.Accepted);
                return new ReturnView(r.Id, r.ChildId, child.Account.Name, r.Deadline, r.Home, r.Status,
                    r.Revision, r.ArrivedAt, r.ArrivedAt > r.Deadline,
                    parentView ? r.ParentAcknowledged : r.ChildAcknowledged, lastKnown, pushAccepted);
            }).ToArray();
        return new(user.Account, parent?.Account, children, (parent ?? user).Home,
            user.Sharing, (parent ?? user).Reminders, requests, pushConfigured, user.Device is not null);
    }
}
