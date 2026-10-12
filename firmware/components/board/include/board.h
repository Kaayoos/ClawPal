#pragma once

#include <stdbool.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

#include "board_profile.h"

/** Initialize the exact Waveshare ESP32-S3-Touch-LCD-4.3B RGB display,
 * GT911 touch controller, CH422G backlight and LVGL 8.4 task.
 * Call once after boot, before using LVGL. */
esp_err_t board_init(void);

/** Hold this recursive lock for all LVGL calls made outside LVGL callbacks.
 * A negative timeout waits indefinitely; zero does not wait. */
bool board_lvgl_lock(int timeout_ms);
void board_lvgl_unlock(void);

/** The CH422G provides binary backlight control, not hardware dimming. */
esp_err_t board_set_backlight(bool enabled);

#ifdef __cplusplus
}
#endif
