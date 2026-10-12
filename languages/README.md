# ClawPal languages

All ClawPal-owned user-facing text lives in this folder. `en.json` is the source catalog; `pl.json` is the complete Polish translation. Each file has two sections:

- `companion`: Windows setup, settings, tray, accessibility labels, validation and installation/update messages.
- `display`: every device app, weather descriptions, dates, crab captions, connection states and the Wi-Fi recovery page.

Protocol identifiers, JSON field names, commands, file paths, hardware names, diagnostic logs and external content (song titles, task names, SSIDs, locations) are not translated. Windows-owned dialog buttons and raw errors from Windows/esptool retain their system/provider language. Numbers and technical units are kept separate from translatable prose.

The PC and display languages are independent. Choose both on the first setup page. Later, open **Language** in Settings and save. The PC changes immediately; the display preference syncs over Wi-Fi and remains stored through reboot and OTA updates. Existing installations start in English. Missing keys in an added language fall back to English; a missing saved language falls back safely as well.

## Add a language

1. Copy `en.json` to a file such as `de.json`. Save UTF-8. Set `meta.code` to the filename without `.json`, `meta.name` to the language's native name, and `meta.culture` to its .NET culture (for example `de-DE`). Keep `direction` as `ltr`.
2. Translate the values in `companion` and `display`. Keep all keys unchanged. Do not translate the `formats` metadata from the English template.
3. Preserve placeholders: C# `{0}`, `{1:0.0}` can move within a sentence, but keep their numbers/formats; firmware `%s`, `%lu`, `%u`, `%.0f`, `%02d` must keep the original order and types. `%%` means a literal percent in a formatted firmware string. Preserve meaningful leading/trailing spaces and `\n` line breaks.
4. If new characters are needed, run `python scripts/generate_language_fonts.py --converter /path/to/lv_font_conv.js` with Node.js and `lv_font_conv` 1.5.3. The included Montserrat font supports Latin scripts, including Polish. The generator rejects characters absent from the font. Adding other writing systems requires a suitable licensed font and layout review; right-to-left layouts are not implemented yet.
5. Run `python scripts/generate_languages.py`. The build discovers every `languages/*.json` automatically; no source-code registration is needed.
6. Build firmware (`scripts/build-firmware.ps1` in the ESP-IDF environment), then build the Windows bundle (`scripts/build.ps1`). Both catalogs and fonts are compiled into the release, so changing JSON on disk does not modify an already-installed executable. Send the new firmware through the existing Wi-Fi updater.

`python scripts/generate_languages.py --check` verifies generated catalogs, lookup keys, placeholder compatibility and glyph coverage. Desktop tests exercise both languages, first-install selectors, live switching, independent display selection and schema migration. Inspect translations on the 800 × 480 display and at the Windows minimum window size before release. Keep crab captions short (roughly 30 characters); use concise labels for device buttons and forecast columns.

Do not edit `firmware/main/language/buddy_language_data.*` or generated font C files by hand. Edit this folder and regenerate instead. The generated sources are committed so ordinary firmware builds do not need the font converter.
