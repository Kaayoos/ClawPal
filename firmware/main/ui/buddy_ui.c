#include "buddy_weather_units.h"
#include "buddy_theme.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "buddy_ui.h"
#include "board.h"
#include "buddy_display_power.h"

#include <math.h>
#include <stdio.h>
#include <string.h>
#include "lvgl.h"
#include "esp_log.h"
#include "esp_app_desc.h"
#include "buddy_mascot.h"
#include "buddy_clock_font.h"
#include "buddy_music_font.h"
#include "buddy_ui_motion.h"
#include "buddy_home.h"
#include "buddy_app_icons.h"
#include "buddy_weather_page.h"

/* Broadcast meter bridge: real channels, shared time, restrained state ink. */
#define C_BG       BUDDY_COLOR_BG
#define C_PRESSED  BUDDY_COLOR_PRESSED
#define C_TEXT     BUDDY_COLOR_TEXT
#define C_MUTED    BUDDY_COLOR_MUTED
#define C_RULE     BUDDY_COLOR_RULE
#define C_ACCENT   BUDDY_COLOR_ACCENT
#define C_WARNING  BUDDY_COLOR_WARNING
#define HISTORY_POINTS 60
#define CLOCK_DELAY_MS 30000U
#define TOUCH_WAKE_POLL_MS 40U
#define BACKLIGHT_RETRY_MS 1000U
#define APP_TRANSITION_MS 1000U
#define C_CLOCK_BG   BUDDY_COLOR_CLOCK_BG
#define C_CLOCK_TEXT BUDDY_COLOR_CLOCK_TEXT
#define C_CLOCK_DATE BUDDY_COLOR_MUTED
#define MUSIC_COMMAND_TIMEOUT_MS 5000U

typedef struct {
    lv_obj_t *button;
    lv_obj_t *name;
    lv_obj_t *usage;
    lv_obj_t *temperature;
    lv_obj_t *model;
    lv_obj_t *bar;
    lv_obj_t *selection;
} channel_t;

static struct {
    lv_obj_t *screen, *overview, *details, *empty;
    lv_obj_t *dim_overlay, *about_page, *about_version;
    lv_obj_t *monitor_header, *monitor_title, *clock_page, *chooser, *weather_page;
    lv_obj_t *home_page, *game_panel, *game_title, *game_fps, *game_status;
    lv_obj_t *clock_large, *clock_date, *clock_mascot;
    lv_obj_t *weather_row, *weather_icon, *weather_text, *weather_note;
    buddy_weather_t weather;
    lv_obj_t *music_page, *music_mascot, *music_buddy, *music_status, *music_title, *music_artist, *music_album;
    lv_obj_t *music_progress, *music_elapsed, *music_duration, *music_feedback;
    lv_obj_t *music_buttons[3], *music_button_labels[3];
    lv_obj_t *timer_page, *timer_mascot, *timer_status, *timer_time, *timer_progress;
    lv_obj_t *timer_buttons[2], *timer_button_labels[2], *timer_adjust[6], *timer_units[3];
    lv_obj_t *codex_page, *codex_mascot, *codex_status, *codex_plan, *codex_age;
    lv_obj_t *claude_page, *claude_mascot, *claude_status, *claude_plan, *claude_age;
    lv_obj_t *codex_windows[2], *codex_window_names[2], *codex_remaining[2], *codex_bars[2], *codex_resets[2];
    lv_obj_t *claude_windows[2], *claude_window_names[2], *claude_remaining[2], *claude_bars[2], *claude_resets[2];
    lv_obj_t *codex_empty_title, *codex_empty_body;
    lv_obj_t *claude_empty_title, *claude_empty_body;
    lv_obj_t *codex_usage_body, *codex_tasks_body, *codex_tabs[2], *codex_tab_labels[2];
    lv_obj_t *claude_usage_body, *claude_tasks_body, *claude_tabs[2], *claude_tab_labels[2];
    lv_obj_t *codex_task_summary, *codex_task_rows[3], *codex_task_titles[3];
    lv_obj_t *claude_task_summary, *claude_task_rows[3], *claude_task_titles[3];
    lv_obj_t *codex_task_projects[3], *codex_task_states[3], *codex_task_ages[3];
    lv_obj_t *claude_task_projects[3], *claude_task_states[3], *claude_task_ages[3];
    lv_obj_t *codex_tasks_empty_title, *codex_tasks_empty_body;
    lv_obj_t *claude_tasks_empty_title, *claude_tasks_empty_body;
    lv_obj_t *apps_button, *apps_text, *nav_button;
    lv_obj_t *app_rows[BUDDY_APP_COUNT], *app_current[BUDDY_APP_COUNT];
    lv_obj_t *app_description[BUDDY_APP_COUNT], *app_hint[BUDDY_APP_COUNT], *app_mascots[BUDDY_APP_COUNT];
    lv_obj_t *music_link, *music_link_title, *music_link_bars[3];
    lv_obj_t *timer_link, *timer_link_title, *timer_link_progress;
    lv_obj_t *app_transition, *transition_image;
    lv_obj_t *host, *clock, *date, *status, *dot;
    lv_obj_t *footer, *nav_text, *details_button, *details_text;
    lv_obj_t *memory, *memory_percent, *vram, *vram_note;
    lv_obj_t *rx, *tx;
    lv_obj_t *chart, *chart_title, *chart_time;
    lv_chart_series_t *cpu_series, *gpu_series;
    lv_obj_t *empty_title, *empty_body, *empty_network;
    lv_obj_t *detail_cpu, *detail_gpu, *detail_uptime;
    lv_obj_t *detail_ssid, *detail_ip, *detail_state, *detail_message;
    lv_obj_t *mascot, *startup, *startup_mascot;
    lv_img_dsc_t startup_frame;
    uint8_t startup_pixels[BUDDY_MASCOT_DOUBLE_BYTES];
    buddy_mascot_state_t mascot_state;
    uint32_t mascot_started, startup_started, happy_started;
    buddy_app_t active_app;
    buddy_mascot_state_t transition_mascot;
    uint32_t transition_started, manual_monitor_started;
    bool offline_automatic, offline_handled, clock_automatic, menu_open, transition_active, manual_monitor_hold;
    bool pc_launch_pending;
    uint64_t pc_launch_sequence;
    bool screen_dimmed, screen_off, night_hours, wake_touch_active;
    bool backlight_attempted, backlight_target_off;
    uint32_t last_touch_tick, power_tick, idle_ms, backlight_attempt_tick;
    bool music_automatic, music_seen_running, music_launch_pending, music_command_pending;
    bool codex_automatic, codex_seen_running, codex_launch_pending;
    bool claude_automatic, claude_seen_running, claude_launch_pending;
    bool codex_tasks_selected, codex_tab_initialized, codex_tab_manual;
    bool claude_tasks_selected, claude_tab_initialized, claude_tab_manual;
    uint32_t music_command_tick, music_command_control_id;
    buddy_music_control_callback_t music_control_callback;
    uint32_t timer_remaining_ms, timer_tick, timer_duration_s, timer_last_seconds;
    uint32_t timer_celebration_tick;
    bool timer_running, timer_started, timer_completed, timer_celebrating, timer_notify;
    bool timer_primary_pause;
    bool music_link_shown, timer_link_shown;
    bool celebrating;
    channel_t cpu, gpu;
    buddy_stats_t stats;
    buddy_connection_state_t connection;
    char ssid[96], ip[48], message[192];
    bool have_stats, details_visible, gpu_selected, fresh_sample;
    uint32_t last_stats_tick;
    lv_timer_t *history_timer, *mascot_timer;
} ui;

static void refresh_visibility(void);
static void refresh_display_power(void);
static void refresh_footer(void);
static void maybe_auto_app(void);
static void refresh_music(void);
static void refresh_timer(void);
static void refresh_codex(void);
static void refresh_claude(void);
static void advance_timer(void);
static bool music_connected(void);
static void refresh_activity(void);
static void refresh_app_cards(void);
static void animate_destination(void);
static const char *connection_name(void);
static bool codex_stale(void);
static bool claude_stale(void);
static bool codex_has_windows(void);
static bool claude_has_windows(void);
static const char *codex_status_text(void);
static const char *claude_status_text(void);
static bool codex_tasks_available(void);
static bool claude_tasks_available(void);
static bool codex_tasks_stale(void);
static bool claude_tasks_stale(void);
static void refresh_home(void);
static void refresh_game(void);

static void dismiss_startup(void)
{
    if (!ui.startup) return;
    lv_obj_del(ui.startup);
    ui.startup = NULL;
    ui.startup_mascot = NULL;
    lv_img_cache_invalidate_src(&ui.startup_frame);
}

static buddy_settings_t view_settings = {.enabled_apps = 255, .weather_enabled = true, .dim_behavior = 1, .dim_after_seconds = 60, .dim_brightness = 40, .night_enabled = true, .night_start = 1, .night_end = 10, .offline_app = 1};
static bool app_enabled(buddy_app_t app) { return (unsigned)app < BUDDY_APP_COUNT && (view_settings.enabled_apps & (1U << app)); }

/* App identities and menu content live together; add future apps here. */
static const struct {
    buddy_app_t id;
    buddy_text_key_t title;
    buddy_text_key_t description;
    buddy_mascot_state_t mascot;
} app_catalog[] = {
    { BUDDY_APP_MONITOR, BT_PC_MONITOR, BT_YOUR_PC_S_ACTIVITY_AT_A_GLANCE, BUDDY_MASCOT_WORKING },
    { BUDDY_APP_CLOCK, BT_CLOCK, BT_A_QUIETER_VIEW_OF_THE_TIME, BUDDY_MASCOT_SLEEPING },
    { BUDDY_APP_MUSIC, BT_MUSIC, BT_SPOTIFY_PLAYBACK, BUDDY_MASCOT_MUSIC },
    { BUDDY_APP_TIMER, BT_TIMER, BT_A_COUNTDOWN_FOR_ANYTHING, BUDDY_MASCOT_TYPING },
    { BUDDY_APP_CODEX, BT_CODEX, BT_YOUR_ACCOUNT_LIMITS, BUDDY_MASCOT_TYPING },
    { BUDDY_APP_HOME, BT_HOME, BT_YOUR_BUDDY_AT_A_GLANCE, BUDDY_MASCOT_HAPPY },
    { BUDDY_APP_WEATHER, BT_WEATHER, BT_OUTSIDE_AT_A_GLANCE, BUDDY_MASCOT_CLOUDY },
    { BUDDY_APP_CLAUDE, BT_CLAUDE, BT_YOUR_ACCOUNT_LIMITS, BUDDY_MASCOT_TYPING },
};

static lv_color_t color(uint32_t value) { return lv_color_hex(value); }

static lv_obj_t *box(lv_obj_t *parent, int x, int y, int w, int h)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_size(obj, w, h);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static lv_obj_t *text(lv_obj_t *parent, const char *value, int x, int y,
                      int width, const lv_font_t *font, uint32_t ink)
{
    lv_obj_t *obj = lv_label_create(parent);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_width(obj, width);
    lv_obj_set_style_text_font(obj, font, 0);
    lv_obj_set_style_text_color(obj, color(ink), 0);
    lv_obj_set_style_text_line_space(obj, 5, 0);
    lv_label_set_long_mode(obj, LV_LABEL_LONG_DOT);
    lv_label_set_text(obj, value);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static void rule(lv_obj_t *parent, int x, int y, int w, int h)
{
    lv_obj_t *obj = box(parent, x, y, w, h);
    lv_obj_set_style_bg_color(obj, color(C_RULE), 0);
    lv_obj_set_style_bg_opa(obj, LV_OPA_COVER, 0);
}

static void visible(lv_obj_t *obj, bool show)
{
    if (show) lv_obj_clear_flag(obj, LV_OBJ_FLAG_HIDDEN);
    else {
        if (!lv_obj_has_flag(obj, LV_OBJ_FLAG_HIDDEN)) buddy_ui_motion_cancel(obj);
        lv_obj_add_flag(obj, LV_OBJ_FLAG_HIDDEN);
    }
}

static void close_about(lv_event_t *event)
{
    (void)event;
    visible(ui.about_page, false);
}

static void open_about(lv_event_t *event)
{
    (void)event;
    if (ui.active_app != BUDDY_APP_HOME || ui.menu_open || ui.startup) return;
    if (!ui.about_page) {
        ui.about_page = box(ui.screen, 0, 0, BOARD_DISPLAY_WIDTH, BOARD_DISPLAY_HEIGHT);
        lv_obj_add_flag(ui.about_page, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_bg_color(ui.about_page, color(C_BG), 0);
        lv_obj_set_style_bg_opa(ui.about_page, LV_OPA_COVER, 0);
        text(ui.about_page, BT(BT_ABOUT_CLAWPAL), 32, 30, 610, &buddy_language_font_32, C_TEXT);
        text(ui.about_page, BT(BT_CREATED_BY_MINIMALPULSE), 32, 86, 710, &buddy_language_font_24, C_ACCENT);
        text(ui.about_page, "minimalpulse.com", 32, 123, 710, &buddy_language_font_20, C_TEXT);
        ui.about_version = text(ui.about_page, "", 32, 161, 710, &buddy_language_font_16, C_MUTED);
        lv_obj_t *credit = text(ui.about_page, BT(BT_ABOUT_LEGAL), 32, 210, 725, &buddy_language_font_16, C_MUTED);
        lv_label_set_long_mode(credit, LV_LABEL_LONG_WRAP);
        lv_obj_set_height(credit, 174);
        lv_obj_t *source_link=text(ui.about_page, "github.com/Kaayoos/clawpal", 32, 402, 580, &buddy_language_font_16, C_ACCENT);
        lv_obj_t *close = box(ui.about_page, 628, 414, 142, 50);
        lv_obj_add_flag(close, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_bg_color(close, color(C_PRESSED), 0);
        lv_obj_set_style_bg_opa(close, LV_OPA_COVER, 0);
        lv_obj_set_style_radius(close, 8, 0);
        text(close, BT(BT_ABOUT_CLOSE), 16, 14, 112, &buddy_language_font_16, C_ACCENT);
        lv_obj_add_event_cb(close, close_about, LV_EVENT_CLICKED, NULL);
        if(CLAWPAL_EXPERIMENTAL)text(ui.about_page,BT(BT_EXPERIMENTAL_HARDWARE),32,190,BOARD_DISPLAY_WIDTH-64,&buddy_language_font_14,C_WARNING);
        if(BOARD_DISPLAY_WIDTH<800){
            lv_obj_add_flag(ui.about_page,LV_OBJ_FLAG_SCROLLABLE);lv_obj_set_scroll_dir(ui.about_page,LV_DIR_VER);
            for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.about_page);++i){lv_obj_t *o=lv_obj_get_child(ui.about_page,i);if(lv_obj_check_type(o,&lv_label_class)){lv_obj_set_x(o,12);lv_obj_set_width(o,BOARD_DISPLAY_WIDTH-24);lv_obj_set_style_text_font(o,&buddy_language_font_16,0);}}
            lv_obj_set_height(credit,LV_SIZE_CONTENT);lv_obj_set_y(credit,228);lv_obj_update_layout(ui.about_page);
            int end=lv_obj_get_y(credit)+lv_obj_get_height(credit)+20;lv_obj_set_y(source_link,end);lv_obj_set_pos(close,BOARD_DISPLAY_WIDTH-154,end+40);
        }
    }
    char version[80];
    snprintf(version, sizeof(version), "ClawPal %s", esp_app_get_description()->version);
    lv_label_set_text(ui.about_version, version);
    visible(ui.about_page, true);
    lv_obj_move_foreground(ui.about_page);
    // Keep the existing wake/dim overlay above every app surface.
    lv_obj_move_foreground(ui.dim_overlay);
}

static void refresh_display_power(void)
{
    if (!ui.dim_overlay) return;
    /* LVGL 8.4 records pressed input before hit-testing, including empty space.
     * Read the timestamp directly so tick interrupts cannot look like touches. */
    lv_disp_t *display = lv_obj_get_disp(ui.screen);
    uint32_t touched = display->last_activity_time;
    uint32_t now = lv_tick_get();
    uint32_t elapsed = now - ui.power_tick;
    ui.power_tick = now;
    bool connected = ui.connection == BUDDY_PC_CONNECTED;
    if ((connected && view_settings.dim_behavior != 2) || touched != ui.last_touch_tick || ui.wake_touch_active) {
        ui.idle_ms = 0;
    } else {
        /* Saturation prevents a long unattended run from waking at tick wrap. */
        uint32_t remaining = 3600000U - ui.idle_ms;
        ui.idle_ms += elapsed < remaining ? elapsed : remaining;
    }
    ui.last_touch_tick = touched;
    buddy_display_mode_t mode = BUDDY_POWER_MODE(connected, ui.night_hours, ui.idle_ms, view_settings.dim_behavior, (uint32_t)view_settings.dim_after_seconds * 1000U);
    bool off = mode == BUDDY_DISPLAY_OFF;
    if (off != ui.screen_off && (!ui.backlight_attempted || off != ui.backlight_target_off ||
        lv_tick_elaps(ui.backlight_attempt_tick) >= BACKLIGHT_RETRY_MS)) {
        ui.backlight_attempted = true;
        ui.backlight_target_off = off;
        ui.backlight_attempt_tick = now;
        esp_err_t error = board_set_backlight(!off);
        if (error == ESP_OK) ui.screen_off = off;
        else ESP_LOGW("display_power", "Backlight update failed: %s", esp_err_to_name(error));
    }
    ui.screen_dimmed = mode == BUDDY_DISPLAY_DIM;
    lv_opa_t shade = off ? LV_OPA_COVER : ui.screen_dimmed ? (lv_opa_t)((100U - view_settings.dim_brightness) * 255U / 100U) : LV_OPA_TRANSP;
    if (lv_obj_get_style_bg_opa(ui.dim_overlay, 0) != shade)
        lv_obj_set_style_bg_opa(ui.dim_overlay, shade, 0);

    /* A dark screen's first press only wakes it. Retain a transparent shield
     * through release so that press cannot trigger an unseen control. */
    bool shield = off || ui.screen_off || ui.wake_touch_active;
    if (shield != lv_obj_has_flag(ui.dim_overlay, LV_OBJ_FLAG_CLICKABLE)) {
        if (shield) lv_obj_add_flag(ui.dim_overlay, LV_OBJ_FLAG_CLICKABLE);
        else lv_obj_clear_flag(ui.dim_overlay, LV_OBJ_FLAG_CLICKABLE);
    }
    bool show = shade != LV_OPA_TRANSP || shield;
    if (show == lv_obj_has_flag(ui.dim_overlay, LV_OBJ_FLAG_HIDDEN))
        visible(ui.dim_overlay, show);
}

static void wake_screen(lv_event_t *event)
{
    lv_event_code_t code = lv_event_get_code(event);
    if (code == LV_EVENT_PRESSED) {
        ui.wake_touch_active = true;
        ui.idle_ms = 0;
    } else if (code == LV_EVENT_RELEASED || code == LV_EVENT_PRESS_LOST) {
        ui.wake_touch_active = false;
        ui.idle_ms = 0;
        ui.power_tick = lv_tick_get();
    } else return;
    refresh_display_power();
}

static void touch_wake_tick(lv_timer_t *timer)
{
    (void)timer;
    refresh_display_power();
}

static void set_string(char *dest, size_t size, const char *src)
{
    snprintf(dest, size, "%s", src ? src : "");
}

static void set_label_if_changed(lv_obj_t *label, const char *value)
{
    if (label && strcmp(lv_label_get_text(label), value) != 0) lv_label_set_text(label, value);
}

static void countdown_text(char *buffer, size_t size, uint32_t seconds)
{
    if (seconds >= 3600U)
        snprintf(buffer, size, "%02lu:%02lu:%02lu", (unsigned long)(seconds / 3600U),
                 (unsigned long)(seconds / 60U % 60U), (unsigned long)(seconds % 60U));
    else snprintf(buffer, size, "%02lu:%02lu", (unsigned long)(seconds / 60U),
                  (unsigned long)(seconds % 60U));
}

static bool codex_window_available(const buddy_codex_window_t *window)
{
    return window->available && isfinite(window->remaining_percent) &&
        window->remaining_percent >= 0 && window->remaining_percent <= 100;
}

static bool claude_window_available(const buddy_codex_window_t *window)
{
    return window->available && isfinite(window->remaining_percent) &&
        window->remaining_percent >= 0 && window->remaining_percent <= 100;
}

static bool codex_has_windows(void)
{
    return ui.have_stats && ui.stats.codex.available &&
        (codex_window_available(&ui.stats.codex.primary) || codex_window_available(&ui.stats.codex.secondary));
}

static bool claude_has_windows(void)
{
    return ui.have_stats && ui.stats.claude.available &&
        (claude_window_available(&ui.stats.claude.primary) || claude_window_available(&ui.stats.claude.secondary));
}

static bool codex_stale(void)
{
    return codex_has_windows() && (ui.stats.codex.stale || !music_connected());
}

static bool claude_stale(void)
{
    return claude_has_windows() && (ui.stats.claude.stale || !music_connected());
}

static bool codex_tasks_available(void)
{
    return ui.have_stats && ui.stats.codex.activity.available;
}

static bool claude_tasks_available(void)
{
    return ui.have_stats && ui.stats.claude.activity.available;
}

static bool codex_tasks_stale(void)
{
    return codex_tasks_available() && (ui.stats.codex.activity.stale || !music_connected() ||
        strcmp(ui.stats.codex.activity.status, "closed") == 0 ||
        (uint64_t)ui.stats.codex.activity.age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U > 30U);
}

static bool claude_tasks_stale(void)
{
    return claude_tasks_available() && (ui.stats.claude.activity.stale || !music_connected() ||
        strcmp(ui.stats.claude.activity.status, "closed") == 0 ||
        (uint64_t)ui.stats.claude.activity.age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U > 30U);
}

static void activity_age_text(char *buffer, size_t size, uint32_t source_age)
{
    uint64_t age = (uint64_t)source_age + lv_tick_elaps(ui.last_stats_tick) / 1000U;
    if (age < 60) snprintf(buffer, size, BT(BT_LU_S_AGO), (unsigned long)age);
    else if (age < 3600) snprintf(buffer, size, BT(BT_LU_MIN_AGO), (unsigned long)(age / 60U));
    else if (age < 86400) snprintf(buffer, size, BT(BT_LU_H_AGO), (unsigned long)(age / 3600U));
    else snprintf(buffer, size, BT(BT_LU_D_AGO), (unsigned long)(age / 86400U));
}

static const char *codex_status_text(void)
{
    if (codex_has_windows()) return codex_stale() ? BT(BT_CACHED_LIMITS) : BT(BT_ACCOUNT_LIMITS);
    if (!music_connected()) return BT(BT_WAITING_FOR_PC);
    const char *status = ui.stats.codex.status;
    if (strcmp(status, "signed_out") == 0 || strcmp(status, "unauthorized") == 0 ||
        strcmp(status, "sign_in") == 0) return BT(BT_SIGN_IN_ON_YOUR_PC);
    if (strcmp(status, "runtime_missing") == 0) return BT(BT_CODEX_UPDATE_NEEDED);
    if (strcmp(status, "no_limits") == 0) return BT(BT_NO_LIMITS_REPORTED);
    if (strcmp(status, "error") == 0 || strcmp(status, "failed") == 0 ||
        (strcmp(status, "unavailable") == 0 && ui.stats.codex.running)) return BT(BT_LIMITS_UNAVAILABLE);
    return ui.stats.codex.running ? BT(BT_WAITING_FOR_LIMITS) : BT(BT_OPEN_CODEX_ON_YOUR_PC);
}

static const char *claude_status_text(void)
{
    if (claude_has_windows()) return claude_stale() ? BT(BT_CACHED_LIMITS) : BT(BT_ACCOUNT_LIMITS);
    if (!music_connected()) return BT(BT_WAITING_FOR_PC);
    const char *status = ui.stats.claude.status;
    if (strcmp(status, "signed_out") == 0 || strcmp(status, "unauthorized") == 0 ||
        strcmp(status, "sign_in") == 0) return BT(BT_SIGN_IN_ON_YOUR_PC);
    if (strcmp(status, "runtime_missing") == 0) return BT(BT_CLAUDE_UPDATE_NEEDED);
    if (strcmp(status, "no_limits") == 0) return BT(BT_NO_LIMITS_REPORTED);
    if (strcmp(status, "error") == 0 || strcmp(status, "failed") == 0 ||
        (strcmp(status, "unavailable") == 0 && ui.stats.claude.running)) return BT(BT_LIMITS_UNAVAILABLE);
    return ui.stats.claude.running ? BT(BT_WAITING_FOR_LIMITS) : BT(BT_OPEN_CLAUDE_ON_YOUR_PC);
}

static void codex_window_name(char *buffer, size_t size, uint32_t minutes)
{
    if (minutes == 10080U) snprintf(buffer, size, BT(BT_WEEKLY_LIMIT));
    else if (minutes == 1440U) snprintf(buffer, size, BT(BT_DAILY_LIMIT));
    else if (minutes && minutes % 1440U == 0)
        snprintf(buffer, size, BT(BT_LU_DAY_LIMIT), (unsigned long)(minutes / 1440U));
    else if (minutes && minutes % 60U == 0)
        snprintf(buffer, size, BT(BT_LU_HOUR_LIMIT), (unsigned long)(minutes / 60U));
    else if (minutes) snprintf(buffer, size, BT(BT_LU_MINUTE_LIMIT), (unsigned long)minutes);
    else snprintf(buffer, size, BT(BT_USAGE_LIMIT));
}

static void claude_window_name(char *buffer, size_t size, uint32_t minutes)
{
    if (minutes == 10080U) snprintf(buffer, size, BT(BT_WEEKLY_LIMIT));
    else if (minutes == 1440U) snprintf(buffer, size, BT(BT_DAILY_LIMIT));
    else if (minutes && minutes % 1440U == 0)
        snprintf(buffer, size, BT(BT_LU_DAY_LIMIT), (unsigned long)(minutes / 1440U));
    else if (minutes && minutes % 60U == 0)
        snprintf(buffer, size, BT(BT_LU_HOUR_LIMIT), (unsigned long)(minutes / 60U));
    else if (minutes) snprintf(buffer, size, BT(BT_LU_MINUTE_LIMIT), (unsigned long)minutes);
    else snprintf(buffer, size, BT(BT_USAGE_LIMIT));
}

static void refresh_app_cards(void)
{
    if (!ui.app_description[0] || !ui.menu_open) return;
    char value[96];
    set_label_if_changed(ui.app_description[BUDDY_APP_HOME], BT(BT_YOUR_APPS_AND_YOUR_CRAB));
    set_label_if_changed(ui.app_hint[BUDDY_APP_HOME], BT(BT_WELCOME_HOME));
    if (music_connected() && isfinite(ui.stats.cpu_usage) && isfinite(ui.stats.gpu_usage))
        snprintf(value, sizeof(value), BT(BT_CPU_0F_GPU_0F), ui.stats.cpu_usage, ui.stats.gpu_usage);
    else snprintf(value, sizeof(value), "%s", connection_name());
    set_label_if_changed(ui.app_description[BUDDY_APP_MONITOR], value);
    set_label_if_changed(ui.app_hint[BUDDY_APP_MONITOR], music_connected() ? BT(BT_LIVE_PC_READINGS) : BT(BT_AUTOMATIC_RECONNECT));
    set_label_if_changed(ui.app_description[BUDDY_APP_CLOCK], lv_label_get_text(ui.clock));
    set_label_if_changed(ui.app_hint[BUDDY_APP_CLOCK], BT(BT_LOCAL_TIME));
    if (ui.weather.available) {
        char temperature[24];buddy_weather_format_temperature(temperature,sizeof(temperature),ui.weather.temperature_c,view_settings.weather_fahrenheit);
        snprintf(value,sizeof(value),"%s / %s",temperature,buddy_weather_description(ui.weather.code,ui.weather.is_day));
    }
    else snprintf(value,sizeof(value),"%s",ui.weather.failed ? BT(BT_WEATHER_UNAVAILABLE) : BT(BT_WEATHER_LOADING));
    set_label_if_changed(ui.app_description[BUDDY_APP_WEATHER],value);
    set_label_if_changed(ui.app_hint[BUDDY_APP_WEATHER],buddy_weather_city());
    const buddy_music_t *music = &ui.stats.music;
    bool available = music_connected() && music->available;
    set_label_if_changed(ui.app_description[BUDDY_APP_MUSIC], available && music->title[0] ?
        music->title : music_connected() && music->running ? BT(BT_START_A_SONG_ON_YOUR_PC) : BT(BT_WAITING_FOR_SPOTIFY));
    set_label_if_changed(ui.app_hint[BUDDY_APP_MUSIC], available ?
        (music->playing ? BT(BT_PLAYING_ON_YOUR_PC) : BT(BT_PAUSED_ON_YOUR_PC)) : BT(BT_SPOTIFY_ON_YOUR_PC));
    countdown_text(value, sizeof(value), (ui.timer_remaining_ms + 999U) / 1000U);
    set_label_if_changed(ui.app_description[BUDDY_APP_TIMER], value);
    set_label_if_changed(ui.app_hint[BUDDY_APP_TIMER], ui.timer_running ? BT(BT_RUNNING_IN_BACKGROUND) :
        ui.timer_completed ? BT(BT_TIME_S_UP) : ui.timer_started ? BT(BT_PAUSED) : BT(BT_READY_TO_START));
    if (codex_has_windows()) {
        const buddy_codex_window_t *window = codex_window_available(&ui.stats.codex.primary) ?
            &ui.stats.codex.primary : &ui.stats.codex.secondary;
        snprintf(value, sizeof(value), BT(BT_0F_REMAINING), window->remaining_percent);
    } else snprintf(value, sizeof(value), "%s", codex_status_text());
    set_label_if_changed(ui.app_description[BUDDY_APP_CODEX], value);
    set_label_if_changed(ui.app_hint[BUDDY_APP_CODEX], codex_has_windows() ? codex_status_text() : BT(BT_LIMITS_FROM_YOUR_PC));
    if (codex_tasks_available() && !codex_tasks_stale() && ui.stats.codex.activity.working_count) {
        snprintf(value, sizeof(value), BT(BT_LU_RECENTLY_WORKING), (unsigned long)ui.stats.codex.activity.working_count);
        set_label_if_changed(ui.app_description[BUDDY_APP_CODEX], value);
        set_label_if_changed(ui.app_hint[BUDDY_APP_CODEX], BT(BT_RECORDED_LOCALLY));
    }
    if (claude_has_windows()) {
        const buddy_codex_window_t *window = claude_window_available(&ui.stats.claude.primary) ?
            &ui.stats.claude.primary : &ui.stats.claude.secondary;
        snprintf(value, sizeof(value), BT(BT_0F_REMAINING), window->remaining_percent);
    } else snprintf(value, sizeof(value), "%s", claude_status_text());
    set_label_if_changed(ui.app_description[BUDDY_APP_CLAUDE], value);
    set_label_if_changed(ui.app_hint[BUDDY_APP_CLAUDE], claude_has_windows() ? claude_status_text() : BT(BT_LIMITS_FROM_YOUR_PC));
    if (claude_tasks_available() && !claude_tasks_stale() && ui.stats.claude.activity.working_count) {
        snprintf(value, sizeof(value), BT(BT_LU_RECENTLY_WORKING), (unsigned long)ui.stats.claude.activity.working_count);
        set_label_if_changed(ui.app_description[BUDDY_APP_CLAUDE], value);
        set_label_if_changed(ui.app_hint[BUDDY_APP_CLAUDE], BT(BT_RECORDED_LOCALLY));
    }
}

static void refresh_game(void)
{
    if (!ui.game_panel) return;
    bool active = music_connected() && ui.stats.game.active;
    visible(ui.game_panel, active);
    if (!active) return;
    set_label_if_changed(ui.game_title, ui.stats.game.name);
    char fps[24];
    if (isfinite(ui.stats.game.fps)) snprintf(fps, sizeof(fps), "%.0f", ui.stats.game.fps);
    else snprintf(fps, sizeof(fps), "--");
    set_label_if_changed(ui.game_fps, fps);
    set_label_if_changed(ui.game_status, isfinite(ui.stats.game.fps) ? BT(BT_GAME_ACTIVITY_LIVE_FRAME_RATE) :
        strcmp(ui.stats.game.status, "measuring") == 0 ? BT(BT_GAME_ACTIVITY_MEASURING_FPS) : BT(BT_GAME_ACTIVITY_FPS_UNAVAILABLE));
}

static void refresh_home(void)
{
    if (!ui.home_page || !ui.clock) return;
    bool wifi = ui.connection == BUDDY_WIFI_READY || ui.connection == BUDDY_PC_CONNECTED ||
        (ui.connection == BUDDY_PC_OFFLINE && ui.ip[0]);
    buddy_home_update(ui.have_stats ? &ui.stats : NULL, music_connected(), wifi, codex_stale(),
                      lv_label_get_text(ui.clock), lv_label_get_text(ui.date), &ui.weather);
}

static void refresh_activity(void)
{
    if (!ui.music_link) return;
    refresh_game();
    bool show = ui.active_app == BUDDY_APP_MONITOR && !ui.menu_open && !ui.startup && music_connected() && app_enabled(BUDDY_APP_MUSIC) &&
        ui.stats.music.running && ui.stats.music.available && ui.stats.music.playing;
    if (show != ui.music_link_shown) {
        ui.music_link_shown = show;
        visible(ui.music_link, show);
        lv_obj_set_width(ui.monitor_title, BOARD_DISPLAY_WIDTH<800 ? (show?BOARD_DISPLAY_WIDTH-(BOARD_DISPLAY_HEIGHT<400?170:218):BOARD_DISPLAY_WIDTH-24) : (show ? 176 : 380));
        lv_obj_set_width(ui.host, BOARD_DISPLAY_WIDTH<800?BOARD_DISPLAY_WIDTH-72:(show ? 176 : 400));
        if (show) buddy_ui_motion_enter(ui.music_link, 6, 180, 0);
        else buddy_ui_motion_cancel(ui.music_link);
    }
    if (show) set_label_if_changed(ui.music_link_title,
        ui.stats.music.title[0] ? ui.stats.music.title : BT(BT_SPOTIFY));
    refresh_app_cards();
}

static const char *connection_name(void)
{
    switch (ui.connection) {
    case BUDDY_WIFI_CONNECTING: return BT(BT_JOINING_WI_FI);
    case BUDDY_WIFI_READY: return BT(BT_WAITING_FOR_PC);
    case BUDDY_PC_CONNECTED: return BT(BT_CONNECTED);
    case BUDDY_PC_OFFLINE: return BT(BT_PC_OFFLINE);
    case BUDDY_PROVISIONING: return BT(BT_SETUP_NEEDED);
    default: return BT(BT_CONNECTING);
    }
}

static buddy_mascot_state_t mascot_state(void)
{
    if (ui.startup) return BUDDY_MASCOT_HAPPY;
    if (ui.transition_active) return ui.transition_mascot;
    if (ui.active_app == BUDDY_APP_HOME) return BUDDY_MASCOT_IDLE;
    if (ui.active_app == BUDDY_APP_TIMER) {
        if (ui.timer_celebrating && lv_tick_elaps(ui.timer_celebration_tick) < 2400)
            return BUDDY_MASCOT_HAPPY;
        return ui.timer_running ? BUDDY_MASCOT_TYPING : BUDDY_MASCOT_IDLE;
    }
    if (ui.active_app == BUDDY_APP_MUSIC)
        return ui.music_feedback && lv_label_get_text(ui.music_feedback)[0] ? BUDDY_MASCOT_ERROR : BUDDY_MASCOT_MUSIC;
    if (ui.active_app == BUDDY_APP_CODEX) {
        if (music_connected() && (strcmp(ui.stats.codex.status, "error") == 0 || strcmp(ui.stats.codex.status, "failed") == 0))
            return BUDDY_MASCOT_ERROR;
        return music_connected() && ui.stats.codex.running ? BUDDY_MASCOT_TYPING : BUDDY_MASCOT_IDLE;
    }
    if (ui.active_app == BUDDY_APP_CLAUDE) {
        if (music_connected() && (strcmp(ui.stats.claude.status, "error") == 0 || strcmp(ui.stats.claude.status, "failed") == 0))
            return BUDDY_MASCOT_ERROR;
        return music_connected() && ui.stats.claude.running ? BUDDY_MASCOT_TYPING : BUDDY_MASCOT_IDLE;
    }
    if (ui.active_app == BUDDY_APP_CLOCK) {
        const bool pc_available = ui.connection == BUDDY_PC_CONNECTED && ui.have_stats &&
                                  lv_tick_elaps(ui.last_stats_tick) < CLOCK_DELAY_MS;
        return pc_available ? BUDDY_MASCOT_IDLE : BUDDY_MASCOT_SLEEPING;
    }
    if (ui.connection == BUDDY_PC_CONNECTED) {
        if (!ui.have_stats) return BUDDY_MASCOT_WORKING;
        if (ui.celebrating && lv_tick_elaps(ui.happy_started) < 1200)
            return BUDDY_MASCOT_HAPPY;
        ui.celebrating = false;
        return BUDDY_MASCOT_IDLE;
    }
    if (ui.connection == BUDDY_PROVISIONING) return BUDDY_MASCOT_ERROR;
    return ui.connection == BUDDY_WIFI_CONNECTING ? BUDDY_MASCOT_WORKING : BUDDY_MASCOT_THINKING;
}

/* LVGL's indexed decoder reads by line, so avoid transformed indexed images.
 * An exact 2x nibble expansion preserves pixels and alpha without extra flash. */
static void update_large_frame(const lv_img_dsc_t *source)
{
    uint8_t *target = ui.startup_pixels;
    memcpy(target, source->data, 64); /* Original BGRA palette. */
    for (unsigned y = 0; y < 48; ++y) {
        uint8_t *row = target + 64 + y * 2 * 48;
        for (unsigned x = 0; x < 24; ++x) {
            uint8_t pair = source->data[64 + y * 24 + x];
            row[x * 2] = (pair & 0xf0) | (pair >> 4);
            row[x * 2 + 1] = (pair & 0x0f) | (pair << 4);
        }
        memcpy(row + 48, row, 48);
    }
    lv_img_cache_invalidate_src(&ui.startup_frame);
    lv_obj_t *target_image = ui.startup_mascot ? ui.startup_mascot :
        ui.active_app == BUDDY_APP_CLOCK ? ui.clock_mascot :
        ui.active_app == BUDDY_APP_TIMER ? ui.timer_mascot :
        ui.active_app == BUDDY_APP_CODEX ? ui.codex_mascot :
        ui.active_app == BUDDY_APP_CLAUDE ? ui.claude_mascot :
        ui.active_app == BUDDY_APP_MUSIC ? ui.music_buddy : ui.transition_image;
    lv_img_set_src(target_image, &ui.startup_frame);
    lv_obj_invalidate(target_image);
}

static void mascot_tick(lv_timer_t *timer)
{
    (void)timer;
    advance_timer();
    if (ui.timer_celebrating && lv_tick_elaps(ui.timer_celebration_tick) >= 2400U)
        ui.timer_celebrating = false;
    if (ui.music_command_pending && lv_tick_elaps(ui.music_command_tick) >= MUSIC_COMMAND_TIMEOUT_MS) {
        ui.music_command_pending = false;
        lv_label_set_text(ui.music_feedback, BT(BT_NO_REPLY_FROM_SPOTIFY_TRY_AGAIN));
        refresh_music();
    }
    if (ui.startup && lv_tick_elaps(ui.startup_started) >= 2000) {
        dismiss_startup();
    }
    if (ui.transition_active && lv_tick_elaps(ui.transition_started) >= APP_TRANSITION_MS) {
        ui.transition_active = false;
        refresh_visibility();
    }
    maybe_auto_app();
    buddy_home_tick();
    refresh_activity();
    if (ui.active_app == BUDDY_APP_WEATHER && !ui.menu_open) buddy_weather_page_tick();
    if (ui.music_link_shown) {
        /* A playback indicator, not measured audio levels. Stop when hidden. */
        static const uint8_t heights[] = { 4, 7, 10, 13, 10, 7 };
        unsigned beat = (lv_tick_get() / 160U) % 6U;
        for (unsigned i = 0; i < 3; ++i) {
            unsigned height = heights[(beat + i * 2) % 6];
            lv_obj_set_height(ui.music_link_bars[i], height);
            lv_obj_set_y(ui.music_link_bars[i], 22 - (int)height);
        }
    }
    buddy_mascot_state_t state = mascot_state();
    if (state != ui.mascot_state) {
        ui.mascot_state = state;
        ui.mascot_started = lv_tick_get();
    }
    unsigned frame = (lv_tick_elaps(ui.mascot_started) / BUDDY_MASCOT_FRAME_MS) % BUDDY_MASCOT_FRAME_COUNT;
    if (ui.active_app == BUDDY_APP_MUSIC &&
        (ui.connection != BUDDY_PC_CONNECTED || !ui.stats.music.available || !ui.stats.music.playing))
        frame = 0;
    const lv_img_dsc_t *source = &buddy_mascot_frames[state][frame];
    if (ui.active_app == BUDDY_APP_MONITOR && !ui.menu_open) lv_img_set_src(ui.mascot, source);
    if (ui.active_app == BUDDY_APP_MUSIC && !ui.menu_open)
        lv_img_set_src(ui.music_mascot, &buddy_mascot_frames[BUDDY_MASCOT_IDLE][frame]);
    if (ui.startup_mascot || (!ui.menu_open &&
        (ui.active_app == BUDDY_APP_CLOCK || ui.active_app == BUDDY_APP_TIMER ||
         ui.active_app == BUDDY_APP_MUSIC || ui.active_app == BUDDY_APP_CODEX || ui.active_app == BUDDY_APP_CLAUDE || ui.transition_active)))
        update_large_frame(source);
}

static void create_startup(void)
{
    ui.startup_started = lv_tick_get();
    ui.mascot_state = BUDDY_MASCOT_STATE_COUNT;
    ui.startup = box(ui.screen, 0, 0, BOARD_DISPLAY_WIDTH, BOARD_DISPLAY_HEIGHT);
    lv_obj_add_flag(ui.startup, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(ui.startup, color(C_BG), 0);
    lv_obj_set_style_bg_opa(ui.startup, LV_OPA_COVER, 0);
    ui.startup_frame = (lv_img_dsc_t) {
        .header = { .cf = LV_IMG_CF_INDEXED_4BIT, .always_zero = 0, .w = 96, .h = 96 },
        .data_size = sizeof(ui.startup_pixels), .data = ui.startup_pixels,
    };
    ui.startup_mascot = lv_img_create(ui.startup);
    lv_img_set_antialias(ui.startup_mascot, false);
    lv_obj_set_pos(ui.startup_mascot, (BOARD_DISPLAY_WIDTH-96)/2, (BOARD_DISPLAY_HEIGHT-230)/2+20);
    lv_obj_t *title = text(ui.startup, BT(BT_CLAWPAL), 12, (BOARD_DISPLAY_HEIGHT-230)/2+140, BOARD_DISPLAY_WIDTH-24, &buddy_language_font_32, C_TEXT);
    lv_obj_set_style_text_align(title, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_t *subtitle = text(ui.startup, BT(BT_YOUR_LITTLE_DESK_COMPANION), 12, (BOARD_DISPLAY_HEIGHT-230)/2+186, BOARD_DISPLAY_WIDTH-24, &buddy_language_font_16, C_MUTED);
    lv_obj_set_style_text_align(subtitle, LV_TEXT_ALIGN_CENTER, 0);
    mascot_tick(NULL);
    ui.mascot_timer = lv_timer_create(mascot_tick, BUDDY_MASCOT_FRAME_MS, NULL);
}

static void refresh_visibility(void)
{
    const bool need_setup = ui.connection == BUDDY_PROVISIONING;
    const bool monitor = ui.active_app == BUDDY_APP_MONITOR && !ui.menu_open;
    visible(ui.monitor_header, monitor);
    visible(ui.home_page, ui.active_app == BUDDY_APP_HOME && !ui.menu_open);
    visible(ui.overview, monitor && !ui.details_visible && ui.have_stats && !need_setup);
    visible(ui.empty, monitor && !ui.details_visible && (!ui.have_stats || need_setup));
    visible(ui.details, monitor && ui.details_visible);
    visible(ui.weather_page, ui.active_app == BUDDY_APP_WEATHER && !ui.menu_open);
    visible(ui.clock_page, ui.active_app == BUDDY_APP_CLOCK && !ui.menu_open);
    if (ui.active_app != BUDDY_APP_CLOCK || ui.menu_open) {
        buddy_ui_motion_cancel(ui.clock_large);
        buddy_ui_motion_cancel(ui.clock_date);
    }
    visible(ui.music_page, ui.active_app == BUDDY_APP_MUSIC && !ui.menu_open);
    visible(ui.timer_page, ui.active_app == BUDDY_APP_TIMER && !ui.menu_open);
    visible(ui.codex_page, ui.active_app == BUDDY_APP_CODEX && !ui.menu_open);
    if (ui.active_app != BUDDY_APP_CODEX || ui.menu_open) {
        buddy_ui_motion_cancel(ui.codex_usage_body);
        buddy_ui_motion_cancel(ui.codex_tasks_body);
    }
    visible(ui.claude_page, ui.active_app == BUDDY_APP_CLAUDE && !ui.menu_open);
    if (ui.active_app != BUDDY_APP_CLAUDE || ui.menu_open) {
        buddy_ui_motion_cancel(ui.claude_usage_body);
        buddy_ui_motion_cancel(ui.claude_tasks_body);
    }
    visible(ui.chooser, ui.menu_open);
    visible(ui.nav_button, !ui.menu_open && !need_setup && (ui.active_app != BUDDY_APP_HOME || app_enabled(BUDDY_APP_CLOCK)));
    visible(ui.details_button, monitor && !need_setup);
    visible(ui.app_transition, monitor && ui.transition_active && !need_setup && !ui.startup);
    lv_label_set_text(ui.nav_text, ui.active_app == BUDDY_APP_HOME ? BT(BT_CLOCK) : BT(BT_HOME));
    lv_label_set_text(ui.details_text, ui.details_visible ? BT(BT_OVERVIEW) : BT(BT_DETAILS));
    lv_label_set_text(ui.apps_text, ui.menu_open ? BT(BT_CLOSE) : BT(BT_APPS));
    lv_obj_set_style_text_color(ui.apps_text, color(need_setup ? C_MUTED : C_ACCENT), 0);
    lv_obj_set_style_opa(ui.apps_button, need_setup ? LV_OPA_50 : LV_OPA_COVER, 0);
    for (unsigned i = 0; i < sizeof(app_catalog) / sizeof(app_catalog[0]); ++i) {
        if (!ui.menu_open) buddy_ui_motion_cancel(ui.app_rows[i]);
        bool selected = app_catalog[i].id == ui.active_app;
        lv_label_set_text(ui.app_current[i], selected ? BT(BT_CURRENT) : BT(BT_OPEN));
        lv_obj_set_style_bg_opa(ui.app_rows[i], selected ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
    }
    unsigned position = 1;
    lv_obj_set_y(ui.app_rows[BUDDY_APP_HOME], BOARD_DISPLAY_WIDTH<800?88:80);
    for (unsigned i = 0; i < BUDDY_APP_COUNT; ++i) {
        if (i == BUDDY_APP_HOME) continue;
        bool enabled = app_enabled((buddy_app_t)i); visible(ui.app_rows[i], enabled);
        if (enabled) lv_obj_set_y(ui.app_rows[i], (BOARD_DISPLAY_WIDTH<800?88:80) + (int)position++ * (BOARD_DISPLAY_WIDTH<800?64:44));
    }
    visible(ui.weather_row, view_settings.weather_enabled);
    visible(ui.weather_note, view_settings.weather_enabled);
    refresh_activity();
    refresh_display_power();
}

static void refresh_footer(void)
{
    if (!ui.footer) return;
    char buf[128];
    bool shortcut = !ui.menu_open && !ui.startup && ui.active_app != BUDDY_APP_TIMER &&
        ui.connection != BUDDY_PROVISIONING && (ui.timer_started || ui.timer_completed);
    if (shortcut != ui.timer_link_shown) {
        ui.timer_link_shown = shortcut;
        visible(ui.timer_link, shortcut);
        if (shortcut) buddy_ui_motion_enter(ui.timer_link, 4, 160, 0);
        else buddy_ui_motion_cancel(ui.timer_link);
    }
    bool monitor = ui.active_app == BUDDY_APP_MONITOR && !ui.menu_open;
    lv_obj_set_x(ui.footer, monitor ? 274 : 142);
    lv_obj_set_width(ui.footer, monitor ? (shortcut ? 132 : 344) : (shortcut ? 262 : 481));
    if (shortcut) {
        char time[24];
        countdown_text(time, sizeof(time), (ui.timer_remaining_ms + 999U) / 1000U);
        if (ui.timer_completed) snprintf(buf, sizeof(buf), BT(BT_TIMER_FINISHED));
        else snprintf(buf, sizeof(buf), "%s %s", ui.timer_running ? BT(BT_TIMER) : BT(BT_PAUSED), time);
        set_label_if_changed(ui.timer_link_title, buf);
        lv_obj_set_style_text_color(ui.timer_link_title, color(ui.timer_completed ? C_WARNING :
            ui.timer_running ? C_ACCENT : C_MUTED), 0);
        uint32_t duration = ui.timer_duration_s * 1000U;
        uint32_t remaining = ui.timer_remaining_ms < duration ? ui.timer_remaining_ms : duration;
        lv_bar_set_value(ui.timer_link_progress, duration ?
            (int)((uint64_t)(duration - remaining) * 1000U / duration) : 0, LV_ANIM_OFF);
    }
    if (ui.menu_open) {
        lv_label_set_text(ui.footer, BT(BT_CHOOSE_AN_APP));
    } else if (ui.active_app == BUDDY_APP_HOME) {
        snprintf(buf, sizeof(buf), BT(BT_CLAWPAL_VERSION), esp_app_get_description()->version);
        lv_label_set_text(ui.footer, buf);
    } else if (shortcut) {
        lv_label_set_text(ui.footer, ui.active_app == BUDDY_APP_MUSIC ? BT(BT_SPOTIFY_ON_YOUR_PC) :
            ui.active_app == BUDDY_APP_HOME ? BT(BT_HOME) :
            ui.active_app == BUDDY_APP_CLOCK ? BT(BT_CLOCK) :
            ui.active_app == BUDDY_APP_CODEX ? BT(BT_CODEX_ACCOUNT_LIMITS) :
            ui.active_app == BUDDY_APP_CLAUDE ? BT(BT_CLAUDE_ACCOUNT_LIMITS) :
            music_connected() ? BT(BT_PC_MONITOR) : BT(BT_PC_RECONNECTING));
    } else if (ui.active_app == BUDDY_APP_TIMER) {
        lv_label_set_text(ui.footer, ui.timer_running ? BT(BT_YOUR_TIMER_KEEPS_RUNNING_BETWEEN_APPS) :
            ui.timer_completed ? BT(BT_TIME_S_UP_START_AGAIN_OR_SET_A_NEW) :
            ui.timer_started ? BT(BT_RESUME_TO_CONTINUE_OR_RESET_TO_ADJUST) : BT(BT_SET_A_DURATION_THEN_PRESS_START));
    } else if (ui.active_app == BUDDY_APP_MUSIC) {
        lv_label_set_text(ui.footer, BT(BT_PLAYBACK_FOLLOWS_SPOTIFY_ON_YOUR_PC));
    } else if (ui.active_app == BUDDY_APP_CODEX) {
        lv_label_set_text(ui.footer, ui.codex_tasks_selected ? BT(BT_RECORDED_LOCALLY_NOT_LIVE_TASK_STATUS) :
            codex_stale() ? BT(BT_SAVED_LIMITS_WAITING_FOR_AN_UPDATE) : BT(BT_LIMITS_FROM_YOUR_CODEX_ACCOUNT));
    } else if (ui.active_app == BUDDY_APP_CLAUDE) {
        lv_label_set_text(ui.footer, ui.claude_tasks_selected ? BT(BT_RECORDED_LOCALLY_NOT_LIVE_TASK_STATUS) :
            claude_stale() ? BT(BT_SAVED_LIMITS_WAITING_FOR_AN_UPDATE) : BT(BT_LIMITS_FROM_YOUR_CLAUDE_ACCOUNT));
    } else if (ui.active_app == BUDDY_APP_WEATHER) {
        lv_label_set_text(ui.footer, BT(BT_WEATHER_BY_OPEN_METEO));
    } else if (ui.active_app == BUDDY_APP_CLOCK) {
        lv_label_set_text(ui.footer, ui.clock_automatic ?
            BT(BT_YOUR_PC_APPS_RETURN_WHEN_IT_RECONNECTS) : BT(BT_CHOOSE_ANOTHER_APP_FROM_APPS));
    } else if (ui.connection == BUDDY_PC_CONNECTED && ui.have_stats) {
        lv_label_set_text(ui.footer, BT(BT_TAP_CPU_OR_GPU_TO_CHANGE_THE_HISTORY));
    } else if (ui.have_stats && ui.connection != BUDDY_PROVISIONING) {
        const unsigned age = lv_tick_elaps(ui.last_stats_tick) / 1000;
        if (age < 60) snprintf(buf, sizeof(buf), BT(BT_LAST_READING_U_S_AGO_RECONNECTING), age);
        else snprintf(buf, sizeof(buf), BT(BT_LAST_READING_U_MIN_AGO_RECONNECTING), age / 60);
        lv_label_set_text(ui.footer, buf);
    } else {
        lv_label_set_text(ui.footer, BT(BT_CLAWPAL_PC_MONITOR));
    }
    if(BOARD_DISPLAY_WIDTH<800){
        lv_obj_set_pos(ui.footer,104,BOARD_DISPLAY_HEIGHT-30);lv_obj_set_width(ui.footer,BOARD_DISPLAY_WIDTH-208);
        visible(ui.footer,!monitor&&!shortcut);visible(ui.timer_link,shortcut&&!monitor);
    }
    lv_obj_set_style_text_color(ui.footer,
        color(ui.active_app == BUDDY_APP_MONITOR && !ui.menu_open && ui.have_stats &&
              ui.connection != BUDDY_PC_CONNECTED ? C_WARNING : C_MUTED), 0);
}

static void mascot_set_y(void *obj, int32_t value)
{
    lv_obj_set_y(obj, (lv_coord_t)value);
}

static void animate_destination(void)
{
    lv_obj_t *objects[] = { ui.overview, ui.details, ui.empty, ui.music_page, ui.timer_page, ui.codex_page, ui.claude_page, ui.home_page, ui.weather_page,
        ui.codex_usage_body, ui.codex_tasks_body, ui.claude_usage_body, ui.claude_tasks_body,
        ui.clock_large, ui.clock_date };
    for (unsigned i = 0; i < sizeof(objects) / sizeof(objects[0]); ++i)
        buddy_ui_motion_cancel(objects[i]);
    for (unsigned i = 0; i < BUDDY_APP_COUNT; ++i) buddy_ui_motion_cancel(ui.app_rows[i]);
    if (ui.menu_open) {
        for (unsigned i = 0; i < BUDDY_APP_COUNT; ++i)
            buddy_ui_motion_enter(ui.app_rows[i], 12, 180, (uint16_t)(i * 20U));
    } else if (ui.active_app == BUDDY_APP_CLOCK) {
        buddy_ui_motion_enter(ui.clock_large, 8, 200, 0);
        buddy_ui_motion_enter(ui.clock_date, 6, 180, 20);
    } else {
        lv_obj_t *destination = ui.active_app == BUDDY_APP_WEATHER ? ui.weather_page : ui.active_app == BUDDY_APP_HOME ? ui.home_page : ui.active_app == BUDDY_APP_MUSIC ? ui.music_page :
            ui.active_app == BUDDY_APP_CODEX ? ui.codex_page :
            ui.active_app == BUDDY_APP_CLAUDE ? ui.claude_page :
            ui.active_app == BUDDY_APP_TIMER ? ui.timer_page : ui.details_visible ? ui.details :
            ui.have_stats && ui.connection != BUDDY_PROVISIONING ? ui.overview : ui.empty;
        buddy_ui_motion_enter(destination, 8, 200, 0);
    }
}

static void begin_app_transition(void)
{
    if (ui.active_app == BUDDY_APP_HOME || ui.active_app == BUDDY_APP_WEATHER) { ui.transition_active = false; return; }
    if (ui.active_app == BUDDY_APP_MUSIC || ui.active_app == BUDDY_APP_TIMER || ui.active_app == BUDDY_APP_CODEX || ui.active_app == BUDDY_APP_CLAUDE) {
        ui.transition_active = false;
        if (!ui.startup) update_large_frame(&buddy_mascot_frames[mascot_state()][0]);
        return;
    }
    ui.transition_active = true;
    ui.transition_started = lv_tick_get();
    ui.transition_mascot = ui.active_app == BUDDY_APP_CLOCK ? BUDDY_MASCOT_SLEEPING : BUDDY_MASCOT_HAPPY;
    ui.mascot_state = BUDDY_MASCOT_STATE_COUNT;
    if (!ui.startup) update_large_frame(&buddy_mascot_frames[ui.transition_mascot][0]);
    lv_obj_t *actor = ui.active_app == BUDDY_APP_CLOCK ? ui.clock_mascot : ui.app_transition;
    int end_y = BOARD_DISPLAY_WIDTH<800 ? (ui.active_app==BUDDY_APP_CLOCK ? (BOARD_DISPLAY_HEIGHT<400?96:286) : BOARD_DISPLAY_HEIGHT-148) : (ui.active_app == BUDDY_APP_CLOCK ? 316 : 336);
    lv_anim_del(ui.clock_mascot, mascot_set_y);
    lv_anim_del(ui.app_transition, mascot_set_y);
    lv_anim_t anim;
    lv_anim_init(&anim);
    lv_anim_set_var(&anim, actor);
    lv_anim_set_exec_cb(&anim, mascot_set_y);
    lv_anim_set_values(&anim, end_y + 12, end_y);
    lv_anim_set_time(&anim, 240);
    lv_anim_set_path_cb(&anim, lv_anim_path_ease_out);
    lv_anim_start(&anim);
}

static void activate_app(buddy_app_t app, bool automatic)
{
    if (!app_enabled(app) && ui.connection != BUDDY_PROVISIONING) app = BUDDY_APP_HOME;
    bool changed = app != ui.active_app;
    ui.active_app = app;
    ui.offline_automatic = automatic && ui.connection != BUDDY_PC_CONNECTED;
    ui.clock_automatic = app == BUDDY_APP_CLOCK && ui.offline_automatic;
    ui.music_automatic = app == BUDDY_APP_MUSIC && automatic;
    ui.codex_automatic = app == BUDDY_APP_CODEX && automatic;
    ui.claude_automatic = app == BUDDY_APP_CLAUDE && automatic;
    if (app == BUDDY_APP_TIMER) ui.timer_notify = false;
    ui.details_visible = false;
    if (changed) begin_app_transition();
    refresh_footer();
    refresh_visibility();
    animate_destination();
}

static buddy_app_t preferred_connected_app(void)
{
    if (!ui.stats.auto_open_apps) return BUDDY_APP_HOME;
    return ui.stats.codex.running && app_enabled(BUDDY_APP_CODEX) ? BUDDY_APP_CODEX :
        ui.stats.claude.running && app_enabled(BUDDY_APP_CLAUDE) ? BUDDY_APP_CLAUDE :
        ui.stats.music.running && app_enabled(BUDDY_APP_MUSIC) ? BUDDY_APP_MUSIC : BUDDY_APP_HOME;
}

static void maybe_auto_app(void)
{
    if (ui.connection == BUDDY_PROVISIONING || ui.menu_open || ui.startup) return;
    uint32_t age = lv_tick_elaps(ui.last_stats_tick);
    bool fresh_pc = ui.have_stats && ui.connection == BUDDY_PC_CONNECTED && age < CLOCK_DELAY_MS;
    if (fresh_pc) ui.offline_handled = false;
    /* Each connection chooses from a fresh frame, so old Spotify/Codex state
     * cannot send a newly connected PC to the wrong app. Timers keep running. */
    if (ui.pc_launch_pending && fresh_pc && ui.stats.sequence != ui.pc_launch_sequence) {
        ui.pc_launch_pending = ui.music_launch_pending = ui.codex_launch_pending = ui.claude_launch_pending = false;
        activate_app(preferred_connected_app(), true);
        return;
    }
    if (fresh_pc && !ui.stats.auto_open_apps) {
        ui.music_launch_pending = ui.codex_launch_pending = ui.claude_launch_pending = false;
        /* Disabling the tray option releases automatic app selections only.
         * A manually opened Music/Codex view remains selected. */
        if (ui.music_automatic || ui.codex_automatic || ui.claude_automatic) {
            activate_app(BUDDY_APP_HOME, false);
            return;
        }
    }
    if (ui.timer_running) return;
    if (ui.offline_automatic && ui.have_stats &&
        fresh_pc) {
        ui.music_launch_pending = false;
        ui.codex_launch_pending = false; ui.claude_launch_pending = false;
        activate_app(preferred_connected_app(), true);
    } else if (!fresh_pc && !ui.offline_handled && age >= CLOCK_DELAY_MS) {
        ui.offline_handled = true;
        buddy_app_t destination = (buddy_app_t)view_settings.offline_app;
        if (!app_enabled(destination)) destination = BUDDY_APP_HOME;
        activate_app(destination, true);
    } else if (app_enabled(BUDDY_APP_CODEX) && ui.codex_launch_pending && fresh_pc && ui.stats.codex.running &&
               (ui.active_app == BUDDY_APP_HOME || ui.active_app == BUDDY_APP_MONITOR || ui.active_app == BUDDY_APP_MUSIC ||
                (ui.offline_automatic))) {
        ui.codex_launch_pending = false; ui.claude_launch_pending = false;
        ui.music_launch_pending = false;
        activate_app(BUDDY_APP_CODEX, true);
    } else if (ui.active_app == BUDDY_APP_CODEX && ui.codex_automatic && fresh_pc && !ui.stats.codex.running) {
        ui.music_launch_pending = false;
        activate_app(preferred_connected_app(), true);
    } else if (app_enabled(BUDDY_APP_CLAUDE) && ui.claude_launch_pending && fresh_pc && ui.stats.claude.running &&
               (ui.active_app == BUDDY_APP_HOME || ui.active_app == BUDDY_APP_MONITOR || ui.active_app == BUDDY_APP_MUSIC ||
                (ui.offline_automatic))) {
        ui.claude_launch_pending = false;
        ui.music_launch_pending = false;
        activate_app(BUDDY_APP_CLAUDE, true);
    } else if (ui.active_app == BUDDY_APP_CLAUDE && ui.claude_automatic && fresh_pc && !ui.stats.claude.running) {
        ui.music_launch_pending = false;
        activate_app(preferred_connected_app(), true);
    } else if (ui.active_app == BUDDY_APP_MUSIC && ui.music_automatic && fresh_pc && !ui.stats.music.running) {
        activate_app(BUDDY_APP_HOME, false);
    } else if (app_enabled(BUDDY_APP_MUSIC) && ui.music_launch_pending && fresh_pc && ui.stats.music.running &&
               (ui.active_app == BUDDY_APP_HOME || ui.active_app == BUDDY_APP_MONITOR || (ui.offline_automatic))) {
        ui.music_launch_pending = false;
        activate_app(BUDDY_APP_MUSIC, true);
    }
}

static void apps_clicked(lv_event_t *event)
{
    (void)event;
    if (ui.connection == BUDDY_PROVISIONING) return;
    ui.menu_open = !ui.menu_open;
    ui.transition_active = false;
    if (!ui.menu_open) maybe_auto_app();
    refresh_footer();
    refresh_visibility();
    animate_destination();
}

static void app_clicked(lv_event_t *event)
{
    buddy_app_t app = (buddy_app_t)(uintptr_t)lv_event_get_user_data(event);
    buddy_ui_select_app(app);
}

static void activity_clicked(lv_event_t *event)
{
    buddy_ui_select_app((buddy_app_t)(uintptr_t)lv_event_get_user_data(event));
}

static void refresh_connection(void)
{
    const bool connected = ui.connection == BUDDY_PC_CONNECTED;
    const uint32_t ink = connected ? C_ACCENT : C_WARNING;
    lv_label_set_text(ui.status, connection_name());
    lv_obj_set_style_text_color(ui.status, color(ink), 0);
    lv_obj_set_style_bg_color(ui.dot, color(ink), 0);
    lv_label_set_text(ui.detail_ssid, ui.ssid[0] ? ui.ssid : BT(BT_NOT_CONFIGURED));
    lv_label_set_text(ui.detail_ip, ui.ip[0] ? ui.ip : BT(BT_NOT_ASSIGNED));
    lv_label_set_text(ui.detail_state, connection_name());
    lv_obj_set_style_text_color(ui.detail_state, color(ink), 0);
    lv_label_set_text(ui.detail_message, buddy_retranslate_copy(ui.message));

    switch (ui.connection) {
    case BUDDY_PROVISIONING:
        lv_label_set_text(ui.empty_title, BT(BT_LET_S_CONNECT_YOUR_BUDDY));
        lv_label_set_text(ui.empty_body, ui.message[0] ? buddy_retranslate_copy(ui.message) :
            BT(BT_FINISH_WI_FI_SETUP_USING_CLAWPAL_ON_YOUR_PC));
        break;
    case BUDDY_WIFI_CONNECTING:
        lv_label_set_text(ui.empty_title, BT(BT_JOINING_YOUR_WI_FI));
        lv_label_set_text(ui.empty_body, ui.message[0] ? buddy_retranslate_copy(ui.message) :
            BT(BT_BUDDY_IS_RECONNECTING_TO_YOUR_SAVED_NETWORK_YOUR_PC));
        break;
    case BUDDY_PC_CONNECTED:
        lv_label_set_text(ui.empty_title, BT(BT_CONNECTED_TO_YOUR_PC));
        lv_label_set_text(ui.empty_body, BT(BT_WAITING_FOR_THE_FIRST_SENSOR_READING));
        break;
    default:
        lv_label_set_text(ui.empty_title, BT(BT_READY_WHEN_YOUR_PC_IS));
        lv_label_set_text(ui.empty_body, ui.message[0] ? buddy_retranslate_copy(ui.message) :
            BT(BT_KEEP_CLAWPAL_RUNNING_IN_YOUR_PC_S_TRAY_BOTH));
        break;
    }
    char network[152];
    if (ui.ssid[0] && ui.ip[0]) snprintf(network, sizeof(network), "%s  /  %s", ui.ssid, ui.ip);
    else snprintf(network, sizeof(network), "%s", ui.ssid[0] ? ui.ssid : BT(BT_WI_FI_NOT_CONFIGURED));
    lv_label_set_text(ui.empty_network, network);

    const uint32_t value_ink = connected ? C_TEXT : C_MUTED;
    lv_obj_t *values[] = {ui.cpu.usage, ui.gpu.usage, ui.cpu.temperature,
        ui.gpu.temperature, ui.memory, ui.vram, ui.rx, ui.tx};
    for (unsigned i = 0; i < sizeof(values) / sizeof(values[0]); ++i)
        lv_obj_set_style_text_color(values[i], color(value_ink), 0);
    refresh_music();
    refresh_codex(); refresh_claude();
    refresh_footer();
    refresh_visibility();
}

static void select_channel(bool gpu)
{
    ui.gpu_selected = gpu;
    visible(ui.cpu.selection, !gpu);
    visible(ui.gpu.selection, gpu);
    lv_obj_set_style_text_color(ui.cpu.name, color(gpu ? C_MUTED : C_ACCENT), 0);
    lv_obj_set_style_text_color(ui.gpu.name, color(gpu ? C_ACCENT : C_MUTED), 0);
    lv_chart_hide_series(ui.chart, ui.cpu_series, gpu);
    lv_chart_hide_series(ui.chart, ui.gpu_series, !gpu);
    lv_label_set_text(ui.chart_title, gpu ? BT(BT_GPU_LOAD_60_SECONDS) : BT(BT_CPU_LOAD_60_SECONDS));
}

static void channel_clicked(lv_event_t *event)
{
    select_channel(lv_event_get_user_data(event) != NULL);
}

static void nav_clicked(lv_event_t *event)
{
    (void)event;
    if (ui.connection == BUDDY_PROVISIONING) return;
    buddy_ui_select_app(ui.active_app == BUDDY_APP_HOME ? BUDDY_APP_CLOCK : BUDDY_APP_HOME);
}

static void details_clicked(lv_event_t *event)
{
    (void)event;
    if (ui.connection == BUDDY_PROVISIONING || ui.active_app != BUDDY_APP_MONITOR) return;
    ui.details_visible = !ui.details_visible;
    refresh_visibility();
    animate_destination();
}

static void create_channel(channel_t *channel, int x, const char *name, bool gpu)
{
    channel->button = box(ui.overview, x, 0, 360, 132);
    lv_obj_add_flag(channel->button, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(channel->button, color(C_PRESSED), LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(channel->button, LV_OPA_COVER, LV_STATE_PRESSED);
    lv_obj_set_style_radius(channel->button, 8, 0);
    lv_obj_add_event_cb(channel->button, channel_clicked, LV_EVENT_CLICKED,
                        gpu ? (void *)1 : NULL);
    channel->name = text(channel->button, name, 0, 0, 110, &buddy_language_font_20, C_ACCENT);
    channel->usage = text(channel->button, "--", 0, 31, 158, &buddy_language_font_48, C_TEXT);
    text(channel->button, BT(BT_LOAD), 161, 55, 76, &buddy_language_font_14, C_MUTED);
    channel->temperature = text(channel->button, "--", 260, 43, 96, &buddy_language_font_24, C_TEXT);
    lv_obj_set_style_text_align(channel->temperature, LV_TEXT_ALIGN_RIGHT, 0);
    text(channel->button, BT(BT_TEMPERATURE), 241, 75, 115, &buddy_language_font_14, C_MUTED);
    channel->model = text(channel->button, BT(BT_WAITING_FOR_SENSORS), 0, 92, 356, &buddy_language_font_14, C_MUTED);
    channel->bar = lv_bar_create(channel->button);
    lv_obj_remove_style_all(channel->bar);
    lv_obj_set_pos(channel->bar, 0, 122);
    lv_obj_set_size(channel->bar, 356, 4);
    lv_bar_set_range(channel->bar, 0, 100);
    lv_obj_set_style_bg_opa(channel->bar, LV_OPA_COVER, LV_PART_MAIN);
    lv_obj_set_style_bg_color(channel->bar, color(C_RULE), LV_PART_MAIN);
    lv_obj_set_style_bg_opa(channel->bar, LV_OPA_COVER, LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(channel->bar, color(C_ACCENT), LV_PART_INDICATOR);
    channel->selection = box(channel->button, 0, 25, 44, 2);
    lv_obj_set_style_bg_color(channel->selection, color(C_ACCENT), 0);
    lv_obj_set_style_bg_opa(channel->selection, LV_OPA_COVER, 0);
    /* Children never consume the 360 x 132 touch target. */
    lv_obj_clear_flag(channel->bar, LV_OBJ_FLAG_CLICKABLE);
}

static lv_coord_t history_value(float value)
{
    if (!isfinite(value) || value < 0) return LV_CHART_POINT_NONE;
    return (lv_coord_t)lroundf(fminf(value, 100));
}

static void history_tick(lv_timer_t *timer)
{
    (void)timer;
    const bool fresh = ui.have_stats && ui.fresh_sample && ui.connection == BUDDY_PC_CONNECTED;
    lv_chart_set_next_value(ui.chart, ui.cpu_series,
                           fresh ? history_value(ui.stats.cpu_usage) : LV_CHART_POINT_NONE);
    lv_chart_set_next_value(ui.chart, ui.gpu_series,
                           fresh ? history_value(ui.stats.gpu_usage) : LV_CHART_POINT_NONE);
    ui.fresh_sample = false;
    refresh_codex(); refresh_claude();
    refresh_footer();
}

static void create_details(void)
{
    ui.details = box(ui.screen, 28, 105, 744, 314);
    text(ui.details, BT(BT_YOUR_PC), 0, 0, 435, &buddy_language_font_24, C_TEXT);
    text(ui.details, BT(BT_CPU), 0, 47, 430, &buddy_language_font_14, C_MUTED);
    ui.detail_cpu = text(ui.details, BT(BT_WAITING_FOR_READINGS), 0, 69, 424, &buddy_language_font_16, C_TEXT);
    lv_obj_set_height(ui.detail_cpu, 43);
    text(ui.details, BT(BT_GPU), 0, 126, 430, &buddy_language_font_14, C_MUTED);
    ui.detail_gpu = text(ui.details, BT(BT_WAITING_FOR_READINGS), 0, 148, 424, &buddy_language_font_16, C_TEXT);
    lv_obj_set_height(ui.detail_gpu, 43);
    text(ui.details, BT(BT_PC_UPTIME), 0, 207, 210, &buddy_language_font_14, C_MUTED);
    ui.detail_uptime = text(ui.details, "--", 0, 232, 420, &buddy_language_font_24, C_TEXT);
    rule(ui.details, 461, 0, 1, 295);
    text(ui.details, BT(BT_CONNECTION), 490, 0, 254, &buddy_language_font_24, C_TEXT);
    text(ui.details, BT(BT_WI_FI_NETWORK), 490, 47, 254, &buddy_language_font_14, C_MUTED);
    ui.detail_ssid = text(ui.details, BT(BT_NOT_CONFIGURED), 490, 69, 254, &buddy_language_font_16, C_TEXT);
    lv_obj_set_height(ui.detail_ssid, 43);
    text(ui.details, BT(BT_BUDDY_S_ADDRESS), 490, 126, 254, &buddy_language_font_14, C_MUTED);
    ui.detail_ip = text(ui.details, BT(BT_NOT_ASSIGNED), 490, 148, 254, &buddy_language_font_16, C_TEXT);
    ui.detail_state = text(ui.details, BT(BT_JOINING_WI_FI), 490, 204, 254, &buddy_language_font_20, C_WARNING);
    ui.detail_message = text(ui.details, "", 490, 238, 254, &buddy_language_font_14, C_MUTED);
    lv_obj_set_height(ui.detail_message, 65);
}

static lv_obj_t *action_button(lv_obj_t *parent, const char *label, int x, int y,
                               int width, int height, lv_obj_t **caption)
{
    lv_obj_t *button = box(parent, x, y, width, height);
    lv_obj_add_flag(button, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(button, color(C_PRESSED), 0);
    lv_obj_set_style_bg_opa(button, LV_OPA_COVER, 0);
    lv_obj_set_style_bg_color(button, color(C_RULE), LV_STATE_PRESSED);
    lv_obj_set_style_radius(button, 8, 0);
    lv_obj_t *text_obj = text(button, label, 8, 0, width - 16, &buddy_language_font_20, C_TEXT);
    lv_obj_set_style_text_align(text_obj, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(text_obj, LV_ALIGN_CENTER, 0, 0);
    if (caption) *caption = text_obj;
    return button;
}

static void button_enabled(lv_obj_t *button, bool enabled)
{
    if (enabled) lv_obj_clear_state(button, LV_STATE_DISABLED);
    else lv_obj_add_state(button, LV_STATE_DISABLED);
    lv_obj_set_style_opa(button, enabled ? LV_OPA_COVER : LV_OPA_40, 0);
}

static lv_obj_t *progress_bar(lv_obj_t *parent, int x, int y, int width)
{
    lv_obj_t *bar = lv_bar_create(parent);
    lv_obj_remove_style_all(bar);
    lv_obj_set_pos(bar, x, y);
    lv_obj_set_size(bar, width, 4);
    lv_bar_set_range(bar, 0, 1000);
    lv_obj_set_style_bg_color(bar, color(C_RULE), LV_PART_MAIN);
    lv_obj_set_style_bg_opa(bar, LV_OPA_COVER, LV_PART_MAIN);
    lv_obj_set_style_bg_color(bar, color(C_ACCENT), LV_PART_INDICATOR);
    lv_obj_set_style_bg_opa(bar, LV_OPA_COVER, LV_PART_INDICATOR);
    lv_obj_clear_flag(bar, LV_OBJ_FLAG_CLICKABLE);
    return bar;
}

static bool music_connected(void)
{
    return ui.have_stats && ui.connection == BUDDY_PC_CONNECTED &&
           lv_tick_elaps(ui.last_stats_tick) < CLOCK_DELAY_MS;
}

static bool music_can_control(unsigned action)
{
    const buddy_music_t *music = &ui.stats.music;
    if (!music_connected() || !music->running || !music->available ||
        !ui.music_control_callback || ui.music_command_pending) return false;
    return action == 0 ? music->can_previous : action == 1 ? music->can_play_pause : music->can_next;
}

static void playback_time(char *buffer, size_t size, uint32_t seconds)
{
    snprintf(buffer, size, "%lu:%02lu", (unsigned long)(seconds / 60), (unsigned long)(seconds % 60));
}

static void refresh_music(void)
{
    if (!ui.music_page) return;
    const buddy_music_t *music = &ui.stats.music;
    bool connected = music_connected();
    const char *status = !connected ? BT(BT_PC_OFFLINE) : !music->running ? BT(BT_NOT_RUNNING) :
                         !music->available ? BT(BT_WAITING_FOR_PLAYBACK) : music->playing ? BT(BT_PLAYING) : BT(BT_PAUSED);
    lv_label_set_text(ui.music_status, status);
    lv_obj_set_style_text_color(ui.music_status, color(!connected ? C_WARNING : C_ACCENT), 0);
    if (music->available) {
        lv_label_set_text(ui.music_title, music->title[0] ? music->title : BT(BT_UNTITLED_TRACK));
        lv_label_set_text(ui.music_artist, music->artist);
        lv_label_set_text(ui.music_album, music->album);
    } else {
        lv_label_set_text(ui.music_title, !connected ? BT(BT_WAITING_FOR_YOUR_PC) :
            music->running ? BT(BT_WAITING_FOR_PLAYBACK) : BT(BT_OPEN_SPOTIFY_ON_YOUR_PC));
        lv_label_set_text(ui.music_artist, !connected ? BT(BT_PLAYBACK_RETURNS_WHEN_YOUR_PC_RECONNECTS) :
            music->running ? BT(BT_START_A_TRACK_IN_SPOTIFY) : BT(BT_YOUR_MUSIC_WILL_APPEAR_HERE_AUTOMATICALLY));
        lv_label_set_text(ui.music_album, "");
    }
    char buffer[24];
    uint32_t position = music->position_s;
    if (music->duration_s && position > music->duration_s) position = music->duration_s;
    if (music->available) playback_time(buffer, sizeof(buffer), position);
    else snprintf(buffer, sizeof(buffer), "--:--");
    lv_label_set_text(ui.music_elapsed, buffer);
    if (music->available && music->duration_s) playback_time(buffer, sizeof(buffer), music->duration_s);
    else snprintf(buffer, sizeof(buffer), "--:--");
    lv_label_set_text(ui.music_duration, buffer);
    lv_bar_set_value(ui.music_progress, music->available && music->duration_s ?
        (int)((uint64_t)position * 1000 / music->duration_s) : 0, LV_ANIM_OFF);
    lv_label_set_text(ui.music_button_labels[1], music->available && music->playing ? BT(BT_PAUSE) : BT(BT_PLAY));
    for (unsigned i = 0; i < 3; ++i) button_enabled(ui.music_buttons[i], music_can_control(i));
}

static void music_clicked(lv_event_t *event)
{
    static const char *actions[] = { "previous", "play_pause", "next" };
    unsigned action = (unsigned)(uintptr_t)lv_event_get_user_data(event);
    if (action >= 3 || !music_can_control(action)) return;
    if (ui.music_control_callback(actions[action])) {
        ui.music_command_pending = true;
        ui.music_command_tick = lv_tick_get();
        ui.music_command_control_id = ui.stats.music.control_id;
        lv_label_set_text(ui.music_feedback, BT(BT_SENDING_TO_SPOTIFY));
    } else {
        lv_label_set_text(ui.music_feedback, BT(BT_COULDN_T_SEND_TRY_AGAIN));
    }
    refresh_music();
}

static void refresh_codex_tasks(void)
{
    const buddy_codex_activity_t *activity = &ui.stats.codex.activity;
    bool available = codex_tasks_available();
    bool stale = codex_tasks_stale();
    unsigned count = available ? activity->item_count : 0;
    if (count > 3) count = 3;
    char buffer[128];
    if (available) {
        if (stale) snprintf(buffer, sizeof(buffer), BT(BT_CACHED_LU_RECORDED_TASKS), (unsigned long)activity->total_count);
        else snprintf(buffer, sizeof(buffer), BT(BT_LU_RECENTLY_WORKING_LU_RECORDED),
            (unsigned long)activity->working_count, (unsigned long)activity->total_count);
    } else snprintf(buffer, sizeof(buffer), BT(BT_RECORDED_ACTIVITY));
    set_label_if_changed(ui.codex_task_summary, buffer);
    lv_obj_set_style_text_color(ui.codex_task_summary, color(stale ? C_WARNING : C_MUTED), 0);
    for (unsigned i = 0; i < 3; ++i) {
        visible(ui.codex_task_rows[i], i < count);
        if (i >= count) continue;
        const buddy_codex_task_t *task = &activity->items[i];
        set_label_if_changed(ui.codex_task_titles[i], task->title[0] ? task->title : BT(BT_UNTITLED_TASK));
        set_label_if_changed(ui.codex_task_projects[i], task->project[0] ? task->project : BT(BT_NO_PROJECT_RECORDED));
        bool recent_working = strcmp(task->state, "working") == 0 &&
            (uint64_t)task->age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U <= 120U;
        const char *state = recent_working ? (stale ? BT(BT_WAS_WORKING) : BT(BT_RECENTLY_WORKING)) :
            strcmp(task->state, "waiting") == 0 ? BT(BT_WAITING_FOR_YOU) :
            strcmp(task->state, "completed") == 0 ? BT(BT_COMPLETED) :
            strcmp(task->state, "interrupted") == 0 ? BT(BT_INTERRUPTED) :
            strcmp(task->state, "recent") == 0 ? BT(BT_RECENT) : BT(BT_UNKNOWN);
        set_label_if_changed(ui.codex_task_states[i], state);
        lv_obj_set_style_text_color(ui.codex_task_states[i], color(recent_working && !stale ? C_ACCENT : C_MUTED), 0);
        char age[40];
        if (strcmp(task->state, "unknown") == 0 && task->age_s >= 31536000U)
            snprintf(buffer, sizeof(buffer), BT(BT_ACTIVITY_TIME_UNKNOWN));
        else {
            activity_age_text(age, sizeof(age), task->age_s);
            snprintf(buffer, sizeof(buffer), BT(BT_LAST_ACTIVITY_S), age);
        }
        set_label_if_changed(ui.codex_task_ages[i], buffer);
    }
    visible(ui.codex_tasks_empty_title, !count);
    visible(ui.codex_tasks_empty_body, !count);
    if (!count) {
        const char *title = available ? BT(BT_NO_RECENT_TASKS_RECORDED) : BT(BT_WAITING_FOR_TASK_ACTIVITY);
        const char *body = available ? BT(BT_THIS_LOCAL_SNAPSHOT_CONTAINS_NO_RECENT_TASK_ENTRIES) :
            BT(BT_RECENT_TASK_RECORDS_WILL_APPEAR_WHEN_THE_PC_CAN);
        if (!available && !music_connected()) {
            title = BT(BT_WAITING_FOR_YOUR_PC);
            body = BT(BT_KEEP_CLAWPAL_RUNNING_IN_YOUR_PC_S_TRAY_RECORDED);
        } else if (!available && (strcmp(activity->status, "closed") == 0 || !ui.stats.codex.running)) {
            title = BT(BT_OPEN_CODEX_ON_YOUR_PC);
            body = BT(BT_BUDDY_WILL_SHOW_RECENT_LOCAL_TASK_RECORDS_WHEN_THEY);
        } else if (!available && (strcmp(activity->status, "error") == 0 || strcmp(activity->status, "unavailable") == 0)) {
            title = BT(BT_TASK_ACTIVITY_UNAVAILABLE);
            body = BT(BT_THE_PC_COULD_NOT_READ_LOCAL_TASK_RECORDS_BUDDY);
        }
        set_label_if_changed(ui.codex_tasks_empty_title, title);
        set_label_if_changed(ui.codex_tasks_empty_body, body);
    }
    if (ui.codex_tasks_selected) {
        const char *status = !available ? BT(BT_ACTIVITY_UNAVAILABLE) : stale ? BT(BT_CACHED_ACTIVITY) :
            strcmp(activity->status, "partial") == 0 ? BT(BT_PARTIAL_SNAPSHOT) : BT(BT_LOCAL_SNAPSHOT);
        set_label_if_changed(ui.codex_status, status);
        lv_obj_set_style_text_color(ui.codex_status, color(stale || !available ? C_WARNING : C_MUTED), 0);
        if (available) {
            char age[40];
            activity_age_text(age, sizeof(age), activity->age_s);
            snprintf(buffer, sizeof(buffer), BT(BT_S_S_RECENT_LOCAL_RECORDS_ONLY), stale ? BT(BT_CACHED_SNAPSHOT) : BT(BT_SNAPSHOT), age);
        } else snprintf(buffer, sizeof(buffer), BT(BT_NO_LOCAL_TASK_SNAPSHOT_AVAILABLE));
        set_label_if_changed(ui.codex_age, buffer);
    }
}

static void refresh_claude_tasks(void)
{
    const buddy_codex_activity_t *activity = &ui.stats.claude.activity;
    bool available = claude_tasks_available();
    bool stale = claude_tasks_stale();
    unsigned count = available ? activity->item_count : 0;
    if (count > 3) count = 3;
    char buffer[128];
    if (available) {
        if (stale) snprintf(buffer, sizeof(buffer), BT(BT_CACHED_LU_RECORDED_TASKS), (unsigned long)activity->total_count);
        else snprintf(buffer, sizeof(buffer), BT(BT_LU_RECENTLY_WORKING_LU_RECORDED),
            (unsigned long)activity->working_count, (unsigned long)activity->total_count);
    } else snprintf(buffer, sizeof(buffer), BT(BT_RECORDED_ACTIVITY));
    set_label_if_changed(ui.claude_task_summary, buffer);
    lv_obj_set_style_text_color(ui.claude_task_summary, color(stale ? C_WARNING : C_MUTED), 0);
    for (unsigned i = 0; i < 3; ++i) {
        visible(ui.claude_task_rows[i], i < count);
        if (i >= count) continue;
        const buddy_codex_task_t *task = &activity->items[i];
        set_label_if_changed(ui.claude_task_titles[i], task->title[0] ? task->title : BT(BT_UNTITLED_TASK));
        set_label_if_changed(ui.claude_task_projects[i], task->project[0] ? task->project : BT(BT_NO_PROJECT_RECORDED));
        bool recent_working = strcmp(task->state, "working") == 0 &&
            (uint64_t)task->age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U <= 120U;
        const char *state = recent_working ? (stale ? BT(BT_WAS_WORKING) : BT(BT_RECENTLY_WORKING)) :
            strcmp(task->state, "waiting") == 0 ? BT(BT_WAITING_FOR_YOU) :
            strcmp(task->state, "completed") == 0 ? BT(BT_COMPLETED) :
            strcmp(task->state, "interrupted") == 0 ? BT(BT_INTERRUPTED) :
            strcmp(task->state, "recent") == 0 ? BT(BT_RECENT) : BT(BT_UNKNOWN);
        set_label_if_changed(ui.claude_task_states[i], state);
        lv_obj_set_style_text_color(ui.claude_task_states[i], color(recent_working && !stale ? C_ACCENT : C_MUTED), 0);
        char age[40];
        if (strcmp(task->state, "unknown") == 0 && task->age_s >= 31536000U)
            snprintf(buffer, sizeof(buffer), BT(BT_ACTIVITY_TIME_UNKNOWN));
        else {
            activity_age_text(age, sizeof(age), task->age_s);
            snprintf(buffer, sizeof(buffer), BT(BT_LAST_ACTIVITY_S), age);
        }
        set_label_if_changed(ui.claude_task_ages[i], buffer);
    }
    visible(ui.claude_tasks_empty_title, !count);
    visible(ui.claude_tasks_empty_body, !count);
    if (!count) {
        const char *title = available ? BT(BT_NO_RECENT_TASKS_RECORDED) : BT(BT_WAITING_FOR_TASK_ACTIVITY);
        const char *body = available ? BT(BT_THIS_LOCAL_SNAPSHOT_CONTAINS_NO_RECENT_TASK_ENTRIES) :
            BT(BT_RECENT_TASK_RECORDS_WILL_APPEAR_WHEN_THE_PC_CAN);
        if (!available && !music_connected()) {
            title = BT(BT_WAITING_FOR_YOUR_PC);
            body = BT(BT_KEEP_CLAWPAL_RUNNING_IN_YOUR_PC_S_TRAY_RECORDED);
        } else if (!available && (strcmp(activity->status, "closed") == 0 || !ui.stats.claude.running)) {
            title = BT(BT_OPEN_CLAUDE_ON_YOUR_PC);
            body = BT(BT_BUDDY_WILL_SHOW_RECENT_LOCAL_TASK_RECORDS_WHEN_THEY);
        } else if (!available && (strcmp(activity->status, "error") == 0 || strcmp(activity->status, "unavailable") == 0)) {
            title = BT(BT_TASK_ACTIVITY_UNAVAILABLE);
            body = BT(BT_THE_PC_COULD_NOT_READ_LOCAL_TASK_RECORDS_BUDDY);
        }
        set_label_if_changed(ui.claude_tasks_empty_title, title);
        set_label_if_changed(ui.claude_tasks_empty_body, body);
    }
    if (ui.claude_tasks_selected) {
        const char *status = !available ? BT(BT_ACTIVITY_UNAVAILABLE) : stale ? BT(BT_CACHED_ACTIVITY) :
            strcmp(activity->status, "partial") == 0 ? BT(BT_PARTIAL_SNAPSHOT) : BT(BT_LOCAL_SNAPSHOT);
        set_label_if_changed(ui.claude_status, status);
        lv_obj_set_style_text_color(ui.claude_status, color(stale || !available ? C_WARNING : C_MUTED), 0);
        if (available) {
            char age[40];
            activity_age_text(age, sizeof(age), activity->age_s);
            snprintf(buffer, sizeof(buffer), BT(BT_S_S_RECENT_LOCAL_RECORDS_ONLY), stale ? BT(BT_CACHED_SNAPSHOT) : BT(BT_SNAPSHOT), age);
        } else snprintf(buffer, sizeof(buffer), BT(BT_NO_LOCAL_TASK_SNAPSHOT_AVAILABLE));
        set_label_if_changed(ui.claude_age, buffer);
    }
}

static void refresh_codex(void)
{
    if (!ui.codex_page) return;
    const buddy_codex_t *codex = &ui.stats.codex;
    bool available = codex_has_windows();
    bool stale = codex_stale();
    if (!ui.codex_tab_initialized && !ui.codex_tab_manual && codex_tasks_available()) {
        ui.codex_tasks_selected = codex->activity.item_count > 0 && codex->activity.working_count > 0 && !codex_tasks_stale();
        ui.codex_tab_initialized = true;
    }
    set_label_if_changed(ui.codex_status, codex_status_text());
    lv_obj_set_style_text_color(ui.codex_status, color(stale || !available ? C_WARNING : C_ACCENT), 0);
    char buffer[128];
    if (ui.codex_tasks_selected) snprintf(buffer, sizeof(buffer), BT(BT_RECORDED_ACTIVITY));
    else if (codex->plan[0]) snprintf(buffer, sizeof(buffer), BT(BT_S_PLAN), codex->plan);
    else snprintf(buffer, sizeof(buffer), BT(BT_ACCOUNT_USAGE));
    set_label_if_changed(ui.codex_plan, buffer);
    const buddy_codex_window_t *windows[2];
    unsigned count = 0;
    if (available && codex_window_available(&codex->primary)) windows[count++] = &codex->primary;
    if (available && codex_window_available(&codex->secondary)) windows[count++] = &codex->secondary;
    for (unsigned i = 0; i < 2; ++i) {
        visible(ui.codex_windows[i], i < count);
        if (i >= count) continue;
        const buddy_codex_window_t *window = windows[i];
        lv_obj_set_y(ui.codex_windows[i], count == 1 ? 74 : 20 + (int)i * 150);
        codex_window_name(buffer, sizeof(buffer), window->window_minutes);
        set_label_if_changed(ui.codex_window_names[i], buffer);
        snprintf(buffer, sizeof(buffer), "%.1f%%", window->remaining_percent);
        set_label_if_changed(ui.codex_remaining[i], buffer);
        lv_obj_set_style_text_color(ui.codex_remaining[i], color(stale ? C_MUTED : C_TEXT), 0);
        lv_obj_set_style_bg_color(ui.codex_bars[i], color(stale ? C_MUTED : C_ACCENT), LV_PART_INDICATOR);
        lv_bar_set_value(ui.codex_bars[i], (int)lroundf(window->remaining_percent * 10.f), LV_ANIM_OFF);
        if (window->resets_at) {
            time_t timestamp=window->resets_at;struct tm local;localtime_r(&timestamp,&local);
            char date[96],hour[24],stamp[128];buddy_date(date,sizeof(date),&local,true);
            strftime(hour,sizeof(hour),view_settings.use_24h?"%H:%M":"%I:%M",&local);
            snprintf(stamp,sizeof(stamp),"%s %s%s%s",date,hour,view_settings.use_24h?"":" ",view_settings.use_24h?"":BT(local.tm_hour<12?BT_AM:BT_PM));
            snprintf(buffer,sizeof(buffer),BT(BT_RESETS_VALUE),stamp);
        } else if (window->reset_text[0]) snprintf(buffer, sizeof(buffer), BT(BT_RESETS_VALUE), window->reset_text);
        else snprintf(buffer, sizeof(buffer), BT(BT_RESET_TIME_UNAVAILABLE));
        set_label_if_changed(ui.codex_resets[i], buffer);
    }
    visible(ui.codex_empty_title, !available);
    visible(ui.codex_empty_body, !available);
    if (available) {
        uint64_t age = (uint64_t)codex->age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U;
        if (age < 60) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_S_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)age);
        else if (age < 3600) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_MIN_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)(age / 60));
        else if (age < 86400) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_H_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)(age / 3600));
        else snprintf(buffer, sizeof(buffer), BT(BT_S_LU_D_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)(age / 86400));
        set_label_if_changed(ui.codex_age, buffer);
    } else {
        set_label_if_changed(ui.codex_empty_title, codex_status_text());
        const char *body = BT(BT_OPEN_CODEX_OR_CHATGPT_ON_YOUR_PC_YOUR_AVAILABLE);
        if (!music_connected()) body = BT(BT_KEEP_CLAWPAL_RUNNING_IN_YOUR_PC_S_TRAY_LIMITS);
        else if (strcmp(codex->status, "sign_in") == 0 || strcmp(codex->status, "signed_out") == 0 ||
                 strcmp(codex->status, "unauthorized") == 0)
            body = BT(BT_SIGN_IN_TO_CODEX_ON_YOUR_PC_BUDDY_WILL);
        else if (strcmp(codex->status, "runtime_missing") == 0)
            body = BT(BT_UPDATE_OR_INSTALL_CODEX_ON_YOUR_PC_BUDDY_NEEDS);
        else if (strcmp(codex->status, "no_limits") == 0)
            body = BT(BT_YOUR_ACCOUNT_DID_NOT_REPORT_A_USAGE_WINDOW_BUDDY);
        else if (strcmp(codex->status, "error") == 0 || strcmp(codex->status, "failed") == 0)
            body = BT(BT_THE_PC_COULD_NOT_REFRESH_YOUR_ACCOUNT_LIMITS_BUDDY);
        else if (strcmp(codex->status, "unavailable") == 0 && codex->running)
            body = BT(BT_YOUR_ACCOUNT_LIMITS_ARE_NOT_AVAILABLE_YET_BUDDY_WILL);
        else if (codex->running) body = BT(BT_READING_YOUR_CODEX_ACCOUNT_ON_THE_PC_YOUR_LIMITS);
        set_label_if_changed(ui.codex_empty_body, body);
        set_label_if_changed(ui.codex_age, BT(BT_NO_ACCOUNT_LIMITS_AVAILABLE));
    }
    refresh_codex_tasks();
    visible(ui.codex_usage_body, !ui.codex_tasks_selected);
    visible(ui.codex_tasks_body, ui.codex_tasks_selected);
    for (unsigned i = 0; i < 2; ++i) {
        bool selected = (i == 0) == ui.codex_tasks_selected;
        lv_obj_set_style_bg_opa(ui.codex_tabs[i], selected ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
        lv_obj_set_style_text_color(ui.codex_tab_labels[i], color(selected ? C_ACCENT : C_MUTED), 0);
    }
    refresh_app_cards();
}

static void refresh_claude(void)
{
    if (!ui.claude_page) return;
    const buddy_codex_t *claude = &ui.stats.claude;
    bool available = claude_has_windows();
    bool stale = claude_stale();
    if (!ui.claude_tab_initialized && !ui.claude_tab_manual && claude_tasks_available()) {
        ui.claude_tasks_selected = claude->activity.item_count > 0 && claude->activity.working_count > 0 && !claude_tasks_stale();
        ui.claude_tab_initialized = true;
    }
    set_label_if_changed(ui.claude_status, claude_status_text());
    lv_obj_set_style_text_color(ui.claude_status, color(stale || !available ? C_WARNING : C_ACCENT), 0);
    char buffer[128];
    if (ui.claude_tasks_selected) snprintf(buffer, sizeof(buffer), BT(BT_RECORDED_ACTIVITY));
    else if (claude->plan[0]) snprintf(buffer, sizeof(buffer), BT(BT_S_PLAN), claude->plan);
    else snprintf(buffer, sizeof(buffer), BT(BT_ACCOUNT_USAGE));
    set_label_if_changed(ui.claude_plan, buffer);
    const buddy_codex_window_t *windows[2];
    unsigned count = 0;
    if (available && claude_window_available(&claude->primary)) windows[count++] = &claude->primary;
    if (available && claude_window_available(&claude->secondary)) windows[count++] = &claude->secondary;
    for (unsigned i = 0; i < 2; ++i) {
        visible(ui.claude_windows[i], i < count);
        if (i >= count) continue;
        const buddy_codex_window_t *window = windows[i];
        lv_obj_set_y(ui.claude_windows[i], count == 1 ? 74 : 20 + (int)i * 150);
        claude_window_name(buffer, sizeof(buffer), window->window_minutes);
        set_label_if_changed(ui.claude_window_names[i], buffer);
        snprintf(buffer, sizeof(buffer), "%.1f%%", window->remaining_percent);
        set_label_if_changed(ui.claude_remaining[i], buffer);
        lv_obj_set_style_text_color(ui.claude_remaining[i], color(stale ? C_MUTED : C_TEXT), 0);
        lv_obj_set_style_bg_color(ui.claude_bars[i], color(stale ? C_MUTED : C_ACCENT), LV_PART_INDICATOR);
        lv_bar_set_value(ui.claude_bars[i], (int)lroundf(window->remaining_percent * 10.f), LV_ANIM_OFF);
        if (window->resets_at) {
            time_t timestamp=window->resets_at;struct tm local;localtime_r(&timestamp,&local);
            char date[96],hour[24],stamp[128];buddy_date(date,sizeof(date),&local,true);
            strftime(hour,sizeof(hour),view_settings.use_24h?"%H:%M":"%I:%M",&local);
            snprintf(stamp,sizeof(stamp),"%s %s%s%s",date,hour,view_settings.use_24h?"":" ",view_settings.use_24h?"":BT(local.tm_hour<12?BT_AM:BT_PM));
            snprintf(buffer,sizeof(buffer),BT(BT_RESETS_VALUE),stamp);
        } else if (window->reset_text[0]) snprintf(buffer, sizeof(buffer), BT(BT_RESETS_VALUE), window->reset_text);
        else snprintf(buffer, sizeof(buffer), BT(BT_RESET_TIME_UNAVAILABLE));
        set_label_if_changed(ui.claude_resets[i], buffer);
    }
    visible(ui.claude_empty_title, !available);
    visible(ui.claude_empty_body, !available);
    if (available) {
        uint64_t age = (uint64_t)claude->age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U;
        if (age < 60) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_S_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)age);
        else if (age < 3600) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_MIN_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)(age / 60));
        else if (age < 86400) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_H_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)(age / 3600));
        else snprintf(buffer, sizeof(buffer), BT(BT_S_LU_D_AGO), stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED), (unsigned long)(age / 86400));
        set_label_if_changed(ui.claude_age, buffer);
    } else {
        set_label_if_changed(ui.claude_empty_title, claude_status_text());
        const char *body = BT(BT_OPEN_CLAUDE_OR_CHATGPT_ON_YOUR_PC_YOUR_AVAILABLE);
        if (!music_connected()) body = BT(BT_KEEP_CLAWPAL_RUNNING_IN_YOUR_PC_S_TRAY_LIMITS);
        else if (strcmp(claude->status, "sign_in") == 0 || strcmp(claude->status, "signed_out") == 0 ||
                 strcmp(claude->status, "unauthorized") == 0)
            body = BT(BT_SIGN_IN_TO_CLAUDE_ON_YOUR_PC_BUDDY_WILL);
        else if (strcmp(claude->status, "runtime_missing") == 0)
            body = BT(BT_UPDATE_OR_INSTALL_CLAUDE_ON_YOUR_PC_BUDDY_NEEDS);
        else if (strcmp(claude->status, "no_limits") == 0)
            body = BT(BT_CLAUDE_NO_LIMITS);
        else if (strcmp(claude->status, "error") == 0 || strcmp(claude->status, "failed") == 0)
            body = BT(BT_THE_PC_COULD_NOT_REFRESH_YOUR_ACCOUNT_LIMITS_BUDDY);
        else if (strcmp(claude->status, "unavailable") == 0 && claude->running)
            body = BT(BT_YOUR_ACCOUNT_LIMITS_ARE_NOT_AVAILABLE_YET_BUDDY_WILL);
        else if (claude->running) body = BT(BT_READING_YOUR_CLAUDE_ACCOUNT_ON_THE_PC_YOUR_LIMITS);
        set_label_if_changed(ui.claude_empty_body, body);
        set_label_if_changed(ui.claude_age, BT(BT_NO_ACCOUNT_LIMITS_AVAILABLE));
    }
    refresh_claude_tasks();
    visible(ui.claude_usage_body, !ui.claude_tasks_selected);
    visible(ui.claude_tasks_body, ui.claude_tasks_selected);
    for (unsigned i = 0; i < 2; ++i) {
        bool selected = (i == 0) == ui.claude_tasks_selected;
        lv_obj_set_style_bg_opa(ui.claude_tabs[i], selected ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
        lv_obj_set_style_text_color(ui.claude_tab_labels[i], color(selected ? C_ACCENT : C_MUTED), 0);
    }
    refresh_app_cards();
}

static void codex_tab_clicked(lv_event_t *event)
{
    bool tasks = (uintptr_t)lv_event_get_user_data(event) == 0;
    ui.codex_tab_manual = true;
    ui.codex_tab_initialized = true;
    if (tasks == ui.codex_tasks_selected) return;
    ui.codex_tasks_selected = tasks;
    refresh_codex(); refresh_claude();
    refresh_footer();
    buddy_ui_motion_enter(tasks ? ui.codex_tasks_body : ui.codex_usage_body, 6, 180, 0);
}

static void claude_tab_clicked(lv_event_t *event)
{
    bool tasks = (uintptr_t)lv_event_get_user_data(event) == 0;
    ui.claude_tab_manual = true;
    ui.claude_tab_initialized = true;
    if (tasks == ui.claude_tasks_selected) return;
    ui.claude_tasks_selected = tasks;
    refresh_claude();
    refresh_footer();
    buddy_ui_motion_enter(tasks ? ui.claude_tasks_body : ui.claude_usage_body, 6, 180, 0);
}

static void create_codex_page(void)
{
    ui.codex_page = box(ui.screen, 0, 0, 800, 440);
    text(ui.codex_page, BT(BT_CODEX), 28, 20, 420, &buddy_language_font_24, C_TEXT);
    ui.codex_plan = text(ui.codex_page, BT(BT_ACCOUNT_USAGE), 28, 53, 420, &buddy_language_font_20, C_MUTED);
    lv_obj_set_height(ui.codex_plan, 27);
    for (unsigned i = 0; i < 2; ++i) {
        ui.codex_tabs[i] = action_button(ui.codex_page, i == 0 ? BT(BT_TASKS) : BT(BT_USAGE),
            i == 0 ? 488 : 632, 20, i == 0 ? 136 : 140, 44, &ui.codex_tab_labels[i]);
        lv_obj_add_event_cb(ui.codex_tabs[i], codex_tab_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)i);
    }
    ui.codex_status = text(ui.codex_page, BT(BT_WAITING_FOR_PC), 610, 294, 162, &buddy_language_font_16, C_WARNING);
    lv_obj_set_height(ui.codex_status, 58);
    lv_obj_set_style_text_align(ui.codex_status, LV_TEXT_ALIGN_CENTER, 0);
    rule(ui.codex_page, 28, 84, 744, 1);
    ui.codex_usage_body = box(ui.codex_page, 0, 85, 600, 315);
    for (unsigned i = 0; i < 2; ++i) {
        lv_obj_t *row = box(ui.codex_usage_body, 28, 20 + (int)i * 150, 548, 132);
        ui.codex_windows[i] = row;
        ui.codex_window_names[i] = text(row, BT(BT_USAGE_LIMIT), 0, 0, 548, &buddy_language_font_20, C_MUTED);
        ui.codex_remaining[i] = text(row, "--", 0, 28, 245, &buddy_language_font_48, C_TEXT);
        text(row, BT(BT_REMAINING), 252, 53, 290, &buddy_language_font_16, C_MUTED);
        ui.codex_bars[i] = progress_bar(row, 0, 89, 548);
        ui.codex_resets[i] = text(row, "", 0, 104, 548, &buddy_language_font_20, C_MUTED);
        lv_obj_set_height(ui.codex_resets[i], 28);
    }
    ui.codex_mascot = lv_img_create(ui.codex_page);
    lv_obj_set_pos(ui.codex_mascot, 648, 165);
    lv_img_set_antialias(ui.codex_mascot, false);
    lv_obj_clear_flag(ui.codex_mascot, LV_OBJ_FLAG_CLICKABLE);
    ui.codex_empty_title = text(ui.codex_usage_body, BT(BT_WAITING_FOR_PC), 28, 65, 548, &buddy_language_font_32, C_TEXT);
    lv_obj_set_height(ui.codex_empty_title, 84);
    ui.codex_empty_body = text(ui.codex_usage_body, "", 28, 168, 548, &buddy_language_font_20, C_MUTED);
    lv_obj_set_height(ui.codex_empty_body, 120);
    lv_label_set_long_mode(ui.codex_empty_body, LV_LABEL_LONG_WRAP);
    ui.codex_tasks_body = box(ui.codex_page, 0, 85, 600, 315);
    ui.codex_task_summary = text(ui.codex_tasks_body, BT(BT_RECORDED_ACTIVITY), 28, 18, 548, &buddy_language_font_16, C_MUTED);
    for (unsigned i = 0; i < 3; ++i) {
        lv_obj_t *row = box(ui.codex_tasks_body, 28, 61 + (int)i * 84, 548, 80);
        ui.codex_task_rows[i] = row;
        ui.codex_task_titles[i] = text(row, "", 0, 0, 398, &buddy_language_font_20, C_TEXT);
        lv_obj_set_height(ui.codex_task_titles[i], 27);
        ui.codex_task_states[i] = text(row, "", 406, 5, 142, &buddy_language_font_14, C_MUTED);
        lv_obj_set_style_text_align(ui.codex_task_states[i], LV_TEXT_ALIGN_RIGHT, 0);
        ui.codex_task_projects[i] = text(row, "", 0, 29, 548, &buddy_language_font_20, C_MUTED);
        lv_obj_set_height(ui.codex_task_projects[i], 27);
        ui.codex_task_ages[i] = text(row, "", 0, 59, 548, &buddy_language_font_14, C_MUTED);
        if (i < 2) rule(row, 0, 79, 548, 1);
    }
    ui.codex_tasks_empty_title = text(ui.codex_tasks_body, BT(BT_WAITING_FOR_TASK_ACTIVITY), 28, 65, 548, &buddy_language_font_32, C_TEXT);
    lv_obj_set_height(ui.codex_tasks_empty_title, 84);
    ui.codex_tasks_empty_body = text(ui.codex_tasks_body, "", 28, 168, 548, &buddy_language_font_20, C_MUTED);
    lv_label_set_long_mode(ui.codex_tasks_empty_body, LV_LABEL_LONG_WRAP);
    lv_obj_set_height(ui.codex_tasks_empty_body, 120);
    ui.codex_age = text(ui.codex_page, BT(BT_NO_ACCOUNT_LIMITS_AVAILABLE), 28, 410, 744, &buddy_language_font_14, C_MUTED);
    refresh_codex(); refresh_claude();
}

static void create_claude_page(void)
{
    ui.claude_page = box(ui.screen, 0, 0, 800, 440);
    text(ui.claude_page, BT(BT_CLAUDE), 28, 20, 420, &buddy_language_font_24, C_TEXT);
    ui.claude_plan = text(ui.claude_page, BT(BT_ACCOUNT_USAGE), 28, 53, 420, &buddy_language_font_20, C_MUTED);
    lv_obj_set_height(ui.claude_plan, 27);
    for (unsigned i = 0; i < 2; ++i) {
        ui.claude_tabs[i] = action_button(ui.claude_page, i == 0 ? BT(BT_TASKS) : BT(BT_USAGE),
            i == 0 ? 488 : 632, 20, i == 0 ? 136 : 140, 44, &ui.claude_tab_labels[i]);
        lv_obj_add_event_cb(ui.claude_tabs[i], claude_tab_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)i);
    }
    ui.claude_status = text(ui.claude_page, BT(BT_WAITING_FOR_PC), 610, 294, 162, &buddy_language_font_16, C_WARNING);
    lv_obj_set_height(ui.claude_status, 58);
    lv_obj_set_style_text_align(ui.claude_status, LV_TEXT_ALIGN_CENTER, 0);
    rule(ui.claude_page, 28, 84, 744, 1);
    ui.claude_usage_body = box(ui.claude_page, 0, 85, 600, 315);
    for (unsigned i = 0; i < 2; ++i) {
        lv_obj_t *row = box(ui.claude_usage_body, 28, 20 + (int)i * 150, 548, 132);
        ui.claude_windows[i] = row;
        ui.claude_window_names[i] = text(row, BT(BT_USAGE_LIMIT), 0, 0, 548, &buddy_language_font_20, C_MUTED);
        ui.claude_remaining[i] = text(row, "--", 0, 28, 245, &buddy_language_font_48, C_TEXT);
        text(row, BT(BT_REMAINING), 252, 53, 290, &buddy_language_font_16, C_MUTED);
        ui.claude_bars[i] = progress_bar(row, 0, 89, 548);
        ui.claude_resets[i] = text(row, "", 0, 104, 548, &buddy_language_font_20, C_MUTED);
        lv_obj_set_height(ui.claude_resets[i], 28);
    }
    ui.claude_mascot = lv_img_create(ui.claude_page);
    lv_obj_set_pos(ui.claude_mascot, 648, 165);
    lv_img_set_antialias(ui.claude_mascot, false);
    lv_obj_clear_flag(ui.claude_mascot, LV_OBJ_FLAG_CLICKABLE);
    ui.claude_empty_title = text(ui.claude_usage_body, BT(BT_WAITING_FOR_PC), 28, 65, 548, &buddy_language_font_32, C_TEXT);
    lv_obj_set_height(ui.claude_empty_title, 84);
    ui.claude_empty_body = text(ui.claude_usage_body, "", 28, 168, 548, &buddy_language_font_20, C_MUTED);
    lv_obj_set_height(ui.claude_empty_body, 120);
    lv_label_set_long_mode(ui.claude_empty_body, LV_LABEL_LONG_WRAP);
    ui.claude_tasks_body = box(ui.claude_page, 0, 85, 600, 315);
    ui.claude_task_summary = text(ui.claude_tasks_body, BT(BT_RECORDED_ACTIVITY), 28, 18, 548, &buddy_language_font_16, C_MUTED);
    for (unsigned i = 0; i < 3; ++i) {
        lv_obj_t *row = box(ui.claude_tasks_body, 28, 61 + (int)i * 84, 548, 80);
        ui.claude_task_rows[i] = row;
        ui.claude_task_titles[i] = text(row, "", 0, 0, 398, &buddy_language_font_20, C_TEXT);
        lv_obj_set_height(ui.claude_task_titles[i], 27);
        ui.claude_task_states[i] = text(row, "", 406, 5, 142, &buddy_language_font_14, C_MUTED);
        lv_obj_set_style_text_align(ui.claude_task_states[i], LV_TEXT_ALIGN_RIGHT, 0);
        ui.claude_task_projects[i] = text(row, "", 0, 29, 548, &buddy_language_font_20, C_MUTED);
        lv_obj_set_height(ui.claude_task_projects[i], 27);
        ui.claude_task_ages[i] = text(row, "", 0, 59, 548, &buddy_language_font_14, C_MUTED);
        if (i < 2) rule(row, 0, 79, 548, 1);
    }
    ui.claude_tasks_empty_title = text(ui.claude_tasks_body, BT(BT_WAITING_FOR_TASK_ACTIVITY), 28, 65, 548, &buddy_language_font_32, C_TEXT);
    lv_obj_set_height(ui.claude_tasks_empty_title, 84);
    ui.claude_tasks_empty_body = text(ui.claude_tasks_body, "", 28, 168, 548, &buddy_language_font_20, C_MUTED);
    lv_label_set_long_mode(ui.claude_tasks_empty_body, LV_LABEL_LONG_WRAP);
    lv_obj_set_height(ui.claude_tasks_empty_body, 120);
    ui.claude_age = text(ui.claude_page, BT(BT_NO_ACCOUNT_LIMITS_AVAILABLE), 28, 410, 744, &buddy_language_font_14, C_MUTED);
    refresh_claude();
}

static uint32_t timer_duration_ms(void)
{
    return ui.timer_duration_s * 1000U;
}

static void refresh_timer(void)
{
    if (!ui.timer_page) return;
    uint32_t seconds = (ui.timer_remaining_ms + 999U) / 1000U;
    char buffer[24];
    snprintf(buffer, sizeof(buffer), "%02lu:%02lu:%02lu", (unsigned long)(seconds / 3600U),
             (unsigned long)(seconds / 60U % 60U), (unsigned long)(seconds % 60U));
    lv_label_set_text(ui.timer_time, buffer);
    ui.timer_last_seconds = seconds;
    lv_label_set_text(ui.timer_status, ui.timer_running ? BT(BT_RUNNING) : ui.timer_completed ? BT(BT_TIME_S_UP) :
        ui.timer_started ? BT(BT_PAUSED) : ui.timer_duration_s ? BT(BT_READY) : BT(BT_SET_A_DURATION));
    lv_obj_set_style_text_color(ui.timer_status, color(ui.timer_completed ? C_WARNING : C_ACCENT), 0);
    lv_label_set_text(ui.timer_button_labels[0], ui.timer_running ? BT(BT_PAUSE) :
        ui.timer_completed ? BT(BT_START_AGAIN) : ui.timer_started ? BT(BT_RESUME) : BT(BT_START));
    button_enabled(ui.timer_buttons[0], ui.timer_duration_s > 0);
    uint32_t duration = timer_duration_ms();
    uint32_t remaining = ui.timer_remaining_ms < duration ? ui.timer_remaining_ms : duration;
    lv_bar_set_value(ui.timer_progress, duration ?
        (int)((uint64_t)(duration - remaining) * 1000U / duration) : 0, LV_ANIM_OFF);
    const uint32_t units[] = { ui.timer_duration_s / 3600U, ui.timer_duration_s / 60U % 60U,
                               ui.timer_duration_s % 60U };
    const uint32_t limits[] = { 23U, 59U, 59U };
    for (unsigned i = 0; i < 3; ++i) {
        snprintf(buffer, sizeof(buffer), "%02lu", (unsigned long)units[i]);
        lv_label_set_text(ui.timer_units[i], buffer);
        bool editable = !ui.timer_started && !ui.timer_running;
        button_enabled(ui.timer_adjust[i * 2], editable && units[i] > 0);
        button_enabled(ui.timer_adjust[i * 2 + 1], editable && units[i] < limits[i]);
    }
    refresh_app_cards();
    refresh_footer();
}

static void advance_timer(void)
{
    uint32_t now = lv_tick_get();
    /* The short, unsigned interval handles LVGL's tick wrap without wall-clock drift. */
    uint32_t elapsed = now - ui.timer_tick;
    ui.timer_tick = now;
    if (!ui.timer_running) return;
    if (elapsed >= ui.timer_remaining_ms) {
        ui.timer_running = false;
        ui.timer_started = false;
        ui.timer_completed = true;
        ui.timer_remaining_ms = 0;
        ui.timer_celebrating = true;
        ui.timer_celebration_tick = now;
        ui.timer_notify = ui.active_app != BUDDY_APP_TIMER || ui.menu_open;
        refresh_timer();
        refresh_footer();
    } else {
        ui.timer_remaining_ms -= elapsed;
        if ((ui.timer_remaining_ms + 999U) / 1000U != ui.timer_last_seconds) refresh_timer();
    }
}

static void timer_clicked(lv_event_t *event)
{
    unsigned action = (unsigned)(uintptr_t)lv_event_get_user_data(event);
    if (action > 1) return;
    if (lv_event_get_code(event) == LV_EVENT_PRESSED) {
        /* Preserve Pause intent if the countdown finishes before release. */
        ui.timer_primary_pause = ui.timer_running;
        return;
    }
    advance_timer();
    if (action == 0) {
        if (ui.timer_primary_pause && !ui.timer_running) return;
        if (!ui.timer_duration_s) return;
        if (ui.timer_completed) ui.timer_remaining_ms = timer_duration_ms();
        ui.timer_running = !ui.timer_running;
        ui.timer_started = true;
    } else {
        ui.timer_running = false;
        ui.timer_started = false;
        ui.timer_remaining_ms = timer_duration_ms();
    }
    ui.timer_tick = lv_tick_get();
    ui.timer_completed = false;
    ui.timer_celebrating = false;
    ui.timer_notify = false;
    refresh_timer();
    refresh_footer();
}

static void timer_adjust_clicked(lv_event_t *event)
{
    if (ui.timer_running || ui.timer_started) return;
    unsigned action = (unsigned)(uintptr_t)lv_event_get_user_data(event);
    if (action >= 6) return;
    static const uint32_t steps[] = { 3600U, 60U, 1U };
    static const uint32_t limits[] = { 23U, 59U, 59U };
    unsigned unit = action / 2;
    uint32_t value = ui.timer_duration_s / steps[unit];
    if (unit) value %= 60U;
    bool increase = action % 2;
    if ((increase && value >= limits[unit]) || (!increase && !value)) return;
    if (increase) ui.timer_duration_s += steps[unit];
    else ui.timer_duration_s -= steps[unit];
    ui.timer_remaining_ms = timer_duration_ms();
    ui.timer_completed = false;
    ui.timer_celebrating = false;
    ui.timer_notify = false;
    refresh_timer();
    refresh_footer();
}

static void create_music_page(void)
{
    ui.music_page = box(ui.screen, 0, 0, 800, 440);
    ui.music_mascot = lv_img_create(ui.music_page);
    lv_img_set_src(ui.music_mascot, &buddy_mascot_frames[BUDDY_MASCOT_IDLE][0]);
    lv_img_set_antialias(ui.music_mascot, false);
    lv_obj_set_pos(ui.music_mascot, 28, 17);
    lv_obj_clear_flag(ui.music_mascot, LV_OBJ_FLAG_CLICKABLE);
    text(ui.music_page, BT(BT_SPOTIFY), 88, 25, 380, &buddy_language_font_24, C_TEXT);
    ui.music_status = text(ui.music_page, BT(BT_WAITING_FOR_PC), 508, 29, 264, &buddy_language_font_16, C_MUTED);
    lv_obj_set_style_text_align(ui.music_status, LV_TEXT_ALIGN_RIGHT, 0);
    rule(ui.music_page, 28, 84, 744, 1);
    ui.music_buddy = lv_img_create(ui.music_page);
    lv_img_set_antialias(ui.music_buddy, false);
    lv_obj_set_pos(ui.music_buddy, 652, 163);
    lv_obj_clear_flag(ui.music_buddy, LV_OBJ_FLAG_CLICKABLE);
    ui.music_title = text(ui.music_page, BT(BT_WAITING_FOR_YOUR_PC), 28, 110, 572, &buddy_language_font_32, C_TEXT);
    lv_obj_set_height(ui.music_title, 84);
    ui.music_artist = text(ui.music_page, "", 28, 209, 572, &buddy_language_font_20, C_TEXT);
    lv_obj_set_height(ui.music_artist, 28);
    ui.music_album = text(ui.music_page, "", 28, 248, 572, &buddy_language_font_20, C_MUTED);
    lv_obj_set_height(ui.music_album, 28);
    ui.music_progress = progress_bar(ui.music_page, 28, 296, 744);
    ui.music_elapsed = text(ui.music_page, "--:--", 28, 309, 260, &buddy_language_font_14, C_MUTED);
    ui.music_duration = text(ui.music_page, "--:--", 512, 309, 260, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.music_duration, LV_TEXT_ALIGN_RIGHT, 0);
    ui.music_feedback = text(ui.music_page, "", 28, 332, 744, &buddy_language_font_14, C_WARNING);
    const char *labels[] = { BT(BT_PREVIOUS), BT(BT_PLAY), BT(BT_NEXT) };
    const int positions[] = { 28, 256, 568 }, widths[] = { 204, 288, 204 };
    for (unsigned i = 0; i < 3; ++i) {
        ui.music_buttons[i] = action_button(ui.music_page, labels[i], positions[i], 359, widths[i], 64,
                                             &ui.music_button_labels[i]);
        lv_obj_add_event_cb(ui.music_buttons[i], music_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)i);
    }
    lv_obj_set_style_text_color(ui.music_button_labels[1], color(C_ACCENT), 0);
    refresh_music();
}

static void create_timer_page(void)
{
    ui.timer_page = box(ui.screen, 0, 0, 800, 440);
    text(ui.timer_page, BT(BT_TIMER), 28, 26, 420, &buddy_language_font_24, C_TEXT);
    ui.timer_status = text(ui.timer_page, BT(BT_READY), 508, 29, 264, &buddy_language_font_16, C_ACCENT);
    lv_obj_set_style_text_align(ui.timer_status, LV_TEXT_ALIGN_RIGHT, 0);
    rule(ui.timer_page, 28, 84, 744, 1);
    ui.timer_time = text(ui.timer_page, "00:05:00", 28, 124, 548, &buddy_clock_font, C_TEXT);
    lv_obj_set_style_text_align(ui.timer_time, LV_TEXT_ALIGN_CENTER, 0);
    ui.timer_progress = progress_bar(ui.timer_page, 28, 222, 744);
    ui.timer_mascot = lv_img_create(ui.timer_page);
    lv_obj_set_pos(ui.timer_mascot, 648, 111);
    lv_img_set_antialias(ui.timer_mascot, false);
    lv_obj_clear_flag(ui.timer_mascot, LV_OBJ_FLAG_CLICKABLE);
    const char *units[] = { BT(BT_HOURS), BT(BT_MINUTES), BT(BT_SECONDS) };
    for (unsigned i = 0; i < 3; ++i) {
        int x = 28 + (int)i * 256;
        lv_obj_t *caption = text(ui.timer_page, units[i], x, 246, 232, &buddy_language_font_14, C_MUTED);
        lv_obj_set_style_text_align(caption, LV_TEXT_ALIGN_CENTER, 0);
        ui.timer_units[i] = text(ui.timer_page, "00", x + 72, 287, 88, &buddy_language_font_24, C_TEXT);
        lv_obj_set_style_text_align(ui.timer_units[i], LV_TEXT_ALIGN_CENTER, 0);
        for (unsigned j = 0; j < 2; ++j) {
            unsigned action = i * 2 + j;
            ui.timer_adjust[action] = action_button(ui.timer_page, j ? "+" : "-",
                x + (j ? 176 : 0), 275, 56, 52, &caption);
            lv_obj_add_event_cb(ui.timer_adjust[action], timer_adjust_clicked, LV_EVENT_PRESSED,
                (void *)(uintptr_t)action);
            lv_obj_add_event_cb(ui.timer_adjust[action], timer_adjust_clicked, LV_EVENT_LONG_PRESSED_REPEAT,
                (void *)(uintptr_t)action);
        }
    }
    const char *labels[] = { BT(BT_START), BT(BT_RESET) };
    for (unsigned i = 0; i < 2; ++i) {
        ui.timer_buttons[i] = action_button(ui.timer_page, labels[i], 28 + (int)i * 384, 359, 360, 64,
                                            &ui.timer_button_labels[i]);
        lv_obj_add_event_cb(ui.timer_buttons[i], timer_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)i);
        if (!i) lv_obj_add_event_cb(ui.timer_buttons[i], timer_clicked, LV_EVENT_PRESSED, NULL);
    }
    lv_obj_set_style_text_color(ui.timer_button_labels[0], color(C_ACCENT), 0);
    refresh_timer();
}

static void weather_disc(lv_draw_ctx_t *ctx, int x, int y, int radius, uint32_t ink)
{
    lv_draw_rect_dsc_t style;
    lv_draw_rect_dsc_init(&style);
    style.bg_color = color(ink);
    style.bg_opa = LV_OPA_COVER;
    style.radius = LV_RADIUS_CIRCLE;
    lv_area_t area = {x - radius, y - radius, x + radius, y + radius};
    lv_draw_rect(ctx, &style, &area);
}

static void weather_stroke(lv_draw_ctx_t *ctx, int x, int y, int scale, int x1, int y1, int x2, int y2)
{
    lv_draw_line_dsc_t style;
    lv_draw_line_dsc_init(&style);
    style.color = color(BUDDY_COLOR_WEATHER_MARK);
    style.width = 2 * scale;
    style.round_start = style.round_end = 1;
    lv_point_t start = {x + x1 * scale, y + y1 * scale}, end = {x + x2 * scale, y + y2 * scale};
    lv_draw_line(ctx, &style, &start, &end);
}

/* Small native vector marks; no extra bitmap buffers or animated weather layer. */
static void draw_weather_icon(lv_event_t *event)
{
    if (!ui.weather.available) return;
    lv_draw_ctx_t *ctx = lv_event_get_draw_ctx(event);
    lv_area_t area;
    lv_obj_get_coords(lv_event_get_target(event), &area);
    int scale = lv_obj_get_width(lv_event_get_target(event)) >= 56 ? 2 : 1;
    int x = area.x1, y = area.y1, code = ui.weather.code;
    if (code <= 1) {
        if (!ui.weather.is_day) {
            weather_disc(ctx, x + 13 * scale, y + 12 * scale, 9 * scale, BUDDY_COLOR_WEATHER_MARK);
            weather_disc(ctx, x + 17 * scale, y + 8 * scale, 8 * scale, ui.active_app == BUDDY_APP_HOME ? BUDDY_COLOR_BG : C_CLOCK_BG);
        } else {
            weather_disc(ctx, x + 13 * scale, y + 12 * scale, 5 * scale, BUDDY_COLOR_WEATHER_MARK);
            static const int8_t rays[][4] = {
                {13,1,13,3}, {13,21,13,23}, {2,12,4,12}, {22,12,24,12},
                {5,4,7,6}, {19,18,21,20}, {5,20,7,18}, {19,6,21,4},
            };
            for (unsigned i = 0; i < sizeof(rays) / sizeof(rays[0]); ++i)
                weather_stroke(ctx, x, y, scale, rays[i][0], rays[i][1], rays[i][2], rays[i][3]);
        }
        return;
    }
    if (code == 45 || code == 48) {
        weather_stroke(ctx, x, y, scale, 3, 7, 23, 7);
        weather_stroke(ctx, x, y, scale, 6, 13, 25, 13);
        weather_stroke(ctx, x, y, scale, 2, 19, 21, 19);
        return;
    }
    bool precipitation = code >= 51;
    int cloud_y = precipitation ? 10 : 14;
    weather_disc(ctx, x + 7 * scale, y + cloud_y * scale, 5 * scale, BUDDY_COLOR_WEATHER_MARK);
    weather_disc(ctx, x + 14 * scale, y + (cloud_y - 3) * scale, 7 * scale, BUDDY_COLOR_WEATHER_MARK);
    weather_disc(ctx, x + 21 * scale, y + cloud_y * scale, 5 * scale, BUDDY_COLOR_WEATHER_MARK);
    weather_stroke(ctx, x, y, scale, 7, cloud_y + 4, 21, cloud_y + 4);
    if (code >= 95) {
        weather_stroke(ctx, x, y, scale, 15, 16, 11, 21);
        weather_stroke(ctx, x, y, scale, 11, 21, 16, 21);
        weather_stroke(ctx, x, y, scale, 16, 21, 13, 25);
    } else if ((code >= 71 && code <= 77) || code == 85 || code == 86) {
        for (int offset = 7; offset <= 21; offset += 14) {
            weather_stroke(ctx, x, y, scale, offset - 2, 21, offset + 2, 21);
            weather_stroke(ctx, x, y, scale, offset, 19, offset, 23);
        }
    } else if (precipitation) {
        weather_stroke(ctx, x, y, scale, 8, 19, 6, 23);
        weather_stroke(ctx, x, y, scale, 15, 19, 13, 23);
        weather_stroke(ctx, x, y, scale, 22, 19, 20, 23);
    }
}

static void create_app_surfaces(void)
{
    ui.home_page = buddy_home_create(ui.screen, activity_clicked, draw_weather_icon);
    ui.clock_page = box(ui.screen, 0, 0, 800, 440);
    lv_obj_set_style_bg_color(ui.clock_page, color(C_CLOCK_BG), 0);
    lv_obj_set_style_bg_opa(ui.clock_page, LV_OPA_COVER, 0);
    ui.clock_large = text(ui.clock_page, "--:--", 60, 145, 680, &buddy_clock_font, C_CLOCK_TEXT);
    lv_obj_set_style_text_align(ui.clock_large, LV_TEXT_ALIGN_CENTER, 0);
    ui.clock_date = text(ui.clock_page, "", 80, 251, 640, &buddy_language_font_24, C_CLOCK_DATE);
    lv_obj_set_style_text_align(ui.clock_date, LV_TEXT_ALIGN_CENTER, 0);
    ui.weather_row = box(ui.clock_page, 28, 290, 744, 26);
    ui.weather_icon = box(ui.weather_row, 0, 0, 28, 26);
    lv_obj_add_event_cb(ui.weather_icon, draw_weather_icon, LV_EVENT_DRAW_MAIN, NULL);
    ui.weather_text = text(ui.weather_row, BT(BT_WEATHER_LOADING), 0, 1, 744,
                           &buddy_language_font_20, C_CLOCK_DATE);
    ui.weather_note = text(ui.clock_page, "", 28, 418, 744,
                           &buddy_language_font_14, C_CLOCK_DATE);
    lv_obj_set_style_text_align(ui.weather_note, LV_TEXT_ALIGN_CENTER, 0);
    ui.clock_mascot = lv_img_create(ui.clock_page);
    lv_obj_set_pos(ui.clock_mascot, 352, 316);
    lv_img_set_antialias(ui.clock_mascot, false);
    lv_obj_clear_flag(ui.clock_mascot, LV_OBJ_FLAG_CLICKABLE);

    ui.weather_page = buddy_weather_page_create(ui.screen);
    create_music_page();
    create_timer_page();
    create_codex_page();
    create_claude_page();

    ui.chooser = box(ui.screen, 0, 0, 800, 440);
    lv_obj_set_style_bg_color(ui.chooser, color(C_BG), 0);
    lv_obj_set_style_bg_opa(ui.chooser, LV_OPA_COVER, 0);
    text(ui.chooser, BT(BT_APPS), 28, 18, 744, &buddy_language_font_32, C_TEXT);
    text(ui.chooser, BT(BT_YOUR_APPS_TOGETHER_TAP_ONE_TO_OPEN), 28, 56, 744, &buddy_language_font_16, C_MUTED);
    for (unsigned i = 0; i < sizeof(app_catalog) / sizeof(app_catalog[0]); ++i) {
        unsigned position = i == BUDDY_APP_HOME ? 0 : i + 1;
        lv_obj_t *row = box(ui.chooser, 28, 80 + (int)position * 44, 744, 42);
        ui.app_rows[i] = row;
        lv_obj_add_flag(row, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_bg_color(row, color(C_PRESSED), 0);
        lv_obj_set_style_bg_color(row, color(C_RULE), LV_STATE_PRESSED);
        lv_obj_set_style_bg_opa(row, LV_OPA_COVER, LV_STATE_PRESSED);
        lv_obj_set_style_radius(row, 8, 0);
        lv_obj_add_event_cb(row, app_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)app_catalog[i].id);
        buddy_app_icon_create(row, (buddy_app_t)i, 12, 3, 36);
        text(row, BT(app_catalog[i].title), 72, 4, 170, &buddy_language_font_20, C_TEXT);
        ui.app_description[i] = text(row, BT(app_catalog[i].description), 250, 13, 350, &buddy_language_font_20, C_TEXT);
        lv_obj_set_height(ui.app_description[i], 26);
        ui.app_hint[i] = text(row, "", 72, 26, 170, &buddy_language_font_14, C_MUTED);
        lv_obj_set_height(ui.app_hint[i], 16);
        ui.app_current[i] = text(row, BT(BT_OPEN), 642, 17, 82, &buddy_language_font_14, C_ACCENT);
        lv_obj_set_style_text_align(ui.app_current[i], LV_TEXT_ALIGN_RIGHT, 0);
    }

    /* Short reconnect flourish lives over the trace, away from readings and Apps. */
    ui.app_transition = box(ui.screen, 336, 336, 128, 104);
    lv_obj_set_style_bg_color(ui.app_transition, color(C_BG), 0);
    lv_obj_set_style_bg_opa(ui.app_transition, LV_OPA_COVER, 0);
    ui.transition_image = lv_img_create(ui.app_transition);
    lv_obj_set_pos(ui.transition_image, 16, 0);
    lv_img_set_antialias(ui.transition_image, false);
    lv_obj_clear_flag(ui.transition_image, LV_OBJ_FLAG_CLICKABLE);
}

#include "buddy_compact_layout.inc"

void buddy_ui_init(void)
{
    if (ui.screen) return;
    ui.connection = BUDDY_WIFI_CONNECTING;
    ui.active_app = BUDDY_APP_HOME;
    ui.last_stats_tick = lv_tick_get();
    ui.timer_duration_s = 5U * 60U;
    ui.timer_remaining_ms = timer_duration_ms();
    ui.timer_tick = lv_tick_get();
    ui.screen = lv_obj_create(NULL);
    lv_obj_remove_style_all(ui.screen);
    lv_obj_set_size(ui.screen, BOARD_DISPLAY_WIDTH, BOARD_DISPLAY_HEIGHT);
    lv_obj_set_style_bg_color(ui.screen, color(C_BG), 0);
    lv_obj_set_style_bg_opa(ui.screen, LV_OPA_COVER, 0);
    lv_obj_set_style_text_font(ui.screen, &buddy_language_font_16, 0);
    lv_obj_clear_flag(ui.screen, LV_OBJ_FLAG_SCROLLABLE);

    ui.monitor_header = box(ui.screen, 0, 0, 800, 85);
    ui.mascot = lv_img_create(ui.monitor_header);
    lv_img_set_antialias(ui.mascot, false);
    lv_obj_set_pos(ui.mascot, 28, 17);
    lv_obj_clear_flag(ui.mascot, LV_OBJ_FLAG_CLICKABLE);
    ui.monitor_title = text(ui.monitor_header, BT(BT_PC_MONITOR), 88, 20, 380, &buddy_language_font_24, C_TEXT);
    ui.host = text(ui.monitor_header, BT(BT_CLAWPAL), 89, 53, 400, &buddy_language_font_14, C_MUTED);
    ui.music_link = box(ui.monitor_header, 282, 13, 234, 58);
    lv_obj_add_flag(ui.music_link, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_HIDDEN);
    lv_obj_set_style_bg_color(ui.music_link, color(C_PRESSED), 0);
    lv_obj_set_style_bg_opa(ui.music_link, LV_OPA_COVER, 0);
    lv_obj_set_style_bg_color(ui.music_link, color(C_RULE), LV_STATE_PRESSED);
    lv_obj_set_style_radius(ui.music_link, 8, 0);
    lv_obj_add_event_cb(ui.music_link, activity_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)BUDDY_APP_MUSIC);
    text(ui.music_link, BT(BT_NOW_PLAYING), 39, 6, 180, &buddy_language_font_14, C_ACCENT);
    ui.music_link_title = text(ui.music_link, BT(BT_SPOTIFY), 12, 29, 210, &buddy_language_font_20, C_TEXT);
    lv_obj_set_height(ui.music_link_title, 26);
    for (unsigned i = 0; i < 3; ++i) {
        ui.music_link_bars[i] = box(ui.music_link, 13 + (int)i * 7, 14, 3, 8);
        lv_obj_set_style_bg_color(ui.music_link_bars[i], color(C_ACCENT), 0);
        lv_obj_set_style_bg_opa(ui.music_link_bars[i], LV_OPA_COVER, 0);
    }
    ui.clock = text(ui.monitor_header, "--:--", 530, 18, 110, &buddy_language_font_24, C_TEXT);
    lv_obj_set_style_text_align(ui.clock, LV_TEXT_ALIGN_RIGHT, 0);
    ui.date = text(ui.monitor_header, "", 654, 24, 118, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.date, LV_TEXT_ALIGN_RIGHT, 0);
    ui.dot = box(ui.monitor_header, 581, 58, 6, 6);
    lv_obj_set_style_radius(ui.dot, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_opa(ui.dot, LV_OPA_COVER, 0);
    ui.status = text(ui.monitor_header, BT(BT_JOINING_WI_FI), 598, 52, 174, &buddy_language_font_14, C_WARNING);
    lv_obj_set_style_text_align(ui.status, LV_TEXT_ALIGN_RIGHT, 0);
    rule(ui.monitor_header, 28, 84, 744, 1);

    ui.overview = box(ui.screen, 28, 102, 744, 325);
    create_channel(&ui.cpu, 0, BT(BT_CPU), false);
    create_channel(&ui.gpu, 388, BT(BT_GPU), true);
    rule(ui.overview, 371, 0, 1, 126);
    text(ui.overview, BT(BT_MEMORY), 0, 151, 232, &buddy_language_font_16, C_MUTED);
    ui.memory = text(ui.overview, "--", 0, 177, 242, &buddy_language_font_24, C_TEXT);
    ui.memory_percent = text(ui.overview, BT(BT_WAITING_FOR_READINGS), 0, 211, 240, &buddy_language_font_14, C_MUTED);
    rule(ui.overview, 254, 155, 1, 71);
    text(ui.overview, BT(BT_GPU_MEMORY), 278, 151, 229, &buddy_language_font_16, C_MUTED);
    ui.vram = text(ui.overview, "--", 278, 177, 229, &buddy_language_font_24, C_TEXT);
    ui.vram_note = text(ui.overview, BT(BT_WAITING_FOR_READINGS), 278, 211, 229, &buddy_language_font_14, C_MUTED);
    rule(ui.overview, 523, 155, 1, 71);
    text(ui.overview, BT(BT_NETWORK), 547, 151, 197, &buddy_language_font_16, C_MUTED);
    text(ui.overview, BT(BT_DOWN), 547, 181, 49, &buddy_language_font_14, C_MUTED);
    text(ui.overview, BT(BT_UP), 547, 207, 49, &buddy_language_font_14, C_MUTED);
    ui.rx = text(ui.overview, "--", 602, 178, 142, &buddy_language_font_20, C_TEXT);
    ui.tx = text(ui.overview, "--", 602, 204, 142, &buddy_language_font_20, C_TEXT);
    lv_obj_set_style_text_align(ui.rx, LV_TEXT_ALIGN_RIGHT, 0);
    lv_obj_set_style_text_align(ui.tx, LV_TEXT_ALIGN_RIGHT, 0);

    ui.chart_title = text(ui.overview, BT(BT_CPU_LOAD_60_SECONDS), 0, 251, 570, &buddy_language_font_14, C_MUTED);
    text(ui.overview, "0 - 100%", 624, 251, 120, &buddy_language_font_14, C_MUTED);
    ui.chart = lv_chart_create(ui.overview);
    lv_obj_remove_style_all(ui.chart);
    lv_obj_set_pos(ui.chart, 0, 281);
    lv_obj_set_size(ui.chart, 744, 42);
    lv_chart_set_type(ui.chart, LV_CHART_TYPE_LINE);
    lv_chart_set_range(ui.chart, LV_CHART_AXIS_PRIMARY_Y, 0, 100);
    lv_chart_set_point_count(ui.chart, HISTORY_POINTS);
    lv_chart_set_div_line_count(ui.chart, 2, 0);
    lv_obj_set_style_line_color(ui.chart, color(C_RULE), LV_PART_MAIN);
    lv_obj_set_style_line_width(ui.chart, 1, LV_PART_MAIN);
    lv_obj_set_style_line_width(ui.chart, 2, LV_PART_ITEMS);
    lv_obj_set_style_size(ui.chart, 0, LV_PART_INDICATOR);
    lv_obj_clear_flag(ui.chart, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
    ui.cpu_series = lv_chart_add_series(ui.chart, color(C_ACCENT), LV_CHART_AXIS_PRIMARY_Y);
    ui.gpu_series = lv_chart_add_series(ui.chart, color(C_ACCENT), LV_CHART_AXIS_PRIMARY_Y);
    lv_chart_set_all_value(ui.chart, ui.cpu_series, LV_CHART_POINT_NONE);
    lv_chart_set_all_value(ui.chart, ui.gpu_series, LV_CHART_POINT_NONE);

    ui.game_panel = box(ui.overview, 0, 244, 744, 81);
    lv_obj_set_style_bg_color(ui.game_panel, color(C_BG), 0);
    lv_obj_set_style_bg_opa(ui.game_panel, LV_OPA_COVER, 0);
    ui.game_title = text(ui.game_panel, "", 0, 3, 550, &buddy_language_font_20, C_TEXT);
    lv_obj_set_height(ui.game_title, 27);
    ui.game_status = text(ui.game_panel, "", 0, 40, 550, &buddy_language_font_14, C_MUTED);
    ui.game_fps = text(ui.game_panel, "--", 568, 0, 176, &buddy_language_font_48, C_ACCENT);
    lv_obj_set_style_text_align(ui.game_fps, LV_TEXT_ALIGN_RIGHT, 0);
    lv_obj_t *fps_unit = text(ui.game_panel, BT(BT_FPS), 568, 55, 176, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(fps_unit, LV_TEXT_ALIGN_RIGHT, 0);
    visible(ui.game_panel, false);

    create_details();
    ui.empty = box(ui.screen, 28, 111, 744, 306);
    ui.empty_title = text(ui.empty, BT(BT_JOINING_YOUR_WI_FI), 0, 42, 736, &buddy_language_font_32, C_TEXT);
    ui.empty_body = text(ui.empty, "", 0, 100, 710, &buddy_language_font_20, C_MUTED);
    lv_label_set_long_mode(ui.empty_body, LV_LABEL_LONG_WRAP);
    lv_obj_set_height(ui.empty_body, 123);
    ui.empty_network = text(ui.empty, "", 0, 248, 736, &buddy_language_font_16, C_ACCENT);

    create_app_surfaces();

    rule(ui.screen, 28, 440, 744, 1);
    ui.footer = text(ui.screen, BT(BT_CLAWPAL_PC_MONITOR), 142, 454, 481, &buddy_language_font_14, C_MUTED);
    lv_obj_set_height(ui.footer, 22);
    lv_obj_add_flag(ui.footer, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(ui.footer, open_about, LV_EVENT_LONG_PRESSED, NULL);
    ui.timer_link = box(ui.screen, 420, 436, 208, 44);
    lv_obj_add_flag(ui.timer_link, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_HIDDEN);
    lv_obj_set_style_bg_color(ui.timer_link, color(C_PRESSED), 0);
    lv_obj_set_style_bg_opa(ui.timer_link, LV_OPA_COVER, 0);
    lv_obj_set_style_bg_color(ui.timer_link, color(C_RULE), LV_STATE_PRESSED);
    lv_obj_set_style_radius(ui.timer_link, 8, 0);
    lv_obj_add_event_cb(ui.timer_link, activity_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)BUDDY_APP_TIMER);
    ui.timer_link_title = text(ui.timer_link, BT(BT_TIMER), 12, 12, 184, &buddy_language_font_16, C_ACCENT);
    lv_obj_set_height(ui.timer_link_title, 22);
    ui.timer_link_progress = progress_bar(ui.timer_link, 12, 39, 184);
    lv_obj_set_height(ui.timer_link_progress, 2);
    ui.apps_button = box(ui.screen, 16, 436, 108, 44);
    lv_obj_add_flag(ui.apps_button, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(ui.apps_button, color(C_PRESSED), LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(ui.apps_button, LV_OPA_COVER, LV_STATE_PRESSED);
    lv_obj_set_style_radius(ui.apps_button, 8, 0);
    lv_obj_add_event_cb(ui.apps_button, apps_clicked, LV_EVENT_CLICKED, NULL);
    for (unsigned i = 0; i < 4; ++i) {
        lv_obj_t *cell = box(ui.apps_button, 12 + (i % 2) * 7, 15 + (i / 2) * 7, 4, 4);
        lv_obj_set_style_bg_color(cell, color(C_ACCENT), 0);
        lv_obj_set_style_bg_opa(cell, LV_OPA_COVER, 0);
    }
    ui.apps_text = text(ui.apps_button, BT(BT_APPS), 32, 13, 68, &buddy_language_font_16, C_ACCENT);
    ui.details_button = box(ui.screen, 136, 436, 120, 44);
    lv_obj_add_flag(ui.details_button, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(ui.details_button, color(C_PRESSED), LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(ui.details_button, LV_OPA_COVER, LV_STATE_PRESSED);
    lv_obj_set_style_radius(ui.details_button, 8, 0);
    lv_obj_add_event_cb(ui.details_button, details_clicked, LV_EVENT_CLICKED, NULL);
    ui.details_text = text(ui.details_button, BT(BT_DETAILS), 12, 13, 96, &buddy_language_font_16, C_ACCENT);
    lv_obj_t *nav = box(ui.screen, 641, 436, 147, 44);
    ui.nav_button = nav;
    lv_obj_add_flag(nav, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_ext_click_area(nav, 5);
    lv_obj_set_style_bg_color(nav, color(C_PRESSED), LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(nav, LV_OPA_COVER, LV_STATE_PRESSED);
    lv_obj_add_event_cb(nav, nav_clicked, LV_EVENT_CLICKED, NULL);
    ui.nav_text = text(nav, BT(BT_DETAILS), 8, 13, 125, &buddy_language_font_16, C_ACCENT);
    lv_obj_set_style_text_align(ui.nav_text, LV_TEXT_ALIGN_RIGHT, 0);
    compact_layout();
    select_channel(false);
    refresh_connection();
    ui.history_timer = lv_timer_create(history_tick, 1000, NULL);
    create_startup();
    lv_scr_load(ui.screen);
    /* Global shade; it captures only the first wake gesture when fully off. */
    ui.dim_overlay = box(ui.screen, 0, 0, BOARD_DISPLAY_WIDTH, BOARD_DISPLAY_HEIGHT);
    lv_obj_set_style_bg_color(ui.dim_overlay, lv_color_black(), 0);
    lv_obj_set_style_bg_opa(ui.dim_overlay, LV_OPA_TRANSP, 0);
    lv_obj_add_event_cb(ui.dim_overlay, wake_screen, LV_EVENT_ALL, NULL);
    lv_obj_add_flag(ui.dim_overlay, LV_OBJ_FLAG_HIDDEN);
    ui.last_touch_tick = lv_obj_get_disp(ui.screen)->last_activity_time;
    ui.power_tick = lv_tick_get();
    lv_timer_create(touch_wake_tick, TOUCH_WAKE_POLL_MS, NULL);
}

void buddy_ui_set_connection(buddy_connection_state_t state, const char *ssid,
                             const char *ip, const char *detail)
{
    if (!ui.screen) return;
    if (state == BUDDY_PC_CONNECTED || ui.connection == BUDDY_PC_CONNECTED) {
        ui.idle_ms = 0;
        ui.power_tick = lv_tick_get();
    }
    if (state == BUDDY_PC_CONNECTED && ui.connection != BUDDY_PC_CONNECTED) {
        ui.celebrating = true;
        ui.happy_started = lv_tick_get();
        ui.pc_launch_pending = true;
        ui.pc_launch_sequence = ui.stats.sequence;
    }
    if (state != BUDDY_PC_CONNECTED) ui.pc_launch_pending = false;
    ui.connection = state;
    if (state == BUDDY_PROVISIONING) {
        /* Setup instructions always remain accessible, including after Clock. */
        dismiss_startup();
        ui.active_app = BUDDY_APP_MONITOR;
        ui.clock_automatic = false;
        ui.music_automatic = false;
        ui.music_launch_pending = false;
        ui.codex_automatic = false; ui.claude_automatic = false;
        ui.codex_launch_pending = false; ui.claude_launch_pending = false;
        ui.menu_open = false;
        ui.details_visible = false;
        ui.transition_active = false;
        ui.manual_monitor_hold = false;
        lv_anim_del(ui.clock_mascot, mascot_set_y);
        lv_anim_del(ui.app_transition, mascot_set_y);
    }
    set_string(ui.ssid, sizeof(ui.ssid), ssid);
    set_string(ui.ip, sizeof(ui.ip), ip);
    set_string(ui.message, sizeof(ui.message), detail);
    if (state != BUDDY_PC_CONNECTED) ui.fresh_sample = false;
    if (state != BUDDY_PC_CONNECTED && ui.music_command_pending) {
        ui.music_command_pending = false;
        lv_label_set_text(ui.music_feedback, BT(BT_PC_DISCONNECTED_RECONNECT_TO_CONTROL_SPOTIFY));
    }
    refresh_connection();
}

static void channel_reading(channel_t *channel, float usage, float temperature, const char *name)
{
    char buf[32];
    if (isfinite(usage) && usage >= 0) snprintf(buf, sizeof(buf), "%.0f", fminf(usage, 100));
    else snprintf(buf, sizeof(buf), "--");
    lv_label_set_text(channel->usage, buf);
    lv_bar_set_value(channel->bar, isfinite(usage) ? (int)fminf(fmaxf(usage, 0), 100) : 0, LV_ANIM_OFF);
    if (isfinite(temperature)) snprintf(buf, sizeof(buf), "%.0f\xC2\xB0" "C", temperature);
    else snprintf(buf, sizeof(buf), "--");
    lv_label_set_text(channel->temperature, buf);
    lv_obj_set_style_text_color(channel->temperature,
        color(ui.connection != BUDDY_PC_CONNECTED ? C_MUTED :
              isfinite(temperature) && temperature >= 85 ? C_WARNING : C_TEXT), 0);
    lv_label_set_text(channel->model, name && name[0] ? name : BT(BT_SENSOR_UNAVAILABLE));
}

static void memory_reading(lv_obj_t *label, float used, float total)
{
    char buf[64];
    if (isfinite(used) && used >= 0 && isfinite(total) && total > 0)
        snprintf(buf, sizeof(buf), BT(BT_1F_0F_GB), used / 1024.f, total / 1024.f);
    else snprintf(buf, sizeof(buf), "--");
    lv_label_set_text(label, buf);
}

static void network_reading(lv_obj_t *label, double bytes_per_second)
{
    char buf[40];
    if (!isfinite(bytes_per_second) || bytes_per_second < 0) snprintf(buf, sizeof(buf), "--");
    else if (bytes_per_second >= 1e9) snprintf(buf, sizeof(buf), BT(BT_1F_GB_S), bytes_per_second / 1e9);
    else if (bytes_per_second >= 1e6) snprintf(buf, sizeof(buf), BT(BT_1F_MB_S), bytes_per_second / 1e6);
    else if (bytes_per_second >= 1e3) snprintf(buf, sizeof(buf), BT(BT_0F_KB_S), bytes_per_second / 1e3);
    else snprintf(buf, sizeof(buf), "%.0f B/s", bytes_per_second);
    lv_label_set_text(label, buf);
}

void buddy_ui_update_stats(const buddy_stats_t *stats)
{
    if (!ui.screen || !stats) return;
    /* The protocol has already authenticated, ordered, and validated this frame. */
    if (!ui.have_stats) {
        ui.celebrating = true;
        ui.happy_started = lv_tick_get();
    }
    if (stats->codex.running && !ui.codex_seen_running) {
        ui.codex_launch_pending = stats->auto_open_apps && !ui.timer_running &&
            (ui.active_app == BUDDY_APP_HOME || ui.active_app == BUDDY_APP_MONITOR || ui.active_app == BUDDY_APP_MUSIC ||
             (ui.offline_automatic));
    }
    if (!stats->codex.running || !stats->auto_open_apps) ui.codex_launch_pending = false;
    ui.codex_seen_running = stats->codex.running;
    if (stats->claude.running && !ui.claude_seen_running) {
        ui.claude_launch_pending = stats->auto_open_apps && !ui.timer_running &&
            (ui.active_app == BUDDY_APP_HOME || ui.active_app == BUDDY_APP_MONITOR || ui.active_app == BUDDY_APP_MUSIC ||
             (ui.offline_automatic));
    }
    if (!stats->claude.running || !stats->auto_open_apps) ui.claude_launch_pending = false;
    ui.claude_seen_running = stats->claude.running;
    /* Consume each launch edge once; manual Clock and active timer keep priority. */
    if (stats->music.running && !ui.music_seen_running) {
        ui.music_launch_pending = stats->auto_open_apps && !ui.timer_running &&
            (ui.active_app == BUDDY_APP_HOME || ui.active_app == BUDDY_APP_MONITOR ||
             (ui.offline_automatic));
    }
    if (!stats->music.running || !stats->auto_open_apps) ui.music_launch_pending = false;
    ui.music_seen_running = stats->music.running;
    if (ui.music_command_pending && stats->music.control_id != ui.music_command_control_id) {
        ui.music_command_pending = false;
        const char *feedback = "";
        if (!stats->music.control_ok) {
            feedback = strcmp(stats->music.control_error, "unsupported") == 0 ? BT(BT_THIS_CONTROL_ISN_T_AVAILABLE_FOR_THIS_TRACK) :
                strcmp(stats->music.control_error, "unavailable") == 0 ? BT(BT_SPOTIFY_ISN_T_READY_START_PLAYBACK_ON_YOUR_PC) :
                BT(BT_COULDN_T_CONTROL_SPOTIFY_TRY_AGAIN);
        }
        lv_label_set_text(ui.music_feedback, feedback);
    }
    ui.stats = *stats;
    ui.have_stats = true;
    ui.fresh_sample = true;
    ui.last_stats_tick = lv_tick_get();
    ui.manual_monitor_hold = false;
    lv_label_set_text(ui.host, stats->hostname[0] ? stats->hostname : BT(BT_PAIRED_PC));
    channel_reading(&ui.cpu, stats->cpu_usage, stats->cpu_temperature, stats->cpu_name);
    channel_reading(&ui.gpu, stats->gpu_usage, stats->gpu_temperature, stats->gpu_name);
    memory_reading(ui.memory, stats->memory_used_mb, stats->memory_total_mb);
    memory_reading(ui.vram, stats->vram_used_mb, stats->vram_total_mb);
    char buf[64];
    if (isfinite(stats->memory_usage) && stats->memory_usage >= 0)
        snprintf(buf, sizeof(buf), BT(BT_0F_IN_USE), fminf(stats->memory_usage, 100));
    else snprintf(buf, sizeof(buf), BT(BT_SENSOR_UNAVAILABLE));
    lv_label_set_text(ui.memory_percent, buf);
    if (isfinite(stats->vram_used_mb) && isfinite(stats->vram_total_mb) && stats->vram_total_mb > 0)
        snprintf(buf, sizeof(buf), BT(BT_0F_IN_USE), fminf(100.f, stats->vram_used_mb / stats->vram_total_mb * 100));
    else snprintf(buf, sizeof(buf), BT(BT_SENSOR_UNAVAILABLE));
    lv_label_set_text(ui.vram_note, buf);
    network_reading(ui.rx, stats->network_rx_bps);
    network_reading(ui.tx, stats->network_tx_bps);
    lv_label_set_text(ui.detail_cpu, stats->cpu_name[0] ? stats->cpu_name : BT(BT_SENSOR_UNAVAILABLE));
    lv_label_set_text(ui.detail_gpu, stats->gpu_name[0] ? stats->gpu_name : BT(BT_SENSOR_UNAVAILABLE));
    const unsigned days = (unsigned)(stats->uptime_s / 86400);
    const unsigned hours = (unsigned)(stats->uptime_s / 3600 % 24);
    const unsigned minutes = (unsigned)(stats->uptime_s / 60 % 60);
    if (days) snprintf(buf, sizeof(buf), BT(BT_U_D_U_H_U_MIN), days, hours, minutes);
    else snprintf(buf, sizeof(buf), BT(BT_U_H_U_MIN), hours, minutes);
    lv_label_set_text(ui.detail_uptime, buf);
    refresh_music();
    refresh_codex(); refresh_claude();
    maybe_auto_app();
    refresh_footer();
    refresh_visibility();
}

void buddy_ui_set_clock(const char *time_text, const char *date_text, bool night_hours)
{
    if (!ui.screen) return;
    ui.night_hours = night_hours;
    lv_label_set_text(ui.clock, time_text && time_text[0] ? time_text : "--:--");
    lv_label_set_text(ui.date, date_text ? date_text : "");
    lv_label_set_text(ui.clock_large, time_text && time_text[0] ? time_text : "--:--");
    lv_label_set_text(ui.clock_date, date_text ? date_text : "");
    refresh_home();
    refresh_app_cards();
    refresh_display_power();
}

void buddy_ui_set_weather(const buddy_weather_t *weather)
{
    if (!ui.weather_row || !weather) return;
    char reading[96], note[160];
    if (weather->available) {
        const char *description = buddy_weather_description(weather->code, weather->is_day);
        char temperature[24];buddy_weather_format_temperature(temperature,sizeof(temperature),weather->temperature_c,view_settings.weather_fahrenheit);
        snprintf(reading,sizeof(reading),"%s  /  %s",temperature,description?description:BT(BT_WEATHER));
    } else {
        snprintf(reading, sizeof(reading), "%s", weather->failed || weather->age_s ?
                 BT(BT_WEATHER_UNAVAILABLE) : BT(BT_WEATHER_LOADING));
    }
    if (weather->available && weather->stale) {
        unsigned minutes = weather->age_s / 60;
        snprintf(note, sizeof(note), BT(BT_S_UPDATED_U_MIN_AGO),
                 buddy_weather_city(), minutes);
    } else {
        snprintf(note, sizeof(note), "%s", buddy_weather_city());
    }
    bool changed_icon = ui.weather.available != weather->available ||
                        ui.weather.code != weather->code || ui.weather.is_day != weather->is_day;
    ui.weather = *weather;
    buddy_weather_page_update(weather);
    refresh_home();
    if (strcmp(lv_label_get_text(ui.weather_text), reading) != 0) {
        lv_label_set_text(ui.weather_text, reading);
        lv_point_t size;
        const lv_font_t *font=BOARD_DISPLAY_WIDTH<800?&buddy_language_font_16:&buddy_language_font_20;
        lv_obj_set_style_text_font(ui.weather_text,font,0);
        lv_txt_get_size(&size, reading, font, 0, 0, 700, LV_TEXT_FLAG_NONE);
        int icon_width = weather->available ? 38 : 0;
        int row_width=BOARD_DISPLAY_WIDTH<800?(BOARD_DISPLAY_HEIGHT<400?BOARD_DISPLAY_WIDTH-116:BOARD_DISPLAY_WIDTH-24):744;
        if(size.x>row_width-icon_width-2)size.x=row_width-icon_width-2;
        int left = (row_width - size.x - icon_width) / 2;
        lv_obj_set_pos(ui.weather_icon, left, 0);
        lv_obj_set_pos(ui.weather_text, left + icon_width, 1);
        lv_obj_set_width(ui.weather_text, size.x + 2);
    }
    if (strcmp(lv_label_get_text(ui.weather_note), note) != 0)
        lv_label_set_text(ui.weather_note, note);
    visible(ui.weather_icon, weather->available);
    if (changed_icon) lv_obj_invalidate(ui.weather_icon);
}

bool buddy_ui_select_app(buddy_app_t app)
{
    if (!ui.screen || !app_enabled(app)) return false;
    if (app != BUDDY_APP_MONITOR && ui.connection == BUDDY_PROVISIONING) return false;
    ui.menu_open = false;
    ui.offline_handled = ui.connection != BUDDY_PC_CONNECTED;
    ui.pc_launch_pending = false;
    ui.music_launch_pending = false;
    ui.codex_launch_pending = false; ui.claude_launch_pending = false;
    ui.manual_monitor_hold = app == BUDDY_APP_HOME || app == BUDDY_APP_MONITOR || app == BUDDY_APP_MUSIC || app == BUDDY_APP_CODEX || app == BUDDY_APP_CLAUDE;
    ui.manual_monitor_started = lv_tick_get();
    activate_app(app, false);
    return true;
}

void buddy_ui_apply_settings(const buddy_settings_t *settings)
{
    bool changed_offline = view_settings.offline_app != settings->offline_app;
    view_settings = *settings;
    buddy_language_set(settings->language);
    if (!ui.screen) return;
    if (changed_offline && ui.offline_automatic) activate_app((buddy_app_t)settings->offline_app, true);
    if (!app_enabled(BUDDY_APP_TIMER)) { ui.timer_running = false; ui.timer_notify = false; ui.timer_started = false; ui.timer_completed = false; }
    buddy_home_apply_settings(settings);
    if (!app_enabled(ui.active_app)) activate_app(BUDDY_APP_HOME, false);
    buddy_ui_set_weather(&ui.weather); refresh_app_cards();
    refresh_visibility(); refresh_connection(); refresh_music(); refresh_codex(); refresh_claude(); refresh_home(); refresh_footer();
}

void buddy_ui_set_music_control_callback(buddy_music_control_callback_t callback)
{
    ui.music_control_callback = callback;
    refresh_music();
}

buddy_ui_state_t buddy_ui_get_state(void)
{
    return (buddy_ui_state_t) {
        .active_app = (uint32_t)ui.active_app,
        .clock_automatic = ui.clock_automatic,
        .offline_automatic = ui.offline_automatic,
        .screen_dimmed = ui.screen_dimmed,
        .screen_off = ui.screen_off,
        .night_hours = ui.night_hours,
        .display_idle_ms = ui.idle_ms,
        .menu_open = ui.menu_open,
        .transition_active = ui.transition_active || ui.startup != NULL,
        .has_stats = ui.have_stats,
        .auto_open_apps = ui.have_stats && ui.stats.auto_open_apps,
        .last_stats_age_ms = ui.screen ? lv_tick_elaps(ui.last_stats_tick) : 0,
        .music_running = ui.have_stats && ui.stats.music.running,
        .music_available = music_connected() && ui.stats.music.available,
        .music_automatic = ui.music_automatic,
        .codex_running = ui.have_stats && ui.stats.codex.running,
        .claude_running = ui.have_stats && ui.stats.claude.running,
        .codex_available = codex_has_windows(),
        .claude_available = claude_has_windows(),
        .codex_stale = codex_stale(),
        .claude_stale = claude_stale(),
        .codex_automatic = ui.codex_automatic,
        .claude_automatic = ui.claude_automatic,
        .codex_tasks_available = codex_tasks_available(),
        .claude_tasks_available = claude_tasks_available(),
        .codex_tasks_stale = codex_tasks_stale(),
        .claude_tasks_stale = claude_tasks_stale(),
        .codex_working_count = codex_tasks_available() ? ui.stats.codex.activity.working_count : 0,
        .claude_working_count = claude_tasks_available() ? ui.stats.claude.activity.working_count : 0,
        .codex_task_count = codex_tasks_available() ? ui.stats.codex.activity.total_count : 0,
        .claude_task_count = claude_tasks_available() ? ui.stats.claude.activity.total_count : 0,
        .timer_remaining_s = (ui.timer_remaining_ms + 999U) / 1000U,
        .timer_running = ui.timer_running,
        .timer_duration_s = ui.timer_duration_s,
        .timer_completed = ui.timer_completed,
        .game_active = music_connected() && ui.stats.game.active,
        .game_fps = music_connected() && ui.stats.game.active ? ui.stats.game.fps : NAN,
    };
}
