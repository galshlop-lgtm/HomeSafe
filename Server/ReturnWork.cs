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

    public static void Arrive(State state, ReturnRow row, DateTimeOffset observed, DateTimeOffset now)
    {
        row.Status = ReturnStatus.Arrived;
        row.ArrivedAt = observed;
        row.EmergencyUntil = null;
        ExpireExtensions(row);
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
                if (row.GraceUntil > now) return;
                bool fresh = row.ObservedAt >= now.AddSeconds(-45);
                if (row.GraceUntil is null && parent.Privacy.SmartGrace && fresh
                    && now < row.Deadline.AddMinutes(1) && row.Approaching && row.ObservedPresence == Presence.Outside)
                {
                    row.GraceUntil = row.Deadline.AddMinutes(10);
                    Queue(state, row, row.ChildId, AlertKind.Grace, "שעת החזרה עברה",
                        "זוהתה התקרבות לבית. יש להגיע בהקדם כדי למנוע התרעה.", now);
                    return;
                }
                ExpireExtensions(row);
                row.Status = fresh && row.ObservedPresence == Presence.Outside
                    ? ReturnStatus.Overdue : ReturnStatus.LocationUnavailable;
                row.AlarmAt = now;
                state.Outbox.RemoveAll(x => x.Message.RequestId == row.Id && !x.Accepted);
                SendAlarm(state, row, now, escalation: false);
                return;
            }

            var dueReminders = parent.Reminders.Append(15).Distinct()
                .Where(m => row.Deadline.AddMinutes(-m) <= now && !row.SentReminders.Contains(m)).ToArray();
            if (dueReminders.Length > 0)
            {
                row.SentReminders = [.. row.SentReminders, .. dueReminders];
                var scheduledAt = row.Deadline.AddMinutes(-dueReminders.Min());
                if (scheduledAt >= row.CreatedAt && now - scheduledAt < TimeSpan.FromMinutes(3))
                {
                    int minutes = Math.Max(1, (int)Math.Ceiling((row.Deadline - now).TotalMinutes));
                    Queue(state, row, row.ChildId, AlertKind.Reminder, "תזכורת לחזרה הביתה",
                        minutes == 15 ? "נשארו 15 דקות עד שעת החזרה הביתה" : $"נותרו כ־{minutes} דקות עד שעת החזרה.", now);
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

    public static void ConfirmOverdue(State state, ReturnRow row, DateTimeOffset now)
    {
        row.Status = ReturnStatus.Overdue;
        state.Outbox.RemoveAll(x => x.Message.RequestId == row.Id && !x.Accepted && x.Message.Urgent);
        SendAlarm(state, row, now, escalation: false);
    }

    public static void ExpireExtensions(ReturnRow row)
    {
        int index = row.Extensions.FindIndex(x => x.Decision == ExtensionDecision.Pending);
        if (index >= 0) row.Extensions[index] = row.Extensions[index] with { Decision = ExtensionDecision.Expired };
    }

    public static ExtensionEntry RequestExtension(State state, ReturnRow row, ExtensionRequest input, DateTimeOffset now)
    {
        if (input.Minutes is not (15 or 30)) throw new ApiError(400, "אפשר לבקש 15 או 30 דקות.");
        if (row.Status is not (ReturnStatus.Pending or ReturnStatus.Accepted) || now >= row.Deadline || input.Revision != row.Revision)
            throw new ApiError(409, "מועד החזרה השתנה או פג.");
        var existing = row.Extensions.Find(x => x.Decision == ExtensionDecision.Pending);
        if (existing is not null) return existing;
        var entry = new ExtensionEntry(Guid.NewGuid(), input.Minutes, row.Revision, ExtensionDecision.Pending, now);
        row.Extensions.Add(entry);
        var parent = state.Users.First(u => u.Account.Id == row.ParentId);
        if (parent.Privacy.AutoApproveExtensions)
            return DecideExtension(state, row, new ExtensionResponse(entry.Id, true), now);
        Queue(state, row, row.ParentId, AlertKind.Extension, "בקשה לתוספת זמן",
            $"התקבלה בקשה לעוד {entry.Minutes} דקות. אפשר לענות בפרטי הבקשה.", now);
        return entry;
    }

    public static ExtensionEntry DecideExtension(State state, ReturnRow row, ExtensionResponse input, DateTimeOffset now)
    {
        int index = row.Extensions.FindIndex(x => x.Id == input.ExtensionId);
        if (index < 0) throw new ApiError(404, "בקשת ההארכה אינה זמינה.");
        var entry = row.Extensions[index];
        if (entry.Decision != ExtensionDecision.Pending) return entry;
        if (entry.Revision != row.Revision || now >= row.Deadline || !ReturnRules.NeedsMonitoring(row.Status))
            throw new ApiError(409, "בקשת ההארכה פגה.");
        var decision = input.Approve ? ExtensionDecision.Approved : ExtensionDecision.Rejected;
        row.Extensions[index] = entry with { Decision = decision };
        if (input.Approve)
        {
            row.Deadline = row.Deadline.AddMinutes(entry.Minutes);
            row.Revision++;
            row.SentReminders = [];
        }
        Queue(state, row, row.ChildId, AlertKind.Extension, "בקשת תוספת זמן",
            input.Approve ? "ההארכה אושרה. שעת החזרה עודכנה." : "הבקשה נדחתה. שעת החזרה נשארה בתוקף.", now);
        return row.Extensions[index];
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
                missed ? $"{child.Name} זוהה/תה מחוץ לאזור הבית לאחר שעת החזרה."
                    : $"אין מיקום מספיק עדכני ומדויק עבור {child.Name}. כדאי ליצור קשר.", now, urgent: true);
        if (!row.ChildAcknowledged)
            Queue(state, row, row.ChildId, kind, missed ? "שעת החזרה הביתה עברה" : "לא הצלחנו לאמת את הגעתך",
                missed ? "המיקום שלך זוהה מחוץ לאזור הבית לאחר המועד שסוכם."
                    : "יש לבדוק את הרשאות המיקום והחיבור או להתקשר להורה.", now, urgent: true);
    }

    public static bool Advance(State state, DateTimeOffset now)
    {
        bool changed = false;
        foreach (var row in state.Requests)
        {
            var previous = (row.Status, row.EscalationStage, row.SentReminders.Length, row.GraceUntil);
            AdvanceOne(state, row, now);
            changed |= previous != (row.Status, row.EscalationStage, row.SentReminders.Length, row.GraceUntil);
            if (row.Latest is {} fix && (row.EmergencyUntil is null || row.EmergencyUntil <= now || now - fix.ObservedAt > TimeSpan.FromMinutes(2)))
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
                bool emergency = Monitoring.EmergencyActive(child, r, now);
                var lastKnown = parentView && emergency && r.Latest is {} fix
                    && now - fix.ObservedAt <= TimeSpan.FromMinutes(2) ? fix : null;
                bool pushAccepted = state.Outbox.Any(x => x.Recipient == id && x.Message.RequestId == r.Id
                    && x.Message.Urgent && x.Accepted);
                return new ReturnView(r.Id, r.ChildId, child.Account.Name, r.Deadline, r.Home, r.Status,
                    r.Revision, r.ArrivedAt, r.ArrivedAt > r.Deadline,
                    parentView ? r.ParentAcknowledged : r.ChildAcknowledged, lastKnown, pushAccepted, r.Extensions.Count == 0 ? null : r.Extensions.ToArray(),
                    r.LastCommunication, r.Battery,
                    ReturnRules.NeedsMonitoring(r.Status) && (r.LastCommunication ?? r.CreatedAt) < now.AddMinutes(-3),
                    r.GraceUntil, emergency, state.Outbox.Any(x => x.Recipient == id
                        && x.Message.RequestId == r.Id && x.Message.Urgent && x.ReceivedAt is not null));
            }).ToArray();
        return new(user.Account, parent?.Account, children, (parent ?? user).Home,
            user.Sharing, (parent ?? user).Reminders, requests, pushConfigured, user.Device is not null, user.Privacy);
    }
}
