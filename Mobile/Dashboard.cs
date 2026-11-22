using HomeSafe.Shared;
using static HomeSafe.Mobile.Design;

namespace HomeSafe.Mobile;

internal sealed partial class HomePage
{
    private View Dashboard()
    {
        var model = family!;
        var today = DateTimeOffset.Now.ToString("dddd, d MMMM", Hebrew);
        var view = Stack(15, Text(today, 13, Blue, true), Text($"שלום, {model.Me.Name}", 32, Ink, true));
        if (!online) view.Add(Box(Text("אין חיבור לשרת. המידע המוצג שמור מהסנכרון האחרון.", 14, Amber), SoftAmber));
        if (model.Me.Role == FamilyRole.Parent)
        {
            var featured = model.Requests.FirstOrDefault(r => r.Status is ReturnStatus.Pending or ReturnStatus.Accepted);
            view.Add(Hero(featured, "מבט על המשפחה", featured is null ? "כל המשפחה במקום אחד"
                : $"{featured.ChildName} · חזרה ב־{Time(featured.Deadline)}"));
            view.Add(Button("＋  קביעת שעת חזרה", () => OpenComposer()));
            var childCount = Pill(model.Children.Length.ToString(Hebrew), Blue, SoftBlue);
            view.Add(Columns(Text("הילדים שלי", 21, Ink, true), childCount));
            foreach (var child in model.Children)
            {
                var row = model.Requests.FirstOrDefault(r => r.ChildId == child.Id);
                view.Add(ChildCard(child, row));
            }
            if (model.Children.Length == 0)
                view.Add(Box(Stack(10, Text("המשפחה שלך מתחילה כאן", 17, Ink, true),
                    Text("שלחו קוד הזמנה לילד/ה כדי לתאם שעת חזרה.", 14, Muted),
                    Button("יצירת קוד חיבור", () => OpenPairing()))));
            else
                view.Add(Button("ניהול חיבור ילדים", () => OpenPairing(), false));
        }
        else
        {
            var row = model.Requests.FirstOrDefault();
            view.Add(Hero(row is { Status: ReturnStatus.Pending or ReturnStatus.Accepted } ? row : null,
                "החזרה שלי הביתה", row is null ? "אין בקשת חזרה פעילה" : $"שעת חזרה · {Time(row.Deadline)}"));
            if (model.Parent is null)
                view.Add(Button("חיבור לחשבון הורה", () => OpenPairing()));
            if (row is not null)
            {
                view.Add(ChildCard(model.Me, row));
                if (row.Status == ReturnStatus.Pending)
                    view.Add(Button("אישור שעת החזרה", () => Accept(row)));
            }
            var tracking = demo is not null ? model.SharingEnabled : DeviceBridge.Tracking;
            view.Add(Box(Stack(5, Pill(tracking ? "שיתוף מיקום פעיל" : "שיתוף מיקום אינו פעיל",
                tracking ? Green : Amber, tracking ? SoftGreen : SoftAmber),
                Text(model.SharingEnabled
                    ? "המיקום משותף עם ההורה המקושר במהלך בקשת חזרה."
                    : "אפשר להפעיל שיתוף מיקום בהגדרות לאחר חיבור להורה.", 14, Muted),
                Button("הגדרות מיקום", () => Go(() => { section = "settings"; Render(); }), false))));
        }
        return view;
    }

    private View Hero(ReturnView? row, string caption, string title)
    {
        var heading = Text(title, 20, Paper, true);
        var content = Stack(7, Text(caption, 13, Color.FromArgb("#B4C8FF"), true), heading);
        if (row is not null)
        {
            content.Add(TimerLabel(row, 37, Paper));
            content.Add(Text("הזמן שנותר", 12, Color.FromArgb("#B4C8FF")));
        }
        else content.Add(Text("תיאום חזרה הביתה עם המשפחה", 14, Color.FromArgb("#C8D6FF")));
        var art = Shield(88, true);
        art.VerticalOptions = LayoutOptions.Center;
        var hero = Box(Columns(content, art), Ink, Ink, 23, 18);
        hero.Background = new LinearGradientBrush(
            [new GradientStop(Color.FromArgb("#152449"), 0), new GradientStop(Color.FromArgb("#243E88"), 1)],
            new Point(0, 0), new Point(1, 1));
        return hero;
    }

    private View ChildCard(Account child, ReturnView? row)
    {
        var badge = row is null ? Pill("אין בקשה", Muted, Canvas) : StatusBadge(row.Status);
        var initials = Text(new string(child.Name.Take(1).ToArray()), 18, Blue, true);
        initials.HorizontalTextAlignment = TextAlignment.Center;
        initials.VerticalTextAlignment = TextAlignment.Center;
        var avatar = Box(initials, SoftBlue, SoftBlue, 13, 0);
        avatar.WidthRequest = 39; avatar.HeightRequest = 39;
        var identity = Columns(avatar, Stack(2, Text(child.Name, 17, Ink, true),
            Text(row is null ? "אין שעת חזרה מוגדרת" : $"חזרה ב־{Time(row.Deadline)}", 13, Muted)), false);
        var header = Columns(identity, badge);
        badge.VerticalOptions = LayoutOptions.Center;
        if (row is null) return Box(header);
        var card = Stack(12, header);
        if (row.Status is ReturnStatus.Pending or ReturnStatus.Accepted)
            card.Add(Columns(Text(row.Home.Name, 13, Muted), TimerLabel(row, 15, Blue)));
        if (row.Status == ReturnStatus.Arrived && row.ArrivedAt is {} arrived)
            card.Add(Text($"{(row.LateArrival ? "הגעה אחרי המועד" : "הגעה אומתה")} ב־{Time(arrived)}", 13, Green));
        var link = Button(row.Status == ReturnStatus.Arrived ? "אישור הגעה" : "פרטי הבקשה",
            async () => await Navigation.PushAsync(row.Status == ReturnStatus.Arrived ? ArrivalPage(row) : DetailPage(row)), false);
        card.Add(link);
        return Box(card);
    }

    private static Border StatusBadge(ReturnStatus status) => status switch
    {
        ReturnStatus.Arrived => Pill("בבית", Green, SoftGreen),
        ReturnStatus.Overdue => Pill("איחור", Red, SoftRed),
        ReturnStatus.LocationUnavailable => Pill("מיקום לא זמין", Amber, SoftAmber),
        ReturnStatus.Accepted => Pill("ממתינים להגעה", Blue, SoftBlue),
        _ => Pill("ממתינים לאישור", Muted, Canvas)
    };

    private View RequestList()
    {
        var result = Stack(14, Text("בקשות חזרה", 29, Ink, true));
        if (family!.Me.Role == FamilyRole.Parent)
            result.Add(Button("＋  קביעת שעת חזרה", () => OpenComposer()));
        foreach (var row in family.Requests)
        {
            result.Add(Box(Stack(12, Columns(Text(row.ChildName, 17, Ink, true), StatusBadge(row.Status)),
                Text($"{Day(row.Deadline)} · {Time(row.Deadline)} · {row.Home.Name}", 14, Muted),
                Button("פתיחת בקשה", async () => await Navigation.PushAsync(
                    row.Status == ReturnStatus.Arrived ? ArrivalPage(row) : DetailPage(row)), false))));
        }
        if (family.Requests.Length == 0) result.Add(Box(Text("בקשות המשפחה יופיעו כאן.", 15, Muted)));
        return result;
    }

    private ContentPage DetailPage(ReturnView row)
    {
        bool parent = family!.Me.Role == FamilyRole.Parent;
        var stack = Stack(13, StatusBadge(row.Status), Text(row.ChildName, 23, Ink, true),
            Box(Stack(8, Text("מועד החזרה", 13, Muted), Text($"{Day(row.Deadline)} · {Time(row.Deadline)}", 24, Ink, true),
                Text($"{row.Home.Name} · רדיוס {row.Home.Radius} מטר", 14, Muted))));
        if (row.Status == ReturnStatus.Pending && !parent)
            stack.Add(Button("אישור שעת החזרה", async () => { await Accept(row); if (Navigation.NavigationStack.Count > 1) await Navigation.PopAsync(); }));
        if (parent && row.Status is ReturnStatus.Pending or ReturnStatus.Accepted)
            stack.Add(Button("שינוי שעת החזרה", () => OpenComposer(row)));
        if (ReturnRules.IsUrgent(row.Status))
            stack.Add(Button("פרטי ההתרעה", async () =>
            {
                await Navigation.PopToRootAsync();
                ShowAlarm(row);
            }, false));
        return FormPage("פרטי בקשה", stack);
    }

    private ContentPage ArrivalPage(ReturnView row)
    {
        var icon = Text("✓", 55, Green, true);
        icon.HorizontalOptions = LayoutOptions.Center;
        return FormPage("הגעה הביתה", Stack(14, Box(Stack(8, icon, Text("ההגעה אומתה", 29, Green, true),
            Text($"{row.ChildName} הגיע/ה לאזור {row.Home.Name}.", 17, Ink),
            Text(row.ArrivedAt is {} timestamp ? $"{Day(timestamp)} · {Time(timestamp)}" : "", 15, Muted),
            Text(row.LateArrival ? "ההגעה הייתה אחרי שעת החזרה שנקבעה." : "הבקשה הושלמה.", 14, Green)), SoftGreen),
            Button("חזרה לבית", async () => await Navigation.PopToRootAsync())));
    }

    private async Task Accept(ReturnView row) => await Run(async () =>
    {
        if (demo is not null) demo.Update(row with { Status = ReturnStatus.Accepted });
        else await api.Send<object>(HttpMethod.Post, $"returns/{row.Id}/accept", new CheckInInput(row.Revision));
        await Refresh(throwFailure: true);
    });

    private async Task OpenComposer(ReturnView? existing = null)
    {
        if (family!.Children.Length == 0) { await OpenPairing(); return; }
        if (family.Home is null) { await OpenHome(); return; }
        var children = family.Children;
        var picker = new Picker { Title = "בחירת ילד/ה", TextColor = Ink, TitleColor = Muted,
            ItemsSource = children.Select(c => c.Name).ToList(), SelectedIndex = existing is null ? 0 : Array.FindIndex(children, x => x.Id == existing.ChildId),
            IsEnabled = existing is null };
        var suggested = existing?.Deadline.ToLocalTime() ?? DateTimeOffset.Now.AddHours(1);
        var date = new DatePicker { MinimumDate = DateTime.Today, MaximumDate = DateTime.Today.AddDays(30),
            Date = suggested.Date, Format = "dd/MM/yyyy", TextColor = Ink };
        var time = new TimePicker { Time = suggested.TimeOfDay, Format = "HH:mm", TextColor = Ink };
        var stack = Stack(13, Text(existing is null ? "מתי חוזרים הביתה?" : "עדכון מועד החזרה", 26, Ink, true),
            Field("ילד/ה", picker), Field("תאריך", date), Field("שעת חזרה", time),
            Box(Text($"אזור הבית · {(existing?.Home ?? family.Home).Name}", 14, Blue), SoftBlue));
        if (demo is not null)
            stack.Add(Button("בחירת מועד בעוד דקה", () => Go(() =>
            {
                var soon = DateTime.Now.AddSeconds(75);
                date.Date = soon.Date; time.Time = soon.TimeOfDay;
            }), false));
        stack.Add(Button(existing is null ? "שליחת בקשת חזרה" : "שמירת השינוי", async () => await Run(async () =>
        {
            if (picker.SelectedIndex < 0 || date.Date is null || time.Time is null)
                throw new InvalidOperationException("יש לבחור ילד/ה, תאריך ושעה.");
            var selectedTime = TimeSpan.FromMinutes(Math.Floor(time.Time.Value.TotalMinutes));
            var localTime = DateTime.SpecifyKind(date.Date.Value.Date + selectedTime, DateTimeKind.Local);
            if (TimeZoneInfo.Local.IsInvalidTime(localTime))
                throw new InvalidOperationException("השעה שנבחרה אינה קיימת ביום זה בגלל מעבר שעון.");
            var deadline = new DateTimeOffset(localTime);
            if (deadline <= DateTimeOffset.Now.AddSeconds(15)) throw new InvalidOperationException("יש לבחור שעת חזרה עתידית.");
            var childId = children[picker.SelectedIndex].Id;
            if (demo is not null)
            {
                var active = demo.View().Requests.FirstOrDefault(r => r.ChildId == childId
                    && r.Status is ReturnStatus.Pending or ReturnStatus.Accepted);
                demo.SaveReturn(childId, deadline, existing ?? active);
            }
            else
            {
                var active = family!.Requests.FirstOrDefault(r => r.ChildId == childId
                    && r.Status is ReturnStatus.Pending or ReturnStatus.Accepted);
                if (existing is null && active is not null)
                {
                    await Notice("יש כבר בקשה פתוחה. אפשר לערוך אותה ממסך פרטי הבקשה.");
                    return;
                }
                await api.Send<object>(HttpMethod.Post, "returns",
                    new ReturnInput(childId, deadline, existing?.Id, existing?.Revision));
            }
            await Navigation.PopToRootAsync();
            await Refresh(throwFailure: true);
        })));
        await Navigation.PushAsync(FormPage("שעת חזרה", stack));
    }

    private ContentPage FormPage(string title, View content) => new()
    {
        Title = title, FlowDirection = FlowDirection.RightToLeft, BackgroundColor = Canvas, Content = Scroll(content)
    };
}
