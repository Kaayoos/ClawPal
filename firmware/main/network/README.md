# ClawPal local network service

`buddy_network_start()` reads the `buddy` NVS namespace (`ssid`, `password`, `token`, `setup_key`). The application initializes NVS first. State and stats callbacks run on network tasks; their arguments must be copied before the callback returns. Credentials and pairing tokens are never logged.

The station reconnects automatically. With no saved SSID, or after 60 seconds without a connection, it enables a WPA2 setup network named `ClawPal-<last six MAC hex digits>`. Its generated password and setup URL are sent to the device UI callback. The port-80 form accepts requests only through 192.168.4.1, checks the Host/Origin, validates SSID/password byte lengths (a blank password selects an open network), persists settings, and restarts. In fallback mode the station keeps trying its saved network. Once connected, the setup HTTP server and access point stop.

## Discovery and transport

- UDP 42170 accepts `BUDDYOS_DISCOVER_V1 <32-hex nonce>`.
- Reply fields: `type=buddy.hello`, `v=1`, `id=<12 lower-case MAC hex digits>`, `name=ClawPal`, `port=42171`, `nonce`, `proof`.
- Proof: lower-case HMAC-SHA256 hex over `<nonce>|<id>|42171`, with the UTF-8 bytes of the 64-character shared token as the key.
- `ws://<device IP>:42171/monitor` requires `Authorization: Bearer <token>` before the WebSocket upgrade.
- A newly authenticated session replaces the previous companion. Ten seconds without valid telemetry closes the session and marks the PC offline.
- Stats must be an unfragmented JSON text frame <=6144 bytes (the companion keeps frames without Claude data <=4096 bytes), version 1 and type `pc.stats`, with the agreed nested CPU/GPU/memory/network fields. Numeric values have finite bounds; unavailable sensors become `NAN`. Nesting >8, embedded NULs, invalid field types and malformed data close the connection.
- Every valid telemetry frame receives `{"type":"buddy.ack","v":1,"seq":<same sequence>}` on its WebSocket. The companion uses this acknowledgement to detect a stalled connection.
- WebSocket Ping/Pong/Close frames are handled explicitly, including empty unsolicited Pong keepalives from .NET. Ping receives a matching Pong; Close receives its close response. Control frames must be final and <=125 bytes, and do not extend the valid-telemetry timeout.
- Firmware 0.3 sends only three allowlisted Spotify controls to its authenticated companion: `play_pause`, `next`, and `previous`. Commands are bound to the current session, expire after three seconds while queued, and are never replayed after reconnection. No remote shell or arbitrary command execution is provided.
- Optional `music` telemetry carries bounded metadata, playback capabilities and the most recent control result. Older companions that omit it remain supported with music unavailable.

Optional `game` telemetry carries an active flag, a bounded window title, nullable measured FPS and status. Missing data remains inactive/unavailable. The total JSON frame limit remains 6144 bytes.

Authenticated diagnostics on the same port:

- `GET /health`: JSON device ID, connection-state enum, selected app (including Home), game activity/nullable FPS, music availability and adjustable countdown timer state (remaining/duration seconds, running and completed). `ui.dimmed` reports the software shade, `ui.screen_off` the last successfully applied hardware backlight-off state, `ui.night_hours` the valid local 01:00–10:00 interval, and `ui.display_idle_ms` idle time capped at 60,000 ms (reset while connected).
- The top-level `weather` health object reports current-cache availability, staleness, fetch failure, age in seconds, Celsius temperature, WMO code and day/night. Numeric weather values are meaningful only when `available=true`. See `../weather/README.md` for the direct HTTPS worker and cache policy.
- `POST /confirm` with `{"action":"firmware"|"wifi"}`: shows an Allow/Deny prompt on the display and answers `{"state":"pending"|"approved"|"denied"}`. Poll it until the user answers. One approval authorizes one `POST /firmware` or `POST /wifi` within two minutes; without it those endpoints answer 403 with `X-ClawPal-Confirm: required`.
- `GET /snapshot`: current LVGL screen, raw RGB565 little-endian bytes; headers `X-Width`, `X-Height`, `X-Format: RGB565-LE`. The LVGL lock protects capture and the snapshot is released after sending. This is intended for occasional visual QA.

Build requirements: `board esp_wifi esp_netif esp_event esp_http_server nvs_flash json mbedtls lwip esp_timer esp_hw_support`; enable `CONFIG_HTTPD_WS_SUPPORT`, `CONFIG_HTTPD_WS_PRE_HANDSHAKE_CB_SUPPORT`, and `CONFIG_LV_USE_SNAPSHOT`.
