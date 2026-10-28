# Building ClawPal

Use the repository itself; no private BuddyOS directory or credentials are needed.
The board catalog is in `boards/catalog.json`. See the README for hardware-test status.

## Firmware

Install ESP-IDF **v5.5.2 with its submodules** and its ESP32-S3 toolchain using
Espressif's instructions. Open the exported ESP-IDF environment and run:

```powershell
./scripts/build-firmware.ps1 -All
```

On Linux in the exported IDF shell:

```sh
python scripts/build-firmware.py --all
```

The build generates a separate `firmware/build-<board-id>` directory and sdkconfig
for every catalog profile. `firmware/sdkconfig.defaults` supplies shared options;
`python scripts/generate-boards.py` regenerates each board header and defaults.
Use `python scripts/build-firmware.py --board <exact-id>` for one development
target. A release installer requires `--all`; partial or mixed-version bundles
are rejected. Packaging generates `payload/boards/<id>.json` and four flash
images for each profile, retaining `payload/board.json` for the default model. No Wi-Fi credentials or pairing secrets are built in.
SDK license and notice files are copied from this exact SDK into the payload.

## Windows companion

Install **.NET SDK 8.0.425** (global.json pins it) and Python 3.12. From PowerShell:

```powershell
./scripts/build.ps1
```

This validates translations, firmware hashes and tools, runs protocol and UI
checks, and produces a self-contained Windows x64 app with .NET 8.0.31.
Downloads are from the official esptool 4.11.0 and PresentMon 2.6.0 releases,
with pinned SHA-256 checks. Their licenses are packaged for offline access.
The output is dist/ClawPal-Setup.exe plus the release manifest, firmware images,
checksum and license bundle. Every tool is packaged; no runtime download is
needed to enable FPS collection. The local release command collects source and licenses into one ZIP automatically.
See [local releases](UPDATES.md) for `./scripts/release.ps1 -Publish`.

Run `python scripts/check-publication.py` before staging changes. This checks
the files Git would publish, including already-tracked ignored files. It is a
focused check, not a replacement for reviewing `git diff --cached`.

## Changing assets and translations

Edit languages/en.json and languages/pl.json, then run
`python scripts/generate_languages.py`. If new characters require fonts, use
scripts/generate_language_fonts.py with lv_font_conv and the included original
font. Keep the font's OFL notice and all original asset sources.

The companion picks the display's time zone from the chosen location using
`companion/Assets/timezones.json`. Refresh it when a new tz database release
changes rules: `python -m pip install --upgrade tzdata` then
`python scripts/generate_timezones.py`. The generator keeps only rules the
display's C library can parse and that match the database for the next three
years; other zones fall back to manual selection.

## Rebuilding and installing modified versions

Normal USB flashing remains available for your builds, and no signing key is
needed to run modified firmware that way. Wi-Fi updates only accept images signed
with the key that signed the running firmware: official releases use the ClawPal
release key, and a fork can install its own signed build over USB once, then
update it over Wi-Fi with its own key (`scripts/sign-firmware.py`).
Keep board/layout compatibility and checksums correct. For local releases,
`ClawPal-Setup.exe --install-bundled-wifi result.json` installs its own bundled
firmware onto an already-paired compatible display. Do not use the official
GitHub update feed to distribute independent builds. The official repository
constant can be changed in a fork's source; configuration cannot override it.
