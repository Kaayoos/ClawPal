#include "buddy_language.h"
#include "buddy_settings.h"
#include "buddy_update.h"
#include "buddy_network.h"
#include "buddy_telemetry_parse.h"
#include "buddy_auth.h"
#include "esp_app_desc.h"
#include "board.h"
#include "buddy_ui.h"
#include "buddy_weather.h"

#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <sys/socket.h>
#include <unistd.h>

#include "cJSON.h"
#include "esp_check.h"
#include "esp_event.h"
#include "esp_http_server.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_netif.h"
#include "esp_random.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "lwip/inet.h"
#include "nvs.h"
#include "lvgl.h"

#define DISCOVERY_PORT 42170
#define MONITOR_PORT 42171
#define MAX_MESSAGE 6144
#define CONNECTED_BIT BIT0
#define RESTART_BIT BIT1
#define PC_TIMEOUT_US (10LL * 1000 * 1000)
#define SETUP_FALLBACK_US (60LL * 1000 * 1000)
#define CONTROL_QUEUE_TIMEOUT_US (3LL * 1000 * 1000)

typedef enum {
    MUSIC_PLAY_PAUSE = 1,
    MUSIC_NEXT = 2,
    MUSIC_PREVIOUS = 4,
} music_action_t;

typedef struct {
    int fd;
    uint32_t id;
    uint32_t session_generation;
    int64_t queued_at_us;
    music_action_t action;
} music_command_t;

static const char *TAG = "buddy_net";
static buddy_network_callbacks_t callbacks;
static EventGroupHandle_t events;
static SemaphoreHandle_t state_lock;
static esp_netif_t *station_interface;
static httpd_handle_t monitor_server;
static httpd_handle_t setup_server;
static char station_ssid[33];
static char station_password[65];
static char shared_token[65];
static char setup_password[65];
static char device_id[13];
static char setup_ssid[33];
static char station_ip[16];
static bool provisioning;
static bool has_report;
static int companion_fd = -1;
static uint32_t companion_generation;
static unsigned music_capabilities;
static QueueHandle_t music_command_queue;
static music_command_t pending_music_command;
static bool music_command_pending;
static uint32_t next_music_control_id;
static int64_t last_frame_us;
static int64_t disconnected_since_us;
static buddy_connection_state_t published_state = (buddy_connection_state_t)-1;
static const char websocket_authorized;
static SemaphoreHandle_t auth_mutex;

/* Firmware and Wi-Fi changes need a tap on the display. Requests are signed (see
 * buddy_auth.h), but LAN traffic is not encrypted, and someone who learned the
 * pairing token elsewhere must still not be able to replace either remotely. */
#define CONFIRM_PROMPT_US 60000000LL
#define CONFIRM_GRANT_US 120000000LL
#define CONFIRM_DENIED_US 15000000LL
static portMUX_TYPE confirm_lock = portMUX_INITIALIZER_UNLOCKED;
static buddy_confirm_action_t confirm_pending, confirm_granted, confirm_denied;
static int64_t confirm_pending_until, confirm_granted_until, confirm_denied_until;

static void publish_state(buddy_connection_state_t state, const char *detail)
{
    published_state = state;
    if (callbacks.on_state) {
        char ip[sizeof(station_ip)];
        xSemaphoreTake(state_lock, portMAX_DELAY);
        strlcpy(ip, station_ip, sizeof(ip));
        xSemaphoreGive(state_lock);
        callbacks.on_state(state, station_ssid, ip, detail ? detail : "");
    }
}

static esp_err_t load_string(nvs_handle_t nvs, const char *name, char *out, size_t capacity)
{
    size_t size = capacity;
    out[0] = '\0';
    esp_err_t result = nvs_get_str(nvs, name, out, &size);
    if (result == ESP_ERR_NVS_NOT_FOUND) {
        return ESP_OK;
    }
    return result;
}

static void random_hex(char *out, size_t bytes)
{
    const char hex[] = "0123456789abcdef";
    uint8_t random[32];
    esp_fill_random(random, bytes);
    for (size_t i = 0; i < bytes; ++i) {
        out[2 * i] = hex[random[i] >> 4];
        out[2 * i + 1] = hex[random[i] & 15];
    }
    out[2 * bytes] = '\0';
    memset(random, 0, sizeof(random));
}

static bool is_hex_string(const char *value, size_t expected_length)
{
    if (strlen(value) != expected_length) {
        return false;
    }
    for (size_t i = 0; i < expected_length; ++i) {
        if (!isxdigit((unsigned char)value[i])) {
            return false;
        }
    }
    return true;
}

static esp_err_t load_settings(void)
{
    nvs_handle_t nvs;
    ESP_RETURN_ON_ERROR(nvs_open("buddy", NVS_READWRITE, &nvs), TAG, "Open settings");
    esp_err_t result = load_string(nvs, "ssid", station_ssid, sizeof(station_ssid));
    if (result == ESP_OK) result = load_string(nvs, "password", station_password, sizeof(station_password));
    if (result == ESP_OK) result = load_string(nvs, "token", shared_token, sizeof(shared_token));
    if (result == ESP_OK) result = load_string(nvs, "setup_key", setup_password, sizeof(setup_password));
    if (result == ESP_OK && !is_hex_string(shared_token, 64)) {
        random_hex(shared_token, 32);
        result = nvs_set_str(nvs, "token", shared_token);
    }
    if (result == ESP_OK && (strlen(setup_password) < 8 || strlen(setup_password) > 63)) {
        random_hex(setup_password, 8);
        result = nvs_set_str(nvs, "setup_key", setup_password);
    }
    if (result == ESP_OK) result = nvs_commit(nvs);
    nvs_close(nvs);
    return result;
}

static void auth_lock(void) { xSemaphoreTake(auth_mutex, portMAX_DELAY); }
static void auth_unlock(void) { xSemaphoreGive(auth_mutex); }

/* The body hash the request was signed with; no header means an empty body. */
static bool declared_body_hash(httpd_req_t *request, char out[65])
{
    size_t length = httpd_req_get_hdr_value_len(request, "X-ClawPal-Body-SHA256");
    if (length == 0) {
        strlcpy(out, BUDDY_AUTH_EMPTY_BODY, 65);
        return true;
    }
    return length == 64 && httpd_req_get_hdr_value_str(request, "X-ClawPal-Body-SHA256", out, 65) == ESP_OK &&
           buddy_auth_is_hex(out, 64);
}

static bool authenticated(httpd_req_t *request)
{
    char authorization[160], body_hash[65];
    size_t length = httpd_req_get_hdr_value_len(request, "Authorization");
    const char *method = request->method == HTTP_GET ? "GET" : request->method == HTTP_POST ? "POST" : NULL;
    if (!method || length == 0 || length >= sizeof(authorization) ||
        httpd_req_get_hdr_value_str(request, "Authorization", authorization, sizeof(authorization)) != ESP_OK ||
        !declared_body_hash(request, body_hash)) return false;
    buddy_auth_result_t result = buddy_auth_verify(authorization, method, request->uri, body_hash, esp_timer_get_time());
    if (result != BUDDY_AUTH_OK) ESP_LOGW(TAG, "Refused %s %s (%d)", method, request->uri, (int)result);
    return result == BUDDY_AUTH_OK;
}

static esp_err_t require_auth(httpd_req_t *request)
{
    if (authenticated(request)) return ESP_OK;
    httpd_resp_set_status(request, "401 Unauthorized");
    httpd_resp_set_hdr(request, "WWW-Authenticate", "ClawPal-HMAC");
    httpd_resp_sendstr(request, BT(BT_AUTHENTICATION_REQUIRED));
    return ESP_FAIL;
}

void buddy_network_confirm_result(buddy_confirm_action_t action, bool approved)
{
    int64_t now = esp_timer_get_time();
    taskENTER_CRITICAL(&confirm_lock);
    if (action != BUDDY_CONFIRM_NONE && confirm_pending == action && now < confirm_pending_until) {
        confirm_pending = BUDDY_CONFIRM_NONE;
        if (approved) { confirm_granted = action; confirm_granted_until = now + CONFIRM_GRANT_US; }
        else { confirm_denied = action; confirm_denied_until = now + CONFIRM_DENIED_US; }
    }
    taskEXIT_CRITICAL(&confirm_lock);
}

/* One approval authorizes exactly one request. */
static esp_err_t require_confirmation(httpd_req_t *request, buddy_confirm_action_t action)
{
    int64_t now = esp_timer_get_time();
    taskENTER_CRITICAL(&confirm_lock);
    bool granted = confirm_granted == action && now < confirm_granted_until;
    if (granted) confirm_granted = BUDDY_CONFIRM_NONE;
    taskEXIT_CRITICAL(&confirm_lock);
    if (granted) return ESP_OK;
    httpd_resp_set_status(request, "403 Forbidden");
    httpd_resp_set_hdr(request, "X-ClawPal-Confirm", "required");
    httpd_resp_sendstr(request, BT(BT_CONFIRM_ON_DISPLAY_FIRST));
    return ESP_FAIL;
}

static void preserve_auth_marker(void *context)
{
    /* Marker has static storage, so HTTPD must not free it. */
}

static esp_err_t websocket_auth(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    request->sess_ctx = (void *)&websocket_authorized;
    request->free_ctx = preserve_auth_marker;
    return ESP_OK;
}

static void invalidate_companion_locked(void)
{
    ++companion_generation;
    music_capabilities = 0;
    music_command_pending = false;
}

static const char *music_action_name(music_action_t action)
{
    switch (action) {
    case MUSIC_PLAY_PAUSE: return "play_pause";
    case MUSIC_NEXT: return "next";
    case MUSIC_PREVIOUS: return "previous";
    default: return NULL;
    }
}

static void expire_music_command_locked(int64_t now)
{
    if (music_command_pending && now - pending_music_command.queued_at_us > CONTROL_QUEUE_TIMEOUT_US) {
        music_command_pending = false;
    }
}

/* Runs only on HTTPD's task, serially with telemetry ACKs and WS control frames.
 * The work argument is an ID, not allocated storage: even a dropped HTTPD
 * control datagram cannot leak a context or later reuse a stale command slot. */
static void send_music_command(void *argument)
{
    uint32_t id = (uint32_t)(uintptr_t)argument;
    music_command_t command;
    xSemaphoreTake(state_lock, portMAX_DELAY);
    int64_t now = esp_timer_get_time();
    expire_music_command_locked(now);
    bool valid = music_command_pending && pending_music_command.id == id &&
                 pending_music_command.session_generation == companion_generation &&
                 pending_music_command.fd == companion_fd && has_report &&
                 now - last_frame_us <= PC_TIMEOUT_US &&
                 (music_capabilities & pending_music_command.action) != 0;
    if (valid) command = pending_music_command;
    if (music_command_pending && pending_music_command.id == id) music_command_pending = false;
    xSemaphoreGive(state_lock);
    if (!valid || !(xEventGroupGetBits(events) & CONNECTED_BIT) ||
        httpd_ws_get_fd_info(monitor_server, command.fd) != HTTPD_WS_CLIENT_WEBSOCKET) return;

    const char *action = music_action_name(command.action);
    if (!action) return;
    char payload[128];
    int length = snprintf(payload, sizeof(payload),
        "{\"type\":\"buddy.control\",\"v\":1,\"id\":%lu,\"action\":\"%s\"}",
        (unsigned long)command.id, action);
    if (length <= 0 || length >= sizeof(payload)) return;
    httpd_ws_frame_t frame = {
        .type = HTTPD_WS_TYPE_TEXT, .payload = (uint8_t *)payload,
        .len = (size_t)length, .final = true,
    };
    if (httpd_ws_send_frame_async(monitor_server, command.fd, &frame) != ESP_OK) {
        httpd_sess_trigger_close(monitor_server, command.fd);
    }
}

static void music_control_task(void *argument)
{
    uint32_t id;
    while (true) {
        if (xQueueReceive(music_command_queue, &id, portMAX_DELAY) != pdTRUE) continue;
        xSemaphoreTake(state_lock, portMAX_DELAY);
        expire_music_command_locked(esp_timer_get_time());
        bool pending = music_command_pending && pending_music_command.id == id;
        xSemaphoreGive(state_lock);
        if (!pending) continue;
        /* httpd_queue_work may block in some IDF configurations. Keep that
         * operation off the UI/LVGL task; the UI only enqueues an integer. */
        if (httpd_queue_work(monitor_server, send_music_command, (void *)(uintptr_t)id) != ESP_OK) {
            xSemaphoreTake(state_lock, portMAX_DELAY);
            if (music_command_pending && pending_music_command.id == id) music_command_pending = false;
            xSemaphoreGive(state_lock);
        }
    }
}

bool buddy_network_music_control(const char *action)
{
    if (!action || !state_lock || !events || !music_command_queue || !monitor_server) return false;
    music_action_t selected;
    if (strcmp(action, "play_pause") == 0) selected = MUSIC_PLAY_PAUSE;
    else if (strcmp(action, "next") == 0) selected = MUSIC_NEXT;
    else if (strcmp(action, "previous") == 0) selected = MUSIC_PREVIOUS;
    else return false;
    if (!(xEventGroupGetBits(events) & CONNECTED_BIT) || xSemaphoreTake(state_lock, 0) != pdTRUE) return false;
    int64_t now = esp_timer_get_time();
    expire_music_command_locked(now);
    if (companion_fd < 0 || !has_report || now - last_frame_us > PC_TIMEOUT_US ||
        music_command_pending || (music_capabilities & selected) == 0) {
        xSemaphoreGive(state_lock);
        return false;
    }
    next_music_control_id = next_music_control_id >= INT_MAX ? 1 : next_music_control_id + 1;
    pending_music_command = (music_command_t) {
        .fd = companion_fd, .id = next_music_control_id,
        .session_generation = companion_generation, .queued_at_us = now, .action = selected,
    };
    music_command_pending = true;
    bool accepted = xQueueSend(music_command_queue, &next_music_control_id, 0) == pdTRUE;
    if (!accepted) music_command_pending = false;
    xSemaphoreGive(state_lock);
    return accepted;
}

static void close_session(httpd_handle_t server, int fd)
{
    xSemaphoreTake(state_lock, portMAX_DELAY);
    if (fd == companion_fd) {
        companion_fd = -1;
        has_report = false;
        invalidate_companion_locked();
    }
    xSemaphoreGive(state_lock);
    close(fd);
}

static esp_err_t websocket_handler(httpd_req_t *request)
{
    int fd = httpd_req_to_sockfd(request);
    if (request->method == HTTP_GET) {
        /* An ordinary HTTP GET does not run the WebSocket pre-handshake callback. */
        if (request->sess_ctx != &websocket_authorized) {
            httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, BT(BT_AUTHENTICATED_WEBSOCKET_UPGRADE_REQUIRED));
            return ESP_FAIL;
        }
        xSemaphoreTake(state_lock, portMAX_DELAY);
        int previous_fd = companion_fd;
        invalidate_companion_locked();
        companion_fd = fd;
        has_report = false;
        last_frame_us = esp_timer_get_time();
        xSemaphoreGive(state_lock);
        if (previous_fd >= 0 && previous_fd != fd) {
            httpd_sess_trigger_close(monitor_server, previous_fd);
        }
        ESP_LOGI(TAG, "Authenticated companion connected");
        return ESP_OK;
    }
    xSemaphoreTake(state_lock, portMAX_DELAY);
    bool current_session = (fd == companion_fd);
    xSemaphoreGive(state_lock);
    if (!current_session) return ESP_FAIL;

    httpd_ws_frame_t frame = {0};
    esp_err_t result = httpd_ws_recv_frame(request, &frame, 0);
    if (result != ESP_OK) return result;
    if (frame.type == HTTPD_WS_TYPE_PING || frame.type == HTTPD_WS_TYPE_PONG ||
        frame.type == HTTPD_WS_TYPE_CLOSE) {
        /* RFC 6455 control frames are final, at most 125 bytes, and a Close
         * payload is either empty or starts with a two-byte status code. */
        if (!frame.final || frame.len > 125 ||
            (frame.type == HTTPD_WS_TYPE_CLOSE && frame.len == 1)) return ESP_FAIL;
        uint8_t control_payload[125];
        if (frame.len > 0) {
            frame.payload = control_payload;
            result = httpd_ws_recv_frame(request, &frame, frame.len);
            if (result != ESP_OK) return result;
        }
        /* An empty control frame was fully consumed by the first receive.
         * Receiving it again would read the next frame as a length byte. */
        if (frame.type == HTTPD_WS_TYPE_PONG) return ESP_OK;
        if (frame.type == HTTPD_WS_TYPE_PING) frame.type = HTTPD_WS_TYPE_PONG;
        /* Echo Ping data in Pong, or acknowledge Close. HTTPD already marks
         * incoming Close sessions for disposal after this handler returns. */
        return httpd_ws_send_frame(request, &frame);
    }
    if (frame.type != HTTPD_WS_TYPE_TEXT || frame.len == 0 || frame.len > MAX_MESSAGE || !frame.final) {
        return ESP_FAIL;
    }
    uint8_t *payload = calloc(1, frame.len + 1);
    if (!payload) return ESP_ERR_NO_MEM;
    frame.payload = payload;
    result = httpd_ws_recv_frame(request, &frame, frame.len);
    buddy_stats_t stats;
    if (result == ESP_OK && !buddy_telemetry_parse((const char *)payload, frame.len, &stats)) result = ESP_ERR_INVALID_ARG;
    free(payload);
    if (result != ESP_OK) return result;
    xSemaphoreTake(state_lock, portMAX_DELAY);
    last_frame_us = esp_timer_get_time();
    has_report = true;
    music_capabilities = 0;
    if (stats.music.running && stats.music.available) {
        if (stats.music.can_play_pause) music_capabilities |= MUSIC_PLAY_PAUSE;
        if (stats.music.can_next) music_capabilities |= MUSIC_NEXT;
        if (stats.music.can_previous) music_capabilities |= MUSIC_PREVIOUS;
    }
    xSemaphoreGive(state_lock);
    if (callbacks.on_stats) callbacks.on_stats(&stats);
    char acknowledgement[96];
    int acknowledgement_length = snprintf(acknowledgement, sizeof(acknowledgement),
        "{\"type\":\"buddy.ack\",\"v\":1,\"seq\":%llu}", (unsigned long long)stats.sequence);
    httpd_ws_frame_t reply = {
        .type = HTTPD_WS_TYPE_TEXT,
        .payload = (uint8_t *)acknowledgement,
        .len = (size_t)acknowledgement_length,
        .final = true,
    };
    return httpd_ws_send_frame(request, &reply);
}

static esp_err_t health_handler(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    if (!board_lvgl_lock(1000)) {
        return httpd_resp_send_err(request, HTTPD_500_INTERNAL_SERVER_ERROR, BT(BT_DISPLAY_BUSY));
    }
    buddy_ui_state_t ui_state = buddy_ui_get_state();
    board_lvgl_unlock();
    const char *app_name;
    switch (ui_state.active_app) {
    case BUDDY_APP_MONITOR: app_name = "pc_monitor"; break;
    case BUDDY_APP_CLOCK: app_name = "clock"; break;
    case BUDDY_APP_MUSIC: app_name = "music"; break;
    case BUDDY_APP_TIMER: app_name = "timer"; break;
    case BUDDY_APP_CODEX: app_name = "codex"; break;
    case BUDDY_APP_CLAUDE: app_name = "claude"; break;
    case BUDDY_APP_HOME: app_name = "home"; break;
    case BUDDY_APP_WEATHER: app_name = "weather"; break;
    default: app_name = "unknown"; break;
    }
    buddy_weather_t weather = buddy_weather_get();
    char fps[24];
    if (isfinite(ui_state.game_fps)) snprintf(fps, sizeof(fps), "%.1f", ui_state.game_fps);
    else strlcpy(fps, "null", sizeof(fps));
    char response[1600];
    snprintf(response, sizeof(response),
        "{\"type\":\"buddy.health\",\"v\":1,\"id\":\"%s\",\"state\":%d,\"version\":\"%s\","
        "\"ui\":{\"app\":\"%s\",\"automatic\":%s,\"menu_open\":%s,"
        "\"dimmed\":%s,\"screen_off\":%s,\"night_hours\":%s,\"display_idle_ms\":%lu,"
        "\"transition\":%s,\"has_stats\":%s,\"auto_open_apps\":%s,\"last_stats_age_ms\":%lu,"
        "\"music\":{\"running\":%s,\"available\":%s,\"automatic\":%s},"
        "\"codex\":{\"running\":%s,\"available\":%s,\"stale\":%s,\"automatic\":%s,"
        "\"tasks_available\":%s,\"tasks_stale\":%s,\"working_count\":%lu,\"task_count\":%lu},"
        "\"claude\":{\"running\":%s,\"available\":%s,\"stale\":%s,\"automatic\":%s,"
        "\"tasks_available\":%s,\"tasks_stale\":%s,\"working_count\":%lu,\"task_count\":%lu},"
        "\"timer\":{\"remaining_s\":%lu,\"running\":%s,\"duration_s\":%lu,\"completed\":%s},"
        "\"game\":{\"active\":%s,\"fps\":%s}},"
        "\"weather\":{\"available\":%s,\"stale\":%s,\"failed\":%s,\"age_s\":%lu,"
        "\"hourly_count\":%u,\"daily_count\":%u,\"temperature_c\":%.1f,\"code\":%d,\"is_day\":%s,"
        "\"fetch_state\":\"%s\",\"http_status\":%d,\"response_bytes\":%d,\"fetch_error\":%d,\"tls_error\":%d,\"tls_flags\":%d}}",
        device_id, (int)published_state,
        esp_app_get_description()->version,
        app_name,
        (ui_state.offline_automatic || ui_state.music_automatic || ui_state.codex_automatic || ui_state.claude_automatic) ? "true" : "false",
        ui_state.menu_open ? "true" : "false",
        ui_state.screen_dimmed ? "true" : "false",
        ui_state.screen_off ? "true" : "false",
        ui_state.night_hours ? "true" : "false",
        (unsigned long)ui_state.display_idle_ms,
        ui_state.transition_active ? "true" : "false",
        ui_state.has_stats ? "true" : "false",
        ui_state.auto_open_apps ? "true" : "false",
        (unsigned long)ui_state.last_stats_age_ms,
        ui_state.music_running ? "true" : "false",
        ui_state.music_available ? "true" : "false",
        ui_state.music_automatic ? "true" : "false",
        ui_state.codex_running ? "true" : "false",
        ui_state.codex_available ? "true" : "false",
        ui_state.codex_stale ? "true" : "false",
        ui_state.codex_automatic ? "true" : "false",
        ui_state.codex_tasks_available ? "true" : "false",
        ui_state.codex_tasks_stale ? "true" : "false",
        (unsigned long)ui_state.codex_working_count,
        (unsigned long)ui_state.codex_task_count,
        ui_state.claude_running ? "true" : "false",
        ui_state.claude_available ? "true" : "false",
        ui_state.claude_stale ? "true" : "false",
        ui_state.claude_automatic ? "true" : "false",
        ui_state.claude_tasks_available ? "true" : "false",
        ui_state.claude_tasks_stale ? "true" : "false",
        (unsigned long)ui_state.claude_working_count,
        (unsigned long)ui_state.claude_task_count,
        (unsigned long)ui_state.timer_remaining_s,
        ui_state.timer_running ? "true" : "false",
        (unsigned long)ui_state.timer_duration_s,
        ui_state.timer_completed ? "true" : "false",
        ui_state.game_active ? "true" : "false", fps,
        weather.available ? "true" : "false",
        weather.stale ? "true" : "false",
        weather.failed ? "true" : "false",
        (unsigned long)weather.age_s,
        (unsigned)weather.hourly_count, (unsigned)weather.daily_count, (double)weather.temperature_c, weather.code,
        weather.is_day ? "true" : "false",
        weather.fetch_state ? weather.fetch_state : "waiting", weather.http_status, weather.response_bytes, (int)weather.fetch_error, weather.tls_error, weather.tls_flags);
    httpd_resp_set_type(request, "application/json");
    httpd_resp_set_hdr(request, "Cache-Control", "no-store");
    return httpd_resp_sendstr(request, response);
}

static esp_err_t snapshot_handler(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    if (!board_lvgl_lock(2000)) {
        return httpd_resp_send_err(request, HTTPD_500_INTERNAL_SERVER_ERROR, BT(BT_DISPLAY_BUSY));
    }
    lv_img_dsc_t *snapshot = lv_snapshot_take(lv_scr_act(), LV_IMG_CF_TRUE_COLOR);
    board_lvgl_unlock();
    if (!snapshot) {
        return httpd_resp_send_err(request, HTTPD_500_INTERNAL_SERVER_ERROR, BT(BT_SNAPSHOT_UNAVAILABLE));
    }
    char width[12], height[12];
    snprintf(width, sizeof(width), "%u", (unsigned)snapshot->header.w);
    snprintf(height, sizeof(height), "%u", (unsigned)snapshot->header.h);
    httpd_resp_set_type(request, "application/octet-stream");
    httpd_resp_set_hdr(request, "X-Width", width);
    httpd_resp_set_hdr(request, "X-Height", height);
    httpd_resp_set_hdr(request, "X-Format", "RGB565-LE");
    httpd_resp_set_hdr(request, "Cache-Control", "no-store");
    esp_err_t result = httpd_resp_send(request, (const char *)snapshot->data, snapshot->data_size);
    lv_snapshot_free(snapshot);
    return result;
}

static void settings_restart(void *unused)
{
    vTaskDelay(pdMS_TO_TICKS(1200));
    esp_restart();
}

static cJSON *read_settings_json(httpd_req_t *request)
{
    if (request->content_len <= 0 || request->content_len > 1535) return NULL;
    char body[1536]; int received = 0;
    while (received < request->content_len) {
        int n = httpd_req_recv(request, body + received, request->content_len - received);
        if (n <= 0) return NULL;
        received += n;
    }
    body[received] = 0;
    /* The signature covers the declared hash; these must be the bytes it described. */
    char declared[65], actual[65];
    if (!declared_body_hash(request, declared) || !buddy_auth_body_hash(body, (size_t)received, actual) ||
        strcmp(declared, actual) != 0) return NULL;
    return cJSON_ParseWithLength(body, received + 1);
}

static esp_err_t settings_get_handler(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    buddy_settings_t settings = buddy_settings_get();
    cJSON *json = buddy_settings_json(&settings);
    if (!json) return ESP_ERR_NO_MEM;
    cJSON_AddStringToObject(json, "version", esp_app_get_description()->version);
    cJSON_AddStringToObject(json, "wifi_ssid", station_ssid);
    cJSON_AddStringToObject(json, "model", CLAWPAL_BOARD_NAME);
    char *response = cJSON_PrintUnformatted(json); cJSON_Delete(json);
    if (!response) return ESP_ERR_NO_MEM;
    httpd_resp_set_type(request, "application/json");
    httpd_resp_set_hdr(request, "Cache-Control", "no-store");
    esp_err_t result = httpd_resp_sendstr(request, response); free(response); return result;
}

static esp_err_t settings_handler(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    cJSON *json = read_settings_json(request); buddy_settings_t settings;
    const cJSON *schema = cJSON_GetObjectItemCaseSensitive(json,"schema");
    bool valid = cJSON_IsNumber(schema) && schema->valuedouble == BUDDY_SETTINGS_SCHEMA && buddy_settings_parse(json, &settings); cJSON_Delete(json);
    if (!valid) return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, BT(BT_INVALID_DISPLAY_SETTINGS));
    if (buddy_settings_save(&settings) != ESP_OK)
        return httpd_resp_send_err(request, HTTPD_500_INTERNAL_SERVER_ERROR, BT(BT_COULD_NOT_SAVE_SETTINGS));
    httpd_resp_set_type(request, "application/json");
    return httpd_resp_sendstr(request, "{\"saved\":true,\"restarting\":false,\"schema\":6}");
}

/* POST {"action":"firmware"|"wifi"}: shows the prompt, then reports pending, approved or denied. */
static esp_err_t confirm_handler(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    cJSON *json = read_settings_json(request);
    const cJSON *name = cJSON_GetObjectItemCaseSensitive(json, "action");
    buddy_confirm_action_t action = !cJSON_IsString(name) ? BUDDY_CONFIRM_NONE :
        strcmp(name->valuestring, "firmware") == 0 ? BUDDY_CONFIRM_FIRMWARE :
        strcmp(name->valuestring, "wifi") == 0 ? BUDDY_CONFIRM_WIFI : BUDDY_CONFIRM_NONE;
    cJSON_Delete(json);
    if (action == BUDDY_CONFIRM_NONE) return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, "Unknown confirmation");
    int64_t now = esp_timer_get_time();
    const char *state = "pending";
    bool prompt = false;
    taskENTER_CRITICAL(&confirm_lock);
    if (confirm_granted == action && now < confirm_granted_until) state = "approved";
    else if (confirm_denied == action && now < confirm_denied_until) { state = "denied"; confirm_denied = BUDDY_CONFIRM_NONE; }
    else {
        if (confirm_pending != action || now >= confirm_pending_until) { confirm_pending = action; confirm_pending_until = now + CONFIRM_PROMPT_US; }
        prompt = true;
    }
    taskEXIT_CRITICAL(&confirm_lock);
    if (prompt && callbacks.on_confirm) callbacks.on_confirm(action);
    char response[32];
    snprintf(response, sizeof(response), "{\"state\":\"%s\"}", state);
    httpd_resp_set_type(request, "application/json");
    httpd_resp_set_hdr(request, "Cache-Control", "no-store");
    return httpd_resp_sendstr(request, response);
}

static esp_err_t wifi_settings_handler(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    if (require_confirmation(request, BUDDY_CONFIRM_WIFI) != ESP_OK) return ESP_FAIL;
    cJSON *json = read_settings_json(request);
    const cJSON *ssid = cJSON_GetObjectItemCaseSensitive(json, "ssid");
    const cJSON *password = cJSON_GetObjectItemCaseSensitive(json, "password");
    bool valid = cJSON_IsString(ssid) && strlen(ssid->valuestring) > 0 && strlen(ssid->valuestring) <= 32 && cJSON_IsString(password);
    size_t length = valid ? strlen(password->valuestring) : 0;
    valid = valid && (length == 0 || (length >= 8 && length <= 64));
    if (valid && length == 64) for (const char *p = password->valuestring; *p; ++p)
        if (!strchr("0123456789abcdefABCDEF", *p)) valid = false;
    if (!valid) { cJSON_Delete(json); return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, BT(BT_INVALID_WI_FI_DETAILS)); }
    nvs_handle_t nvs;
    esp_err_t result = nvs_open("buddy", NVS_READWRITE, &nvs);
    if (result == ESP_OK) {
        result = nvs_set_str(nvs, "ssid", ssid->valuestring);
        if (result == ESP_OK) result = nvs_set_str(nvs, "password", password->valuestring);
        if (result == ESP_OK) result = nvs_commit(nvs);
        nvs_close(nvs);
    }
    cJSON_Delete(json);
    if (result != ESP_OK) return httpd_resp_send_err(request, HTTPD_500_INTERNAL_SERVER_ERROR, BT(BT_COULD_NOT_SAVE_WI_FI));
    httpd_resp_set_type(request, "application/json");
    result = httpd_resp_sendstr(request, "{\"saved\":true,\"restarting\":true}");
    if (xTaskCreate(settings_restart, "settings_restart", 2048, NULL, 2, NULL) != pdPASS) esp_restart();
    return result;
}

static esp_err_t firmware_get_handler(httpd_req_t *request)
{ if(require_auth(request)!=ESP_OK)return ESP_FAIL;return buddy_update_info(request); }
/* The upload streams too much to buffer, so it is signed with the image hash it
 * declares; buddy_update_upload then refuses any image that does not match it. */
static bool firmware_body_declared(httpd_req_t *request)
{
    char declared[65], image[65];
    return declared_body_hash(request, declared) &&
           httpd_req_get_hdr_value_str(request, "X-ClawPal-SHA256", image, sizeof(image)) == ESP_OK &&
           strcmp(declared, image) == 0;
}
static esp_err_t firmware_post_handler(httpd_req_t *request)
{
    if (require_auth(request) != ESP_OK) return ESP_FAIL;
    if (!firmware_body_declared(request))
        return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, "Signed body hash must match the image hash");
    if (require_confirmation(request, BUDDY_CONFIRM_FIRMWARE) != ESP_OK) return ESP_FAIL;
    return buddy_update_upload(request);
}

static esp_err_t start_monitor_server(void)
{
    httpd_config_t config = HTTPD_DEFAULT_CONFIG();
    config.server_port = MONITOR_PORT;
    config.ctrl_port = MONITOR_PORT + 1;
    config.stack_size = 8192;
    config.max_open_sockets = 4;
    config.max_uri_handlers = 10;
    config.lru_purge_enable = true;
    config.close_fn = close_session;
    config.recv_wait_timeout = 3;
    config.send_wait_timeout = 3;
    ESP_RETURN_ON_ERROR(httpd_start(&monitor_server, &config), TAG, "Monitor HTTP server");
    const httpd_uri_t firmware_read={.uri="/firmware",.method=HTTP_GET,.handler=firmware_get_handler};
    const httpd_uri_t firmware_write={.uri="/firmware",.method=HTTP_POST,.handler=firmware_post_handler};
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server,&firmware_read),TAG,"Firmware read URI");
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server,&firmware_write),TAG,"Firmware write URI");
    const httpd_uri_t websocket = {
        .uri = "/monitor", .method = HTTP_GET, .handler = websocket_handler,
        .is_websocket = true, .ws_pre_handshake_cb = websocket_auth,
        .handle_ws_control_frames = true,
    };
    const httpd_uri_t settings_read = {.uri = "/settings", .method = HTTP_GET, .handler = settings_get_handler};
    const httpd_uri_t wifi_settings = {.uri = "/wifi", .method = HTTP_POST, .handler = wifi_settings_handler};
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server, &settings_read), TAG, "Read settings URI");
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server, &wifi_settings), TAG, "Wi-Fi settings URI");
    const httpd_uri_t confirm = {.uri = "/confirm", .method = HTTP_POST, .handler = confirm_handler};
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server, &confirm), TAG, "Confirmation URI");
    const httpd_uri_t settings = {.uri = "/settings", .method = HTTP_POST, .handler = settings_handler};
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server, &settings), TAG, "Settings URI");
    const httpd_uri_t health = {.uri = "/health", .method = HTTP_GET, .handler = health_handler};
    const httpd_uri_t snapshot = {.uri = "/snapshot", .method = HTTP_GET, .handler = snapshot_handler};
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server, &websocket), TAG, "WebSocket URI");
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(monitor_server, &health), TAG, "Health URI");
    return httpd_register_uri_handler(monitor_server, &snapshot);
}

static void discovery_task(void *argument)
{
    static const char prefix[] = "BUDDYOS_DISCOVER_V1 ";
    while (true) {
        int socket_fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_IP);
        if (socket_fd < 0) {
            vTaskDelay(pdMS_TO_TICKS(5000));
            continue;
        }
        struct sockaddr_in listen_address = {
            .sin_family = AF_INET, .sin_port = htons(DISCOVERY_PORT),
            .sin_addr.s_addr = htonl(INADDR_ANY),
        };
        if (bind(socket_fd, (struct sockaddr *)&listen_address, sizeof(listen_address)) != 0) {
            close(socket_fd);
            vTaskDelay(pdMS_TO_TICKS(5000));
            continue;
        }
        while (true) {
            char request[128];
            struct sockaddr_in source;
            socklen_t source_size = sizeof(source);
            int length = recvfrom(socket_fd, request, sizeof(request) - 1, 0,
                                  (struct sockaddr *)&source, &source_size);
            if (length < 0) break;
            request[length] = '\0';
            if (!(xEventGroupGetBits(events) & CONNECTED_BIT) ||
                length != (int)(sizeof(prefix) - 1 + 32) ||
                memcmp(request, prefix, sizeof(prefix) - 1) != 0) continue;
            const char *nonce = request + sizeof(prefix) - 1;
            if (!is_hex_string(nonce, 32)) continue;
            /* Protocol 2: the reply hands out a challenge for signed requests, and
             * the proof covers it so it cannot be swapped on the way. */
            uint8_t random_bytes[16];
            esp_fill_random(random_bytes, sizeof(random_bytes));
            char challenge[33], proof[65];
            buddy_auth_challenge(nonce, random_bytes, esp_timer_get_time(), challenge);
            if (!buddy_auth_hello_proof(nonce, device_id, MONITOR_PORT, challenge, proof)) continue;
            char response[280];
            int response_length = snprintf(response, sizeof(response),
                "{\"type\":\"buddy.hello\",\"v\":2,\"id\":\"%.12s\",\"name\":\"ClawPal\","
                "\"port\":%d,\"nonce\":\"%.32s\",\"challenge\":\"%.32s\",\"proof\":\"%.64s\"}",
                device_id, MONITOR_PORT, nonce, challenge, proof);
            if (response_length <= 0 || response_length >= sizeof(response)) continue;
            sendto(socket_fd, response, response_length, 0, (struct sockaddr *)&source, source_size);
        }
        close(socket_fd);
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

static esp_err_t send_html_text(httpd_req_t *request,const char *text)
{
    for(const char *p=text;*p;p++){
        const char *escaped=*p=='&'?"&amp;":*p=='<'?"&lt;":*p=='>'?"&gt;":*p=='"'?"&quot;":NULL;
        esp_err_t result=escaped?httpd_resp_send_chunk(request,escaped,strlen(escaped)):httpd_resp_send_chunk(request,p,1);
        if(result!=ESP_OK)return result;
    }
    return ESP_OK;
}
static esp_err_t send_setup_page(httpd_req_t *request,bool connecting)
{
    httpd_resp_sendstr_chunk(request,"<!doctype html><html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>");
    send_html_text(request,BT(BT_SETUP_TITLE));
    httpd_resp_sendstr_chunk(request,"</title><style>body{background:#101010;color:#f5f1eb;font:17px system-ui;margin:0;padding:28px}main{max-width:440px;margin:40px auto}h1{font-size:36px}p{color:#b1a9a0;line-height:1.5}label{display:block;margin-top:24px}input,button{box-sizing:border-box;width:100%;border:1px solid #39342f;border-radius:10px;padding:16px;font:inherit;margin-top:8px;background:#191919;color:#f5f1eb}button{background:#ff8b4f;color:#101010;margin-top:28px}</style><main><h1>");
    send_html_text(request,BT(connecting?BT_CONNECTING_HEADING:BT_SETUP_HEADING));httpd_resp_sendstr_chunk(request,"</h1><p>");
    send_html_text(request,BT(connecting?BT_CONNECTING_DESCRIPTION:BT_SETUP_INTRO));httpd_resp_sendstr_chunk(request,"</p>");
    if(!connecting){
        httpd_resp_sendstr_chunk(request,"<form method=post action=/configure><label>");send_html_text(request,BT(BT_NETWORK_NAME));
        httpd_resp_sendstr_chunk(request,"<input name=ssid required maxlength=32 autocomplete=off></label><label>");send_html_text(request,BT(BT_WIFI_PASSWORD));
        httpd_resp_sendstr_chunk(request,"<input type=password name=password maxlength=64 autocomplete=off></label><button>");send_html_text(request,BT(BT_CONNECT_CLAWPAL));
        httpd_resp_sendstr_chunk(request,"</button></form><p>");send_html_text(request,BT(BT_SETTINGS_STAY));httpd_resp_sendstr_chunk(request,"</p>");
    }
    httpd_resp_sendstr_chunk(request,"</main></html>");return httpd_resp_send_chunk(request,NULL,0);
}

/* Setup is reachable only through the device's setup interface, even in APSTA. */
static bool on_setup_interface(httpd_req_t *request)
{
    char host[40];
    if (httpd_req_get_hdr_value_str(request, "Host", host, sizeof(host)) != ESP_OK ||
        (strcmp(host, "192.168.4.1") != 0 && strcmp(host, "192.168.4.1:80") != 0)) return false;
    struct sockaddr_in local;
    socklen_t size = sizeof(local);
    return provisioning && getsockname(httpd_req_to_sockfd(request), (struct sockaddr *)&local, &size) == 0 &&
           local.sin_family == AF_INET && local.sin_addr.s_addr == inet_addr("192.168.4.1");
}

static esp_err_t setup_get(httpd_req_t *request)
{
    if (!on_setup_interface(request)) return httpd_resp_send_err(request, HTTPD_403_FORBIDDEN, BT(BT_SETUP_NETWORK_REQUIRED));
    httpd_resp_set_type(request, "text/html; charset=utf-8");
    httpd_resp_set_hdr(request, "Cache-Control", "no-store");
    return send_setup_page(request,false);
}

static int hex_digit(unsigned char value)
{
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

static bool decode_form_value(const char *source, size_t source_size, char *destination, size_t capacity)
{
    size_t output = 0;
    for (size_t i = 0; i < source_size; ++i) {
        unsigned char value = (unsigned char)source[i];
        if (value == '+') value = ' ';
        else if (value == '%') {
            if (i + 2 >= source_size) return false;
            int high = hex_digit((unsigned char)source[i + 1]);
            int low = hex_digit((unsigned char)source[i + 2]);
            if (high < 0 || low < 0) return false;
            value = (unsigned char)(high * 16 + low);
            i += 2;
        }
        if (value < 32 || value == 127 || output + 1 >= capacity) return false;
        destination[output++] = (char)value;
    }
    destination[output] = '\0';
    return true;
}

static bool form_field(const char *body, const char *key, char *out, size_t size)
{
    bool found = false;
    size_t key_size = strlen(key);
    const char *part = body;
    while (*part) {
        const char *end = strchr(part, '&');
        if (!end) end = part + strlen(part);
        const char *equals = memchr(part, '=', end - part);
        if (equals && (size_t)(equals - part) == key_size && memcmp(part, key, key_size) == 0) {
            if (found || !decode_form_value(equals + 1, end - equals - 1, out, size)) return false;
            found = true;
        }
        part = *end ? end + 1 : end;
    }
    return found;
}

static esp_err_t setup_post(httpd_req_t *request)
{
    if (!on_setup_interface(request)) return httpd_resp_send_err(request, HTTPD_403_FORBIDDEN, BT(BT_SETUP_NETWORK_REQUIRED));
    if (httpd_req_get_hdr_value_len(request, "Origin") > 0) {
        char origin[48];
        if (httpd_req_get_hdr_value_str(request, "Origin", origin, sizeof(origin)) != ESP_OK ||
            (strcmp(origin, "http://192.168.4.1") != 0 && strcmp(origin, "http://192.168.4.1:80") != 0)) {
            return httpd_resp_send_err(request, HTTPD_403_FORBIDDEN, BT(BT_USE_THIS_DEVICE_S_SETUP_PAGE));
        }
    }
    if (request->content_len <= 0 || request->content_len > 1024) {
        return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, BT(BT_INVALID_FORM_SIZE));
    }
    char content_type[80];
    if (httpd_req_get_hdr_value_str(request, "Content-Type", content_type, sizeof(content_type)) != ESP_OK ||
        strncmp(content_type, "application/x-www-form-urlencoded", 33) != 0) {
        return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST, BT(BT_USE_THE_SETUP_FORM));
    }
    char *body = calloc(1, request->content_len + 1);
    if (!body) return ESP_ERR_NO_MEM;
    int received = 0;
    while (received < request->content_len) {
        int bytes = httpd_req_recv(request, body + received, request->content_len - received);
        if (bytes <= 0) {
            free(body);
            return ESP_FAIL;
        }
        received += bytes;
    }
    char ssid[33], password[65];
    bool valid = form_field(body, "ssid", ssid, sizeof(ssid)) && ssid[0] &&
                 form_field(body, "password", password, sizeof(password)) &&
                 (strlen(password) == 0 || (strlen(password) >= 8 && strlen(password) <= 63) || is_hex_string(password, 64));
    memset(body, 0, request->content_len);
    free(body);
    if (!valid) return httpd_resp_send_err(request, HTTPD_400_BAD_REQUEST,
                                          BT(BT_USE_A_1_32_BYTE_NETWORK_NAME_AND_AN));
    nvs_handle_t nvs;
    esp_err_t result = nvs_open("buddy", NVS_READWRITE, &nvs);
    if (result == ESP_OK) {
        result = nvs_set_str(nvs, "ssid", ssid);
        if (result == ESP_OK) result = nvs_set_str(nvs, "password", password);
        if (result == ESP_OK) result = nvs_commit(nvs);
        nvs_close(nvs);
    }
    memset(password, 0, sizeof(password));
    if (result != ESP_OK) return httpd_resp_send_err(request, HTTPD_500_INTERNAL_SERVER_ERROR, BT(BT_COULD_NOT_SAVE_SETTINGS));
    httpd_resp_set_type(request, "text/html; charset=utf-8");
    send_setup_page(request,true);
    xEventGroupSetBits(events, RESTART_BIT);
    return ESP_OK;
}

static esp_err_t start_provisioning(void)
{
    if (provisioning) return ESP_OK;
    wifi_config_t access_point = {0};
    strlcpy((char *)access_point.ap.ssid, setup_ssid, sizeof(access_point.ap.ssid));
    access_point.ap.ssid_len = strlen(setup_ssid);
    strlcpy((char *)access_point.ap.password, setup_password, sizeof(access_point.ap.password));
    access_point.ap.authmode = WIFI_AUTH_WPA2_PSK;
    access_point.ap.max_connection = 2;
    access_point.ap.channel = 1;
    ESP_RETURN_ON_ERROR(esp_wifi_set_mode(WIFI_MODE_APSTA), TAG, "Setup AP mode");
    ESP_RETURN_ON_ERROR(esp_wifi_set_config(WIFI_IF_AP, &access_point), TAG, "Setup AP config");
    httpd_config_t config = HTTPD_DEFAULT_CONFIG();
    config.server_port = 80;
    config.ctrl_port = 32769;
    config.stack_size = 6144;
    config.max_open_sockets = 2;
    config.lru_purge_enable = true;
    ESP_RETURN_ON_ERROR(httpd_start(&setup_server, &config), TAG, "Setup server");
    provisioning = true;
    const httpd_uri_t get = {.uri = "/", .method = HTTP_GET, .handler = setup_get};
    const httpd_uri_t post = {.uri = "/configure", .method = HTTP_POST, .handler = setup_post};
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(setup_server, &get), TAG, "Setup page");
    ESP_RETURN_ON_ERROR(httpd_register_uri_handler(setup_server, &post), TAG, "Setup form");
    char detail[200];
    snprintf(detail, sizeof(detail), BT(BT_JOIN_S_PASSWORD_S_OPEN_HTTP_192_168_4), setup_ssid, setup_password);
    publish_state(BUDDY_PROVISIONING, detail);
    ESP_LOGI(TAG, "Wi-Fi setup network enabled");
    return ESP_OK;
}

static void wifi_event(void *argument, esp_event_base_t base, int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        if (station_ssid[0]) esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        xEventGroupClearBits(events, CONNECTED_BIT);
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        const ip_event_got_ip_t *event = data;
        xSemaphoreTake(state_lock, portMAX_DELAY);
        snprintf(station_ip, sizeof(station_ip), IPSTR, IP2STR(&event->ip_info.ip));
        xSemaphoreGive(state_lock);
        xEventGroupSetBits(events, CONNECTED_BIT);
    }
}

static void connection_task(void *argument)
{
    bool was_connected = false;
    int64_t last_connect_attempt = esp_timer_get_time();
    disconnected_since_us = last_connect_attempt;
    publish_state(BUDDY_WIFI_CONNECTING, BT(BT_CONNECTING_TO_WI_FI));
    while (true) {
        EventBits_t bits = xEventGroupGetBits(events);
        int64_t now = esp_timer_get_time();
        if (bits & RESTART_BIT) {
            vTaskDelay(pdMS_TO_TICKS(1000));
            esp_restart();
        }
        bool connected = (bits & CONNECTED_BIT) != 0;
        if (connected) {
            if (provisioning) {
                provisioning = false;
                httpd_stop(setup_server);
                setup_server = NULL;
                esp_wifi_set_mode(WIFI_MODE_STA);
            }
            xSemaphoreTake(state_lock, portMAX_DELAY);
            expire_music_command_locked(now);
            bool online = has_report && companion_fd >= 0 && now - last_frame_us <= PC_TIMEOUT_US;
            int stale_fd = (companion_fd >= 0 && now - last_frame_us > PC_TIMEOUT_US) ? companion_fd : -1;
            if (stale_fd >= 0) {
                companion_fd = -1;
                has_report = false;
                invalidate_companion_locked();
            }
            xSemaphoreGive(state_lock);
            if (stale_fd >= 0) httpd_sess_trigger_close(monitor_server, stale_fd);
            buddy_connection_state_t state = online ? BUDDY_PC_CONNECTED : BUDDY_PC_OFFLINE;
            if (!was_connected || published_state != state) {
                publish_state(state, online ? BT(BT_LIVE_OVER_WI_FI) : BT(BT_WAITING_FOR_YOUR_PC));
            }
        } else {
            if (was_connected) {
                disconnected_since_us = now;
                xSemaphoreTake(state_lock, portMAX_DELAY);
                station_ip[0] = '\0';
                int old_fd = companion_fd;
                companion_fd = -1;
                has_report = false;
                invalidate_companion_locked();
                xSemaphoreGive(state_lock);
                if (old_fd >= 0) httpd_sess_trigger_close(monitor_server, old_fd);
                publish_state(BUDDY_WIFI_CONNECTING, BT(BT_RECONNECTING_TO_WI_FI));
            }
            if (!provisioning && (!station_ssid[0] || now - disconnected_since_us >= SETUP_FALLBACK_US)) {
                if (start_provisioning() != ESP_OK) {
                    ESP_LOGW(TAG, "Wi-Fi setup could not start; will retry");
                }
            }
            if (station_ssid[0] && now - last_connect_attempt >= 5LL * 1000 * 1000) {
                esp_wifi_connect();
                last_connect_attempt = now;
            }
        }
        was_connected = connected;
        vTaskDelay(pdMS_TO_TICKS(500));
    }
}

esp_err_t buddy_network_start(const buddy_network_callbacks_t *requested_callbacks)
{
    if (events) return ESP_ERR_INVALID_STATE;
    if (requested_callbacks) callbacks = *requested_callbacks;
    ESP_RETURN_ON_ERROR(load_settings(), TAG, "Load network settings");
    uint8_t mac[6];
    ESP_RETURN_ON_ERROR(esp_read_mac(mac, ESP_MAC_WIFI_STA), TAG, "Device identifier");
    snprintf(device_id, sizeof(device_id), "%02x%02x%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    snprintf(setup_ssid, sizeof(setup_ssid), "ClawPal-%s", device_id + 6);
    events = xEventGroupCreate();
    state_lock = xSemaphoreCreateMutex();
    auth_mutex = xSemaphoreCreateMutex();
    music_command_queue = xQueueCreate(1, sizeof(uint32_t));
    if (!events || !state_lock || !auth_mutex || !music_command_queue) return ESP_ERR_NO_MEM;
    buddy_auth_init(shared_token, auth_lock, auth_unlock);
    ESP_RETURN_ON_ERROR(esp_netif_init(), TAG, "Network stack");
    esp_err_t result = esp_event_loop_create_default();
    if (result != ESP_OK && result != ESP_ERR_INVALID_STATE) return result;
    station_interface = esp_netif_create_default_wifi_sta();
    esp_netif_t *setup_interface = esp_netif_create_default_wifi_ap();
    if (!station_interface || !setup_interface) return ESP_ERR_NO_MEM;
    ESP_RETURN_ON_ERROR(esp_netif_set_hostname(station_interface, "buddyos"), TAG, "Hostname");
    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    ESP_RETURN_ON_ERROR(esp_wifi_init(&init), TAG, "Wi-Fi driver");
    ESP_RETURN_ON_ERROR(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, wifi_event, NULL), TAG, "Wi-Fi events");
    ESP_RETURN_ON_ERROR(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, wifi_event, NULL), TAG, "IP events");
    ESP_RETURN_ON_ERROR(esp_wifi_set_storage(WIFI_STORAGE_RAM), TAG, "Wi-Fi runtime storage");
    ESP_RETURN_ON_ERROR(esp_wifi_set_mode(WIFI_MODE_STA), TAG, "Wi-Fi station mode");
    wifi_config_t station = {0};
    memcpy(station.sta.ssid, station_ssid, strlen(station_ssid));
    memcpy(station.sta.password, station_password, strlen(station_password));
    /* An explicit WPA2 threshold filters out the open networks that setup allows with a blank password. */
    station.sta.threshold.authmode = station_password[0] ? WIFI_AUTH_WPA2_PSK : WIFI_AUTH_OPEN;
    station.sta.pmf_cfg.capable = true;
    station.sta.pmf_cfg.required = false;
    station.sta.sae_pwe_h2e = WPA3_SAE_PWE_BOTH;
    if (station_ssid[0]) {
        ESP_RETURN_ON_ERROR(esp_wifi_set_config(WIFI_IF_STA, &station), TAG, "Wi-Fi station config");
    }
    ESP_RETURN_ON_ERROR(start_monitor_server(), TAG, "Monitor service");
    ESP_RETURN_ON_ERROR(esp_wifi_start(), TAG, "Wi-Fi start");
    ESP_RETURN_ON_ERROR(esp_wifi_set_ps(WIFI_PS_NONE), TAG, "Wi-Fi performance mode");
    if (xTaskCreate(discovery_task, "buddy_discovery", 4096, NULL, 3, NULL) != pdPASS ||
        xTaskCreate(connection_task, "buddy_connection", 5120, NULL, 3, NULL) != pdPASS ||
        xTaskCreate(music_control_task, "buddy_music_tx", 3072, NULL, 3, NULL) != pdPASS) {
        return ESP_ERR_NO_MEM;
    }
    ESP_LOGI(TAG, "LAN monitor and authenticated discovery ready");
    return ESP_OK;
}

