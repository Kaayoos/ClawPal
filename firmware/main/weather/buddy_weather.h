#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"

#define BUDDY_WEATHER_HOURS 25
#define BUDDY_WEATHER_DAYS 5
#define BUDDY_WEATHER_RESPONSE_CAPACITY 16384

typedef struct {
    int64_t timestamp;
    float temperature_c, precipitation_probability;
    int code;
    bool is_day;
} buddy_weather_hour_t;
typedef struct {
    int64_t timestamp;
    float high_c, low_c, precipitation_probability;
    int code;
} buddy_weather_day_t;

typedef struct {
    bool available;
    bool stale;
    bool failed;
    bool is_day;
    float temperature_c;
    float feels_like_c, humidity_percent, wind_kmh, precipitation_mm;
    int code;
    uint32_t age_s;
    int32_t region_utc_offset;
    uint8_t hourly_count, daily_count;
    /* Last fetch diagnostics; never contains credentials or provider response data. */
    const char *fetch_state;
    int http_status, response_bytes;
    esp_err_t fetch_error;
    int tls_error, tls_flags;
    buddy_weather_hour_t hours[BUDDY_WEATHER_HOURS];
    buddy_weather_day_t days[BUDDY_WEATHER_DAYS];
} buddy_weather_t;

/* Dedicated HTTPS worker; never performs network I/O on the UI task. */
esp_err_t buddy_weather_start(void);
buddy_weather_t buddy_weather_get(void);
const char *buddy_weather_city(void);
const char *buddy_weather_description(int code, bool is_day);
