#include "buddy_language.h"
#include "buddy_settings.h"
#include <string.h>
#include "buddy_weather.h"
#include "buddy_weather_config.h"
#include "buddy_weather_parse.h"

#include <stdlib.h>
#include <stdio.h>
#include "nvs.h"
#include <time.h>
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_log.h"
#include "esp_heap_caps.h"
#include "esp_netif.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#define REFRESH_US (15LL * 60 * 1000000)
#define RETRY_US (5LL * 60 * 1000000)
#define STALE_SECONDS (30U * 60)
#define EXPIRE_SECONDS (6U * 60 * 60)
#define RESPONSE_CAPACITY BUDDY_WEATHER_RESPONSE_CAPACITY

static const char *TAG = "buddy_weather";
static char weather_url[768];
static char weather_city[64] = "Weather";
static SemaphoreHandle_t weather_lock;
static buddy_weather_t cached;
static int64_t received_us;

const char *buddy_weather_city(void) { buddy_settings_t s = buddy_settings_get(); strlcpy(weather_city, strcmp(s.city,"Weather")==0?BT(BT_WEATHER):s.city, sizeof(weather_city)); return weather_city; }

static bool wifi_ready(void)
{
    esp_netif_t *station = esp_netif_get_handle_from_ifkey("WIFI_STA_DEF");
    esp_netif_ip_info_t ip;
    return station && esp_netif_is_netif_up(station) &&
           esp_netif_get_ip_info(station, &ip) == ESP_OK && ip.ip.addr != 0;
}

typedef struct {
    const char *state;
    int http_status, bytes;
    esp_err_t error;
    int tls_error, tls_flags;
} fetch_diagnostic_t;

static bool fetch_weather(buddy_weather_t *result, fetch_diagnostic_t *diagnostic)
{
    *diagnostic = (fetch_diagnostic_t){.state = "allocation"};
    /* Forecast bodies do not need scarce internal RAM used by TLS and DMA. */
    char *body = heap_caps_calloc(1, RESPONSE_CAPACITY, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (!body) { diagnostic->error = ESP_ERR_NO_MEM; return false; }
    const esp_http_client_config_t config = {
        .url = weather_url,
        .crt_bundle_attach = esp_crt_bundle_attach,
        .timeout_ms = 10000,
        .buffer_size = 1024,
        .buffer_size_tx = 1024,
        .disable_auto_redirect = true,
        .user_agent = "ClawPal/0.7.0 (desk weather)",
    };
    diagnostic->state = "init";
    esp_http_client_handle_t client = esp_http_client_init(&config);
    bool success = false;
    if (client) {
        diagnostic->state = "connect";
        diagnostic->error = esp_http_client_open(client, 0);
        if (diagnostic->error == ESP_OK) {
            diagnostic->state = "headers";
            int64_t length = esp_http_client_fetch_headers(client);
            diagnostic->http_status = esp_http_client_get_status_code(client);
            if (length >= 0 && length < RESPONSE_CAPACITY) {
                diagnostic->state = "http";
                if (diagnostic->http_status == 200) {
                    diagnostic->state = "body";
                    int bytes = esp_http_client_read_response(client, body, RESPONSE_CAPACITY - 1);
                    diagnostic->bytes = bytes;
                    if (bytes > 0 && bytes < RESPONSE_CAPACITY - 1 &&
                        esp_http_client_is_complete_data_received(client)) {
                        body[bytes] = '\0';
                        diagnostic->state = "parse";
                        success = buddy_weather_parse(body, (size_t)bytes + 1, time(NULL), result);
                        if (success) diagnostic->state = "ok";
                    }
                }
            }
        }
    }
    if (client) {
        if (!success) esp_http_client_get_and_clear_last_tls_error(client, &diagnostic->tls_error, &diagnostic->tls_flags);
        esp_http_client_cleanup(client);
    }
    free(body);
    return success;
}

static void weather_task(void *argument)
{
    int64_t next_fetch_us = 0;
    buddy_settings_t previous = {0};
    for (;;) {
        buddy_settings_t settings = buddy_settings_get();
        if (previous.revision == 0 || settings.latitude != previous.latitude || settings.longitude != previous.longitude ||
            settings.weather_enabled != previous.weather_enabled || ((settings.enabled_apps ^ previous.enabled_apps) & 64) || strcmp(settings.city, previous.city)) {
            snprintf(weather_url, sizeof(weather_url), "https://api.open-meteo.com/v1/forecast?latitude=%.6f&longitude=%.6f&current=temperature_2m,weather_code,is_day,apparent_temperature,relative_humidity_2m,wind_speed_10m,precipitation&hourly=temperature_2m,weather_code,precipitation_probability,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_hours=25&forecast_days=5&timezone=auto&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm&timeformat=unixtime", settings.latitude, settings.longitude);
            xSemaphoreTake(weather_lock, portMAX_DELAY); memset(&cached, 0, sizeof(cached)); received_us = 0; xSemaphoreGive(weather_lock);
            next_fetch_us = 0;
        }
        previous = settings;
        /* Time is needed for TLS verification, independently of the paired PC. */
        if ((settings.weather_enabled || (settings.enabled_apps & 64)) && wifi_ready() && time(NULL) >= 1700000000 && esp_timer_get_time() >= next_fetch_us) {
            buddy_weather_t reading;
            fetch_diagnostic_t diagnostic;
            bool success = fetch_weather(&reading, &diagnostic);
            int64_t now = esp_timer_get_time();
            xSemaphoreTake(weather_lock, portMAX_DELAY);
            if (success) {
                cached = reading;
                received_us = now;
            } else {
                cached.failed = true;
            }
            cached.fetch_state = diagnostic.state;
            cached.http_status = diagnostic.http_status;
            cached.response_bytes = diagnostic.bytes;
            cached.fetch_error = diagnostic.error;
            cached.tls_error = diagnostic.tls_error;
            cached.tls_flags = diagnostic.tls_flags;
            xSemaphoreGive(weather_lock);
            next_fetch_us = now + (success ? REFRESH_US : RETRY_US);
            if (success) ESP_LOGI(TAG, "Current conditions and forecasts refreshed");
            else ESP_LOGW(TAG, "Weather unavailable; retrying in five minutes");
        }
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

esp_err_t buddy_weather_start(void)
{
    if (weather_lock) return ESP_ERR_INVALID_STATE;
    weather_lock = xSemaphoreCreateMutex();
    if (!weather_lock) return ESP_ERR_NO_MEM;
    if (xTaskCreate(weather_task, "buddy_weather", 8192, NULL, 2, NULL) != pdPASS) {
        vSemaphoreDelete(weather_lock);
        weather_lock = NULL;
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

buddy_weather_t buddy_weather_get(void)
{
    if (!weather_lock) return (buddy_weather_t){.failed = true};
    xSemaphoreTake(weather_lock, portMAX_DELAY);
    buddy_weather_t result = cached;
    int64_t updated = received_us;
    xSemaphoreGive(weather_lock);
    if (!wifi_ready()) result.failed = true;
    if (result.available) {
        int64_t age = result.age_s + (esp_timer_get_time() - updated) / 1000000;
        result.age_s = (uint32_t)(age > UINT32_MAX ? UINT32_MAX : age);
        result.stale = result.failed || result.age_s >= STALE_SECONDS;
        result.available = result.age_s < EXPIRE_SECONDS;
    }
    return result;
}
