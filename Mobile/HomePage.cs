using System.Globalization;
using System.Net;
using HomeSafe.Shared;
using static HomeSafe.Mobile.Design;

namespace HomeSafe.Mobile;

internal sealed partial class HomePage : ContentPage
{
    private static readonly CultureInfo Hebrew = CultureInfo.GetCultureInfo("he-IL");
    private readonly ApiClient api = ApiClient.Current;
    private DemoFamily? demo;
    private FamilyView? family;
    private string section = "dashboard";
    private bool started;
    private bool fetching;
    private bool online = true;
    private bool submitting;
    private DateTimeOffset lastFetch;
    private ReturnView? alarm;
    private AlertMessage? offlineAlert;
    private bool testingAlarm;
    private bool alarmSilenced;
    private Guid? snoozed;
    private readonly List<(Label Label, DateTimeOffset Deadline)> clocks = [];
    private readonly Grid root = new() { RowDefinitions = Rows(GridLength.Auto, GridLength.Star, GridLength.Auto) };

    public HomePage()
    {
        FlowDirection = FlowDirection.RightToLeft;
        BackgroundColor = Canvas;
        NavigationPage.SetHasNavigationBar(this, false);
        Content = root;
        Welcome();
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += async (_, _) =>
        {
            if (!IsVisible || Window is null || !DeviceBridge.Active) return;
            foreach (var clock in clocks) clock.Label.Text = Countdown(clock.Deadline);
            if (!submitting && DateTimeOffset.UtcNow - lastFetch > TimeSpan.FromSeconds(demo is null ? 8 : 1))
                await Refresh();
        };
        timer.Start();
        DeviceBridge.AlertOpened += async item =>
        {
            if (demo is not null) return;
            if (item is { Urgent: true }) offlineAlert = item;
            if (snoozed == item.RequestId && item.Kind == AlertKind.Escalation) snoozed = null;
            await Refresh(rebuild: false);
            ShowAnyAlarm();
        };
        DeviceBridge.StateChanged += async () => await Refresh();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (started) return;
        started = true;
        try
        {
            await api.Restore();
            if (api.SignedIn)
            {
                section = "dashboard";
                PaintSingle("מתחברים לשרת", "טוענים את המשפחה", Text("יש לוודא שהמכשיר מחובר לאינטרנט."),
                    Button("רענון", async () => await Refresh()));
                await Refresh();
                await DeviceBridge.RegisterQuietly(api);
            }
            if (DeviceBridge.ConsumePending() is {} pending)
            {
                offlineAlert = pending.Urgent ? pending : null;
                ShowAnyAlarm();
            }
        }
        catch (Exception ex) { await Notice(ex.Message); }
    }

    private async Task Refresh(bool rebuild = true, bool throwFailure = false)
    {
        if (fetching || demo is null && !api.SignedIn) return;
        fetching = true;
        lastFetch = DateTimeOffset.UtcNow;
        var before = family;
        bool previouslyOnline = online;
        try
        {
            family = demo?.View() ?? await api.Family();
            online = true;
            if (demo is null)
            {
                await DeviceBridge.Apply(family);
                if (offlineAlert is {} pending)
                {
                    var current = family.Requests.FirstOrDefault(r => r.Id == pending.RequestId);
                    if (current is not null && (!ReturnRules.IsUrgent(current.Status) || current.Acknowledged))
                        offlineAlert = null;
                }
            }
            if (rebuild && AlarmIsVisible && !testingAlarm && alarm is {} displayed
                && family.Requests.Any(r => r.Id == displayed.Id && !ReturnRules.IsUrgent(r.Status)))
            {
                CloseAlarm();
                if (family.Requests.FirstOrDefault(r => r.Id == displayed.Id) is {} arrived)
                    await Navigation.PushAsync(ArrivalPage(arrived));
                return;
            }

            if (rebuild && !ShowAnyAlarm() && section is "dashboard" or "requests"
                && (before is null || !Equivalent(before, family) || !previouslyOnline))
                Render();
        }
        catch (RequestFailure ex) when (ex.Code == HttpStatusCode.Unauthorized)
        {
            DeviceBridge.Reset();
            api.Forget();
            family = null;
            Welcome();
            if (throwFailure) throw;
        }
        catch (Exception)
        {
            online = false;
            if (rebuild && family is not null && previouslyOnline && !AlarmIsVisible) Render();
            else if (rebuild && family is null && previouslyOnline && !AlarmIsVisible)
                PaintSingle("אין חיבור לשרת", "סנכרון משפחה",
                    Text("יש לבדוק את החיבור ולנסות שוב.", 15, Muted), Button("רענון", () => Refresh()));
            if (throwFailure) throw;
        }
        finally { fetching = false; }
    }

    private static bool Equivalent(FamilyView a, FamilyView b) =>
        a.Home == b.Home && a.Me == b.Me && a.Parent == b.Parent && a.SharingEnabled == b.SharingEnabled
        && a.PushConfigured == b.PushConfigured && a.DeviceRegistered == b.DeviceRegistered
        && a.Children.SequenceEqual(b.Children) && a.ReminderMinutes.SequenceEqual(b.ReminderMinutes)
        && a.Requests.Select(r => r with { LastKnown = null }).SequenceEqual(b.Requests.Select(r => r with { LastKnown = null }));

    private bool AlarmIsVisible => alarm is not null || offlineAlert is not null && root.BackgroundColor == SoftRed;

    private bool ShowAnyAlarm()
    {
        if (testingAlarm) return true;
        var next = family?.Requests.FirstOrDefault(r => ReturnRules.IsUrgent(r.Status) && !r.Acknowledged
            && r.Id != snoozed && DateTimeOffset.UtcNow < r.Deadline.AddHours(2));
        if (next is not null)
        {
            if (alarm?.Id != next.Id) ShowAlarm(next);
            return true;
        }
        if (offlineAlert is { } pending && !online && snoozed != pending.RequestId && !AlarmIsVisible)
        {
            ShowAlarm(null);
            return true;
        }
        return AlarmIsVisible;
    }

    private void StartDemo(FamilyRole role)
    {
        demo = new DemoFamily(role);
        family = demo.View();
        section = "dashboard";
        online = true;
        Render();
    }

    private void Welcome()
    {
        section = "welcome";
        PaintSingle("חוזרים הביתה. בראש שקט.", "בטיחות משפחתית", Shield(104, true),
            Text("מתאמים שעת חזרה, מקבלים אישור הגעה ויודעים מתי כדאי לבדוק מה קרה.", 17, Muted),
            Button("מתחילים", () => Go(() => RoleChoice())),
            Button("כניסה לחשבון קיים", () => Go(() => AccountForm(null)), false),
            Button("ניסיון במצב הדגמה", () => Go(() => RoleChoice(true)), false));
    }

    private void RoleChoice(bool isDemo = false)
    {
        PaintSingle("איך משתמשים ב־HomeSafe?", isDemo ? "הדגמה מקומית" : "יצירת חשבון",
            Box(Stack(8, Text("חשבון הורה", 19, Ink, true),
                Text("חיבור ילדים, הגדרת שעת חזרה וקבלת התרעות.", 14, Muted),
                Button("המשך כהורה", () => Go(() => Choose(FamilyRole.Parent))))),
            Box(Stack(8, Text("חשבון ילד/ה", 19, Ink, true),
                Text("קבלת בקשות, תזכורות ושיתוף מיקום לפי בחירה.", 14, Muted),
                Button("המשך כילד/ה", () => Go(() => Choose(FamilyRole.Child))))),
            Button("חזרה", () => Go(Welcome), false));
        void Choose(FamilyRole role)
        {
            if (isDemo) StartDemo(role);
            else AccountForm(role);
        }
    }

    private void AccountForm(FamilyRole? role)
    {
        var address = Entry("https://homesafe.example", api.Endpoint, Keyboard.Url);
        address.FlowDirection = FlowDirection.LeftToRight;
        var email = Entry("name@example.com", keyboard: Keyboard.Email);
        email.FlowDirection = FlowDirection.LeftToRight;
        var password = Entry("לפחות 10 תווים"); password.IsPassword = true;
        var name = Entry("השם שיוצג למשפחה");
        var phone = Entry("אופציונלי", keyboard: Keyboard.Telephone);
        var fields = new List<View> { Field("כתובת שרת HomeSafe", address) };
        if (role is not null) fields.Add(Field("שם", name));
        fields.Add(Field("דוא״ל", email));
        fields.Add(Field("סיסמה", password));
        if (role is not null) fields.Add(Field("טלפון לשיחות מהירות", phone));
        fields.Add(Button(role is null ? "כניסה" : "יצירת חשבון", async () => await Run(async () =>
        {
            api.SetEndpoint(address.Text ?? "");
            var session = role is {} selected
                ? await api.Send<Session>(HttpMethod.Post, "auth/register",
                    new RegisterInput(name.Text ?? "", email.Text ?? "", password.Text ?? "", selected, phone.Text))
                : await api.Send<Session>(HttpMethod.Post, "auth/login",
                    new LoginInput(email.Text ?? "", password.Text ?? ""));
            await api.SignIn(session);
            section = "dashboard";
            await Refresh(throwFailure: true);
            Render();
        })));
        fields.Add(Button("חזרה", () => Go(Welcome), false));
        PaintSingle(role is null ? "טוב שחזרת" : "פותחים חשבון", role switch
        {
            FamilyRole.Parent => "חשבון הורה", FamilyRole.Child => "חשבון ילד/ה", _ => "כניסה לחשבון"
        }, Box(Stack(12, fields.ToArray())));
    }

    private void Render()
    {
        if (family is null) { Welcome(); return; }
        root.BackgroundColor = Canvas;
        root.Children.Clear();
        clocks.Clear();
        root.Add(Header(demo is not null ? "הדגמה" : online ? "HomeSafe" : "אין חיבור"), 0, 0);
        var content = section switch
        {
            "requests" => RequestList(),
            "settings" => Settings(),
            _ => Dashboard()
        };
        root.Add(Scroll(content), 0, 1);
        root.Add(NavigationBar(), 0, 2);
    }

    private View Header(string tag)
    {
        var right = Columns(Shield(37), Text("HomeSafe", 22, Ink, true), startExpands: false);
        right.ColumnSpacing = 8;
        right.VerticalOptions = LayoutOptions.Center;
        ((Label)right.Children[1]).VerticalOptions = LayoutOptions.Center;
        var pill = Pill(tag == "HomeSafe" ? family?.Me.Role == FamilyRole.Child ? "חשבון ילד/ה" : "חשבון הורה" : tag,
            Blue, SoftBlue);
        pill.IsVisible = family is not null || tag != "HomeSafe";
        pill.VerticalOptions = LayoutOptions.Center;
        var header = Columns(right, pill);
        header.Padding = new Thickness(19, 12, 19, 12);
        header.BackgroundColor = Paper;
        return header;
    }

    private View NavigationBar()
    {
        var grid = new Grid { Padding = new Thickness(10, 7, 10, 8), ColumnSpacing = 6, BackgroundColor = Paper };
        foreach (var tab in new[] { ("dashboard", "⌂", "בית"), ("requests", "≡", "בקשות"), ("settings", "⚙", "הגדרות") })
        {
            int col = grid.ColumnDefinitions.Count;
            grid.ColumnDefinitions.Add(new(GridLength.Star));
            bool active = tab.Item1 == section;
            var button = Button($"{tab.Item2}  {tab.Item3}", () => Go(() =>
            {
                section = tab.Item1;
                Render();
            }), false, active ? SoftBlue : Paper);
            button.TextColor = active ? Blue : Muted;
            SemanticProperties.SetDescription(button, tab.Item3);
            grid.Add(button, col, 0);
        }
        return grid;
    }

    private void PaintSingle(string title, string subtitle, params View[] content)
    {
        root.BackgroundColor = Canvas;
        root.Children.Clear();
        clocks.Clear();
        root.Add(Header(demo is not null ? "הדגמה" : "HomeSafe"), 0, 0);
        var all = Stack(16, Text(subtitle, 13, Blue, true), Text(title, 31, Ink, true));
        foreach (var item in content) all.Add(item);
        root.Add(Scroll(all), 0, 1);
    }

    private static ScrollView Scroll(View content) => new()
    {
        Content = content, Padding = new Thickness(18, 18, 18, 16), VerticalScrollBarVisibility = ScrollBarVisibility.Never
    };

    private Task Notice(string message) => DisplayAlertAsync("HomeSafe", message, "סגירה");

    private async Task Run(Func<Task> action)
    {
        if (submitting) return;
        submitting = true;
        try { await action(); }
        catch (Exception ex) { await Notice(ex.Message); }
        finally { submitting = false; }
    }

    private Task Go(Action change)
    {
        change();
        return Task.CompletedTask;
    }

    private static RowDefinitionCollection Rows(params GridLength[] sizes)
    {
        var rows = new RowDefinitionCollection();
        foreach (var size in sizes) rows.Add(new(size));
        return rows;
    }

    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm", Hebrew);
    private static string Day(DateTimeOffset time) => time.ToLocalTime().ToString("dd.MM.yyyy", Hebrew);

    private static string Countdown(DateTimeOffset deadline)
    {
        long seconds = Math.Max(0, (long)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    private Label TimerLabel(ReturnView row, int size, Color tone)
    {
        var label = Text(Countdown(row.Deadline), size, tone, true);
        label.FlowDirection = FlowDirection.LeftToRight;
        label.HorizontalOptions = LayoutOptions.Start;
        clocks.Add((label, row.Deadline));
        return label;
    }

    private void CloseAlarm()
    {
        DeviceBridge.StopSound();
        var id = alarm?.Id ?? offlineAlert?.RequestId;
        if (id is {} requestId) DeviceBridge.ClearAlert(requestId);
        alarm = null;
        offlineAlert = null;
        alarmSilenced = false;
        testingAlarm = false;
        Render();
    }
}
