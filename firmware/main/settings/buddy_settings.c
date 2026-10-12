#include "buddy_language.h"
#include "buddy_settings.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include "nvs.h"
#include "freertos/FreeRTOS.h"

static portMUX_TYPE settings_lock = portMUX_INITIALIZER_UNLOCKED;
static buddy_settings_t current;
static bool integer(const cJSON *json, const char *key, int low, int high, int *out)
{
    const cJSON *v = cJSON_GetObjectItemCaseSensitive(json, key);
    if (!cJSON_IsNumber(v) || v->valuedouble != v->valueint || v->valueint < low || v->valueint > high) return false;
    *out = v->valueint; return true;
}

bool buddy_settings_parse(const cJSON *json, buddy_settings_t *out)
{
    const cJSON *schema = cJSON_GetObjectItemCaseSensitive(json, "schema");
    const cJSON *apps = cJSON_GetObjectItemCaseSensitive(json, "enabled_apps");
    const cJSON *order = cJSON_GetObjectItemCaseSensitive(json, "home_order");
    const cJSON *hours = cJSON_GetObjectItemCaseSensitive(json, "use_24h");
    const cJSON *weather = cJSON_GetObjectItemCaseSensitive(json, "weather_enabled");
    const cJSON *city = cJSON_GetObjectItemCaseSensitive(json, "city");
    const cJSON *lat = cJSON_GetObjectItemCaseSensitive(json, "latitude");
    const cJSON *lon = cJSON_GetObjectItemCaseSensitive(json, "longitude");
    const cJSON *tz = cJSON_GetObjectItemCaseSensitive(json, "timezone");
    if (!cJSON_IsObject(json) || !cJSON_IsNumber(schema) || (schema->valuedouble != 2 && schema->valuedouble != 3 && schema->valuedouble != 4 && schema->valuedouble != 5 && schema->valuedouble != BUDDY_SETTINGS_SCHEMA) ||
        !cJSON_IsNumber(apps) || apps->valuedouble != apps->valueint || !(apps->valueint & 32) || apps->valueint < 0 || apps->valueint > (schema->valueint == 2 ? 63 : schema->valueint < 6 ? 127 : 255) ||
        !cJSON_IsArray(order) || cJSON_GetArraySize(order) > BUDDY_HOME_SLOTS ||
        !cJSON_IsBool(hours) || !cJSON_IsBool(weather) ||
        !cJSON_IsString(city) || strlen(city->valuestring) < 1 || strlen(city->valuestring) >= sizeof(out->city) ||
        !cJSON_IsString(tz) || strlen(tz->valuestring) < 3 || strlen(tz->valuestring) >= sizeof(out->timezone) ||
        !cJSON_IsNumber(lat) || !isfinite(lat->valuedouble) || fabs(lat->valuedouble) > 90 ||
        !cJSON_IsNumber(lon) || !isfinite(lon->valuedouble) || fabs(lon->valuedouble) > 180) return false;
    for (const char *p = tz->valuestring; *p; ++p)
        if (!strchr("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+,.:/<>-", *p)) return false;
    buddy_settings_t value = {.enabled_apps = (uint8_t)(apps->valueint | (schema->valueint == 2 ? 64 : 0) | (schema->valueint < 6 ? 128 : 0)), .use_24h = cJSON_IsTrue(hours),
        .weather_enabled = cJSON_IsTrue(weather), .latitude = lat->valuedouble, .longitude = lon->valuedouble,
        .dim_behavior = 1, .dim_after_seconds = 60, .dim_brightness = 40,
        .night_enabled = true, .night_start = 1, .night_end = 10, .offline_app = 1};
    if (schema->valueint >= 3) {
        int behavior, delay, brightness, start, end, offline;
        const cJSON *night = cJSON_GetObjectItemCaseSensitive(json, "night_enabled");
        if (!integer(json,"dim_behavior",0,2,&behavior) || !integer(json,"dim_after_seconds",15,3600,&delay) ||
            !integer(json,"dim_brightness",5,90,&brightness) || !integer(json,"night_start",0,23,&start) ||
            !integer(json,"night_end",0,23,&end) || start == end || !cJSON_IsBool(night) ||
            !integer(json,"offline_app",0,schema->valueint<6?6:7,&offline)) return false;
        value.dim_behavior=behavior; value.dim_after_seconds=delay; value.dim_brightness=brightness;
        value.night_enabled=cJSON_IsTrue(night); value.night_start=start; value.night_end=end; value.offline_app=offline;
    }
    strcpy(value.language,"en");
    if(schema->valueint>=4){
        const cJSON *language=cJSON_GetObjectItemCaseSensitive(json,"language");
        if(!cJSON_IsString(language)||!buddy_language_supported(language->valuestring))return false;
        strlcpy(value.language,language->valuestring,sizeof(value.language));
    }
    if(schema->valueint>=5){
        const cJSON *fahrenheit=cJSON_GetObjectItemCaseSensitive(json,"weather_fahrenheit");
        if(!cJSON_IsBool(fahrenheit))return false;
        value.weather_fahrenheit=cJSON_IsTrue(fahrenheit);
    }
    unsigned used = 0;
    const cJSON *item;
    cJSON_ArrayForEach(item, order) {
        if (!cJSON_IsNumber(item) || item->valuedouble != item->valueint || item->valueint < 0 || item->valueint > (schema->valueint<6?6:7) || item->valueint == 5) return false;
        unsigned bit = 1U << item->valueint;
        if ((used & bit) || !(value.enabled_apps & bit)) return false;
        used |= bit;
        value.home_order[value.home_count++] = (uint8_t)item->valueint;
    }
    strlcpy(value.city, city->valuestring, sizeof(value.city));
    strlcpy(value.timezone, tz->valuestring, sizeof(value.timezone));
    *out = value;
    return true;
}

cJSON *buddy_settings_json(const buddy_settings_t *s)
{
    cJSON *json = cJSON_CreateObject();
    if (!json) return NULL;
    cJSON_AddNumberToObject(json, "schema", BUDDY_SETTINGS_SCHEMA);
    cJSON_AddStringToObject(json,"language",s->language[0]?s->language:"en");
    cJSON_AddNumberToObject(json, "enabled_apps", s->enabled_apps);
    cJSON_AddBoolToObject(json, "use_24h", s->use_24h);
    cJSON_AddBoolToObject(json, "weather_enabled", s->weather_enabled);
    cJSON_AddBoolToObject(json, "weather_fahrenheit", s->weather_fahrenheit);
    cJSON_AddStringToObject(json, "city", s->city);
    cJSON_AddStringToObject(json, "timezone", s->timezone);
    cJSON_AddNumberToObject(json, "latitude", s->latitude);
    cJSON_AddNumberToObject(json, "longitude", s->longitude);
    cJSON_AddNumberToObject(json, "dim_behavior", s->dim_behavior);
    cJSON_AddNumberToObject(json, "dim_after_seconds", s->dim_after_seconds);
    cJSON_AddNumberToObject(json, "dim_brightness", s->dim_brightness);
    cJSON_AddBoolToObject(json, "night_enabled", s->night_enabled);
    cJSON_AddNumberToObject(json, "night_start", s->night_start);
    cJSON_AddNumberToObject(json, "night_end", s->night_end);
    cJSON_AddNumberToObject(json, "offline_app", s->offline_app);
    cJSON *order = cJSON_AddArrayToObject(json, "home_order");
    for (unsigned i = 0; i < s->home_count; ++i) cJSON_AddItemToArray(order, cJSON_CreateNumber(s->home_order[i]));
    return json;
}

buddy_settings_t buddy_settings_get(void)
{
    portENTER_CRITICAL(&settings_lock);
    buddy_settings_t copy = current;
    portEXIT_CRITICAL(&settings_lock);
    return copy;
}

esp_err_t buddy_settings_save(const buddy_settings_t *settings)
{
    cJSON *json = buddy_settings_json(settings);
    char *encoded = cJSON_PrintUnformatted(json);
    cJSON_Delete(json);
    if (!encoded) return ESP_ERR_NO_MEM;
    nvs_handle_t nvs;
    esp_err_t result = nvs_open("buddy", NVS_READWRITE, &nvs);
    if (result == ESP_OK) {
        result = nvs_set_str(nvs, "display_cfg", encoded);
        if (result == ESP_OK) result = nvs_commit(nvs);
        nvs_close(nvs);
    }
    free(encoded);
    if (result == ESP_OK) {
        portENTER_CRITICAL(&settings_lock);
        uint32_t revision = current.revision + 1;
        current = *settings; current.revision = revision;
        portEXIT_CRITICAL(&settings_lock);
    }
    return result;
}

esp_err_t buddy_settings_init(void)
{
    current = (buddy_settings_t){.revision = 1, .enabled_apps = 255, .home_count = 5,
        .home_order = {0,4,7,2,1}, .use_24h = true, .weather_enabled = true,
        .language = "en", .city = "Weather", .timezone = "UTC0", .dim_behavior = 1, .dim_after_seconds = 60,
        .dim_brightness = 40, .night_enabled = true, .night_start = 1, .night_end = 10, .offline_app = 1};
    nvs_handle_t nvs;
    esp_err_t result = nvs_open("buddy", NVS_READONLY, &nvs);
    if (result == ESP_ERR_NVS_NOT_FOUND) return ESP_OK;
    if (result != ESP_OK) return result;
    char saved[1536]; size_t size = sizeof(saved);
    if (nvs_get_str(nvs, "display_cfg", saved, &size) == ESP_OK) {
        cJSON *json = cJSON_Parse(saved); buddy_settings_t restored;
        const cJSON *saved_language=cJSON_GetObjectItemCaseSensitive(json,"language");
        if(cJSON_IsString(saved_language)&&!buddy_language_supported(saved_language->valuestring))cJSON_ReplaceItemInObjectCaseSensitive(json,"language",cJSON_CreateString("en"));
        bool valid = buddy_settings_parse(json, &restored); cJSON_Delete(json);
        if (valid) { current = restored; current.revision = 1; nvs_close(nvs); return ESP_OK; }
    }
    size=sizeof(current.language); nvs_get_str(nvs,"language",current.language,&size);
    if(!buddy_language_supported(current.language))strcpy(current.language,"en");
    size = sizeof(current.city); nvs_get_str(nvs, "city", current.city, &size);
    size = sizeof(current.timezone); nvs_get_str(nvs, "timezone", current.timezone, &size);
    char value[32] = "0"; size = sizeof(value); nvs_get_str(nvs, "latitude", value, &size); current.latitude = atof(value);
    strcpy(value, "0"); size = sizeof(value); nvs_get_str(nvs, "longitude", value, &size); current.longitude = atof(value);
    nvs_close(nvs);
    return ESP_OK;
}
