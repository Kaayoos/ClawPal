/* Runtime checks for the firmware's JSON parsers on the build host.
 * Built by run.py against the same cJSON that ESP-IDF ships. */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "buddy_language.h"
#include "buddy_settings.h"
#include "buddy_theme_data.h"
#include "buddy_weather_parse.h"
#include "buddy_shortcuts.h"
#include "buddy_shortcut_data.h"

/* The parsers only need text lookups to exist; display text is not under test. */
const char *buddy_text(buddy_text_key_t key) { (void)key; return "text"; }
bool buddy_language_supported(const char *code) { return code && (!strcmp(code, "en") || !strcmp(code, "pl")); }

static int passed, failed;
static void check(bool value, const char *name)
{
    if (value) { ++passed; printf("PASS: %s\n", name); }
    else { ++failed; printf("FAIL: %s\n", name); }
}

#include "test_telemetry.c"
#include "test_auth.c"

/* `look` holds the schema 7 theme fields, or nothing for older payloads. */
static bool settings_look(const char *city, const char *timezone, int schema, int apps, const char *look, buddy_settings_t *out)
{
    char json[1024];
    snprintf(json, sizeof(json),
        "{\"schema\":%d,\"enabled_apps\":%d,\"home_order\":[0,4],\"use_24h\":true,\"weather_enabled\":true,"
        "\"city\":\"%s\",\"latitude\":52.2,\"longitude\":21.0,\"timezone\":\"%s\",\"language\":\"pl\","
        "\"weather_fahrenheit\":false,\"dim_behavior\":1,\"dim_after_seconds\":60,\"dim_brightness\":40,"
        "%s\"night_enabled\":true,\"night_start\":1,\"night_end\":10,\"offline_app\":1}",
        schema, apps, city, timezone, look);
    cJSON *root = cJSON_Parse(json);
    buddy_settings_t value;
    bool valid = root && buddy_settings_parse(root, out ? out : &value);
    cJSON_Delete(root);
    return valid;
}

static bool settings(const char *city, const char *timezone, int schema, int apps, buddy_settings_t *out)
{
    return settings_look(city, timezone, schema, apps, schema >= 7 ? "\"theme\":\"crab\",\"accent\":\"champagne\"," : "", out);
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
    check(!settings("Warsaw", "UTC0", 9, 511, NULL), "unknown settings schema is rejected");
    check(settings("Warsaw", "UTC0", 8, 511, &parsed) && (parsed.enabled_apps & 256), "schema 8 settings carry the Shortcuts app");
    check(settings("Warsaw", "UTC0", 8, 255, &parsed) && !(parsed.enabled_apps & 256), "schema 8 settings can turn Shortcuts off");
    check(!settings("Warsaw", "UTC0", 7, 511, NULL), "schema 7 settings cannot name a ninth app");
    check(settings("Warsaw", "UTC0", 7, 255, &parsed) && (parsed.enabled_apps & 256), "older settings gain the Shortcuts app");
    check(!settings("Warsaw", "UTC0", 8, 1023, NULL), "apps beyond Shortcuts are rejected");
    check(settings("Warsaw", "UTC0", 6, 255, &parsed) && parsed.theme == BUDDY_THEME_CRAB && parsed.accent == BUDDY_ACCENT_CHAMPAGNE,
          "schema 6 settings keep the crab theme");
    check(settings_look("Warsaw", "UTC0", 7, 255, "\"theme\":\"studio\",\"accent\":\"sage\",", &parsed) &&
          parsed.theme == BUDDY_THEME_STUDIO && parsed.accent == BUDDY_ACCENT_SAGE, "schema 7 settings carry the theme and accent");
    check(!settings_look("Warsaw", "UTC0", 7, 255, "\"accent\":\"sage\",", NULL), "schema 7 requires a theme");
    check(!settings_look("Warsaw", "UTC0", 7, 255, "\"theme\":\"studio\",", NULL), "schema 7 requires an accent");
    check(!settings_look("Warsaw", "UTC0", 7, 255, "\"theme\":\"neon\",\"accent\":\"sage\",", NULL), "unknown themes are rejected");
    check(!settings_look("Warsaw", "UTC0", 7, 255, "\"theme\":\"studio\",\"accent\":7,", NULL), "accents are named, not numbered");
    settings_look("Warsaw", "UTC0", 7, 255, "\"theme\":\"studio\",\"accent\":\"ice\",", &parsed);
    cJSON *saved = buddy_settings_json(&parsed); buddy_settings_t restored = {0};
    check(saved && buddy_settings_parse(saved, &restored) && restored.theme == BUDDY_THEME_STUDIO && restored.accent == BUDDY_ACCENT_ICE,
          "the saved look round-trips");
    cJSON_Delete(saved);
    check(settings_look("Warsaw", "UTC0", 7, 255, "\"theme\":\"studio\",\"accent\":\"teal\",", &parsed) && parsed.accent == BUDDY_ACCENT_TEAL,
          "every accent in themes.json is accepted by name");
    check(settings("Warsaw", "UTC0", 2, 63, &parsed) && (parsed.enabled_apps & 64) && (parsed.enabled_apps & 128),
          "schema 2 settings gain the Weather and Claude apps");
    cJSON *root = cJSON_Parse("[1,2,3]");
    check(!buddy_settings_parse(root, &parsed), "non-object settings are rejected");
    cJSON_Delete(root);
}

static bool deck(const char *json, buddy_shortcut_deck_t *out)
{
    cJSON *root = cJSON_Parse(json);
    buddy_shortcut_deck_t value;
    bool valid = root && buddy_shortcuts_parse(root, out ? out : &value);
    cJSON_Delete(root);
    return valid;
}

static void shortcut_checks(void)
{
    buddy_shortcut_deck_t parsed;
    check(deck("{\"rev\":42,\"size\":12,\"tiles\":[{\"slot\":0,\"label\":\"Mute mic\",\"label_on\":\"Mic muted\",\"icon\":\"mic\",\"color\":\"red\","
               "\"toggle\":true,\"locked_ok\":true},{\"slot\":11,\"label\":\"Notes\",\"icon\":\"app\",\"color\":\"accent\",\"app_icon\":true,\"repeat\":false}]}", &parsed) &&
          parsed.rev == 42 && parsed.size == 12 && parsed.tiles[0].used && parsed.tiles[0].toggle && parsed.tiles[0].locked_ok &&
          parsed.tiles[0].icon == BUDDY_SHORTCUT_ICON_MIC && !strcmp(parsed.tiles[0].label_on, "Mic muted") &&
          !parsed.tiles[1].used && parsed.tiles[11].app_icon && parsed.tiles[11].color == 0, "a deck parses with gaps between its tiles");
    check(deck("{\"rev\":1,\"size\":6,\"tiles\":[]}", &parsed) && parsed.size == 6, "an empty deck is valid");
    check(!deck("{\"rev\":1,\"size\":10,\"tiles\":[]}", NULL), "decks come in 6, 8, 12 or 16 slots");
    check(!deck("{\"rev\":0,\"size\":6,\"tiles\":[]}", NULL) && !deck("{\"rev\":65536,\"size\":6,\"tiles\":[]}", NULL),
          "the revision fits the tap's 16 bits and is never zero");
    check(!deck("{\"rev\":1,\"size\":6,\"tiles\":[{\"slot\":6,\"label\":\"x\",\"icon\":\"mic\",\"color\":\"red\"}]}", NULL), "a tile outside the deck is rejected");
    check(!deck("{\"rev\":1,\"size\":6,\"tiles\":[{\"slot\":1,\"icon\":\"mic\",\"color\":\"red\"},{\"slot\":1,\"icon\":\"lock\",\"color\":\"red\"}]}", NULL),
          "two tiles cannot share a slot");
    check(!deck("{\"rev\":1,\"size\":6,\"tiles\":[{\"slot\":0,\"icon\":\"rocket\",\"color\":\"red\"}]}", NULL), "unknown icons are rejected");
    check(!deck("{\"rev\":1,\"size\":6,\"tiles\":[{\"slot\":0,\"icon\":\"mic\",\"color\":\"#ff0000\"}]}", NULL), "colours are named, not free-form");
    check(!deck("{\"rev\":1,\"size\":6,\"tiles\":[{\"slot\":0,\"icon\":\"mic\",\"color\":\"red\",\"label\":\"bad\\u0007\"}]}", NULL), "control characters in labels are rejected");
    char json[512], label[60];
    memset(label, 'a', 48); label[48] = 0;
    snprintf(json, sizeof(json), "{\"rev\":1,\"size\":6,\"tiles\":[{\"slot\":0,\"icon\":\"mic\",\"color\":\"red\",\"label\":\"%s\"}]}", label);
    check(deck(json, NULL), "a 48-byte label fits");
    memset(label, 'a', 49); label[49] = 0;
    snprintf(json, sizeof(json), "{\"rev\":1,\"size\":6,\"tiles\":[{\"slot\":0,\"icon\":\"mic\",\"color\":\"red\",\"label\":\"%s\"}]}", label);
    check(!deck(json, NULL), "a 49-byte label is rejected");
    check(buddy_shortcut_control_value(65535, 15) == (65535U << 5 | 15U), "a tap packs the revision above the slot");
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

int main(int argc, char **argv)
{
    settings_checks();
    weather_checks();
    shortcut_checks();
    telemetry_checks();
    auth_checks();
    telemetry_fuzz(argc > 1 ? (unsigned)strtoul(argv[1], NULL, 10) : 200000);
    printf("%d firmware parser checks passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
