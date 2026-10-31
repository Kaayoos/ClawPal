#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"
#include "cJSON.h"

#define BUDDY_SETTINGS_SCHEMA 7
#define BUDDY_HOME_SLOTS 5
typedef struct {
    uint32_t revision;
    uint8_t enabled_apps, home_count, home_order[BUDDY_HOME_SLOTS];
    bool use_24h, weather_enabled, weather_fahrenheit;
    uint8_t dim_behavior, dim_brightness, night_start, night_end, offline_app;
    uint8_t theme, accent; /* buddy_theme_id_t and buddy_accent_id_t */
    uint16_t dim_after_seconds;
    bool night_enabled;
    char language[16];
    char city[64], timezone[128];
    double latitude, longitude;
} buddy_settings_t;

esp_err_t buddy_settings_init(void);
buddy_settings_t buddy_settings_get(void);
bool buddy_settings_parse(const cJSON *json, buddy_settings_t *out);
cJSON *buddy_settings_json(const buddy_settings_t *settings);
esp_err_t buddy_settings_save(const buddy_settings_t *settings);
/* The UI is built once per boot, so a different look needs a restart. */
bool buddy_settings_restart_needed(const buddy_settings_t *settings);
