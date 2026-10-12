# Weather and screen preferences (0.4.0)

Weather is a separate app (stable ID 6), with current temperature, feels-like temperature, relative humidity, wind in km/h, and precipitation in mm. Its sunny, cloudy, and rainy crabs use the supplied sprite sheets. Clock (ID 1) retains the optional minimal forecast, without a heading. Weather continues fetching independently when its app is enabled even if the Clock forecast is hidden. Data comes directly from Open-Meteo over verified HTTPS; cached and unavailable readings are labeled. Current precipitation represents the API's current model interval, not a daily total. [API reference](https://open-meteo.com/en/docs).

Settings → Screen controls:
- Dimming: never, only with the PC offline (default), or whenever idle.
- Delay: 15 seconds through 60 minutes. Default: 60 seconds.
- Dimmed brightness: 5–90%; default 40% (about 60% darker). This board has an on/off backlight, so dimming uses a black display overlay; night sleep physically switches off its backlight.
- Night hours: enabled by default, 01:00–10:00. Any different whole-hour pair works, including across midnight. Local display time applies. A connected PC prevents night shutoff. Touch gives at least a minute before night shutoff (the independent dim policy can dim sooner).
- Offline app: opened once after 30 seconds without PC telemetry; default Clock. A running timer postpones switching. Manual app selection while offline stays selected. Reconnection retains existing Home / opted-in Spotify / Codex / Claude behavior.

The new settings travel through the authenticated Wi-Fi settings endpoint and persist in NVS. Schema 2 records upgrade in memory to schema 3 without discarding existing settings; Weather is enabled as the new app, and Home shortcuts keep their order. Saving writes schema 3. Update ClawPal companion before firmware; releases require companion 0.4.0. Pending new settings are held until the compatible firmware is installed. Home holds five shortcuts, with all enabled apps reachable through Apps.

The Apps menu uses shared vector icons and the supplied Spotify/Codex/Claude logos. Firmware asset encoding remains lossless with one 16-color palette per source sheet and exact integer pixel scaling.

## Hourly and daily forecasts (0.4.1)

Weather has Now, 24 hours, and 5 days tabs. It opens on 24 hours, with current temperature and the crab alongside five upcoming hourly readings. Previous / Next pages cover the available next 24 hours. Each hour shows its conditions, temperature, and precipitation probability. The daily tab shows today and the following four days, with conditions, daily high/low, and maximum precipitation probability. Now retains feels-like temperature, humidity, wind, and current precipitation.

Hour labels follow the display's configured timezone and 12/24-hour format. Daily aggregation and dates follow the weather location resolved by Open-Meteo (`timezone=auto`). The API uses its response's fixed UTC offset for daily boundaries, including ranges crossing daylight-saving changes. Cached forecasts discard elapsed hours/days; they never relabel old data as a new forecast. Missing probabilities display `--`, and missing/malformed forecast sections have an explicit unavailable state while valid current conditions remain usable.

Crab policy: clear/mostly clear daytime uses sunny; partly cloudy/overcast uses cloudy; rain, drizzle, showers, and thunderstorms use rainy. All conditions without a matching sheet, including clear nights, fog, and snow, use idle. Sleeping is never used in Weather. Working/error remain loading/failure indicators.

The same HTTPS request fetches current conditions, 25 hourly timestamps (the current boundary plus the next 24), and five daily entries. The worker keeps its 15-minute refresh and five-minute failure retry. It validates response size, units, array alignment, chronological timestamps, and numeric bounds; no additional API account or PC connection is required. This release keeps settings schema 3 unchanged.

Forecast response storage uses external PSRAM to leave internal memory available for TLS and display DMA. The authenticated health response reports forecast counts plus the last fetch stage, HTTP status, response size, and TLS error codes for diagnosis; it does not expose request credentials or response contents.

## Desktop controls and night crab (0.4.2)

Screen preferences now use a brightness slider (5–90%, keyboard arrows adjust by 1%; Shift+arrow by 10%), a minutes:seconds idle delay with 15-second step buttons, and editable 24-hour night times with one-hour step buttons. These map to the existing firmware settings and bounds. Dropdowns remain for categorical choices and location results.

The owner's night sheet is used only for clear/cloudless nights (WMO code 0 and `is_day=false`). Mostly clear nights without an exact matching sheet, fog, snow and unknown conditions retain idle. Sunny daytime, cloudy, and rainy rules are unchanged. The policy applies to the current-condition crab shared by all three Weather tabs.
