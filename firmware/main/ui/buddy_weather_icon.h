#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "lvgl.h"

/* Draws the weather mark for an Open-Meteo code at (x, y). The mark spans about
 * 26 x 26 pixels times scale; background fills the crescent cut-out at night. */
void buddy_weather_icon_draw(lv_draw_ctx_t *ctx, int x, int y, int scale, int code, bool is_day, uint32_t background);
