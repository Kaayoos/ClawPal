#include "buddy_language.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>
#include <time.h>

#include "board.h"
#include "buddy_model.h"
#include "buddy_network.h"
#include "buddy_ui.h"
#include "buddy_display_power.h"
#include "buddy_weather.h"
#include "buddy_settings.h"
#include "buddy_update.h"
#include "esp_app_desc.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_sntp.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"
#include "nvs_flash.h"
#include "nvs.h"

static const char *TAG = "buddyos";

typedef enum { EVENT_STATE, EVENT_STATS, EVENT_CONFIRM } event_kind_t;
typedef struct {
    event_kind_t kind;
    union {
        buddy_stats_t stats;
        buddy_confirm_action_t confirm;
        struct {
            buddy_connection_state_t state;
            char ssid[33];
            char ip[48];
            char detail[256];
        } connection;
    } data;
} ui_event_t;

static QueueHandle_t ui_events;

static void on_state(buddy_connection_state_t state, const char *ssid,
                     const char *ip, const char *detail)
{
    ui_event_t event = {.kind = EVENT_STATE};
    event.data.connection.state = state;
    strlcpy(event.data.connection.ssid, ssid ? ssid : "", sizeof(event.data.connection.ssid));
    strlcpy(event.data.connection.ip, ip ? ip : "", sizeof(event.data.connection.ip));
    strlcpy(event.data.connection.detail, detail ? detail : "", sizeof(event.data.connection.detail));
    xQueueSend(ui_events, &event, 0);
}

static void on_stats(const buddy_stats_t *stats)
{
    ui_event_t event = {.kind = EVENT_STATS};
    event.data.stats = *stats;
    xQueueSend(ui_events, &event, 0);
}

static void on_confirm(buddy_confirm_action_t action)
{
    /* Dropped prompts are harmless: the companion repeats the request while it waits. */
    ui_event_t event = {.kind = EVENT_CONFIRM};
    event.data.confirm = action;
    xQueueSend(ui_events, &event, 0);
}

static void update_clock(void)
{
    buddy_settings_t preferences = buddy_settings_get();
    static uint32_t revision;
    if (revision != preferences.revision) {
        setenv("TZ", preferences.timezone, 1); tzset();
        buddy_ui_apply_settings(&preferences); revision = preferences.revision;
    }
    buddy_weather_t weather = buddy_weather_get();
    buddy_ui_set_weather(&weather);
    time_t now;
    time(&now);
    if (now < 1700000000) {
        buddy_ui_set_clock("--:--", BT(BT_WAITING_FOR_TIME), false);
        return;
    }
    struct tm local;
    localtime_r(&now, &local);
    char clock_text[16], date_text[96];
    strftime(clock_text, sizeof(clock_text), preferences.use_24h ? "%H:%M" : "%I:%M", &local);
    if (!preferences.use_24h && clock_text[0] == '0') memmove(clock_text, clock_text + 1, strlen(clock_text));
    buddy_date(date_text,sizeof(date_text),&local,preferences.use_24h);
    buddy_ui_set_clock(clock_text, date_text, BUDDY_NIGHT_RANGE(true, preferences.night_enabled, local.tm_hour, preferences.night_start, preferences.night_end));
}

void app_main(void)
{
    ESP_LOGI(TAG, "ClawPal %s starting", esp_app_get_description()->version);
    buddy_update_boot_guard();
    esp_err_t err = nvs_flash_init();
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "NVS init failed (%s); preserving provisioned settings", esp_err_to_name(err));
        return;
    }
    ESP_ERROR_CHECK(buddy_settings_init());
    buddy_language_set(buddy_settings_get().language);
    ui_events = xQueueCreate(12, sizeof(ui_event_t));
    ESP_ERROR_CHECK(ui_events ? ESP_OK : ESP_ERR_NO_MEM);
    ESP_ERROR_CHECK(board_init());
    char timezone[128] = "UTC0";
    nvs_handle_t settings;
    if (nvs_open("buddy", NVS_READONLY, &settings) == ESP_OK) {
        size_t size = sizeof(timezone); nvs_get_str(settings, "timezone", timezone, &size); nvs_close(settings);
    }
    setenv("TZ", timezone, 1);
    tzset();
    board_lvgl_lock(-1);
    buddy_ui_init();
    buddy_ui_set_music_control_callback(buddy_network_music_control);
    buddy_ui_set_confirm_callback(buddy_network_confirm_result);
    buddy_ui_set_connection(BUDDY_WIFI_CONNECTING, "", "", BT(BT_STARTING_WI_FI));
    update_clock();
    board_lvgl_unlock();

    const buddy_network_callbacks_t callbacks = {.on_state = on_state, .on_stats = on_stats, .on_confirm = on_confirm};
    err = buddy_network_start(&callbacks);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "Network startup failed: %s", esp_err_to_name(err));
        on_state(BUDDY_PC_OFFLINE, "", "", BT(BT_NETWORK_STARTUP_FAILED_RESTART_BUDDY));
    }
    esp_sntp_setoperatingmode(SNTP_OPMODE_POLL);
    esp_sntp_setservername(0, "pool.ntp.org");
    esp_sntp_setservername(1, "time.cloudflare.com");
    esp_sntp_init();
    if (err == ESP_OK) {
        esp_err_t weather_error = buddy_weather_start();
        if (weather_error != ESP_OK)
            ESP_LOGW(TAG, "Weather startup failed: %s", esp_err_to_name(weather_error));
    }

    int64_t last_clock_us = 0;
    int64_t last_log_us = 0;
    ui_event_t event;
    for (;;) {
        if (err == ESP_OK) buddy_update_boot_ready();
        bool received = xQueueReceive(ui_events, &event, pdMS_TO_TICKS(100)) == pdTRUE;
        int64_t now_us = esp_timer_get_time();
        if (received && event.kind == EVENT_STATS) {
            /* A paired PC supplies time even when internet time is unavailable. */
            time_t current;
            time(&current);
            if (current < 1700000000 && event.data.stats.timestamp_ms > 1700000000000LL) {
                struct timeval tv = {.tv_sec = event.data.stats.timestamp_ms / 1000,
                                     .tv_usec = (event.data.stats.timestamp_ms % 1000) * 1000};
                settimeofday(&tv, NULL);
            }
        }
        if (received || now_us - last_clock_us >= 1000000) {
            board_lvgl_lock(-1);
            if (received && event.kind == EVENT_STATE) {
                buddy_ui_set_connection(event.data.connection.state,
                    event.data.connection.ssid, event.data.connection.ip, event.data.connection.detail);
            } else if (received && event.kind == EVENT_CONFIRM) {
                buddy_ui_request_confirmation(event.data.confirm);
            } else if (received) {
                buddy_ui_update_stats(&event.data.stats);
                if (last_log_us == 0 || now_us - last_log_us >= 60000000) {
                    ESP_LOGI(TAG, "Live PC stats received, sequence=%llu, CPU=%.1f%%, GPU=%.1f%%",
                             (unsigned long long)event.data.stats.sequence,
                             event.data.stats.cpu_usage, event.data.stats.gpu_usage);
                    last_log_us = now_us;
                }
            }
            if (now_us - last_clock_us >= 1000000) {
                update_clock();
                last_clock_us = now_us;
            }
            board_lvgl_unlock();
        }
    }
}
