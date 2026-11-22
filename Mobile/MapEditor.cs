using System.Globalization;
using HomeSafe.Shared;
using Microsoft.Maui.Storage;
using static HomeSafe.Mobile.Design;

namespace HomeSafe.Mobile;

internal sealed partial class HomePage
{
    private async Task OpenHome() => await Run(async () =>
    {
        var current = family!.Home ?? new HomeArea("בית המשפחה", 32.08091, 34.78057, 100);
        double lat = current.Latitude, lon = current.Longitude;
        bool selected = demo is not null || family.Home is not null;
        var name = Entry("בית המשפחה", current.Name);
        var radius = new Slider(50, 300, current.Radius)
        {
            MinimumTrackColor = Blue, MaximumTrackColor = Line, ThumbColor = Blue
        };
        var radiusLabel = Text($"רדיוס · {current.Radius} מטר", 15, Blue, true);
        var status = Text("אפשר לבחור נקודה במפה או להשתמש במיקום המכשיר.", 14, Muted);
        var map = new WebView { HeightRequest = 302, FlowDirection = FlowDirection.LeftToRight };
        var template = await ReadAsset("home-map.html");
        map.Navigating += (_, e) =>
        {
            if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri)) return;
            if (uri.Host == "homesafe.invalid" && uri.AbsolutePath == "/point")
            {
                e.Cancel = true;
                var parts = uri.Query.TrimStart('?').Split('&');
                if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a)
                    || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b)
                    || !ReturnRules.ValidCoordinates(a, b)) return;
                selected = true;
                lat = a;
                lon = b;
                status.Text = "נקודה נבחרה במפה.";
            }
            else if (uri.Host == "homesafe.invalid" && uri.AbsolutePath == "/tiles-unavailable")
            {
                e.Cancel = true;
                status.Text = "המפה כרגע אינה זמינה. ניתן לבחור במיקום המכשיר.";
            }
            else if (uri.Host == "www.openstreetmap.org")
            {
                e.Cancel = true;
                _ = Launcher.Default.OpenAsync(uri);
            }
            else if (uri.Scheme is "http" or "https" && uri.Host != "homesafe.invalid")
                e.Cancel = true;
        };
        string html = template.Replace("/*LEAFLET_CSS*/", await ReadAsset("leaflet/leaflet.css"))
            .Replace("/*LEAFLET_JS*/", await ReadAsset("leaflet/leaflet.js"))
            .Replace("/*POSITION*/", FormattableString.Invariant($"{lat},{lon},{radius.Value}"));
        map.Source = new HtmlWebViewSource { Html = html, BaseUrl = "https://homesafe.invalid/" };
        radius.ValueChanged += async (_, _) =>
        {
            radiusLabel.Text = $"רדיוס · {(int)Math.Round(radius.Value)} מטר";
            try { await map.EvaluateJavaScriptAsync(FormattableString.Invariant($"setRadius({radius.Value})")); }
            catch { }
        };
        var content = Stack(12, Text("איפה הבית?", 28, Ink, true), status, Box(map, Paper, Line, 16, 0),
            Field("שם המקום", name), Box(Stack(6, radiusLabel, radius)),
            Button("בחירה לפי המיקום הנוכחי", async () => await Run(async () =>
            {
                var location = await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(15)));
                if (location is null) throw new InvalidOperationException("לא ניתן לקבל מיקום מהמכשיר.");
                selected = true;
                lat = location.Latitude;
                lon = location.Longitude;
                await map.EvaluateJavaScriptAsync(FormattableString.Invariant($"selectPoint({lat},{lon},true)"));
                status.Text = "נקודה נקבעה לפי מיקום המכשיר.";
            }), false),
            Button("שמירת אזור הבית", async () => await Run(async () =>
            {
                if (!selected) throw new InvalidOperationException("יש לבחור במפה את מיקום הבית.");
                var label = name.Text?.Trim() ?? "";
                if (label.Length is < 1 or > 60) throw new InvalidOperationException("יש להזין שם למקום.");
                var area = new HomeArea(label, lat, lon, (int)Math.Round(radius.Value));
                if (demo is not null) demo.SaveHome(area);
                else await api.Send<object>(HttpMethod.Put, "home", area);
                await Navigation.PopAsync();
                await Refresh(throwFailure: true);
                Render();
            })));
        await Navigation.PushAsync(FormPage("מיקום הבית", content));
    });

    private static async Task<string> ReadAsset(string name)
    {
        await using var stream = await FileSystem.Current.OpenAppPackageFileAsync(name);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
