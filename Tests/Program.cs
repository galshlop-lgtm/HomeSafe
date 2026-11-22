using HomeSafe.Server;
using HomeSafe.Shared;

var now = DateTimeOffset.Parse("2026-11-22T18:20:00Z");
var home = new HomeArea("Home", 32.08, 34.78, 100);
var parent = new UserRow { Account = new(Guid.NewGuid(), "Parent", FamilyRole.Parent, null), Email = "p", PasswordHash = "" };
var child = new UserRow { Account = new(Guid.NewGuid(), "Child", FamilyRole.Child, null), Email = "c", PasswordHash = "", ParentId = parent.Account.Id, Sharing = true };
var state = new State { Users = [parent, child] };

ReturnRow Start(DateTimeOffset deadline)
{
    var row = new ReturnRow { ParentId = parent.Account.Id, ChildId = child.Account.Id,
        Home = home, CreatedAt = now.AddHours(-1), Deadline = deadline };
    state.Requests.Add(row);
    return row;
}
void Check(bool result, string scenario)
{
    if (!result) throw new Exception($"Failed: {scenario}");
    Console.WriteLine($"PASS: {scenario}");
}
ReturnView ParentView(ReturnRow row, DateTimeOffset at) =>
    ReturnWork.View(state, parent, at, false).Requests.Single(x => x.Id == row.Id);

var early = Start(now.AddMinutes(10));
Monitoring.Record(state, child, new(early.Id, Presence.Inside, now, 70), now);
Check(early.Status == ReturnStatus.Arrived && !ParentView(early, now).LateArrival, "arrival before deadline");
var late = Start(now.AddMinutes(-1));
ReturnWork.AdvanceOne(state, late, now);
Monitoring.Record(state, child, new(late.Id, Presence.Inside, now, 70), now);
Check(ParentView(late, now).LateArrival && !state.Outbox.Any(x => x.Message.RequestId == late.Id && x.Message.Urgent),
    "late arrival clears unconfirmed alarms");

var extension = Start(now.AddMinutes(10));
var first = ReturnWork.RequestExtension(state, extension, new(15, 1), now);
Check(first.Decision == ExtensionDecision.Pending && extension.Deadline == now.AddMinutes(10), "manual extension by default");
ReturnWork.DecideExtension(state, extension, new(first.Id, false), now);
Check(extension.Extensions[0].Decision == ExtensionDecision.Rejected && extension.Deadline == now.AddMinutes(10), "extension rejected");
var second = ReturnWork.RequestExtension(state, extension, new(30, 1), now);
ReturnWork.DecideExtension(state, extension, new(second.Id, true), now);
ReturnWork.DecideExtension(state, extension, new(second.Id, true), now);
Check(extension.Extensions[1].Decision == ExtensionDecision.Approved && extension.Deadline == now.AddMinutes(40), "approved extension applied once");

var reminder = Start(now.AddMinutes(15));
ReturnWork.AdvanceOne(state, reminder, now);
var notice = state.Outbox.Single(x => x.Message.RequestId == reminder.Id);
Check(notice.Recipient == child.Account.Id && !notice.Message.Urgent
    && notice.Message.Body == "נשארו 15 דקות עד שעת החזרה הביתה", "15-minute reminder goes quietly to child");
parent.Privacy = parent.Privacy with { AutoApproveExtensions = true };
var automatic = ReturnWork.RequestExtension(state, reminder, new(15, 1), now);
Check(automatic.Decision == ExtensionDecision.Approved && reminder.Deadline == now.AddMinutes(30), "opt-in automatic extension");
parent.Privacy = parent.Privacy with { AutoApproveExtensions = false };

var unavailable = Start(now.AddMinutes(1));
Monitoring.Record(state, child, new(unavailable.Id, Presence.Outside, now.AddMinutes(-4), 50, HasLocationSample: false), now.AddMinutes(-4));
ReturnWork.AdvanceOne(state, unavailable, now.AddMinutes(1));
Check(ParentView(unavailable, now.AddMinutes(1)) is { Status: ReturnStatus.LocationUnavailable, DeviceUnavailable: true, Battery: 50 },
    "disconnected or powered-off device remains unavailable with last reported battery");
var battery = Start(now.AddMinutes(30));
Monitoring.Record(state, child, new(battery.Id, Presence.Unknown, now, 14, HasLocationSample: false), now);
Monitoring.Record(state, child, new(battery.Id, Presence.Unknown, now.AddSeconds(10), 13, HasLocationSample: false), now.AddSeconds(10));
Check(state.Outbox.Count(x => x.Message.RequestId == battery.Id && x.Message.Kind == AlertKind.Battery) == 1,
    "low battery warns child once and leaves parent quiet by default");
Check(ReturnRules.Locate(home, new(home.Latitude, home.Longitude, 80, now), now) == Presence.Unknown,
    "inaccurate GPS does not confirm arrival");

var grace = Start(now.AddSeconds(10));
parent.Privacy = parent.Privacy with { SmartGrace = true };
Monitoring.Record(state, child, new(grace.Id, Presence.Outside, now, 80, Approaching: true), now);
ReturnWork.AdvanceOne(state, grace, now.AddSeconds(11));
Check(grace.GraceUntil == grace.Deadline.AddMinutes(10) && grace.Status == ReturnStatus.Pending, "bounded opt-in grace");
ReturnWork.AdvanceOne(state, grace, grace.Deadline.AddMinutes(10));
Check(ReturnRules.IsUrgent(grace.Status), "grace expires into alarm workflow");

var overdue = Start(now.AddSeconds(10));
var elsewhere = new LocationFix(home.Latitude + .02, home.Longitude, 8, now);
Monitoring.SaveEmergency(state, child, new(overdue.Id, elsewhere), now);
Check(overdue.Latest is null && ParentView(overdue, now).LastKnown is null, "normal periods never retain coordinates");
Monitoring.Record(state, child, new(overdue.Id, Presence.Outside, now, 40), now);
ReturnWork.AdvanceOne(state, overdue, now.AddSeconds(11));
var delivery = state.Outbox.Where(x => x.Message.RequestId == overdue.Id && x.Message.Urgent).ToArray();
Check(delivery.Length == 2 && delivery.All(x => !x.Accepted && x.ReceivedAt is null), "unconfirmed provider delivery remains visible");
ReturnWork.AdvanceOne(state, overdue, now.AddMinutes(3));
Check(state.Outbox.Count(x => x.Message.RequestId == overdue.Id && x.Message.Kind == AlertKind.Escalation) == 2,
    "alarm escalates when neither device acknowledged");
child.Privacy = child.Privacy with { EmergencyLocation = true };
var emergencyTime = now.AddSeconds(15);
Monitoring.RequestEmergency(state, overdue, emergencyTime);
Monitoring.SaveEmergency(state, child, new(overdue.Id, elsewhere with { ObservedAt = emergencyTime }), emergencyTime);
Check(ParentView(overdue, emergencyTime).LastKnown is not null, "emergency sharing after consent and parent request");
ReturnWork.Advance(state, emergencyTime.AddMinutes(3));
Check(overdue.Latest is null, "short emergency data retention");

Check(ServerAddress.Normalize("http://example.org") is null
    && ServerAddress.Normalize("https://user@example.org") is null
    && !ServerAddress.IsTrusted("https://example.org.evil.test", "https://example.org")
    && !ServerAddress.IsTrusted("https://example.org/path", "https://example.org")
    && ServerAddress.IsTrusted("https://example.org/", "https://example.org"), "HTTPS and exact approved deployment validation");
