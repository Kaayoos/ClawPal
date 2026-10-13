# Board profiles

`catalog.json` is the shared installer/companion/build catalog. Numeric Profile
values are stable; append new entries. `scripts/generate-boards.py` generates
firmware identities, dimensions, memory defaults and the CMake index. CI checks
that generated files match. Hardware adapters live in `firmware/components/board`.

## Experimental status

The 4.3B is the only physically exercised target. Every other profile is
experimental and has not been flashed onto that hardware by this project.
Published pin maps and initialization sequences are the basis of these ports,
not evidence of successful hardware operation. CoreS3 display revisions are
selected using the touch-controller firmware ID (ILI9342C / ILI9342E).
The Guition mapping is based on the upstream `ESP32_4848S040C_I_Y_3` definition;
other revisions with different wiring may need a separate profile.

| Adapter | Display / touch | Backlight / power | Source |
| --- | --- | --- | --- |
| Original 4.3B | RGB / GT911 | CH422G | Existing Waveshare demo; see THIRD_PARTY_NOTICES.md |
| Waveshare 5 and 7 | ST7262 RGB / GT911 | CH422G; EXIO5 low for USB | [Espressif board definitions](https://github.com/esp-arduino-libs/ESP32_Display_Panel/tree/92b790ed6d24b0678e2f45b1fc85f0abd2d41b33/src/board/supported/waveshare) |
| Waveshare 2.8C | ST7701 RGB / GT911 | TCA9554 reset/CS; GPIO6 backlight | Same pinned upstream |
| Guition C_I | ST7701 RGB / GT911 | GPIO38 backlight; USB-UART for programming | [Espressif Guition/Jingcai definition](https://github.com/esp-arduino-libs/ESP32_Display_Panel/blob/92b790ed6d24b0678e2f45b1fc85f0abd2d41b33/src/board/supported/jingcai/BOARD_JINGCAI_ESP32_4848S040C_I_Y_3.h) |
| CoreS3 | ILI9342C/E SPI / FT5x06 | AW9523B resets; AXP2101 DLDO1 | [Espressif BSP](https://github.com/espressif/esp-bsp/tree/f259a6abd5808019b1903a422092a4234ca95a51/bsp/m5stack_core_s3) |

All boards use a shared software shade for configured dimming and actual
backlight power-off for night mode. RGB boards use two PSRAM framebuffers;
CoreS3 uses one internal DMA stripe buffer and a transfer-completion callback.
Native touch coordinates match each landscape/square panel's dimensions.

## Installation and recovery

USB detection verifies ESP32-S3, MAC and exact flash size. It cannot determine
screen wiring. The model selector and printed-board confirmation are mandatory.
Backups use the selected model's whole 8/16 MB flash; restoration requires both
matching model and MAC. Leave backup enabled for experimental hardware.
If an experimental image does not boot, close other serial tools, enter the
board vendor's ROM download mode and use setup's Restore a backup. BOOT/RESET
button procedures vary by board; consult the exact model's documentation.

Wi-Fi updates never change a partition table or switch to another board ID.
The original 16 MB layout is preserved. The separate 8 MB layout has NVS at
0x9000, OTA data at 0xf000 and 3 MB application slots at 0x20000/0x320000.
Changing physical models requires a fresh USB installation and pairing.

## Adding a board

1. Add the exact chip, flash/PSRAM, dimensions, driver and unique release asset
   to the catalog. Mark Experimental true until hardware evidence exists.
2. Implement its display bus, touch reset/orientation and backlight adapter
   from licensed vendor sources. Do not infer pin mappings from resolution.
3. Run `python scripts/generate-boards.py`, then build the complete matrix.
   If a new partition layout is needed, add explicit offsets and validation;
   never migrate existing partitions through ordinary OTA.
4. Render every app with `tests/simulator`, test the installer/release contracts
   and document remaining hardware uncertainty in README and release notes.

No board is auto-promoted to tested after compilation. Reports should include
exact model, revision, flash size, firmware version and the observed behavior.
Do not attach a full flash backup, Wi-Fi password or pairing configuration.
