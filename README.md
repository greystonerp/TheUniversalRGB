# Universal RGB Control

A standalone Windows app that detects RGB hardware, explains what is actually controllable (and why not), diagnoses missing requirements, and drives lighting through one consistent interface. It is built from `UniversalRGB_Development_Plan.html`.

> One consistent RGB control interface over tested hardware backends — not an imaginary universal driver.

## Install it

Double-click **`dist\UniversalRGB-Setup.exe`**. That one file is the whole product, and it installs itself:

1. A short setup screen asks whether Universal RGB should start with Windows (quietly in the tray, or as a window) and whether to add a desktop shortcut. It then installs for your Windows account to `%LOCALAPPDATA%\Programs\UniversalRGB`. No administrator rights are needed.
2. It adds Start menu and desktop shortcuts and an entry in **Settings → Apps**.
3. It opens the installed app. From then on, use the shortcuts.

Running a newer `UniversalRGB-Setup.exe` later updates the installed copy in place. It closes the running copy first, keeps your desktop-shortcut choice, and keeps all settings and profiles. Running the same or an older version just opens what's installed.

To uninstall, use **Settings → Apps → Universal RGB Control**, or **Settings → About → Uninstall…** inside the app. This removes the program, its shortcuts and its startup entry. Settings, profiles and logs stay in your user folder, and no drivers are touched.

To run without installing, start the EXE with `--portable`.

### Automatic updates

The installed app checks [GitHub Releases](https://github.com/greystonerp/UniversalRGB/releases) 20 seconds after it starts, then every 6 hours. When there's a newer version:

1. It downloads `UniversalRGB-Setup.exe` together with its `.sig` signature file.
2. It checks the file against the SHA-256 that GitHub publishes.
3. It verifies the signature against the release public key built into the app (ECDSA P-256).
4. Only if both checks pass does it run the setup, which closes the app, replaces it, and reopens it with an "updated" message.

If anything fails to verify, the download is deleted and nothing is installed.

You can turn this off with **Settings → Updates → Install updates automatically**, and check by hand there with **Check now**.

Copies installed before v0.6.0 have no updater yet. Install v0.6.0 once by hand on each PC; after that, updates are automatic.

You need **Windows 10/11 x64**. Nothing else has to be installed: the EXE is self-contained.

### Requirements set themselves up

Universal RGB controls lighting through OpenRGB's SDK server. With **Settings → Set up OpenRGB automatically** on (the default), every launch makes sure the backend is ready:

1. **Install.** If OpenRGB is missing, the app downloads the official 1.1 portable release from Codeberg (URL in `manifests/drivers.json`).
   - The download is accepted only if it matches the pinned SHA-256. OpenRGB isn't code-signed upstream, so the hash is the integrity check.
   - It is unpacked to `%LOCALAPPDATA%\Programs\OpenRGB`. No administrator rights are needed.
2. **Start.** It starts OpenRGB's SDK server in the background (`OpenRGB.exe --server`: no window, no tray icon, no pop-ups) and connects.
3. **Keep running.** If the server stops, it's restarted, at most every two minutes. If you have your own OpenRGB window open, it is left alone.

**PawnIO** is the kernel driver needed for SMBus lighting: RGB memory, and some motherboards and GPUs.
- **Install PawnIO…** on the Drivers page downloads the official signed installer and checks both the pinned SHA-256 and the signer (`namazso.eu`).
- It then shows a consent dialog, followed by the Windows admin prompt. Kernel drivers are never installed silently.
- After installing it, turn on **Settings → Start OpenRGB as administrator** so OpenRGB can use it.

You can also press **Install missing** on the Drivers page at any time.

**Why something shows as not controllable.** Universal RGB reads OpenRGB's own detection log (`%APPDATA%\OpenRGB\logs`) and shows the real reason on each device:

- **SMBus blocked.** RGB memory (for example Corsair Vengeance RGB `CMH…`) is hidden when OpenRGB runs without administrator rights, because it then can't open PawnIO. When the app sees RGB memory affected this way, it offers once to **Run OpenRGB as administrator**. Windows then asks each time OpenRGB starts, and the choice is remembered.
- **Board lighting controller.** Each board is shown together with its own USB lighting controller (MSI Mystic Light `1462:7Cxx`, ASUS Aura, Gigabyte RGB Fusion 2, ASRock Polychrome). The app explains which of these happened:
  - The controller's LED firmware is too old (for example MSI MPG B550 GAMING PLUS below firmware 6). Update it with MSI Center → Live Update → Advanced scan → LED Firmware; this is separate from the BIOS. Then restart and press **Restart OpenRGB**.
  - It didn't answer OpenRGB. MSI Center / Mystic Light is usually holding it: close it, then press **Restart OpenRGB**.
  - OpenRGB has no lighting layout for that board.

After the first connection:

- **Devices** lists every controller. ARGB headers with no LED count set stay dark until you enter one under **Zones**, so put in the number of LEDs on the fans or strip attached.
- Pick effects in **Lighting Studio**, then save or schedule them in **Profiles**.

### Command-line options

| Option | Effect |
|---|---|
| `--page devices` | Open a page: `dashboard`, `devices`, `lighting`, `profiles`, `drivers` or `settings` |
| `--profile Gaming` | Apply a profile by name at launch |
| `--startup` | Used by the "Start with Windows" entry: start minimized to the tray if enabled |
| `--portable` | Run the EXE where it is, without installing |
| `--uninstall` (`--quiet`) | Uninstall (silently with `--quiet`); this is what Settings → Apps runs |
| `UNIVERSALRGB_HOME=<folder>` (env var) | Keep all settings, profiles, database and logs in one folder (portable / testing); also skips installing |

## Build

```powershell
dotnet build UniversalRGB.sln -c Release
dotnet test src/UniversalRGB.Tests -c Release
dotnet publish src/UniversalRGB.Desktop/UniversalRGB.Desktop.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

The published `publish\UniversalRGB.exe` is the self-installing setup (release assets name it `UniversalRGB-Setup.exe`). Developer builds (`dotnet run`, `bin\Debug`) never install themselves.

### Releasing an update

1. Raise `<Version>` in `Directory.Build.props`, build, and publish the EXE as `UniversalRGB-Setup.exe`.
2. Sign it: `dotnet run --project tools/UniversalRGB.ReleaseTool -- sign UniversalRGB-Setup.exe <private key>`. This writes `UniversalRGB-Setup.exe.sig`.
3. Create a GitHub Release tagged `v<Version>` and attach both files. Installed copies pick it up automatically.

Releases are signed with the private key in `%USERPROFILE%\.universalrgb\release-signing-key.pem`.

- **Never commit it**; `.gitignore` blocks `*.pem`.
- **Back it up.** If it's lost, installed copies can't accept new updates and must be reinstalled by hand once a new key is created with `tools/UniversalRGB.ReleaseTool keygen`.
- **Only the public key lives in the repo**, in `src/UniversalRGB.Desktop/Services/ReleaseKey.cs`.

GitHub Actions builds and tests every push. Releases are published from your PC, so the private key never leaves it.

### Developing without RGB hardware

```powershell
dotnet run --project tools/UniversalRGB.Simulator      # fake OpenRGB SDK server on 127.0.0.1:6742
$env:UNIVERSALRGB_HOME = "$env:TEMP\urgb-sim"          # keep simulated devices out of your real device history
dotnet run --project src/UniversalRGB.Desktop
```

The simulator speaks the real OpenRGB wire protocol. Its four devices cover every control path:

- a Direct-mode motherboard with an unconfigured ARGB header
- a per-key keyboard
- a fan hub with **no** Direct mode (static only)
- a GPU that supports only built-in hardware effects

## Project layout

| Project | Contents |
|---|---|
| `src/UniversalRGB.Core` | Device models, effect renderer, lighting engine, profiles (JSON), SQLite database, device classifier |
| `src/UniversalRGB.Hardware` | Read-only Windows inventory (SetupAPI/CfgMgr32, SMBIOS, WMI), GPU temperature (`nvidia-smi`), audio level (WASAPI loopback) |
| `src/UniversalRGB.Adapters` | OpenRGB SDK client (protocol checked against OpenRGB source), hardware-effect mapping, OpenRGB locator and launcher |
| `src/UniversalRGB.Drivers` | Dependency checks, conflicting-app detection, Authenticode + SHA-256 verification, consent-gated installer, install history |
| `src/UniversalRGB.Desktop` | WPF + MVVM app: Dashboard, Devices, Lighting Studio, Profiles, Drivers, Settings; tray icon; start with Windows |
| `src/UniversalRGB.Tests` | xUnit tests, including a fake OpenRGB server that exercises the real wire protocol |
| `tools/UniversalRGB.Simulator` | Simulated OpenRGB SDK server |
| `manifests/` | `compatibility.json` (vendor families, all unverified) and `drivers.json` (official package sources) |
| `src/UniversalRGB.Desktop/Services/SelfInstaller.cs` | The EXE's built-in per-user setup, update and uninstall |

Where things are stored:

- **Settings and profiles:** `%APPDATA%\UniversalRGB`
- **Database, logs and the verified-package cache:** `%LOCALAPPDATA%\UniversalRGB`

## Safety guarantees (plan §13)

- **Lighting only.** No code path changes fan or pump speed.
- **Animations stream only to devices with a Direct mode.** Devices without one get colors only when they change, or the closest built-in hardware effect. This avoids hammering on-device flash memory.
- **Unsupported commands are never sent.** Hardware effects only set the speed, brightness, direction and colors a mode declares, within the device's reported ranges.
- **Nothing is installed silently.**
  - Installers must pass Authenticode verification (WinVerifyTrust).
  - Pinned hashes and expected signers in `drivers.json` are enforced.
  - The file is copied, re-hashed, and run only after an explicit consent dialog (publisher, purpose, size, SHA-256, signer, elevation, restart).
  - Driver signature enforcement is never touched.
- **Other RGB apps are detected and reported, never closed.**
- **Least privilege.** The app runs as the user (`asInvoker`). Elevation is requested only for OpenRGB (opt-in) or an approved installer.
- **Failures don't crash the app.** Failed scans and dropped connections are logged and retried with backoff, and the app reconnects automatically.
- **Clean uninstall.** The uninstaller removes the app and its startup entry, and never touches drivers.

## Status against the plan

| Phase | State |
|---|---|
| 01 Foundation | Done: solution, dark UI and navigation, logging, SQLite, interfaces |
| 02 Hardware detection | Done: PnP/USB/HID enumeration, hardware IDs, compatibility matching, hot-plug rescans |
| 03 RGB control | Done: OpenRGB SDK client, zones, colors and brightness, disconnect handling |
| 04 Effects & profiles | Done: 10 effects, sync and per-device effects, profiles with import/export, startup restore, daily schedules, ARGB header sizing |
| 05 Driver manager | Done, with limits (see below) |
| 06 Testing & distribution | 80 unit tests plus a protocol simulator; one self-installing EXE (install, update, uninstall, Apps & features entry) |

Known limits:

- **One backend.** OpenRGB is the only backend so far. The `IRgbDeviceAdapter` interface is ready for manufacturer SDK adapters.
- **Pinned releases.** `drivers.json` pins OpenRGB 1.1 and PawnIO 2.2.0 by URL and SHA-256. Newer upstream releases are adopted by updating the manifest, never by trusting "latest" automatically.
- **Temperature source.** The Temperature effect uses NVIDIA GPU temperature via `nvidia-smi` only. No kernel driver is loaded for sensors.
- **Unsigned binary.** The EXE is not code-signed, so Windows SmartScreen may warn the first time it runs (choose **More info → Run anyway**). Public distribution needs a code-signing certificate.
- **Real hardware untested.** Controlling real devices still needs physical testing, which is plan phase 06.
