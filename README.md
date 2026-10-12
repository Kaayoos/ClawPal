# ClawPal

A little company for your desk. An animated pixel crab brings PC statistics,
Spotify controls, Codex and Claude Code usage, weather, a clock and a timer to a Wi-Fi display.

**Created by [MinimalPulse](https://minimalpulse.com/).**
Official source and updates: **[Kaayoos/clawpal](https://github.com/Kaayoos/clawpal)**.
ClawPal is independent of Spotify, OpenAI, Anthropic, Intel, Espressif and Waveshare.

## Case

ClawPal comes with a ready 3D Printable case, which you can find on [MakerWorld](https://makerworld.com/en/models/3430625-esp32-clawpal-your-desk-pc-buddy-for-daily-use#profileId-3911176)

## Hardware support

Only the original **Waveshare 4.3B** has been exercised on physical hardware.
All other profiles below are **EXPERIMENTAL - NOT HARDWARE TESTED**. They may
fail to boot, display an image, read touch or wake correctly. Compilation and
desktop UI simulation cannot establish electrical or hardware compatibility.
They are offered for users willing to test and report the exact board revision.

| Exact touch display | Resolution | Flash / PSRAM | Status |
| --- | --- | --- | --- |
| Waveshare ESP32-S3-Touch-LCD-4.3B | 800 × 480 | 16 MB / 8 MB octal | Existing hardware-tested target |
| Waveshare ESP32-S3-Touch-LCD-5, standard 800 × 480 model | 800 × 480 | 16 MB / 8 MB octal | **Experimental; untested** |
| Waveshare ESP32-S3-Touch-LCD-7, 16 MB variant | 800 × 480 | 16 MB / 8 MB octal | **Experimental; untested** |
| Waveshare ESP32-S3-Touch-LCD-7, 8 MB variant | 800 × 480 | 8 MB / 8 MB octal | **Experimental; untested** |
| Waveshare ESP32-S3-Touch-LCD-2.8C | 480 × 480 | 16 MB / 8 MB octal | **Experimental; untested** |
| Guition ESP32-4848S040C_I (capacitive touch) | 480 × 480 | 16 MB / 8 MB octal | **Experimental; untested** |
| M5Stack CoreS3 | 320 × 240 | 16 MB / 8 MB quad | **Experimental; untested** |

Choose the exact model printed on your board in setup. Do not select a board
because it has the same ESP32 chip or screen size. In particular, Waveshare
5B/1024 × 600, non-B 4.3-inch boards, resistive-touch Guition versions and
Core/Core2 are different hardware and are not included. The 7-inch flash-size
selection must match the USB chip check. See [board profiles](boards/README.md)
for sources, revisions, layout behavior and recovery.

Square and compact displays keep all apps, using vertically scrollable content
and a fixed Apps/Home footer. Swipe up to reach additional Home shortcuts,
forecast rows, task details or timer settings.

## Install

Download **ClawPal-Setup.exe** from the [latest release](https://github.com/Kaayoos/clawpal/releases/latest).
No separate Python, .NET, PresentMon or ESP-IDF installation is needed.

1. Connect a display from the hardware table using its USB data connection.
2. Confirm the detected device and board model. Choose 2.4 GHz Wi-Fi, location,
   time zone, and separate languages for your PC and display.
3. Choose Windows startup, optional game FPS tracking and automatic app navigation.
4. Leave **Save a full device backup** enabled (recommended), then install.
   The full 8 or 16 MB backup can take several minutes. Keep the cable connected.

Backups are optional and enabled by default. When enabled, an unsuccessful or
unverified backup stops installation. The installer checks the chip, flash size
and firmware before writing; USB detection cannot identify the LCD wiring, so
the exact board-model confirmation matters. Only the listed profiles are included.

The Windows x64 app includes its runtime and installs per user. After setup it
opens a configuration window; Windows startup stays in the tray. Close the
window to leave the companion running. Backups and configuration are stored
under `%USERPROFILE%\.clawpal`; keep them private because backups may contain
network settings. USB setup replaces existing firmware.

## Make it yours

- Drag Home shortcuts into your preferred order and choose which apps appear.
- Set English or Polish independently on the PC and display, 12/24-hour time,
  Celsius or Fahrenheit, location and weather preferences.
- Configure idle dimming, brightness, night hours and the app shown offline.
- Keep Home visible or allow Spotify/Codex/Claude Code to open their display apps automatically.
- Enable optional FPS/game detection, Windows startup or pause PC monitoring.
- Use the Weather app for current conditions, the next 24 hours and five days.
  Clock keeps its minimal weather line. Tap your crab for a hello.

Settings sync over authenticated local Wi-Fi. The PC may use Ethernet on the
same reachable network. Offline changes wait until reconnection. Initial
installation and backup/restore use USB; ordinary firmware updates use Wi-Fi
and preserve settings. A future bootloader/partition change may require USB.

Click the companion's version label for **About**, credits and offline licenses.
On the display, hold the version text on Home to find the same project attribution.

## Claude Code

Native Windows Claude Code sessions have their own display app, usage windows,
keyboard crab and recorded-session list. Enable Claude Code in Apps, then use
**Companion → Connect / repair Claude Code** if it has not connected automatically.
Restart already-open Claude sessions once. New installations include a Home
shortcut; upgrades preserve your existing shortcut order. Add Claude from Home
settings if you want it pinned. Automatic navigation follows the shared preference.

CLI usage arrives through Claude’s supported status line after its first response.
The five-hour and seven-day windows appear only when Claude reports them for
your subscription. Missing or expired windows stay unavailable; API billing and
context-window usage are not treated as subscription limits. Desktop Code sessions
use the local plan-usage history: percentages are marked cached with their age,
and reset times remain unavailable. Desktop controls the refresh cadence; readings
older than 30 minutes are hidden. No login credentials are decrypted or used, and
no cookies or conversation transcripts are inspected. Prompts and tool arguments in hook input are
discarded rather than stored or sent to the display.

ClawPal merges its own hooks and wraps an existing status line, preserving its
output and other settings. A backup and the prior status-line configuration live
in `%USERPROFILE%/.clawpal/claude`. Task names and project folder names travel only
over your paired local connection. WSL, remote sessions and ordinary Desktop Chat
activity are not supported by this Windows bridge. See [integration details](docs/CLAUDE.md).

## FPS and privacy

FPS collection uses the bundled Intel PresentMon console helper and Windows ETW
events. It does not change game executables or inject an overlay. Results and
overhead depend on Windows permissions and the game; unavailable FPS stays blank.
There is no promise about every game's anti-cheat policy. Tracking is optional.

PC telemetry and pairing travel on the local network. Location coordinates are
sent to Open-Meteo to request forecasts; location searches use its geocoding API.
Update checks contact GitHub. Codex data comes from the local Codex installation;
compatibility may change with that app. Do not publish pairing tokens, network
passwords, local logs or device backups. See [architecture](docs/ARCHITECTURE.md).

## Development and releases

Build from source with **ESP-IDF 5.5.2**, **.NET SDK 8.0.425** and Python 3.12.
Firmware binaries and downloaded tools are intentionally excluded from Git.
GitHub Actions builds firmware first, then tests and packages Windows.

- [Build instructions](docs/BUILDING.md)
- [Release format and publication commands](docs/UPDATES.md)
- [Translations](languages/README.md)
- [Contributing](CONTRIBUTING.md)
- [Screen and weather settings](docs/SCREEN-AND-WEATHER.md)

The prepared release is **0.8.1**. The Windows executable is unsigned.
The original 4.3B's main workflows have been exercised during development;
backup restoration and broader Windows/hardware acceptance still need testing.

## License and credits

Original ClawPal code, documentation and crab artwork are **GPLv3**; firmware
has an [ESP-IDF linking permission](LICENSES/ESP-IDF-exception.txt), and the
companion has a [Windows SDK linking permission](LICENSES/Windows-SDK-exception.txt). Forks and
renamed versions are allowed under the license, with copyright/license notices
preserved and corresponding source supplied when required. See [LICENSE](LICENSE),
[NOTICE](NOTICE), [branding policy](TRADEMARKS.md) and [credits](THIRD_PARTY_NOTICES.md).

PresentMon is MIT licensed. Weather data is from [Open-Meteo](https://open-meteo.com/)
under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), with GeoNames
location data. Forecast values are summarized, rounded and optionally converted
to Fahrenheit. The free hosted weather API has separate noncommercial-use terms.
Spotify, OpenAI/Codex and Anthropic/Claude marks retain their respective ownership and brand terms.
