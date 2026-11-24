# 3D printers

The Printer app shows how your 3D prints are going: how far along it is, the file, the time
left, when the print will be done, the layer and the nozzle and bed temperatures, next to a
pixel-art printer that builds the part slice by slice. With more than one printer, a switch at
the top picks which one is shown (on the smallest screen, tap the heading). When a print the
display has been watching finishes, fails or pauses for filament or an error, a full-screen
alert covers whatever app is open, like the needs-you alert for Claude Code and Codex. While a
print runs, the app opens by itself if **automatically open apps** is on.

Add printers in the PC app under **Apps**, **Printer**, **Manage printers**: **Find printers** lists what answers on your
network, or type the address yourself. The display gets the printers through the PC app's
telemetry ([PROTOCOL.md](PROTOCOL.md#the-printer-object)). It shows up to four.

## Read-only and local

ClawPal never controls a printer. It never starts, pauses, stops, heats or moves one, never
sends it G-code or files, and never changes its settings. It needs no Bambu account and no cloud
service, and you don't have to turn on LAN Only or Developer mode.

- **Bambu Lab** (X1, P1, A1, H2): ClawPal connects to the printer's own MQTT server over TLS on
  port 8883 as `bblp` with the LAN access code, and subscribes to `device/<serial>/report`.
  Printers bound to the cloud still answer here. Once per connection ClawPal sends Bambu's
  standard full-status request, `{"pushing":{"sequence_id":"0","command":"pushall"}}`, to
  `device/<serial>/request`; that is the only message it ever publishes. P1 and A1 printers then
  send only what changed, and ClawPal merges those into one state.
- **Klipper / Moonraker**: `GET /printer/objects/query?print_stats&display_status&virtual_sdcard&extruder&heater_bed`,
  with `X-Api-Key` if you give one.
- **OctoPrint**: `GET /api/job` and `GET /api/printer` with `X-Api-Key`.
- **PrusaLink**: `GET /api/v1/status` and, while there is a job, `GET /api/v1/job`, with
  `X-Api-Key`; firmware that asks for HTTP digest gets the user `maker` and the same key.

This is enforced in code, not just in habit:

- `MqttPackets.PublishPacket` in [PrinterMqtt.cs](../companion/PrinterMqtt.cs) is the only way
  the PC app can build an MQTT PUBLISH, and it throws unless the topic is a printer's request
  topic and the payload is exactly the full-status request.
- Every HTTP request to a printer passes `ReadOnlyHttpGuard` in
  [PrinterClients.cs](../companion/PrinterClients.cs), which throws for anything but a GET
  without a body to one of the status paths above.
- The protocol tests try the commands that would change a printer (pause, resume, stop,
  `gcode_line`, `project_file`, lights, speed, AMS, firmware update, cancel, emergency stop, file
  upload) and expect each to be refused; record every packet ClawPal sends to a fake printer and
  expect only connect, subscribe, the one status request, pings and disconnect; and scan the
  sources for any other publish or any POST, PUT, PATCH or DELETE in printer code.

One connection per printer: printers allow only a few clients at once, so ClawPal keeps one MQTT
connection, or one HTTP connection polled every 5 seconds while printing and every 15 seconds
otherwise. A printer that is off is retried with waits growing to a minute. A refused access code
or key waits five minutes before the next try, so the printer isn't asked over and over.

## Where secrets live

Printers are saved in `%USERPROFILE%\.clawpal\printers.json`. The access code or API key is
encrypted with Windows DPAPI for your Windows account, like the pairing token. It is never sent to
the display, put into telemetry or written to the log. Remove a printer to delete its secret.

## The Bambu certificate

Bambu printers use a self-signed certificate. ClawPal pins it: the first time it connects to a
printer it saves the certificate's SHA-256 with that printer (trust on first use), and later
connections to that printer must show the same certificate. A different one stops the connection
and the PC app says the certificate changed; remove the printer and add it again if you trust
the change. The pin belongs to one printer entry, so it is only ever used for that printer's
address. ClawPal does not check the certificate against Bambu's own CA.

## Finding printers

Bambu printers announce themselves with SSDP NOTIFY messages on UDP port 2021; ClawPal listens
for a few seconds and also sends one SSDP M-SEARCH. An announcement counts only when it comes
from the address it names, on a private network. Moonraker, OctoPrint and PrusaLink are found
over mDNS (`_moonraker._tcp`, `_octoprint._tcp`, `_prusa-link._tcp`) when they announce
themselves; otherwise type the address, with a port if it isn't 80.

## What the state means

| Display | Bambu `gcode_state` | Klipper `print_stats.state` | OctoPrint flags | PrusaLink `state` |
| --- | --- | --- | --- | --- |
| preparing | `PREPARE`, `SLICING`, `INIT` | – | – | `BUSY` with a job |
| printing | `RUNNING` | `printing` | `printing`, `finishing` | `PRINTING` |
| paused | `PAUSE` | `paused` | `paused`, `pausing` | `PAUSED`, `ATTENTION` |
| finished | `FINISH` | `complete` | operational with the job at 100% | `FINISHED` |
| failed | `FAILED` | `cancelled`, `error` | `error`, `cancelling` | `STOPPED`, `ERROR` |
| idle | `IDLE` and anything else | `standby` | operational | `IDLE`, `READY` |

A Bambu pause or failure with `print_error` 0300_400C ("the task was canceled") or a pause with
no error counts as your own (no alert). An error ending in 8011 is a filament runout. Any other
`print_error`, or an HMS message of level 1–3, is an error and its code goes to the alert.
Klipper and OctoPrint don't say why a print paused, so every pause there alerts.

## Not done yet

- Camera snapshots. Some Bambu models only serve the camera in LAN Only mode, so it needs care.
- Moonraker's websocket (polling is used instead) and Klipper's slicer time estimate, which
  needs a metadata request.
