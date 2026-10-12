#pragma once
#include "buddy_ui.h"
#include "lvgl.h"

lv_obj_t *buddy_home_create(lv_obj_t *screen, lv_event_cb_t open_app,
                           lv_event_cb_t weather_icon);
void buddy_home_update(const buddy_stats_t *stats, bool connected, bool wifi,
                       bool codex_cached, const char *time, const char *date,
                       const buddy_weather_t *weather);
void buddy_home_tick(void);
void buddy_home_apply_settings(const buddy_settings_t *settings);
