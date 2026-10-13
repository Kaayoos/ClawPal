# Credits and third-party notices

**ClawPal was created by [MinimalPulse](https://minimalpulse.com/).**
Copyright © 2026 MinimalPulse and contributors. Original code and crab artwork
use GPLv3, with additional ESP-IDF/Windows SDK platform linking permissions. This
document is also bundled in the companion's About page alongside full licenses.

## Artwork and integration marks

- **Crab sprite sheets:** original artwork supplied by MinimalPulse, distributed
  with ClawPal under GPLv3. Preserve authorship. Trademark rights are separate.
- **Spotify logo:** Spotify's mark, used to identify the Spotify integration.
  [Brand and design guidelines](https://developer.spotify.com/documentation/design).
  The mark is not licensed under ClawPal's GPL license. No endorsement is claimed.
- **OpenAI / Codex logo:** OpenAI's mark, used to identify the Codex integration.
  [OpenAI brand guidelines](https://openai.com/brand/). The mark is not licensed
  under ClawPal's GPL license. No endorsement is claimed.
- **Claude / Claude Code logo:** Anthropic’s mark, supplied by the project owner
  as `assets/logos/ClaudeLogo.png`, used only to identify the Claude Code integration.
  [Anthropic newsroom and media resources](https://www.anthropic.com/news).
  Copyright and trademark rights remain with Anthropic; the mark is outside
  ClawPal’s GPL grant. No affiliation or endorsement is claimed.
- **Montserrat Medium:** copyright holders and Reserved Font Names are listed
  in [the SIL Open Font License](LICENSES/Montserrat-OFL.txt). Generated bitmap
  fonts derive from this font; original font source is included in assets/fonts.
- **Weather and app symbols:** drawn in ClawPal source; the third-party logos
  above remain unchanged in color and proportions.

The project owner confirms that the Spotify and Codex logo files came from their official brand
resources and were reduced in resolution by the owner. Their original colors
and proportions are preserved. Generated firmware renditions only adapt pixel
format and size. The linked brand guidelines still govern their use; these
notices do not grant additional rights to the marks.

## PresentMon / FPS

Intel **PresentMon 2.6.0**, Copyright (C) 2017–2024 Intel Corporation,
[MIT license](LICENSES/PresentMon-MIT.txt).
[Source](https://github.com/GameTechDev/PresentMon/tree/v2.6.0) ·
[Official binary](https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe).

SHA-256: `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`.
The build downloads this pinned console executable and checks its hash; the
installer embeds it with the MIT license and source reference. The binary is
not checked into Git. The extracted helper is checked again before use.
ClawPal invokes it as a separate process to read Windows ETW presentation
events. It does not patch game executables or inject an overlay. FPS tracking
is optional; availability and collection overhead vary by game and system.
The PresentMon GUI, driver packages and overlay are not installed.

## Weather and locations

Weather data by **[Open-Meteo](https://open-meteo.com/)**, licensed under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)
([full text](LICENSES/CC-BY-4.0.txt)). ClawPal selects and summarizes forecasts,
rounds values, assigns condition artwork and optionally converts Celsius to
Fahrenheit. Cached forecasts may be older than the latest provider data.
Location data comes from **[GeoNames](https://www.geonames.org/)** through
Open-Meteo's geocoding service, under GeoNames' [CC BY terms](https://www.geonames.org/export/).
Attribution is available in the companion About page and the display About page.
Open-Meteo's [hosted API terms](https://open-meteo.com/en/terms) are separate from
the data license: its free service is for noncommercial use and has usage limits.
Commercial deployments need an appropriate provider arrangement.

## Windows runtime and flashing

- **.NET 8.0.31**, Microsoft and contributors: [license](LICENSES/dotnet-LICENSE.txt)
  and [third-party notices](LICENSES/dotnet-ThirdPartyNotices.txt).
  [Matching runtime source](https://github.com/dotnet/runtime/tree/v8.0.31).
  The self-contained installer carries the runtime and required Windows SDK projections.
  Windows Desktop runtime notices: LICENSES/WindowsDesktop-LICENSE.txt.
  C#/WinRT runtime 2.2.0: LICENSES/CsWinRT-MIT.txt. Windows SDK targeting pack
  10.0.19041.56: LICENSES/Windows-SDK-terms.txt; system SDK files retain their
  Microsoft terms and are not relicensed as ClawPal source.
- **esptool 4.11.0**, Espressif: GPL-2.0-or-later, invoked as a separate process.
  [License](LICENSES/esptool-GPL-2.0.txt) ·
  [matching source and build instructions](https://github.com/espressif/esptool/tree/v4.11.0).
  Builds verify the official Windows archive and executable checksums. Releases
  include a matching source archive alongside the binary distribution. Its
  Python/PyInstaller dependency notices from LICENSES/esptool-runtime are copied
  by scripts/prepare-licenses.py. Espressif does not supply a dependency lockfile
  for this binary; the notice snapshot provenance is recorded separately.
  The included legacy flasher stub is GPL-2.0-or-later, source version
  [v1.11.1](https://github.com/espressif/esptool-legacy-flasher-stub/tree/v1.11.1).
  Releases also include its corresponding source archive. The newer stub is
  MIT/Apache-2.0, [v0.3.0](https://github.com/esp-rs/esp-flasher-stub/tree/v0.3.0).
  Both stub license texts are retained in LICENSES/esptool-runtime.

## Display firmware

- **LVGL 8.4.0**, LVGL Kft, [MIT](LICENSES/LVGL-MIT.txt), source in
  vendor/components/lvgl__lvgl, upstream commit `4495f428630cc1741bd8bfd977f080e8460e8e8d`.
  Three unused demo fonts whose licenses forbid redistribution are removed from
  the vendored copy: `src/extra/libs/freetype/arial.ttf`, `src/font/korean.ttf`
  and `scripts/built_in_font/SimSun.woff`. No ClawPal build uses them.
- **Font Awesome Free 5** symbol glyphs, Fonticons, Inc., embedded in LVGL's
  built-in Montserrat bitmap fonts, which the display uses as symbol fallbacks.
  Fonts: [SIL OFL 1.1](LICENSES/FontAwesome-OFL.txt); icons:
  [CC BY 4.0](LICENSES/CC-BY-4.0.txt). https://fontawesome.com/license/free
- **Espressif touch components**: esp_lcd_touch 1.1.2, GT911 1.1.1~1;
  Apache-2.0. Source and license notices are in vendor/components.
- **Waveshare display / Espressif LVGL port**: original source notices retained
  in firmware/components/board; CC0-1.0 and Apache-2.0.
  [Upstream demo archive](https://files.waveshare.com/wiki/ESP32-S3-Touch-LCD-4.3B/ESP32-S3-Touch-LCD-4.3B-BOX-Demo.zip),
  SHA-256 `57186c266f5ed5d9cd22b80b3d3d1d19d54dec2449d7ec220c8b3abc60ee0092`.
- **ESP-IDF 5.5.2**, Espressif and contributors: Apache-2.0 plus individual
  component licenses, including FreeRTOS, lwIP, Mbed TLS, cJSON, newlib and
  wpa_supplicant. The build copies license/notice files from its exact SDK into
  the distributed license bundle. Source headers retain component authorship.
  [Matching SDK source](https://github.com/espressif/esp-idf/tree/v5.5.2).
  Official releases include a recursive SDK source archive with its submodules.

Third-party material keeps its own copyrights and licenses. Its authors are
credited for their components; this is not an endorsement or a claim that
integration providers contributed to ClawPal. Full licenses are bundled with
ClawPal-Setup.exe and published as ClawPal-licenses.zip.

## Claude Code integration

ClawPal uses Anthropic’s documented [status-line fields](https://code.claude.com/docs/en/statusline)
and [session hooks](https://code.claude.com/docs/en/hooks). Claude Code is installed
separately and is not bundled or relicensed. The bridge reads no OAuth tokens,
cookies or transcripts, and does not call a private usage endpoint.

[Clawdmeter by HermannBjorgvin](https://github.com/HermannBjorgvin/Clawdmeter)
was reviewed as a reference for the usage-meter idea. No Clawdmeter source,
artwork, binaries or dependency is included; ClawPal’s bridge is original code.

## Additional experimental display adapters

- Espressif ESP32_Display_Panel pin maps and ST7701 command tables, commit
  `92b790ed6d24b0678e2f45b1fc85f0abd2d41b33`, copyright 2023–2025 Espressif
  Systems (Shanghai) CO LTD, [Apache-2.0](LICENSES/Apache-2.0.txt).
- Espressif esp-bsp CoreS3 setup, ILI9342E command table, ILI9341 2.1.0 and
  FT5x06 touch drivers, commit `f259a6abd5808019b1903a422092a4234ca95a51`,
  [Apache-2.0](LICENSES/Apache-2.0.txt). Source headers and driver licenses
  are retained. Local compatibility changes are described in
  [board documentation](firmware/components/board/README.md).

These credits describe upstream components; they do not imply that the vendors
tested or endorsed ClawPal's experimental board support.
