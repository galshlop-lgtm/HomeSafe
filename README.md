# HomeSafe

HomeSafe is a Hebrew, right-to-left mobile app built in C# with .NET MAUI for Android and iOS. The ASP.NET Core API handles family accounts, pairing, shared return requests, arrival verification, and deadline escalation.

## Try the demo

Install the debug APK on a 64-bit Android 8.0 or newer device, or run the MAUI project from your IDE. From the welcome screen, choose **ניסיון במצב הדגמה**, then **המשך כהורה**. The dashboard opens with Daniel's upcoming return and Maya's verified arrival.

- **קביעת שעת חזרה** opens the date and time picker. The demo can set the deadline one minute ahead.
- **הגדרות → מעבר לחשבון ילד/ה** shows the same request from Daniel's account.
- **הגדרות → בדיקת אזעקה** plays the app's alarm locally. Use **סיום הבדיקה** to stop it.
- **הגדרות → דימוי הגעה לבית** records a simulated arrival. The demo resets when you exit demo mode or restart the app.
- The home editor uses OpenStreetMap tiles over the internet. The radius and pin can be edited independently of the server.

## Run directly in a Windows virtual machine

Install Visual Studio with the **.NET Multi-platform App UI development** workload inside a Windows 10 (version 2004 or newer) or Windows 11 VM. Open `HomeSafe.slnx`, set `HomeSafe.Mobile` as the startup project, choose **Windows Machine**, and press **F5**. The Windows build is an unpackaged desktop app. Demo mode opens from **ניסיון במצב הדגמה**.

To sign in, run the **HomeSafe Local API** profile inside the Windows VM. The Debug app suggests `http://127.0.0.1:5080`. Keep the window active for live status updates and alarms. Windows location depends on the guest's location services and hardware. Mobile push delivery and background alarm scheduling are available on the mobile targets.

## Run in the Android emulator from Visual Studio

1. In Visual Studio Installer, install the **.NET Multi-platform App UI development** workload and the Android emulator. Open `HomeSafe.slnx` and set **HomeSafe.Mobile** as the startup project.
2. Open **Tools → Android → Android Device Manager**. Create or start a virtual device, for example a Pixel with an API 35 or 36 image. Use an x86_64 image on Intel or AMD, or an arm64 image on an Arm host.
3. In Visual Studio, select **Debug** and the running Android emulator, then press **F5**. Choose **ניסיון במצב הדגמה** to use the app immediately.

To test family accounts on the emulator, start the **HomeSafe Local API** launch profile in Visual Studio or run the following from the solution directory:

```sh
dotnet run --project Server/HomeSafe.Server.csproj --launch-profile "HomeSafe Local API"
```

The API listens on `http://127.0.0.1:5080`. Android maps its host's loopback address to `10.0.2.2`, so the Debug account form suggests `http://10.0.2.2:5080` on a virtual device. Run the API on the same Windows machine as the Android emulator. A device with a previously saved server address keeps that address, which can be edited in the account form.

If Visual Studio itself runs inside a Windows VM, the host hypervisor must expose nested virtualization for an accelerated Android emulator. Enable **Windows Hypervisor Platform** in the Windows guest, then confirm acceleration with `emulator -accel-check` from the Android SDK.

## Build and connect two devices

Install the .NET 10 SDK and platform workload. Android needs JDK 21 and the Android SDK for API 36. An iOS build needs a Mac, compatible Xcode, and the MAUI iOS workload. The solution entry point is `HomeSafe.slnx`.

```sh
dotnet workload install maui-android
dotnet build Mobile/HomeSafe.Mobile.csproj -f net10.0-android -c Debug
dotnet run --project Server/HomeSafe.Server.csproj --launch-profile "HomeSafe Local API"
```

Run the server before creating accounts. An Android emulator can reach that development listener at `http://10.0.2.2:5080`. On two phones, point both to the same reachable HTTPS server. On iOS, use `dotnet workload install maui-ios` on the Mac, then build with `-f net10.0-ios` and the relevant signing settings. If Apple Push Notifications are configured, also pass `-p:HomeSafePush=true`.

On the parent phone, register, choose the family home and its 50–300 metre radius, and generate an invitation. On the child phone, create a child account, enter the one-time invitation, and enable precise location sharing. On each phone enable notifications in **הגדרות**. Set a future time on the parent phone and open and accept the request on the child phone. For Android, open the child app after a new request so it can start the visible foreground location service.

## Push configuration

Deploy the API as a single HTTPS instance with its data file on a persistent, access-restricted volume. Set `HomeSafe__DataFile` to the path on that volume. The default is `data/homesafe.json` under the API's working directory. The state includes account information, requests, the latest permitted fix for an open request, and a persistent push queue. Provision secrets outside the app and source archive.

| Platform | App configuration | Server environment variables |
|---|---|---|
| Android | Register the app ID with Firebase and put its `google-services.json` in `Mobile/` before building. | `Push__FcmProject`, `Push__FcmKeyFile` |
| iOS | Enable Push Notifications and Time Sensitive Notifications for the bundle ID. Build with `-p:HomeSafePush=true`, then sign with the matching profile. | `Push__ApnsKeyFile`, `Push__ApnsKeyId`, `Push__ApnsTeamId`, `Push__ApnsTopic`, `Push__ApnsSandbox` |

The included APNs entitlements use the development environment. For App Store distribution, use the production APNs entitlement and endpoint. Apple Critical Alerts need Apple approval and explicit permission on each phone. After both are available, use the critical entitlements with `-p:HomeSafeCriticalAlerts=true` and set `Push__ApnsCritical=true`. The app identifier defaults to `org.homesafe.mobile`. Use your own registered identifier when preparing a distribution build.

## How arrival and alarms work

- The server closes a request at its first accurately verified entry inside the home radius. It saves the home's coordinates and radius with each request. An update to the family home applies to future requests.
- At the deadline, the server uses a fix sampled within three minutes. A fix must be accurate within at most 50 metres and within half of the selected radius. The estimated accuracy circle must fit inside the home boundary to verify arrival, or be wholly outside to verify a missed return. Missing, stale, or ambiguous GPS produces **מיקום לא זמין**. A later confirmed entry is recorded as a late arrival if it was sampled after the deadline.
- The API queues urgent alerts for both accounts. It retries provider failures and resends at roughly two and four minutes to each account that has yet to acknowledge. A provider's acceptance confirms a submission. Audible delivery depends on the device and its settings.
- Android uses a high-importance alarm channel, alarm audio, repeating sound and vibration while the alarm screen is open, and a lock-screen full-screen intent where enabled. Known deadlines have a local check scheduled with AlarmManager. Android can delay that check when exact-alarm permission is unavailable. The user can review exact-alarm and full-screen permissions in the app.
- iOS uses time-sensitive notifications with a bundled alarm sound. Critical delivery follows Apple's entitlement and user-permission rules. While the alarm screen is open, the app can repeat the sound. A local fallback asks the user to verify arrival for a previously synced deadline if it has not been cleared. iOS can restrict background work, including cancellation of that reminder when the device is offline.
- On both platforms, locked-screen delivery depends on operating-system policy, settings, and network availability. A known deadline can trigger a local verification prompt when the server is unreachable. Automatic push requires connectivity.
- Location sharing requires explicit consent. The server keeps at most the latest fix for a request. It clears the fix on verified arrival or successful revocation and expires it after 24 hours. Monitoring can continue for up to two hours after the deadline to record a late arrival. The Android location-service notification or the iOS background location indicator shows when tracking is active. Turning off sharing stops tracking locally and queues server-side revocation if the connection is down.

The source includes a single-instance pilot backend and one push device registration per account. Running it for real families requires configured push credentials, HTTPS hosting, and a two-device check with the phones locked, backgrounded, and offline. For a public release, use a map tile service with capacity and terms appropriate to your deployment. The included map is built with Leaflet, whose license is bundled.
