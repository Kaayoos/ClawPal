# Display and PC app protocol

This is how the ClawPal display and the PC app talk over your local network. Use it as a
reference for hardware mods and for anyone reading the code. The wire names still say
`buddy` (ClawPal's earlier name). They stay that way so old displays and old PC apps keep
working together.

The source of truth is the code. The PC side is in [Protocol.cs](../companion/Protocol.cs),
[DeviceAuth.cs](../companion/DeviceAuth.cs) and [Connection.cs](../companion/Connection.cs).
The display side is in [buddy_network.c](../firmware/main/network/buddy_network.c),
[buddy_auth.h](../firmware/main/network/buddy_auth.h) and
[buddy_telemetry_parse.c](../firmware/main/network/buddy_telemetry_parse.c).
[ARCHITECTURE.md](ARCHITECTURE.md) explains why each part works the way it does.

## In one picture

```
PC app                                              Display
  |  UDP 42170   BUDDYOS_DISCOVER_V1 <nonce>  ------>  |
  |  <-------  buddy.hello (id, challenge, proof)      |
  |                                                    |
  |  WebSocket ws://<display>:42171/monitor (signed)   |
  |  pc.stats, once a second  ----------------------->  |
  |  <-------------------------------  buddy.ack (seq) |
  |  <-----------------  buddy.control (button, fader) |
  |  next pc.stats reports the result  -------------->  |
  |                                                    |
  |  signed HTTP on port 42171: /settings, /art,       |
  |  /shortcuts, /mixer-icon, /firmware, /health ...   |
```

The PC app always starts the conversation. The display never opens a connection to the
PC. It only answers, and sends commands back over the WebSocket the PC app opened.

## Pairing token

During setup the PC app writes a random 64-character token into the display's NVS (namespace
`buddy`, key `token`) and keeps its own copy, protected with Windows DPAPI. Everything below
is authenticated with that token. The token itself never crosses the network.

## Discovery: UDP 42170

The PC app broadcasts this ASCII datagram to port 42170, on each network adapter's broadcast
address:

```
BUDDYOS_DISCOVER_V1 <32 lower-case hex characters of random nonce>
```

A display that is connected to Wi-Fi replies to the sender:

```json
{"type":"buddy.hello","v":2,"id":"<12 hex: the display's MAC>","name":"ClawPal",
 "port":42171,"nonce":"<the same nonce>","challenge":"<32 hex>","proof":"<64 hex>"}
```

- `proof` is lower-case hex of `HMAC-SHA256(token, "<nonce>|<id>|42171|<challenge>")`.
- The PC app accepts a reply only from a private LAN address (10/8, 172.16/12, 192.168/16,
  169.254/16), only for its own nonce, only from the display it paired with, and only with a
  valid proof.
- `challenge` is fresh for each discovery. It is the key to signed requests (next section).
  A repeated nonce gets the same challenge back, because the PC app sends one discovery to
  several addresses.
- Protocol 1 displays (before signed requests) reply with `"v":1`, no `challenge`, and a proof
  over `"<nonce>|<id>|42171"`.

## Signed requests

Since protocol 2, every HTTP request and the WebSocket upgrade carry:

```
Authorization: ClawPal-HMAC c=<challenge>,n=<counter>,s=<signature>
X-ClawPal-Body-SHA256: <SHA-256 of the body, 64 lower-case hex>
```

```
signature = hex(HMAC-SHA256(token,
    "clawpal-auth-v2\n" + METHOD + "\n" + PATH_AND_QUERY + "\n" +
    challenge + "\n" + counter + "\n" + body_hash))
```

- `counter` starts at 1 and must increase with every request on the same challenge. The
  display refuses a counter it has already seen, so a captured request can't be replayed.
- A request without a body leaves out the body-hash header and signs the SHA-256 of nothing:
  `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
- A handler that reads a body checks it against the signed hash. A firmware upload signs the
  image hash it declares in `X-ClawPal-SHA256` instead, which the update checks as it writes.
- The display keeps 16 challenges for ten minutes and forgets them on restart. A `401` with
  `WWW-Authenticate: ClawPal-HMAC` means: discover again and use the new challenge.
- Protocol 1 displays take `Authorization: Bearer <token>` instead. Protocol 2 displays refuse
  it.

The PC app and the display share test vectors for this, in the firmware host tests and the
PC app's protocol tests.

## Telemetry WebSocket: port 42171, `/monitor`

The PC app opens `ws://<display>:42171/monitor`. The upgrade request is signed as `GET` and
`/monitor` with an empty body. A new authenticated connection replaces the previous one; the
display talks to one PC at a time.

### PC to display: `pc.stats`

Once a second, sooner when something changes, the PC app sends one unfragmented text frame:

```json
{"type":"pc.stats","v":1,"seq":1234,"hostname":"DESK","timestamp":1763280000000,
 "cpu":{"usage":12.5,"temperature":48.0,"name":"..."},
 "gpu":{"usage":3.0,"temperature":41.0,"vram_used_mb":900,"vram_total_mb":8192,"name":"..."},
 "memory":{"used_mb":10240,"total_mb":32768,"usage":31.2},
 "network":{"rx_bps":1200,"tx_bps":800},
 "uptime_s":86400,
 "music":{...}, "codex":{...}, "claude":{...}, "game":{...},
 "shortcuts":{"rev":4242,"on":0},
 "mixer":{"channels":[{"id":0,"kind":0,"volume":40}, ...]},
 "auto_open_apps":true, "away":true}
```

- The fields from `type` to `uptime_s` are required. `timestamp` is Unix time in milliseconds.
  Sensor values may be `null` when Windows doesn't report them.
- Everything after `uptime_s` is optional. A display ignores what it doesn't know, and treats a
  missing object as "this PC app doesn't offer that".
- The frame is at most 6144 bytes, and at most 4096 bytes when it has no `claude` object
  (older firmware's limit). When a frame is too big, mixer apps drop off first.
- Invalid JSON, a wrong type, a value out of range or a too-deep object closes the connection.
- With no valid frame for ten seconds, the display closes the connection and shows the PC as
  offline.

The optional objects are described with their apps in [ARCHITECTURE.md](ARCHITECTURE.md)
(music, shortcuts, mixer, away) and [CLAUDE.md](CLAUDE.md) (claude). Their exact fields and
limits are in [buddy_telemetry_parse.c](../firmware/main/network/buddy_telemetry_parse.c).
The ones that matter for commands:

| Object | Fields | Purpose |
| --- | --- | --- |
| `music` | `can_play_pause`, `can_next`, `can_previous`, `can_shuffle`, `can_repeat`, `can_seek`, `can_volume` | Which media commands the display may send now |
| `music` | `control_id`, `control_ok`, `control_error` | The result of the latest command, whatever its kind |
| `mixer.channels[]` | `id`, `kind` (0 output, 1 microphone, 2 Windows' sounds, 3 app), `name`, `volume` (0–100), `muted`, `color` | The Audio app's faders. Output is always `id` 0 and microphone `id` 1 |
| `shortcuts` | `rev`, `on` | The Shortcuts deck the PC means, and which toggle tiles are lit (one bit per slot) |
| `music.lyrics` | `id`, `state`, `anchor`, `pos_ms`, `at_ms`, `playing` | Synced lyrics, present only while they're switched on in the PC app. See [Synced lyrics](#synced-lyrics) |

### Display to PC: `buddy.ack`

The display answers every valid frame with `{"type":"buddy.ack","v":1,"seq":<the same seq>}`.
The PC app treats a missing or out-of-order ack as a stalled connection and reconnects.

### Display to PC: `buddy.control`

A tap, button or fader on the display becomes one text frame on the same WebSocket:

```json
{"type":"buddy.control","v":1,"id":17,"action":"next"}
{"type":"buddy.control","v":1,"id":18,"action":"mixer_volume","value":5160}
```

- `id` is a positive integer that rises with each command. A repeated or lower `id` makes the
  PC app drop the connection.
- `value` appears exactly when the action takes one.
- The frame is at most 512 bytes and has no other fields.

| Action | `value` | Allowed while | Does |
| --- | --- | --- | --- |
| `play_pause`, `next`, `previous` | – | `music.can_…` is true | Controls the current Windows media session |
| `shuffle`, `repeat` | – | `music.can_shuffle` / `can_repeat` | Toggles them on the player |
| `seek` | seconds, 0–604800 | `music.can_seek` | Jumps within the track |
| `volume` | 0–100 | `music.can_volume` | Sets the player's own volume mixer entry |
| `mute` | – | `music.can_volume` | Toggles the player's mute |
| `mixer_volume` | `id << 7 \| percent` | telemetry has `mixer` | Sets one mixer channel. Any level above 0 also unmutes it |
| `mixer_mute` | `id << 1 \| muted` | telemetry has `mixer` | Sets one channel's mute to the given state, not a toggle |
| `shortcut` | `rev << 5 \| slot` | telemetry has `shortcuts` | Runs the action the PC stored for that Shortcuts tile |
| `focus` | 4 (Codex) or 7 (Claude Code) | a Codex or Claude task is waiting | Brings that app's window forward |

The result arrives in the `music` object of a later `pc.stats` frame: `control_id` names the
command and `control_ok` says whether it worked. On failure `control_error` says why:
`unavailable` (the player or channel has gone), `failed` (Windows refused), `changed` (the
Shortcuts deck changed under the tap), `locked` (that tile can't run while the PC is locked)
or `unsupported`.

The display won't send a command the latest telemetry doesn't allow. It queues one command
at a time, drops a command still queued after three seconds, and never sends one again after
a reconnect.

**What the display can't do:** it can't name a program, path, web address, key
combination or process. Shortcuts send a tile number, and the PC app looks up what that tile
does. The mixer sends a channel number that the PC app gave out. There's no remote shell and
no way to run arbitrary commands on the PC.

## HTTP on port 42171

All of these need a signed request. Bodies are sent with `Content-Length`, because the
display's server doesn't read chunked bodies. An endpoint an older firmware doesn't have
answers `404`, and the PC app then stops using it for that connection.

| Request | Body | What it does |
| --- | --- | --- |
| `GET /settings` | – | The display's settings as JSON, with their `schema` number |
| `POST /settings` | settings JSON | Saves settings; the `schema` must match the display's exactly |
| `POST /art` | RGB565 little-endian, square, 16–160 px; headers `X-ClawPal-Art-Id`, `X-ClawPal-Art-Size` | The current album cover |
| `POST /shortcuts` | `{rev, size, tiles:[…]}`, at most 4096 bytes | How the Shortcuts tiles look: labels, icons and colours, never what they run |
| `POST /shortcut-icon` | 48 × 48 RGB565 + alpha; headers `X-ClawPal-Shortcut-Rev`, `X-ClawPal-Shortcut-Slot` | An app's own icon for one tile |
| `POST /mixer-icon` | 48 × 48 RGB565 + alpha; header `X-ClawPal-Mixer-Id` | An app's icon for one mixer channel |
| `POST /lyrics` | `{id, kind, lines:[[ms, text\|null], …]}`, at most 49152 bytes | One song's lyrics, sent once per song |
| `GET /clock` | – | `{"ms":…}`: the display's millisecond clock, for timing lyrics |
| `POST /confirm` | `{"action":"firmware"}` or `{"action":"wifi"}` | Shows Allow/Deny on the display; answers `pending`, `approved` or `denied` |
| `POST /wifi` | SSID and password | Changes Wi-Fi and restarts. Needs a fresh approval from `/confirm` |
| `GET /firmware` | – | Board, layout, version, running slot and boot state |
| `POST /firmware` | signed firmware image; headers `X-ClawPal-SHA256` and board/layout | A Wi-Fi update. Needs a fresh approval from `/confirm` |
| `GET /health` | – | Connection state, current app, timer, weather cache, memory and last reset |
| `GET /snapshot` | – | The screen as raw RGB565 little-endian (`X-Width`, `X-Height`) |
| `GET /coredump` | – | The last crash's core dump, for `idf.py coredump-info` |

`/wifi` and `/firmware` answer `403` with `X-ClawPal-Confirm: required` without a fresh
approval. One approval allows one request within two minutes. Someone has to be at the
display to change its Wi-Fi or its firmware, even with the token.

## Synced lyrics

The PC app looks lyrics up on [LRCLIB](https://lrclib.net/) only while **Show synced
lyrics** is on (Settings → Apps → Media). The display never talks to LRCLIB. Lines must
appear in time with the music although Wi-Fi delivers frames late and unevenly, so the
lines and their timing travel separately:

1. **The lyrics, once per song.** `POST /lyrics` carries the whole song:

   ```json
   {"id":1834,"kind":"synced","lines":[[12340,"First line"],[15000,""],[18200,null]]}
   ```

   `id` is positive and names this document. `kind` is `synced` (times in milliseconds,
   never going backwards) or `plain` (every time is 0). A line is at most 255 bytes of
   display text, and there are at most 400 lines. `""` is a pause. `null` is a line the
   display's fonts can't draw, which the display shows as a music note instead of empty
   boxes. The display keeps one document and forgets it on restart, so the PC app sends it
   again on every new connection.
2. **The display's clock, measured.** `GET /clock` answers `{"ms":n}`, where `n` is the
   display's LVGL tick: milliseconds since it started, wrapping at 2³². The PC app sends
   six of these on one kept-alive connection, timing each on its own monotonic clock. It
   ignores the first, which also opens the connection, and keeps the one with the shortest
   round trip, at most 400 ms. As in NTP, it assumes the display read its clock halfway
   through: `offset = ms − (sent + received) / 2`. It measures again every two minutes.
3. **Reference points in telemetry.** `music.lyrics` says the song was at `pos_ms` when the
   display's clock read `at_ms`:

   ```json
   "lyrics":{"id":1834,"state":"synced","anchor":12,"pos_ms":83400,"at_ms":2914551,"playing":true}
   ```

   The display counts on from there by itself: `pos_ms + (now − at_ms)` while `playing`,
   `pos_ms` while paused. It takes a new point only when `anchor` changes. The PC app
   makes a new point when the song changes, the player pauses or resumes, the position
   jumps by more than 300 ms (a seek), the clock or the user's timing offset changes, and
   every ten seconds as a safety resync. `anchor` is 0 until the clock has been measured.
   `pos_ms` already includes the user's offset (±1 s, positive is earlier). The display
   lights each line 150 ms before its time, because a late line is noticed and an early one
   isn't, and slides it into place over about a quarter of a second.

Windows media controls report the player's position only now and then; Spotify reports
it on play, pause and seek. Each report carries the time it was true, and the PC app
carries the position forward from it.

`state` is `loading`, `synced`, `plain`, `none` (LRCLIB has nothing), `instrumental`,
`unsupported` (most lines use letters the display can't draw) or `unavailable` (LRCLIB
couldn't be reached; the PC app tries again after one minute, then two). A display reads a
state it doesn't know as `unavailable`. `id` names the document that goes with `synced` or
`plain`. Until a document with that `id` arrives, the display says it is looking for lyrics.

`"auto_open":true` appears while **Open lyrics by themselves** is on in the PC app (off by
default). The display then opens its Lyrics view when a new song's lyrics arrive, and closes
it again for a song with none (`none`, `instrumental` or `unsupported`) if it opened that
way. A tap on Lyrics decides for the rest of that song. Songs are told apart by title and
artist.

Older displays ignore `music.lyrics` and answer `404` to `/lyrics` and `/clock`. The PC
app then stops both for that connection. Older PC apps send no `lyrics`, and the display
offers no Lyrics button.

## Building a hardware mod

A knob, button or switch wired to the display's ESP32-S3 doesn't need a new protocol. It
needs a few lines of firmware that send the same `buddy.control` messages the touch screen
already sends:

```c
#include "buddy_network.h"
#include "buddy_model.h"

/* Call from your encoder handler. `output` is the speakers' level from the last
 * telemetry (the mixer channel with id 0); keep it from the on_stats callback. */
static void knob_turned(int steps, uint8_t output)
{
    int level = output + steps * 2;
    if (level < 0) level = 0;
    if (level > 100) level = 100;
    buddy_network_music_control("mixer_volume", BUDDY_MIXER_VOLUME_VALUE(0, level));
}

static void knob_pressed(void) { buddy_network_music_control("play_pause", 0); }
```

`buddy_network_music_control()` returns `false` when the PC is offline, when the latest
telemetry doesn't allow that action, or when a command is already queued. A knob turned
quickly should keep only its newest level and send it once the call succeeds. See
[buddy_network.h](../firmware/main/network/buddy_network.h).

A separate gadget (a second ESP32, a macro pad) can't send commands straight to the PC
app. The PC app only listens to the display it paired with, on the connection it opened. Put
the hardware on the display, or forward its input through the display's firmware.

Keep these rules if you add something of your own:

- Add, never change. New telemetry fields and actions must be optional, so an older display
  or PC app ignores them.
- Telemetry advertises what the display may send. A new action belongs behind a telemetry
  flag, the way `can_seek` gates `seek`.
- Validate on both sides. [Protocol.cs](../companion/Protocol.cs) `ParseControl` refuses any
  action or value it doesn't know, so a new action needs a matching change there.
- The display sends identifiers and levels, never commands. Whatever the PC does stays
  decided on the PC.

The traffic is signed but not encrypted. Keep the display and the PC on a network you trust.
