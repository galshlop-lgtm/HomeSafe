using HomeSafe.Shared;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace HomeSafe.Mobile;

internal static partial class DeviceBridge
{
    internal static string PlatformName => "windows";
    private static ReturnView? monitoring;
    private static DateTimeOffset nextFix;
    private static bool findingLocation;
    private static MediaPlayer? player;
    private static CancellationTokenSource? sound;

    private static Task<string?> AskForNotifications() =>
        throw new InvalidOperationException("ב־Windows התרעות מופיעות בתוך חלון האפליקציה כשהוא פעיל.");

    internal static async Task<bool> RequestLocation() =>
        await Permissions.RequestAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted;

    private static bool Monitor(ReturnView request)
    {
        if (monitoring?.Id != request.Id) nextFix = DateTimeOffset.MinValue;
        monitoring = request;
        if (Active && !findingLocation && DateTimeOffset.UtcNow >= nextFix) _ = FetchFix(request);
        return Active;
    }

    private static async Task FetchFix(ReturnView request)
    {
        findingLocation = true;
        nextFix = DateTimeOffset.UtcNow.AddSeconds(30);
        try
        {
            var location = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(10)));
            if (Active && monitoring?.Id == request.Id && location?.Accuracy is double accuracy)
                await Report(request.Id, new LocationFix(location.Latitude, location.Longitude, accuracy, location.Timestamp));
        }
        catch (FeatureNotSupportedException) { }
        catch (FeatureNotEnabledException) { }
        catch (PermissionException) { }
        finally { findingLocation = false; }
    }

    private static void StopTracking() => monitoring = null;

    private static void SyncDeadlineFallback(ReturnView[] items) { }
    internal static void ClearAlert(Guid id) { }
    private static void ClearAll() { }

    internal static async void StartSound()
    {
        StopSound();
        using var playback = new CancellationTokenSource();
        sound = playback;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "alarm.wav"));
            playback.Token.ThrowIfCancellationRequested();
            player = new MediaPlayer { Source = MediaSource.CreateFromStorageFile(file), IsLoopingEnabled = true };
            player.Play();
            await Task.Delay(TimeSpan.FromSeconds(90), playback.Token);
        }
        catch (Exception) { }
        finally
        {
            if (ReferenceEquals(sound, playback)) StopSound();
        }
    }

    internal static void StopSound()
    {
        sound?.Cancel();
        sound = null;
        player?.Dispose();
        player = null;
    }
}
