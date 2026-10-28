#pragma once

#include "buddy_model.h"
#include "buddy_weather.h"
#include "buddy_settings.h"

typedef enum {
    BUDDY_APP_MONITOR = 0,
    BUDDY_APP_CLOCK = 1,
    BUDDY_APP_MUSIC = 2,
    BUDDY_APP_TIMER = 3,
    BUDDY_APP_CODEX = 4,
    BUDDY_APP_HOME = 5,
    BUDDY_APP_WEATHER = 6,
    BUDDY_APP_CLAUDE = 7,
    BUDDY_APP_COUNT
} buddy_app_t;

typedef bool (*buddy_music_control_callback_t)(const char *action, uint32_t value);
typedef void (*buddy_confirm_callback_t)(buddy_confirm_action_t action, bool approved);

typedef struct {
    uint32_t active_app;
    uint32_t clock_automatic;
    uint32_t offline_automatic;
    uint32_t screen_dimmed;
    uint32_t screen_off;
    uint32_t night_hours;
    uint32_t display_idle_ms;
    uint32_t menu_open;
    uint32_t transition_active;
    uint32_t has_stats;
    uint32_t auto_open_apps;
    /* Age starts at UI initialization before the first valid telemetry frame. */
    uint32_t last_stats_age_ms;
    uint32_t music_running;
    uint32_t music_available;
    uint32_t music_automatic;
    uint32_t codex_running;
    uint32_t claude_running;
    uint32_t codex_available;
    uint32_t claude_available;
    uint32_t codex_stale;
    uint32_t claude_stale;
    uint32_t codex_automatic;
    uint32_t claude_automatic;
    uint32_t codex_tasks_available;
    uint32_t claude_tasks_available;
    uint32_t codex_tasks_stale;
    uint32_t claude_tasks_stale;
    uint32_t codex_working_count;
    uint32_t claude_working_count;
    uint32_t codex_task_count;
    uint32_t claude_task_count;
    uint32_t timer_remaining_s;
    uint32_t timer_running;
    uint32_t timer_duration_s;
    uint32_t timer_completed;
    uint32_t game_active;
    float game_fps;
    uint32_t pc_away;
    uint32_t needs_you;
} buddy_ui_state_t;

/* Invoke with the board LVGL lock held. The UI owns no network resources. */
void buddy_ui_init(void);
void buddy_ui_apply_settings(const buddy_settings_t *settings);
void buddy_ui_set_connection(buddy_connection_state_t state, const char *ssid,
                             const char *ip, const char *detail);
void buddy_ui_update_stats(const buddy_stats_t *stats);
/* Night hours must be derived from valid local time; false while unsynchronized. */
void buddy_ui_set_clock(const char *time_text, const char *date_text, bool night_hours);
void buddy_ui_set_weather(const buddy_weather_t *weather);
/* Callback must queue immediately; true means queued, not playback confirmed. */
void buddy_ui_set_music_control_callback(buddy_music_control_callback_t callback);
/* Shows an Allow/Deny prompt over every app; repeated requests for the same action are ignored.
 * The callback receives the answer, or a denial when the prompt times out. */
void buddy_ui_set_confirm_callback(buddy_confirm_callback_t callback);
void buddy_ui_request_confirmation(buddy_confirm_action_t action);

/* Takes ownership of a heap buffer of size x size RGB565 little-endian pixels; the music
 * page shows it while telemetry's music art_id equals `id`. */
void buddy_ui_set_album_art(uint32_t id, uint16_t size, uint8_t *pixels);

/* Explicit/manual selection. Only Monitor is available while provisioning. */
bool buddy_ui_select_app(buddy_app_t app);
buddy_ui_state_t buddy_ui_get_state(void);
