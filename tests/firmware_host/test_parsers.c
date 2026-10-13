/* Runtime checks for the firmware's JSON parsers on the build host.
 * Built by run.py against the same cJSON that ESP-IDF ships. */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "buddy_language.h"
#include "buddy_settings.h"
#include "buddy_weather_parse.h"

/* The parsers only need text lookups to exist; display text is not under test. */
const char *buddy_text(buddy_text_key_t key) { (void)key; return "text"; }
bool buddy_language_supported(const char *code) { return code && (!strcmp(code, "en") || !strcmp(code, "pl")); }

static int passed, failed;
static void check(bool value, const char *name)
{
    if (value) { ++passed; printf("PASS: %s\n", name); }
    else { ++failed; printf("FAIL: %s\n", name); }
}

static bool settings(const char *city, const char *timezone, int schema, int apps, buddy_settings_t *out)
{
    char json[1024];
    snprintf(json, sizeof(json),
        "{\"schema\":%d,\"enabled_apps\":%d,\"home_order\":[0,4],\"use_24h\":true,\"weather_enabled\":true,"
        "\"city\":\"%s\",\"latitude\":52.2,\"longitude\":21.0,\"timezone\":\"%s\",\"language\":\"pl\","
        "\"weather_fahrenheit\":false,\"dim_behavior\":1,\"dim_after_seconds\":60,\"dim_brightness\":40,"
        "\"night_enabled\":true,\"night_start\":1,\"night_end\":10,\"offline_app\":1}",
        schema, apps, city, timezone);
    cJSON *root = cJSON_Parse(json);
    buddy_settings_t value;
    bool valid = root && buddy_settings_parse(root, out ? out : &value);
    cJSON_Delete(root);
    return valid;
}

static void settings_checks(void)
{
    char city[80];
    buddy_settings_t parsed;
    check(settings("Warsaw", "CET-1CEST,M3.5.0,M10.5.0/3", 6, 255, &parsed) && !strcmp(parsed.city, "Warsaw") &&
          parsed.home_count == 2 && !strcmp(parsed.language, "pl"), "valid settings parse with every field");
    memset(city, 'a', 63); city[63] = 0;
    check(settings(city, "UTC0", 6, 255, NULL), "63-byte city fits the display buffer");
    memset(city, 'a', 64); city[64] = 0;
    check(!settings(city, "UTC0", 6, 255, NULL), "64-byte city is rejected, matching the companion limit");
    check(!settings("Warsaw", "UTC0;rm", 6, 255, NULL), "time zone outside the POSIX character set is rejected");
    check(!settings("", "UTC0", 6, 255, NULL), "empty city is rejected");
    check(!settings("Warsaw", "UTC0", 6, 255 & ~32, NULL), "Home app cannot be disabled");
    check(!settings("Warsaw", "UTC0", 7, 255, NULL), "unknown settings schema is rejected");
    check(settings("Warsaw", "UTC0", 2, 63, &parsed) && (parsed.enabled_apps & 64) && (parsed.enabled_apps & 128),
          "schema 2 settings gain the Weather and Claude apps");
    cJSON *root = cJSON_Parse("[1,2,3]");
    check(!buddy_settings_parse(root, &parsed), "non-object settings are rejected");
    cJSON_Delete(root);
}

static bool weather(const char *body, time_t now, buddy_weather_t *out)
{
    return buddy_weather_parse(body, strlen(body) + 1, now, out);
}

static void weather_checks(void)
{
    const time_t now = 1760000000;
    char body[1024];
    buddy_weather_t parsed;
    snprintf(body, sizeof(body),
        "{\"current_units\":{\"time\":\"unixtime\",\"temperature_2m\":\"\xc2\xb0" "C\",\"relative_humidity_2m\":\"%%\"},"
        "\"current\":{\"time\":%lld,\"temperature_2m\":21.5,\"weather_code\":2,\"is_day\":1,\"relative_humidity_2m\":40}}",
        (long long)now - 60);
    check(weather(body, now, &parsed) && parsed.available && fabsf(parsed.temperature_c - 21.5f) < 0.01f &&
          parsed.code == 2 && parsed.age_s == 60 && fabsf(parsed.humidity_percent - 40.0f) < 0.01f,
          "current conditions parse with units and age");
    check(isnan(parsed.wind_kmh), "missing optional reading stays unavailable");
    snprintf(body, sizeof(body),
        "{\"current_units\":{\"time\":\"unixtime\",\"temperature_2m\":\"\xc2\xb0" "F\"},"
        "\"current\":{\"time\":%lld,\"temperature_2m\":70,\"weather_code\":2,\"is_day\":1}}", (long long)now);
    check(!weather(body, now, &parsed), "unexpected temperature unit is rejected");
    snprintf(body, sizeof(body),
        "{\"current_units\":{\"time\":\"unixtime\",\"temperature_2m\":\"\xc2\xb0" "C\"},"
        "\"current\":{\"time\":%lld,\"temperature_2m\":20,\"weather_code\":2,\"is_day\":1}}", (long long)now - 86400);
    check(!weather(body, now, &parsed), "stale reading is rejected");
    snprintf(body, sizeof(body),
        "{\"current_units\":{\"time\":\"unixtime\",\"temperature_2m\":\"\xc2\xb0" "C\"},"
        "\"current\":{\"time\":%lld,\"temperature_2m\":20,\"weather_code\":4,\"is_day\":1}}", (long long)now);
    check(!weather(body, now, &parsed), "unknown weather code is rejected");
    check(!weather("{\"current\":{}} trailing", now, &parsed), "trailing data is rejected");
    check(!weather("[[[[[[[[[[1]]]]]]]]]]", now, &parsed), "deep nesting is rejected before parsing");
    const char truncated[] = "{\"current\":";
    check(!buddy_weather_parse(truncated, sizeof(truncated) - 1, now, &parsed), "unterminated buffer is rejected");
}

int main(void)
{
    settings_checks();
    weather_checks();
    printf("%d firmware parser checks passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
