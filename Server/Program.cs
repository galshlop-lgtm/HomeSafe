using System.Security.Cryptography;
using System.Threading.RateLimiting;
using HomeSafe.Server;
using HomeSafe.Shared;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Store>();
builder.Services.AddHttpClient<PushSender>(client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHostedService<DeadlineWorker>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("credentials", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    try { await next(ctx); }
    catch (ApiError error)
    {
        ctx.Response.StatusCode = error.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = error.Message });
    }
});
app.MapGet("/health", () => new HealthStatus("ok", "HomeSafe", 2));
var api = app.MapGroup("/api");

api.MapPost("/auth/register", (RegisterInput input, Store store, TimeProvider clock) =>
{
    var email = Accounts.Email(input.Email);
    var name = input.Name?.Trim() ?? "";
    if (name.Length is < 2 or > 50 || input.Password?.Length is not (>= 10 and <= 128)
        || !Enum.IsDefined(input.Role))
        throw new ApiError(400, "יש להזין שם וסיסמה באורך 10 תווים לפחות.");
    var user = new UserRow
    {
        Account = new(Guid.NewGuid(), name, input.Role, Accounts.Phone(input.Phone)),
        Email = email, PasswordHash = Accounts.PasswordHash(input.Password)
    };
    return store.Write(state =>
    {
        if (state.Users.Any(u => u.Email == email)) throw new ApiError(409, "כתובת הדוא״ל כבר רשומה.");
        state.Users.Add(user);
        return Accounts.NewSession(state, user, clock.GetUtcNow());
    });
}).RequireRateLimiting("credentials");

var dummyHash = Accounts.PasswordHash(Convert.ToHexString(RandomNumberGenerator.GetBytes(30)));

api.MapPost("/auth/login", (LoginInput input, Store store, TimeProvider clock) =>
{
    var email = Accounts.Email(input.Email);
    if (input.Password?.Length is not (>= 1 and <= 128)) throw new ApiError(401, "פרטי הכניסה אינם תקינים.");
    var user = store.Read(s => s.Users.Find(u => u.Email == email));
    if (!Accounts.Verify(input.Password, user?.PasswordHash ?? dummyHash) || user is null)
        throw new ApiError(401, "פרטי הכניסה אינם תקינים.");
    return store.Write(state => Accounts.NewSession(state, user, clock.GetUtcNow()));
}).RequireRateLimiting("credentials");

api.MapPost("/auth/logout", (HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, user, _) =>
    {
        state.Sessions.RemoveAll(s => s.TokenHash == Accounts.Token(ctx));
        return Results.NoContent();
    }));

api.MapGet("/family", (HttpContext ctx, Store store, TimeProvider clock, PushSender push) =>
{
    var token = Accounts.Token(ctx);
    var now = clock.GetUtcNow();
    return store.Read(state =>
    {
        var user = Accounts.Require(state, token, now);
        return ReturnWork.View(state, user, now, push.Configured(user.Device?.Platform));
    });
});

api.MapPost("/pair/invite", (HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, parent, now) =>
    {
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        var expires = now.AddMinutes(30);
        state.Invites.RemoveAll(i => i.ParentId == parent.Account.Id);
        state.Invites.Add(new(parent.Account.Id, Accounts.Hash(code), expires));
        return new Invitation(code, expires);
    }, FamilyRole.Parent)).RequireRateLimiting("credentials");

api.MapPost("/pair/join", (PairInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, child, now) =>
    {
        if (child.ParentId is not null) throw new ApiError(409, "כבר קיים חשבון הורה מקושר.");
        string code = (input.Code ?? "").Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (code.Length != 10 || code.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ApiError(400, "קוד החיבור אינו תקין.");
        var invite = state.Invites.Find(i => i.CodeHash == Accounts.Hash(code) && i.ExpiresAt > now)
            ?? throw new ApiError(404, "הקוד פג או אינו תקין.");
        child.ParentId = invite.ParentId;
        state.Invites.Remove(invite);
        return Results.NoContent();
    }, FamilyRole.Child)).RequireRateLimiting("credentials");

api.MapPut("/home", (HomeArea input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (_, parent, _) =>
    {
        if (!ReturnRules.ValidCoordinates(input.Latitude, input.Longitude)
            || input.Radius is < 50 or > 300 || input.Name?.Trim().Length is not (>= 1 and <= 60))
            throw new ApiError(400, "יש לבחור נקודה ורדיוס בין 50 ל־300 מטר.");
        parent.Home = input with { Name = input.Name.Trim() };
        return Results.NoContent();
    }, FamilyRole.Parent));

api.MapPut("/settings/phone", (PhoneInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (_, user, _) =>
    {
        user.Account = user.Account with { Phone = Accounts.Phone(input.Phone) };
        return Results.NoContent();
    }));

api.MapPut("/settings/reminders", (RemindersInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (_, parent, _) =>
    {
        if (input.Minutes is null || input.Minutes.Length > 4 || input.Minutes.Any(m => m is < 1 or > 180))
            throw new ApiError(400, "אפשר להגדיר עד ארבע תזכורות, בין דקה ל־180 דקות מראש.");
        parent.Reminders = input.Minutes.Distinct().OrderDescending().ToArray();
        return Results.NoContent();
    }, FamilyRole.Parent));

api.MapPut("/settings/sharing", (SharingInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, child, _) =>
    {
        if (input.Enabled && child.ParentId is null)
            throw new ApiError(409, "יש להתחבר להורה לפני הפעלת שיתוף המיקום.");
        child.Sharing = input.Enabled;
        if (!input.Enabled)
            foreach (var request in state.Requests.Where(r => r.ChildId == child.Account.Id))
            {
                request.Latest = null;
                request.EmergencyUntil = null;
                request.ObservedPresence = Presence.Unknown;
                request.Approaching = false;
            }
        return Results.NoContent();
    }, FamilyRole.Child));

api.MapPut("/devices", (DeviceInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, user, _) =>
    {
        if (input.Platform is not ("android" or "ios") || input.Token?.Length is not (>= 32 and <= 4_096))
            throw new ApiError(400, "רישום ההתראות אינו תקין.");
        foreach (var other in state.Users.Where(u => u.Account.Id != user.Account.Id && u.Device == input))
            other.Device = null;
        user.Device = input;
        return Results.NoContent();
    }));

api.MapDelete("/devices", ([Microsoft.AspNetCore.Mvc.FromBody] DeviceInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (_, user, _) =>
    {
        if (user.Device == input) user.Device = null;
        return Results.NoContent();
    }));

api.MapPost("/returns", (ReturnInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, parent, now) =>
    {
        if (input.Deadline <= now.AddSeconds(15) || input.Deadline > now.AddDays(30))
            throw new ApiError(400, "יש לבחור זמן עתידי בטווח של 30 יום.");
        _ = state.Users.Find(u => u.Account.Id == input.ChildId && u.ParentId == parent.Account.Id)
            ?? throw new ApiError(404, "הילד אינו מקושר לחשבון זה.");
        if (input.RequestId is {} id)
        {
            var current = ReturnWork.RequireRequest(state, id, parent.Account.Id);
            if (current.ChildId != input.ChildId || current.Revision != input.Revision || now >= current.Deadline
                || current.Status is not (ReturnStatus.Pending or ReturnStatus.Accepted))
                throw new ApiError(409, "הבקשה השתנתה או פגה. יש לרענן ולנסות שוב.");
            current.Deadline = input.Deadline.ToUniversalTime();
            current.Revision++;
            current.Status = ReturnStatus.Pending;
            current.SentReminders = [];
            ReturnWork.ExpireExtensions(current);
            ReturnWork.RequestChanged(state, current, parent, now);
            return new { current.Id };
        }
        var latest = state.Requests.LastOrDefault(r => r.ChildId == input.ChildId);
        if (latest is not null && (latest.Status is ReturnStatus.Pending or ReturnStatus.Accepted
            || ReturnRules.IsUrgent(latest.Status) && !latest.ParentAcknowledged && now < latest.Deadline.AddHours(2)))
            throw new ApiError(409, "כבר קיימת בקשה פתוחה. אפשר לעדכן אותה או לאשר את ההתרעה.");
        var row = new ReturnRow
        {
            ChildId = input.ChildId, ParentId = parent.Account.Id, Deadline = input.Deadline.ToUniversalTime(),
            CreatedAt = now, Home = parent.Home ?? throw new ApiError(409, "יש להגדיר קודם את מיקום הבית.")
        };
        state.Requests.Add(row);
        ReturnWork.RequestChanged(state, row, parent, now);
        return new { row.Id };
    }, FamilyRole.Parent));

api.MapPost("/returns/{id:guid}/accept", (Guid id, CheckInInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, child, now) =>
    {
        var row = ReturnWork.RequireRequest(state, id, child.Account.Id);
        if (row.Revision != input.Revision || now >= row.Deadline || row.Status is not (ReturnStatus.Pending or ReturnStatus.Accepted))
            throw new ApiError(409, "מועד החזרה השתנה או פג. יש לרענן את הבקשה.");
        row.Status = ReturnStatus.Accepted;
        return Results.NoContent();
    }, FamilyRole.Child));

api.MapPost("/returns/{id:guid}/acknowledge", (Guid id, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, user, _) =>
    {
        var row = ReturnWork.RequireRequest(state, id, user.Account.Id);
        if (!ReturnRules.IsUrgent(row.Status)) return Results.NoContent();
        if (user.Account.Id == row.ParentId)
        {
            row.ParentAcknowledged = true;
            row.Latest = null;
            row.EmergencyUntil = null;
        }
        else row.ChildAcknowledged = true;
        state.Outbox.RemoveAll(x => x.Recipient == user.Account.Id && x.Message.RequestId == id
            && x.Message.Urgent && !x.Accepted);
        return Results.NoContent();
    }));

api.MapPost("/alerts/{id:guid}/receipt", (Guid id, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, user, now) =>
    {
        var message = state.Outbox.Find(x => x.Recipient == user.Account.Id && x.Message.Id == id);
        if (message is not null) message.ReceivedAt ??= now;
        return Results.NoContent();
    }));

api.MapPost("/locations", (LocationReport input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, child, now) => Monitoring.SaveEmergency(state, child, input, now), FamilyRole.Child));

api.MapPost("/monitoring", (MonitoringInput input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, child, now) => Monitoring.Record(state, child, input, now), FamilyRole.Child));

api.MapPut("/settings/privacy", (PrivacySettings input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, user, _) =>
    {
        if (user.Account.Role == FamilyRole.Parent)
            user.Privacy = user.Privacy with { SmartGrace = input.SmartGrace,
                LowBatteryToParent = input.LowBatteryToParent, AutoApproveExtensions = input.AutoApproveExtensions };
        else
        {
            user.Privacy = user.Privacy with { EmergencyLocation = input.EmergencyLocation };
            if (!input.EmergencyLocation)
                foreach (var row in state.Requests.Where(r => r.ChildId == user.Account.Id))
                {
                    row.EmergencyUntil = null;
                    row.Latest = null;
                }
        }
        return Results.NoContent();
    }));

api.MapPost("/returns/{id:guid}/emergency-location", (Guid id, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, parent, now) =>
    {
        Monitoring.RequestEmergency(state, ReturnWork.RequireRequest(state, id, parent.Account.Id), now);
        return Results.NoContent();
    }, FamilyRole.Parent));

api.MapPost("/returns/{id:guid}/extensions", (Guid id, ExtensionRequest input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, child, now) =>
        ReturnWork.RequestExtension(state, ReturnWork.RequireRequest(state, id, child.Account.Id), input, now),
        FamilyRole.Child));

api.MapPost("/returns/{id:guid}/extensions/decide", (Guid id, ExtensionResponse input, HttpContext ctx, Store store, TimeProvider clock) =>
    Update(ctx, store, clock, (state, parent, now) =>
        ReturnWork.DecideExtension(state, ReturnWork.RequireRequest(state, id, parent.Account.Id), input, now),
        FamilyRole.Parent));

app.Run();

static T Update<T>(HttpContext ctx, Store store, TimeProvider clock, Func<State, UserRow, DateTimeOffset, T> action,
    FamilyRole? role = null)
{
    var token = Accounts.Token(ctx);
    var now = clock.GetUtcNow();
    return store.Write(state => action(state, Accounts.Require(state, token, now, role), now));
}
