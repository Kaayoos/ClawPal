# Hardware profiles and release design

The current profile is `payload/board.json`. It declares the chip, exact model, screen dimensions, flash capacity, firmware version, file offsets, and checksums. The installer refuses an unexpected chip or flash size. A MAC address binds each backup to its original board. Backup is on by default. When selected, it must complete and verify before settings generation or flashing; the user can explicitly disable it on the review page. Restorable metadata is created only after verification succeeds.

To add a board:

1. Add and test a board component for its display bus, touch controller, pins, backlight, flash, and PSRAM.
2. Add a dedicated firmware build configuration and partition map; generate independent firmware assets and hashes.
3. Introduce profile selection before probing. Only profiles with tested hardware belong in the picker. `BoardProfile` and `FirmwarePart` already separate flashing data from UI logic; extract the current NVS offset and partition size into the profile if they differ.
4. Add a screen layout for its resolution. Current LVGL coordinates are 800 × 480; smaller boards need a compact composition rather than automatic image scaling. The Windows installer itself does not depend on screen dimensions.
5. Exercise backup, interrupted-write recovery, clean install, touch, Wi-Fi, settings, and restore on the actual hardware.

## Configuration

First installation creates the ESP-IDF v2 NVS image in C#, using byte-based validation and the CRC/entry layout from ESP-IDF. SSID, Wi-Fi password, random pairing token, random setup key, weather coordinates/name, and POSIX timezone go into namespace `buddy`. That namespace and the authenticated `buddy.*` LAN protocol remain stable for compatibility; branding is ClawPal.

The companion protects its pairing token with Windows DPAPI. Discovery uses a random nonce and HMAC proof. Weather is fetched by the ESP over verified HTTPS independently of the PC. Credentials never go to the weather service. PC preferences are saved locally and take effect without a firmware write.

Firmware 0.7.0 exposes authenticated `GET /settings` and `POST /settings` using schema 6 (schemas 2–5 NVS records migrate on boot). Settings include dim behavior/delay/brightness, night enabled/start/end, offline app, and: `enabled_apps`, `home_order`, `use_24h`, `weather_enabled`, city, latitude, longitude, and POSIX timezone. IDs are Monitor 0, Clock 1, Spotify 2, Timer 3, Codex 4, Home 5, Weather 6, Claude Code 7. Home's bit is mandatory. Home order contains zero to five distinct enabled app IDs, excluding Home itself. The complete validated record is committed to one NVS string, `buddy/display_cfg`, before publishing the in-memory revision. Boot migrates the original location keys when no saved record exists. The UI applies revisions under its LVGL lock; weather invalidates its cache after a location change. No restart is required.

The PC persists unsent display preferences and retries them after reconnecting. JSON writes use buffered UTF-8 content with an explicit Content-Length: the ESP-IDF server does not decode chunked request bodies. Request rejection and pairing errors are reported separately from offline retries. It checks the device's schema before writing, requires a save acknowledgment, then reads the settings back. A reference check prevents an older completed request from clearing a newer pending edit. Display configuration also syncs while PC monitoring is paused. Disabled Spotify/Codex/Claude integrations stop discovery/collection; disabling Monitor stops optional FPS collection.

Authenticated `POST /wifi` validates and saves SSID/password, then restarts the device to reconnect. It is accepted only after a fresh on-screen approval (see below). Passwords are never returned by the API or saved by the configuration window. Settings traffic uses the existing authenticated local HTTP protocol; it is not TLS-encrypted, so use a trusted LAN.

Firmware 0.3.0 retains the original factory partition, NVS, PHY, and storage offsets. It adds OTA metadata at `0x800000`, `ota_0` at `0x810000` (0x3f0000 bytes), and `ota_1` at `0xc00000` (0x400000 bytes). A one-time USB migration updates the bootloader, partition table, factory image, and blank OTA metadata without writing NVS. It first saves and verifies the first 64 KiB, including existing boot and settings data, plus the companion pairing profile. This prefix backup is separate from the full-device backup/restore flow.

Authenticated `GET /firmware` advertises board, layout, version, running slot, capacity, and confirmed boot status. `POST /firmware` requires a fresh on-screen approval, Content-Length, board/layout headers, and an expected SHA-256. Images older than the running version are refused over Wi-Fi; they can still be installed over USB. It validates the ESP32-S3/ClawPal image header before writing the inactive slot, hashes streamed bytes, then runs ESP-IDF image validation. Only a complete valid image becomes the next boot target. Settings and storage partitions are never part of an OTA upload.

ESP-IDF boot rollback is enabled. After a pending image starts, a 45-second guard requests rollback unless the main loop confirms board, NVS, networking initialization, and ten seconds of responsive operation. Confirmation does not require internet or a connected PC. This detects startup failures; it is not a guarantee against every later firmware defect. The companion waits for the expected slot/version to report confirmed, then reads settings back before reporting success.

The PC downloads release metadata and images over HTTPS from the official Kaayoos/clawpal GitHub repository. Asset redirects are limited to GitHub's release CDN. The publisher's manifest pins version, board, layout, byte size, SHA-256, and minimum companion version. Ordinary updates affect only an application slot. New partition layouts require an explicit migration, not an OTA write. The display upload continues using pairing-authenticated LAN HTTP, with SHA-256 for transfer integrity; keep the device and PC on a trusted network.

The pairing token travels in plain HTTP, so anyone who can read LAN traffic could replay it. Firmware and Wi-Fi changes therefore also need someone at the display: the companion calls authenticated `POST /confirm` with `{"action":"firmware"}` or `{"action":"wifi"}`, the display shows an Allow/Deny prompt, and the call reports `pending`, `approved` or `denied`. An approval lasts two minutes and authorizes exactly one `POST /firmware` or `POST /wifi`; an unanswered prompt is denied after 60 seconds. Firmware without `/confirm` answers 404 and the companion proceeds as before. Images are not signed: this protects against remote replacement, not against someone with USB access.

## Windows lifetime

Manual launch opens setup only for an unpaired or incomplete installation. A completed pairing opens `SettingsForm` on its Home preview, including when the display is offline. Legacy paired configurations migrate as completed. Installation marks setup incomplete before replacing pairing and complete only after firmware verification. The Device page explicitly opens USB maintenance. Windows startup creates only the tray companion.

A session mutex prevents duplicate companions, and a named event brings up the existing window. Closing configuration hides it and retains unsaved edits; closing setup leaves the tray running. Tray actions are Open, status/version, and Exit. While a USB operation is in progress, closing the window or using tray Exit is blocked. External termination/power loss remains recoverable through the previously saved backup.

`ClawWindow` supplies dark titlebar controls and resizing; shared buttons, switches, dropdowns, and scrollbars use the same palette. Motion follows Windows' menu-animation preference. `HomeOrderList` supports mouse dragging and keyboard ordering. Preview rendering is isolated behind explicit diagnostic arguments and does not load or change pairing.

The single release EXE contains the desktop runtime, firmware, flash helper, and PresentMon. It copies itself into the per-user application directory after the optional backup stage. No machine-wide installer or administrator account is needed for ordinary setup; system driver restrictions may need user intervention.

## USB discovery and progress

Only present USB serial ports are listed, using Windows SERIALCOMM plus USB enumeration metadata. Legacy motherboard COM ports are excluded. A unique Espressif VID 303A candidate is automatically selected; other serial bridges and multiple Espressif candidates require selection. The read-only esptool flash_id check confirms ESP32-S3, flash capacity, and MAC before continuing. These facts cannot identify the LCD wiring, so physical board-model confirmation remains required. Installation probes again immediately before the backup/write flow. Hotplug changes invalidate stale device selections.

esptool 4.11 read_flash emits backspace-delimited progress. The companion decodes stdout/stderr in chunks rather than waiting for a newline, throttles UI updates, and logs coarse progress. A read with no output for 60 seconds stops before any write. Device probes have a 25-second limit; backup/write/verify operations have a 20-minute upper bound. Ordinary full backups can take several minutes at the conservative 460800 baud setting.

Claude uses the same bounded usage/activity wire shape in an independent `claude` object.
Native Windows Claude supplies documented status-line and hook events; see [CLAUDE.md](CLAUDE.md).
Firmware 0.7.0 accepts up to 6144 telemetry bytes. Older firmware receives the original
4096-byte payload without Claude, until upgraded.
