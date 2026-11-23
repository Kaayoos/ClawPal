# Display UI simulator

This runs the actual LVGL UI, assets and translations on Windows with synthetic,
anonymous telemetry. It exercises all eight apps, scroll gestures, timer and music
buttons, the Apps footer and offline Clock fallback at 800x480, 480x480 and 320x240.
`clawpal_sim <dir> <theme> - printer` runs the Printer app on its own: automatic opening, switching printers, the
Apps tile, the paused, finished and failed alerts and the offline and empty states, with fictional printers.
It does not emulate the LCD, touch controller, power circuit or PSRAM hardware.

Install Python 3.12 and `python -m pip install --require-hashes -r tests/simulator/requirements.txt`, then run
`./scripts/test-ui.ps1`. Pass `-Python <path>` when needed. Outputs and PPM snapshots
are ignored under `.sim-output/`. CI runs the same checks on Windows. A successful
simulation does not promote a board out of experimental status.
