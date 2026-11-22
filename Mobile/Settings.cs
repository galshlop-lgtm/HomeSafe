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
                Text("שיתוף המיקום מיועד להורה המקושר ולמשך בקשת חזרה. אפשר לכבות אותו בכל עת.", 14, Muted),
                Button(model.SharingEnabled ? "כיבוי שיתוף מיקום" : "הפעלת שיתוף מיקום",
                    () => SetSharing(!model.SharingEnabled)),
                model.SharingEnabled && demo is null
                    ? Button("בדיקת הרשאות מיקום", () => SetSharing(true), false)
                    : Button("חיבור להורה", () => OpenPairing(), false))));
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
                bool consent = await DisplayAlertAsync("שיתוף מיקום עם ההורה",
                    "בזמן בקשת חזרה האפליקציה תבדוק כניסה לאזור הבית, גם כשהמסך כבוי אם המכשיר מאפשר. ניתן לבטל את השיתוף בכל עת.",
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

    private void ShowAlarm(ReturnView? row)
    {
        alarm = row;
        alarmSilenced = false;
        bool parent = family?.Me.Role != FamilyRole.Child;
        string title = testingAlarm ? "בדיקת אזעקה" : row?.Status == ReturnStatus.Overdue
            ? parent ? "ילדך עדיין לא הגיע הביתה" : "שעת החזרה הביתה עברה"
            : offlineAlert?.Title ?? "לא ניתן לאמת הגעה הביתה";
        string helper = row?.Status == ReturnStatus.LocationUnavailable
            ? "המיקום חסר, לא עדכני או לא מדויק מספיק. כדאי ליצור קשר."
            : row is not null ? "המיקום האחרון בזמן החזרה זוהה מחוץ לאזור הבית."
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
            if (parent && row is not null)
                content.Add(Button("מיקום אחרון ידוע", async () => await Run(async () =>
                {
                    await Refresh(rebuild: false, throwFailure: true);
                    if (family?.Requests.FirstOrDefault(r => r.Id == row.Id)?.LastKnown is not {} latest)
                        throw new InvalidOperationException("אין מיקום משותף זמין.");
                    await Map.Default.OpenAsync(latest.Latitude, latest.Longitude, new MapLaunchOptions
                    {
                        Name = $"מיקום אחרון · {Time(latest.ObservedAt)}", NavigationMode = NavigationMode.None
                    });
                }), false, Paper));
            if (row is { PushAccepted: false } && demo is null)
                content.Add(Text("טרם התקבל אישור מספק ההתראות עבור המכשיר הזה.", 13, Muted));
        }
        root.Add(Scroll(content), 0, 1);
        if (Navigation.NavigationStack.Count > 1) _ = Navigation.PopToRootAsync();
        if (testingAlarm || row is not { Acknowledged: true }) DeviceBridge.StartSound();
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

