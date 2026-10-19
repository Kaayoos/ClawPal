# ClawPal board adapters

See [board catalog and hardware status](../../../boards/README.md).
The original `board.c` retains the proven 4.3B setup. `board_rgb.c` implements
the additional RGB targets; `board_cores3.c` handles SPI DMA, touch and power.
Call `board_init()` once and hold `board_lvgl_lock()` around external LVGL calls.
Every adapter supplies backlight on/off; the shared UI owns dimming policy.

The added RGB pin maps and ST7701 initialization tables derive from Espressif
ESP32_Display_Panel commit 92b790ed6d24b0678e2f45b1fc85f0abd2d41b33,
copyright 2023–2025 Espressif Systems (Shanghai) CO LTD, Apache-2.0.
CoreS3 setup and ILI9342E commands derive from esp-bsp commit
f259a6abd5808019b1903a422092a4234ca95a51, Apache-2.0. Copyright headers are
retained in the copied command table and vendored drivers. The FT5x06 driver
omits newer multitouch tracking-ID support to fit the existing touch 1.1.2 API;
ClawPal uses one touch point. ILI9341 2.1.0 has explicit CMake version defines
because this repository disables the component manager.

Full Apache-2.0 license: ../../../LICENSES/Apache-2.0.txt.
Original 4.3B/LVGL-port provenance: ../../../THIRD_PARTY_NOTICES.md.
