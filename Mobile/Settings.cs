using Microsoft.Maui.ApplicationModel.DataTransfer;
using HomeSafe.Shared;
using static HomeSafe.Mobile.Design;

namespace HomeSafe.Mobile;

internal sealed partial class HomePage
{
    private View Settings()
    {
        var model = family!;
        bool parent = model.Me.Role == FamilyRole.Parent;
        var phone = Entry("מספר טלפון", model.Me.Phone, Keyboard.Telephone);
        phone.FlowDirection = FlowDirection.LeftToRight;
        var layout = Stack(14, Text("הגדרות", 30, Ink, true),
            Box(Stack(8, Text(model.Me.Name, 19, Ink, true), Text(parent ? "חשבון הורה" : "חשבון ילד/ה", 14, Muted))));
        if (demo is not null)
        {
            layout.Add(Box(Stack(9, Text("מצב הדגמה", 18, Blue, true),
                Text("נתוני משפחה מקומיים. אפשר לבדוק את מסך ההתרעה ולדמות הגעה, בלי לשלוח הודעות למכשיר אחר.", 14, Muted),
                Button(parent ? "מעבר לחשבון ילד/ה" : "מעבר לחשבון הורה", async () =>
                {
                    demo.Role = parent ? FamilyRole.Child : FamilyRole.Parent;
                    snoozed = null;
                    section = "dashboard";
                    await Refresh();
                    Render();
                }, false),
                Button("בדיקת אזעקה", () => Go(() =>
                {
                    testingAlarm = true;
                    ShowAlarm(demo.TestAlarm());
                }), false),
                Button("דימוי הגעה לבית", async () =>
                {
                    if (family?.Requests.FirstOrDefault(r => ReturnRules.NeedsMonitoring(r.Status)) is not {} current)
                    {
                        await Notice("אין בקשת חזרה פתוחה.");
                        return;
                    }
                    var arrived = current with { ArrivedAt = DateTimeOffset.UtcNow,
                        LateArrival = DateTimeOffset.UtcNow > current.Deadline, Status = ReturnStatus.Arrived };
                    demo.Update(arrived);
                    await Refresh();
                    await Navigation.PushAsync(ArrivalPage(arrived));
                }, false)), SoftBlue));
        }
        else
        {
            layout.Add(Box(Stack(9, Text("התראות ואזעקות", 19, Ink, true),
#if WINDOWS
                Text("התרעות מוצגות בחלון הפעיל של HomeSafe.", 14, Muted),
#else
                Text(model.PushConfigured && model.DeviceRegistered ? "המכשיר רשום אצל שירות ההתראות."
                    : "יש להפעיל הרשאות ולוודא ששירות ההתראות מחובר לשרת.", 14, Muted),
                Button("הפעלת התראות", async () => await Run(async () =>
                {
                    await DeviceBridge.RequestNotifications(api);
                    await Refresh(throwFailure: true);
                    Render();
                    await Notice("אפשר לבדוק הרשאות צליל ומסך נעילה בהגדרות המכשיר.");
                })),
#endif
                Button("בדיקת צליל אזעקה", () => Go(() =>
                {
                    testingAlarm = true;
                    var request = model.Requests.FirstOrDefault();
                    ShowAlarm(new ReturnView(Guid.NewGuid(), request?.ChildId ?? Guid.Empty, request?.ChildName ?? "בדיקה",
                        DateTimeOffset.Now.AddMinutes(-1), model.Home ?? new("בית המשפחה", 0, 0, 100), ReturnStatus.Overdue, 1,
                        null, false, false, null, false));
                }), false),
#if ANDROID
                Button("הגדרות אזעקה מדויקת", () => Go(DeviceBridge.OpenExactSettings), false),
                Button("הגדרות התראה במסך נעילה", () => Go(DeviceBridge.OpenFullScreenSettings), false),
#endif
                Button("הגדרות המכשיר", () => Go(AppInfo.ShowSettingsUI), false))));
            layout.Add(Box(Stack(9, Text("שיחה מהירה", 19, Ink, true), Field("טלפון", phone),
                Button("שמירת טלפון", async () => await Run(async () =>
                {
                    await api.Send<object>(HttpMethod.Put, "settings/phone", new PhoneInput(phone.Text));
                    await Refresh(throwFailure: true);
                    Render();
                }), false))));
        }
        if (parent)
        {
            layout.Add(Box(Stack(10, Text("בית המשפחה", 19, Ink, true),
                Text(model.Home is {} area ? $"{area.Name} · {area.Radius} מטר" : "בחירת נקודה ורדיוס במפה.", 14, Muted),
                Button("הגדרת מיקום הבית", () => OpenHome(), false),
                Button("חיבור ילד/ה למשפחה", () => OpenPairing(), false))));
            var reminders = Entry("30, 10", string.Join(", ", model.ReminderMinutes));
            reminders.FlowDirection = FlowDirection.LeftToRight;
            layout.Add(Box(Stack(8, Text("תזכורות", 19, Ink, true),
                Text("הזינו עד ארבעה זמנים בדקות לפני מועד החזרה, מופרדים בפסיק.", 14, Muted),
                Field("דקות לפני המועד", reminders),
                Button("שמירת תזכורות", async () => await Run(async () =>
                {
                    var parts = (reminders.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var minutes = new List<int>();
                    foreach (string part in parts)
                    {
                        if (!int.TryParse(part, out int value) || value is < 1 or > 180 || minutes.Count == 4)
                            throw new InvalidOperationException("אפשר להגדיר עד ארבע תזכורות, בין דקה ל־180 דקות מראש.");
                        minutes.Add(value);
                    }
                    var values = minutes.Distinct().OrderDescending().ToArray();
                    if (demo is not null) demo.SetReminders(values);
                    else await api.Send<object>(HttpMethod.Put, "settings/reminders", new RemindersInput(values));
                    await Refresh(throwFailure: true);
                    Render();
                }), false))));
        }
        else
        {
            layout.Add(Box(Stack(9, Text("פרטיות ומיקום", 19, Ink, true),
                Pill((demo is not null ? model.SharingEnabled : DeviceBridge.Tracking) ? "ניטור פעיל" : "ניטור אינו פעיל", Blue, SoftBlue),
                Text("בדיקת ההגעה מתבצעת במכשיר. אפשר לבטל אותה בכל עת.", 14, Muted),
                Button(model.SharingEnabled ? "כיבוי בדיקת הגעה" : "הפעלת בדיקת הגעה",
                    () => SetSharing(!model.SharingEnabled)),
                model.SharingEnabled && demo is null
                    ? Button("בדיקת הרשאות מיקום", () => SetSharing(true), false)
                    : Button("חיבור להורה", () => OpenPairing(), false))));
        }
        if (demo is null)
        {
            var privacy = model.Privacy ?? new PrivacySettings(false, false, false, false);
            var grace = new Switch { IsToggled = privacy.SmartGrace };
            var battery = new Switch { IsToggled = privacy.LowBatteryToParent };
            var automatic = new Switch { IsToggled = privacy.AutoApproveExtensions };
            var emergency = new Switch { IsToggled = privacy.EmergencyLocation && DeviceBridge.EmergencyConsent };
            var options = Stack(9, Text("עדכונים ופרטיות", 19, Ink, true));
            if (parent)
            {
                options.Add(Columns(Text("השהיית התרעה בהתקרבות מאומתת לבית"), grace));
                options.Add(Columns(Text("עדכון הורה על סוללה חלשה"), battery));
                options.Add(Columns(Text("אישור אוטומטי של בקשות הארכה"), automatic));
            }
            else
                options.Add(Columns(Text("אפשר להורה לבקש מיקום לזמן מוגבל במקרה של איחור מאומת"), emergency));
            options.Add(Button("שמירת הגדרות", () => Run(async () =>
            {
                if (!parent && !emergency.IsToggled) DeviceBridge.SetEmergencyConsent(false);
                await api.Send<object>(HttpMethod.Put, "settings/privacy", new PrivacySettings(
                    grace.IsToggled, emergency.IsToggled, battery.IsToggled, automatic.IsToggled));
                if (!parent) DeviceBridge.SetEmergencyConsent(emergency.IsToggled);
                await Refresh(throwFailure: true);
                Render();
            }), false));
            layout.Add(Box(options));
        }
        layout.Add(Button(demo is not null ? "יציאה מהדגמה" : "התנתקות", async () => await Run(async () =>
        {
            if (demo is null)
            {
                if (DeviceBridge.Registration is {} registration)
                    await api.Send<object>(HttpMethod.Delete, "devices", registration);
                await api.Send<object>(HttpMethod.Post, "auth/logout");
                DeviceBridge.Reset();
                api.Forget();
            }
            demo = null;
            family = null;
            snoozed = null;
            Welcome();
        }), false));
        return layout;
    }

    private async Task SetSharing(bool enabled) => await Run(async () =>
    {
        if (demo is not null) demo.SetSharing(enabled);
        else
        {
            if (enabled && family?.Parent is null)
                throw new InvalidOperationException("יש לחבר חשבון הורה לפני הפעלת שיתוף.");
            if (enabled)
            {
                bool consent = await DisplayAlertAsync("בדיקת הגעה במכשיר",
                    "בזמן בקשת חזרה האפליקציה תבדוק כניסה לאזור הבית, גם כשהמסך כבוי אם המכשיר מאפשר. רק סטטוס ההגעה נשלח בשגרה. אפשר לכבות את הבדיקה בכל עת.",
                    "מאשר/ת", "ביטול");
                if (!consent) return;
                if (!await DeviceBridge.RequestLocation())
                    throw new InvalidOperationException("כדי להפעיל שיתוף, יש לאפשר גישה למיקום מדויק.");
            }
            else DeviceBridge.RevokeLocalTracking();
            await api.Send<object>(HttpMethod.Put, "settings/sharing", new SharingInput(enabled));
            DeviceBridge.SetLocalConsent(enabled);
        }
        await Refresh(throwFailure: true);
        Render();
    });

    private async Task OpenPairing()
    {
        bool parent = family!.Me.Role == FamilyRole.Parent;
        var stack = Stack(13, Text(parent ? "מחברים את המשפחה" : "חיבור להורה", 26, Ink, true));
        if (parent)
        {
            var token = Text("—", 29, Blue, true);
            token.FlowDirection = FlowDirection.LeftToRight;
            var expires = Text("הקוד חד־פעמי ותקף לחצי שעה.", 14, Muted);
            stack.Add(Box(Stack(10, token, expires,
                Button("יצירת קוד הזמנה", async () => await Run(async () =>
                {
                    var invitation = demo is not null ? new Invitation("A7219CF80D", DateTimeOffset.Now.AddMinutes(30))
                        : await api.Send<Invitation>(HttpMethod.Post, "pair/invite");
                    token.Text = invitation.Code;
                    expires.Text = $"הקוד בתוקף עד {Time(invitation.ExpiresAt)} וניתן לשימוש פעם אחת.";
                })),
                Button("העתקת קוד", async () =>
                {
                    if (token.Text != "—") await Clipboard.Default.SetTextAsync(token.Text);
                }, false))));
        }
        else if (family.Parent is {} linked)
            stack.Add(Box(Text($"החשבון מקושר ל־{linked.Name}.", 16, Green), SoftGreen));
        else
        {
            var entry = Entry("קוד מההורה");
            entry.FlowDirection = FlowDirection.LeftToRight;
            stack.Add(Field("קוד הזמנה", entry));
            stack.Add(Button("חיבור למשפחה", async () => await Run(async () =>
            {
                await api.Send<object>(HttpMethod.Post, "pair/join", new PairInput(entry.Text ?? ""));
                await Navigation.PopToRootAsync();
                await Refresh(throwFailure: true);
            })));
        }
        await Navigation.PushAsync(FormPage("חיבור משפחתי", stack));
    }

    private void ShowAlarm(ReturnView? row, bool refreshOnly = false)
    {
        alarm = row;
        if (!refreshOnly) alarmSilenced = false;
        bool parent = family?.Me.Role != FamilyRole.Child;
        string title = testingAlarm ? "בדיקת אזעקה" : row?.Status == ReturnStatus.Overdue
            ? parent ? "🚨 הילד שלך עדיין לא הגיע הביתה!" : "שעת החזרה הביתה עברה"
            : offlineAlert?.Title ?? "לא ניתן לאמת הגעה הביתה";
        string helper = row?.Status == ReturnStatus.LocationUnavailable
            ? "המיקום חסר, לא עדכני או לא מדויק מספיק. כדאי ליצור קשר."
            : row is not null ? "המכשיר זוהה מחוץ לאזור הבית לאחר שעת החזרה."
            : offlineAlert?.Body ?? "פתחו את האפליקציה כדי לבדוק את מצב החזרה.";
        if (testingAlarm) helper = "הצליל והרטט פועלים במכשיר הזה בלבד.";
        root.Children.Clear();
        root.BackgroundColor = SoftRed;
        clocks.Clear();
        root.Add(Header("התרעה"), 0, 0);
        var content = Stack(16, Pill(testingAlarm ? "בדיקה מקומית" : "נדרשת תשומת לב", Red, Paper),
            Text(title, 35, Red, true), Text(helper, 16, Ink));
        if (row is not null && !testingAlarm)
            content.Add(Box(Stack(6, Text(row.ChildName, 24, Ink, true),
                Text($"מועד החזרה · {Day(row.Deadline)} · {Time(row.Deadline)}", 14, Muted))));
        else if (offlineAlert is not null)
            content.Add(Text("אפשר לאשר את ההתרעה לאחר חידוש החיבור.", 14, Muted));
        content.Add(Button(testingAlarm ? "סיום הבדיקה" : "אישור ההתרעה", async () =>
        {
            DeviceBridge.StopSound();
            if (testingAlarm) { CloseAlarm(); return; }
            var id = row?.Id ?? offlineAlert?.RequestId;
            if (id is null) { CloseAlarm(); return; }
            snoozed = id;
            try
            {
                if (demo is not null && row is not null) demo.Update(row with { Acknowledged = true });
                else await api.Send<object>(HttpMethod.Post, $"returns/{id}/acknowledge");
                CloseAlarm();
                await Refresh();
            }
            catch (Exception ex)
            {
                CloseAlarm();
                await Notice(ex.Message + " אישור ההתרעה עדיין לא נשלח.");
            }
        }, true, Red));
        content.Add(Button("השתקת צליל במכשיר", () => Go(() =>
        {
            alarmSilenced = true;
            DeviceBridge.StopSound();
        }), false, Paper));
        if (!testingAlarm)
        {
            string? number = parent && row is not null ? family?.Children.FirstOrDefault(c => c.Id == row.ChildId)?.Phone
                : family?.Parent?.Phone;
            if (!string.IsNullOrWhiteSpace(number))
                content.Add(Button(parent ? "התקשרות לילד/ה" : "התקשרות להורה", async () => await Run(() =>
                {
                    PhoneDialer.Default.Open(number);
                    return Task.CompletedTask;
                }), false, Paper));
            if (row is not null && demo is null) content.Add(ConnectionStatus(row));
            if (!parent && row?.EmergencyLocationRequested == true)
                content.Add(Text("ההורה ביקש מיקום חירום. השיתוף זמני ואפשר לכבות אותו בהגדרות.", 15, Red));
            if (parent && row is { Status: ReturnStatus.Overdue, Acknowledged: false } && demo is null)
                content.Add(Button("בקשה או צפייה במיקום חירום", () => Run(async () =>
                {
                    await Refresh(rebuild: false, throwFailure: true);
                    var current = family?.Requests.FirstOrDefault(r => r.Id == row.Id);
                    if (current?.LastKnown is not {} latest)
                    {
                        await api.Send<object>(HttpMethod.Post, $"returns/{row.Id}/emergency-location");
                        await Notice("הבקשה פעילה. כשהמכשיר ישלח מיקום אפשר ללחוץ שוב לצפייה.");
                        return;
                    }
                    var location = FormattableString.Invariant($"{latest.Latitude},{latest.Longitude}");
                    var title = $"מיקום חירום · {Time(latest.ObservedAt)} · דיוק {latest.Accuracy:0} מטר";
                    var map = new WebView { HeightRequest = 350 };
                    string template = await ReadAsset("home-map.html");
                    map.Navigating += (_, e) =>
                    {
                        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                            e.Cancel = true;
                    };
                    map.Source = new HtmlWebViewSource
                    {
                        Html = template.Replace("/*POSITION*/", location + "," + latest.Accuracy.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        BaseUrl = "https://homesafe.invalid/"
                    };
                    var page = FormPage(title, Stack(9, Text(title, 16, Ink, true), map));
                    emergencyPage = page;
                    emergencyRequest = row.Id;
                    page.Disappearing += (_, _) =>
                    {
                        emergencyPage = null;
                        emergencyRequest = null;
                    };
                    await Navigation.PushAsync(page);
                }), false, Paper));
            if (row is not null && demo is null)
                content.Add(Text(row.DeviceConfirmedPush ? "המכשיר אישר שקיבל את ההתראה."
                    : row.PushAccepted ? "ספק ההתראות קיבל את השליחה. עדיין אין אישור מהמכשיר."
                    : "עדיין אין אישור מספק ההתראות.", 13, Muted));
        }
        root.Add(Scroll(content), 0, 1);
        if (!refreshOnly && Navigation.NavigationStack.Count > 1) _ = Navigation.PopToRootAsync();
        if (!refreshOnly && (testingAlarm || row is not { Acknowledged: true })) DeviceBridge.StartSound();
    }

    protected override bool OnBackButtonPressed()
    {
        if (!AlarmIsVisible && !testingAlarm) return base.OnBackButtonPressed();
        if (testingAlarm) CloseAlarm();
        else if (!alarmSilenced)
        {
            DeviceBridge.StopSound();
            alarmSilenced = true;
        }
        return true;
    }
}

