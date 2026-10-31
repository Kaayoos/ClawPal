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
#include <stdlib.h>
#include <string.h>
#include "lvgl.h"
#include "esp_log.h"
#include "esp_app_desc.h"
#include "esp_heap_caps.h"
#include "buddy_mascot.h"
#include "buddy_clock_font.h"
#include "buddy_music_font.h"
#include "buddy_ui_motion.h"
#include "buddy_home.h"
#include "buddy_app_icons.h"
#include "buddy_home_logos.h"
#include "buddy_weather_page.h"
#include "buddy_weather_icon.h"

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
#define CONFIRM_TIMEOUT_MS 60000U
#define INSTALL_FAILURE_MS 12000U
#define APP_TRANSITION_MS 1000U
#define C_CLOCK_BG   BUDDY_COLOR_CLOCK_BG
#define C_CLOCK_TEXT BUDDY_COLOR_CLOCK_TEXT
#define C_CLOCK_DATE BUDDY_COLOR_MUTED
#define MUSIC_COMMAND_TIMEOUT_MS 5000U
#define MUSIC_SENDING_DELAY_MS 700U
#define MUSIC_HOLD_MS 3000U

/* Player controls in transport order; the two sliders follow the buttons. */
enum {
    MUSIC_SHUFFLE_BUTTON, MUSIC_PREVIOUS_BUTTON, MUSIC_PLAY_BUTTON, MUSIC_NEXT_BUTTON, MUSIC_REPEAT_BUTTON, MUSIC_MUTE_BUTTON,
    MUSIC_BUTTON_COUNT, MUSIC_SEEK_CONTROL = MUSIC_BUTTON_COUNT, MUSIC_VOLUME_CONTROL, MUSIC_CONTROL_COUNT
};

/* A tappable CPU or GPU card: load gauge, temperature and model name. */
typedef struct {
    lv_obj_t *button;
    lv_obj_t *name;
    lv_obj_t *gauge;
    lv_obj_t *usage;
    lv_obj_t *usage_caption;
    lv_obj_t *temperature;
    lv_obj_t *temperature_caption;
    lv_obj_t *model;
} channel_t;

/* Codex and Claude Code share one account page; only identity and copy differ. */
typedef struct {
    const lv_img_dsc_t *logo;
    uint32_t accent;
    buddy_text_key_t title, update_needed, open_on_pc, open_body, sign_in_body, update_body, no_limits_body, reading_body;
} agent_copy_t;

typedef struct {
    const buddy_codex_t *data;
    const agent_copy_t *copy;
    lv_obj_t *page, *logo, *heading, *plan, *status, *age, *mascot;
    lv_obj_t *tab_track, *tabs[2], *tab_labels[2];
    lv_obj_t *usage_body, *tasks_body, *empty_title, *empty_body, *tasks_empty_title, *tasks_empty_body;
    /* One card per usage window: a gauge of what is left, a marker for time left, and its reset. */
    lv_obj_t *cards[2], *gauges[2], *time_marks[2], *remaining[2], *remaining_captions[2];
    lv_obj_t *window_names[2], *countdowns[2], *resets[2], *paces[2];
    lv_obj_t *task_rows[3], *task_dots[3], *task_titles[3], *task_details[3], *task_states[3];
    int card_top, card_step, card_single_top, gauge_size;
    bool working[3], chip_on_details, tasks_selected, tab_initialized, tab_manual;
} agent_view_t;

/* A full-screen prompt: the approval request and the needs-you alert share this shape. */
#define PROMPT_ROWS 3
typedef struct {
    lv_obj_t *page, *tile, *mascot, *logo, *eyebrow, *title, *body, *details;
    lv_obj_t *names[PROMPT_ROWS], *values[PROMPT_ROWS];
    lv_obj_t *track, *countdown, *secondary, *secondary_label, *primary, *primary_label;
    unsigned rows;
} prompt_view_t;

static struct {
    lv_obj_t *screen, *overview, *details, *empty;
    lv_obj_t *dim_overlay, *about_page, *about_version;
    lv_obj_t *monitor_header, *monitor_title, *clock_page, *chooser, *weather_page;
    lv_obj_t *home_page, *game_panel, *game_title, *game_fps, *game_status;
    lv_obj_t *clock_large, *clock_date, *clock_mascot;
    lv_obj_t *weather_row, *weather_icon, *weather_text, *weather_note;
    buddy_weather_t weather;
    lv_obj_t *music_page, *music_logo, *music_heading, *music_status, *music_eq, *music_eq_bars[3];
    lv_obj_t *music_cover, *music_rings[2], *music_buddy, *music_meta, *music_title, *music_artist, *music_album;
    lv_obj_t *music_progress, *music_elapsed, *music_duration, *music_feedback;
    lv_obj_t *music_buttons[MUSIC_BUTTON_COUNT], *music_button_labels[MUSIC_BUTTON_COUNT];
    lv_obj_t *music_toggle_dots[2], *music_repeat_one, *music_volume, *music_volume_label;
    lv_obj_t *timer_page, *timer_heading, *timer_mascot, *timer_status, *timer_time, *timer_ring, *timer_end;
    lv_obj_t *timer_buttons[2], *timer_button_labels[2], *timer_adjust[6], *timer_units[3], *timer_captions[3];
    lv_obj_t *timer_presets[4], *timer_preset_labels[4];
    agent_view_t codex, claude;
    lv_obj_t *apps_button, *apps_text, *nav_button;
    lv_obj_t *chooser_title, *app_rows[BUDDY_APP_COUNT], *app_names[BUDDY_APP_COUNT], *app_description[BUDDY_APP_COUNT];
    lv_obj_t *music_link, *music_link_title, *music_link_bars[3];
    lv_obj_t *timer_link, *timer_link_title, *timer_link_progress;
    lv_obj_t *app_transition, *transition_image;
    lv_obj_t *host, *clock, *date, *status, *dot;
    lv_obj_t *footer, *nav_text, *details_button, *details_text;
    lv_obj_t *memory, *memory_percent, *vram, *vram_note;
    lv_obj_t *tiles[3], *tile_names[3], *memory_bar, *vram_bar, *net_captions[2], *chart_card, *chart_range, *game_fps_unit;
    lv_obj_t *rx, *tx;
    lv_obj_t *chart, *chart_title, *chart_time;
    lv_chart_series_t *cpu_series, *gpu_series;
    lv_obj_t *empty_title, *empty_body, *empty_network;
    lv_obj_t *detail_cpu, *detail_gpu, *detail_uptime;
    lv_obj_t *detail_ssid, *detail_ip, *detail_state, *detail_message;
    lv_obj_t *mascot, *startup, *startup_mascot;
    lv_img_dsc_t startup_frame;
    uint8_t *startup_pixels; /* The doubled crab, in PSRAM; NULL when the theme has none. */
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
    uint32_t music_command_tick, music_command_control_id;
    /* Slider gestures and the values shown until Spotify reports them back. */
    bool music_feedback_error, music_seeking, music_volume_dragging, music_seek_hold, music_volume_hold;
    uint32_t music_accent, music_seek_target, music_volume_target, music_seek_tick, music_volume_tick;
    uint32_t music_title_lines, music_deferred_value;
    unsigned music_deferred; /* Slider control + 1 waiting for the pending command, or 0. */
    buddy_music_control_callback_t music_control_callback;
    buddy_confirm_callback_t confirm_callback;
    buddy_confirm_action_t confirm_action;
    uint32_t confirm_started;
    char confirm_detail[BUDDY_CONFIRM_DETAIL_SIZE], install_detail[BUDDY_CONFIRM_DETAIL_SIZE];
    /* The approval prompt, and the alert while a Codex or Claude task waits for the user. */
    prompt_view_t confirm, alert;
    buddy_app_t alert_app;
    char alert_key[112], alert_dismissed[112];
    /* A Wi-Fi firmware install in progress. */
    lv_obj_t *install_page, *install_title, *install_version, *install_track, *install_fill, *install_mascot;
    lv_obj_t *install_percent, *install_stage_label, *install_note;
    int install_crab; /* 96 for the doubled crab, 48 for the original on small screens */
    int install_stage_y;
    buddy_install_stage_t install_stage;
    uint32_t install_tick;
    lv_timer_t *overlay_timer;
    /* The companion's last uploaded cover, shown while telemetry names the same ID. */
    lv_obj_t *music_art;
    lv_img_dsc_t art_frame;
    uint8_t *art_pixels;
    uint32_t art_id;
    /* The PC reported it is locked or going to sleep. */
    bool pc_away;
    uint32_t away_tick;
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
static void chooser_layout(void);
static void maybe_auto_app(void);
static void refresh_music(void);
static lv_obj_t *usage_gauge(lv_obj_t *parent);
static lv_obj_t *surface_card(lv_obj_t *parent, int radius);
static lv_obj_t *progress_bar(lv_obj_t *parent, int x, int y, int width);
static void set_music_feedback(const char *message, bool error);
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
static void refresh_alert(void);

/* A PC that is connected and not locked or asleep. */
static bool pc_live(void)
{
    return ui.connection == BUDDY_PC_CONNECTED && !ui.pc_away;
}

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
    bool connected = pc_live();
    if ((connected && view_settings.dim_behavior != 2) || touched != ui.last_touch_tick || ui.wake_touch_active ||
        ui.confirm_action != BUDDY_CONFIRM_NONE || ui.alert_key[0] || ui.install_stage != BUDDY_INSTALL_NONE) {
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

static bool usage_window_available(const buddy_codex_window_t *window)
{
    return window->available && isfinite(window->remaining_percent) &&
        window->remaining_percent >= 0 && window->remaining_percent <= 100;
}

static bool agent_has_windows(const buddy_codex_t *data)
{
    return ui.have_stats && data->available &&
        (usage_window_available(&data->primary) || usage_window_available(&data->secondary));
}

static bool agent_stale(const buddy_codex_t *data)
{
    return agent_has_windows(data) && (data->stale || !music_connected());
}

static bool agent_tasks_available(const buddy_codex_t *data)
{
    return ui.have_stats && data->activity.available;
}

static bool agent_tasks_stale(const buddy_codex_t *data)
{
    return agent_tasks_available(data) && (data->activity.stale || !music_connected() ||
        strcmp(data->activity.status, "closed") == 0 ||
        (uint64_t)data->activity.age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U > 30U);
}

/* Accents follow each product's logo; everything else uses the shared theme. */
static const agent_copy_t codex_copy = {
    &buddy_logo_codex, 0x8A9CF8, BT_CODEX, BT_CODEX_UPDATE_NEEDED, BT_OPEN_CODEX_ON_YOUR_PC,
    BT_OPEN_CODEX_OR_CHATGPT_ON_YOUR_PC_YOUR_AVAILABLE, BT_SIGN_IN_TO_CODEX_ON_YOUR_PC_BUDDY_WILL,
    BT_UPDATE_OR_INSTALL_CODEX_ON_YOUR_PC_BUDDY_NEEDS, BT_YOUR_ACCOUNT_DID_NOT_REPORT_A_USAGE_WINDOW_BUDDY,
    BT_READING_YOUR_CODEX_ACCOUNT_ON_THE_PC_YOUR_LIMITS,
};
static const agent_copy_t claude_copy = {
    &buddy_logo_claude, 0xDE7656, BT_CLAUDE, BT_CLAUDE_UPDATE_NEEDED, BT_OPEN_CLAUDE_ON_YOUR_PC,
    BT_OPEN_CLAUDE_OR_CHATGPT_ON_YOUR_PC_YOUR_AVAILABLE, BT_SIGN_IN_TO_CLAUDE_ON_YOUR_PC_BUDDY_WILL,
    BT_UPDATE_OR_INSTALL_CLAUDE_ON_YOUR_PC_BUDDY_NEEDS, BT_CLAUDE_NO_LIMITS,
    BT_READING_YOUR_CLAUDE_ACCOUNT_ON_THE_PC_YOUR_LIMITS,
};

static const char *agent_status_text(const buddy_codex_t *data, const agent_copy_t *copy)
{
    if (agent_has_windows(data)) return agent_stale(data) ? BT(BT_CACHED_LIMITS) : BT(BT_ACCOUNT_LIMITS);
    if (!music_connected()) return BT(BT_WAITING_FOR_PC);
    const char *status = data->status;
    if (strcmp(status, "signed_out") == 0 || strcmp(status, "unauthorized") == 0 ||
        strcmp(status, "sign_in") == 0) return BT(BT_SIGN_IN_ON_YOUR_PC);
    if (strcmp(status, "runtime_missing") == 0) return BT(copy->update_needed);
    if (strcmp(status, "no_limits") == 0) return BT(BT_NO_LIMITS_REPORTED);
    if (strcmp(status, "error") == 0 || strcmp(status, "failed") == 0 ||
        (strcmp(status, "unavailable") == 0 && data->running)) return BT(BT_LIMITS_UNAVAILABLE);
    return data->running ? BT(BT_WAITING_FOR_LIMITS) : BT(copy->open_on_pc);
}

static bool codex_has_windows(void) { return agent_has_windows(&ui.stats.codex); }
static bool claude_has_windows(void) { return agent_has_windows(&ui.stats.claude); }
static bool codex_stale(void) { return agent_stale(&ui.stats.codex); }
static bool claude_stale(void) { return agent_stale(&ui.stats.claude); }
static bool codex_tasks_available(void) { return agent_tasks_available(&ui.stats.codex); }
static bool claude_tasks_available(void) { return agent_tasks_available(&ui.stats.claude); }
static bool codex_tasks_stale(void) { return agent_tasks_stale(&ui.stats.codex); }
static bool claude_tasks_stale(void) { return agent_tasks_stale(&ui.stats.claude); }
static const char *codex_status_text(void) { return agent_status_text(&ui.stats.codex, &codex_copy); }
static const char *claude_status_text(void) { return agent_status_text(&ui.stats.claude, &claude_copy); }

static void activity_age_text(char *buffer, size_t size, uint32_t source_age)
{
    uint64_t age = (uint64_t)source_age + lv_tick_elaps(ui.last_stats_tick) / 1000U;
    if (age < 60) snprintf(buffer, size, BT(BT_LU_S_AGO), (unsigned long)age);
    else if (age < 3600) snprintf(buffer, size, BT(BT_LU_MIN_AGO), (unsigned long)(age / 60U));
    else if (age < 86400) snprintf(buffer, size, BT(BT_LU_H_AGO), (unsigned long)(age / 3600U));
    else snprintf(buffer, size, BT(BT_LU_D_AGO), (unsigned long)(age / 86400U));
}

static void usage_window_name(char *buffer, size_t size, uint32_t minutes)
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
    char value[96], time[24];
    set_label_if_changed(ui.app_description[BUDDY_APP_HOME], BT(BT_YOUR_APPS_AND_YOUR_CRAB));
    if (music_connected() && isfinite(ui.stats.cpu_usage) && isfinite(ui.stats.gpu_usage))
        snprintf(value, sizeof(value), BT(BT_CPU_0F_GPU_0F_LINES), ui.stats.cpu_usage, ui.stats.gpu_usage);
    else snprintf(value, sizeof(value), "%s", connection_name());
    set_label_if_changed(ui.app_description[BUDDY_APP_MONITOR], value);
    set_label_if_changed(ui.app_description[BUDDY_APP_CLOCK], lv_label_get_text(ui.clock));
    if (ui.weather.available) {
        char temperature[24];buddy_weather_format_temperature(temperature,sizeof(temperature),ui.weather.temperature_c,view_settings.weather_fahrenheit);
        snprintf(value,sizeof(value),"%s\n%s",temperature,buddy_weather_description(ui.weather.code,ui.weather.is_day));
    }
    else snprintf(value,sizeof(value),"%s",ui.weather.failed ? BT(BT_WEATHER_UNAVAILABLE) : BT(BT_WEATHER_LOADING));
    set_label_if_changed(ui.app_description[BUDDY_APP_WEATHER],value);
    const buddy_music_t *music = &ui.stats.music;
    set_label_if_changed(ui.app_description[BUDDY_APP_MUSIC], music_connected() && music->available ?
        (music->title[0] ? music->title : BT(BT_PLAYING)) : music_connected() && music->running ? BT(BT_NOT_PLAYING) : BT(BT_WAITING_FOR_SPOTIFY));
    countdown_text(time, sizeof(time), (ui.timer_remaining_ms + 999U) / 1000U);
    snprintf(value, sizeof(value), "%s\n%s", time, ui.timer_running ? BT(BT_RUNNING) :
        ui.timer_completed ? BT(BT_TIME_S_UP) : ui.timer_started ? BT(BT_PAUSED) : BT(BT_READY));
    set_label_if_changed(ui.app_description[BUDDY_APP_TIMER], value);
    const struct { buddy_app_t app; const buddy_codex_t *agent; bool windows, working; const char *status; } agents[] = {
        { BUDDY_APP_CODEX, &ui.stats.codex, codex_has_windows(), codex_tasks_available() && !codex_tasks_stale(), codex_status_text() },
        { BUDDY_APP_CLAUDE, &ui.stats.claude, claude_has_windows(), claude_tasks_available() && !claude_tasks_stale(), claude_status_text() },
    };
    for (unsigned i = 0; i < 2; ++i) {
        const buddy_codex_t *agent = agents[i].agent;
        if (agents[i].working && agent->activity.working_count)
            snprintf(value, sizeof(value), BT(BT_LU_RECENTLY_WORKING), (unsigned long)agent->activity.working_count);
        else if (agents[i].windows) {
            const buddy_codex_window_t *window = usage_window_available(&agent->primary) ? &agent->primary : &agent->secondary;
            snprintf(value, sizeof(value), BT(BT_0F_REMAINING), window->remaining_percent);
        } else snprintf(value, sizeof(value), "%s", agents[i].status);
        set_label_if_changed(ui.app_description[agents[i].app], value);
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
        ui.stats.music.title[0] ? ui.stats.music.title : BT(BT_MUSIC));
    refresh_app_cards();
}

static const char *connection_name(void)
{
    switch (ui.connection) {
    case BUDDY_WIFI_CONNECTING: return BT(BT_JOINING_WI_FI);
    case BUDDY_WIFI_READY: return BT(BT_WAITING_FOR_PC);
    case BUDDY_PC_CONNECTED: return ui.pc_away ? BT(BT_PC_AWAY) : BT(BT_CONNECTED);
    case BUDDY_PC_OFFLINE: return ui.pc_away ? BT(BT_PC_AWAY) : BT(BT_PC_OFFLINE);
    case BUDDY_PROVISIONING: return BT(BT_SETUP_NEEDED);
    default: return BT(BT_CONNECTING);
    }
}

/* The large crab of whichever overlay is on top, if it has one. */
static lv_obj_t *overlay_mascot(void)
{
    if (ui.install_stage != BUDDY_INSTALL_NONE) return ui.install_crab == 96 ? ui.install_mascot : NULL;
    if (ui.confirm_action != BUDDY_CONFIRM_NONE) return ui.confirm.mascot;
    if (ui.alert_key[0]) return ui.alert.mascot;
    return NULL;
}

static buddy_mascot_state_t mascot_state(void)
{
    if (ui.startup) return BUDDY_MASCOT_HAPPY;
    /* The crab carries updates in, celebrates the restart and frowns at a failure. */
    if (ui.install_stage == BUDDY_INSTALL_FAILED) return BUDDY_MASCOT_ERROR;
    if (ui.install_stage == BUDDY_INSTALL_RESTARTING) return BUDDY_MASCOT_HAPPY;
    if (ui.install_stage != BUDDY_INSTALL_NONE) return BUDDY_MASCOT_CARRYING;
    if (ui.confirm_action != BUDDY_CONFIRM_NONE)
        return ui.confirm_action == BUDDY_CONFIRM_FIRMWARE ? BUDDY_MASCOT_CARRYING : BUDDY_MASCOT_THINKING;
    if (ui.alert_key[0]) return BUDDY_MASCOT_THINKING;
    if (ui.transition_active) return ui.transition_mascot;
    if (ui.active_app == BUDDY_APP_HOME) return BUDDY_MASCOT_IDLE;
    /* The crab naps with the PC; the timer keeps its own animation. */
    if (ui.pc_away && ui.active_app != BUDDY_APP_TIMER) return BUDDY_MASCOT_SLEEPING;
    if (ui.active_app == BUDDY_APP_TIMER) {
        if (ui.timer_celebrating && lv_tick_elaps(ui.timer_celebration_tick) < 2400)
            return BUDDY_MASCOT_HAPPY;
        return ui.timer_running ? BUDDY_MASCOT_TYPING : BUDDY_MASCOT_IDLE;
    }
    if (ui.active_app == BUDDY_APP_MUSIC)
        return ui.music_feedback_error ? BUDDY_MASCOT_ERROR : BUDDY_MASCOT_MUSIC;
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
    if (!target) return;
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
    lv_obj_t *target_image = ui.startup_mascot ? ui.startup_mascot : overlay_mascot() ? overlay_mascot() :
        ui.active_app == BUDDY_APP_CLOCK ? ui.clock_mascot :
        ui.active_app == BUDDY_APP_TIMER ? ui.timer_mascot :
        ui.active_app == BUDDY_APP_CODEX ? ui.codex.mascot :
        ui.active_app == BUDDY_APP_CLAUDE ? ui.claude.mascot :
        ui.active_app == BUDDY_APP_MUSIC ? ui.music_buddy : ui.transition_image;
    lv_img_set_src(target_image, &ui.startup_frame);
    lv_obj_invalidate(target_image);
}

static void feed_mascot(void);

/* The UI heartbeat: timers, pending commands, live indicators and, when the theme has one, the crab. */
static void mascot_tick(lv_timer_t *timer)
{
    (void)timer;
    advance_timer();
    if (ui.timer_celebrating && lv_tick_elaps(ui.timer_celebration_tick) >= 2400U)
        ui.timer_celebrating = false;
    if (ui.music_command_pending && lv_tick_elaps(ui.music_command_tick) >= MUSIC_COMMAND_TIMEOUT_MS) {
        ui.music_command_pending = false;
        ui.music_deferred = 0;
        set_music_feedback(BT(BT_NO_REPLY_FROM_SPOTIFY_TRY_AGAIN), true);
        refresh_music();
    } else if (ui.music_command_pending && lv_tick_elaps(ui.music_command_tick) >= MUSIC_SENDING_DELAY_MS &&
               !lv_label_get_text(ui.music_feedback)[0]) {
        /* Quick replies stay silent; only a slow one says it is still on its way. */
        set_music_feedback(BT(BT_SENDING_TO_SPOTIFY), false);
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
    /* Playback indicators, not measured audio levels. Stop when hidden. */
    static const uint8_t heights[] = { 4, 7, 10, 13, 10, 7 };
    unsigned beat = (lv_tick_get() / 160U) % 6U;
    if (ui.music_link_shown) {
        for (unsigned i = 0; i < 3; ++i) {
            unsigned height = heights[(beat + i * 2) % 6];
            lv_obj_set_height(ui.music_link_bars[i], height);
            lv_obj_set_y(ui.music_link_bars[i], 22 - (int)height);
        }
    }
    if (ui.active_app == BUDDY_APP_MUSIC && !ui.menu_open && ui.music_eq && !lv_obj_has_flag(ui.music_eq, LV_OBJ_FLAG_HIDDEN)) {
        for (unsigned i = 0; i < 3; ++i) {
            unsigned height = heights[(beat + i * 2) % 6] + 1U;
            lv_obj_set_height(ui.music_eq_bars[i], height);
            lv_obj_set_y(ui.music_eq_bars[i], 14 - (int)height);
        }
    }
    /* Tasks that are working right now breathe; the others stay still. */
    agent_view_t *agent = ui.active_app == BUDDY_APP_CODEX ? &ui.codex : ui.active_app == BUDDY_APP_CLAUDE ? &ui.claude : NULL;
    if (agent && agent->page && agent->tasks_selected && !ui.menu_open) {
        lv_opa_t pulse = (lv_tick_get() / 600U) % 2U ? LV_OPA_COVER : LV_OPA_40;
        for (unsigned i = 0; i < 3; ++i)
            if (agent->working[i]) lv_obj_set_style_bg_opa(agent->task_dots[i], pulse, 0);
    }
    /* The approval's time runs out visibly; a waiting task's tile pulses. */
    if (ui.confirm_action != BUDDY_CONFIRM_NONE && ui.confirm.countdown) {
        uint32_t elapsed = lv_tick_elaps(ui.confirm_started), left = elapsed >= CONFIRM_TIMEOUT_MS ? 0 : CONFIRM_TIMEOUT_MS - elapsed;
        lv_obj_set_width(ui.confirm.countdown, (lv_coord_t)((int64_t)lv_obj_get_width(ui.confirm.track) * left / CONFIRM_TIMEOUT_MS));
    }
    if (ui.alert_key[0] && ui.alert.tile)
        lv_obj_set_style_border_opa(ui.alert.tile, (lv_tick_get() / 600U) % 2U ? LV_OPA_COVER : LV_OPA_20, 0);
    if (buddy_theme_has_mascot()) feed_mascot();
}

/* Hands the current crab frame to whichever crab is on screen. */
static void feed_mascot(void)
{
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
    if (ui.install_stage != BUDDY_INSTALL_NONE && ui.install_crab == 48) lv_img_set_src(ui.install_mascot, source);
    if (ui.startup_mascot || overlay_mascot() || (!ui.menu_open &&
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
    /* Internal RAM stays free for Wi-Fi and TLS; the frame falls back there only without PSRAM. */
    if (buddy_theme_has_mascot() && !ui.startup_pixels) {
        ui.startup_pixels = heap_caps_malloc(BUDDY_MASCOT_DOUBLE_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
        if (!ui.startup_pixels) ui.startup_pixels = heap_caps_malloc(BUDDY_MASCOT_DOUBLE_BYTES, MALLOC_CAP_8BIT);
    }
    ui.startup_frame = (lv_img_dsc_t) {
        .header = { .cf = LV_IMG_CF_INDEXED_4BIT, .always_zero = 0, .w = 96, .h = 96 },
        .data_size = BUDDY_MASCOT_DOUBLE_BYTES, .data = ui.startup_pixels,
    };
    const bool crab = buddy_theme_has_mascot();
    if (crab) {
        ui.startup_mascot = lv_img_create(ui.startup);
        lv_img_set_antialias(ui.startup_mascot, false);
        lv_obj_set_pos(ui.startup_mascot, (BOARD_DISPLAY_WIDTH-96)/2, (BOARD_DISPLAY_HEIGHT-230)/2+20);
    }
    const lv_font_t *wordmark = !crab && BOARD_DISPLAY_HEIGHT >= 400 ? &buddy_language_font_48 : &buddy_language_font_32;
    const int title_y = crab ? (BOARD_DISPLAY_HEIGHT-230)/2+140 : BOARD_DISPLAY_HEIGHT/2 - lv_font_get_line_height(wordmark) + 4;
    lv_obj_t *title = text(ui.startup, BT(BT_CLAWPAL), 12, title_y, BOARD_DISPLAY_WIDTH-24, wordmark, C_TEXT);
    lv_obj_set_style_text_align(title, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_t *subtitle = text(ui.startup, BT(BT_YOUR_LITTLE_DESK_COMPANION), 12, crab ? (BOARD_DISPLAY_HEIGHT-230)/2+186 : BOARD_DISPLAY_HEIGHT/2 + 18, BOARD_DISPLAY_WIDTH-24, &buddy_language_font_16, C_MUTED);
    lv_obj_set_style_text_align(subtitle, LV_TEXT_ALIGN_CENTER, 0);
    if (!crab) {
        lv_obj_t *line = box(ui.startup, (BOARD_DISPLAY_WIDTH - 48) / 2, BOARD_DISPLAY_HEIGHT/2 + 6, 48, 1);
        lv_obj_set_style_bg_color(line, color(C_ACCENT), 0);
        lv_obj_set_style_bg_opa(line, LV_OPA_COVER, 0);
        buddy_ui_motion_enter(title, 10, 600, 0);
        buddy_ui_motion_enter(subtitle, 10, 600, 120);
    }
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
    visible(ui.codex.page, ui.active_app == BUDDY_APP_CODEX && !ui.menu_open);
    if (ui.active_app != BUDDY_APP_CODEX || ui.menu_open) {
        buddy_ui_motion_cancel(ui.codex.usage_body);
        buddy_ui_motion_cancel(ui.codex.tasks_body);
    }
    visible(ui.claude.page, ui.active_app == BUDDY_APP_CLAUDE && !ui.menu_open);
    if (ui.active_app != BUDDY_APP_CLAUDE || ui.menu_open) {
        buddy_ui_motion_cancel(ui.claude.usage_body);
        buddy_ui_motion_cancel(ui.claude.tasks_body);
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
    if (!ui.menu_open) for (unsigned i = 0; i < BUDDY_APP_COUNT; ++i) buddy_ui_motion_cancel(ui.app_rows[i]);
    chooser_layout();
    visible(ui.weather_row, view_settings.weather_enabled);
    visible(ui.weather_note, view_settings.weather_enabled);
    refresh_activity();
    refresh_display_power();
}

/* The Apps menu is a launcher grid: four tiles across on the wide screen, three on the others. */
typedef struct { int columns, margin, gap, top, width, height, icon, pad; } chooser_grid_t;
static chooser_grid_t chooser_grid(void)
{
    chooser_grid_t grid = BOARD_DISPLAY_WIDTH >= 800 ? (chooser_grid_t){ 4, 28, 16, 76, 0, 166, 48, 18 } :
        BOARD_DISPLAY_HEIGHT < 400 ? (chooser_grid_t){ 3, 12, 6, 40, 0, 72, 32, 8 } : (chooser_grid_t){ 3, 12, 8, 52, 0, 120, 32, 10 };
    grid.width = (BOARD_DISPLAY_WIDTH - 2 * grid.margin - (grid.columns - 1) * grid.gap) / grid.columns;
    return grid;
}

/* Home leads, the enabled apps follow in their usual order; the open app wears an accent outline. */
static void chooser_layout(void)
{
    static const buddy_app_t order[] = { BUDDY_APP_HOME, BUDDY_APP_MONITOR, BUDDY_APP_CLOCK, BUDDY_APP_MUSIC,
        BUDDY_APP_TIMER, BUDDY_APP_CODEX, BUDDY_APP_WEATHER, BUDDY_APP_CLAUDE };
    const chooser_grid_t grid = chooser_grid();
    unsigned position = 0;
    for (unsigned i = 0; i < sizeof(order) / sizeof(order[0]); ++i) {
        lv_obj_t *tile = ui.app_rows[order[i]];
        bool shown = order[i] == BUDDY_APP_HOME || app_enabled(order[i]), selected = order[i] == ui.active_app;
        visible(tile, shown);
        lv_obj_set_style_border_color(tile, color(selected ? C_ACCENT : C_RULE), 0);
        lv_obj_set_style_border_width(tile, selected ? buddy_theme.border_w : buddy_theme.outlined_cards ? 1 : 0, 0);
        if (!shown) continue;
        lv_obj_set_pos(tile, grid.margin + (int)(position % grid.columns) * (grid.width + grid.gap),
                       grid.top + (int)(position / grid.columns) * (grid.height + grid.gap));
        ++position;
    }
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
        lv_label_set_text(ui.footer, ui.codex.tasks_selected ? BT(BT_RECORDED_LOCALLY_NOT_LIVE_TASK_STATUS) :
            codex_stale() ? BT(BT_SAVED_LIMITS_WAITING_FOR_AN_UPDATE) : BT(BT_LIMITS_FROM_YOUR_CODEX_ACCOUNT));
    } else if (ui.active_app == BUDDY_APP_CLAUDE) {
        lv_label_set_text(ui.footer, ui.claude.tasks_selected ? BT(BT_RECORDED_LOCALLY_NOT_LIVE_TASK_STATUS) :
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
    lv_obj_t *objects[] = { ui.overview, ui.details, ui.empty, ui.music_page, ui.timer_page, ui.codex.page, ui.claude.page, ui.home_page, ui.weather_page,
        ui.codex.usage_body, ui.codex.tasks_body, ui.claude.usage_body, ui.claude.tasks_body,
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
            ui.active_app == BUDDY_APP_CODEX ? ui.codex.page :
            ui.active_app == BUDDY_APP_CLAUDE ? ui.claude.page :
            ui.active_app == BUDDY_APP_TIMER ? ui.timer_page : ui.details_visible ? ui.details :
            ui.have_stats && ui.connection != BUDDY_PROVISIONING ? ui.overview : ui.empty;
        buddy_ui_motion_enter(destination, 8, 200, 0);
    }
}

static void begin_app_transition(void)
{
    if (!buddy_theme.app_flourish) { ui.transition_active = false; return; }
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
    ui.offline_automatic = automatic && !pc_live();
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
    bool fresh_pc = ui.have_stats && pc_live() && age < CLOCK_DELAY_MS;
    /* A locked PC keeps reporting; it counts as away from the moment it locked. */
    if (ui.pc_away && ui.connection == BUDDY_PC_CONNECTED) age = lv_tick_elaps(ui.away_tick);
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
        if (ui.pc_away) {
            lv_label_set_text(ui.empty_title, BT(BT_YOUR_PC_IS_LOCKED_OR_ASLEEP));
            lv_label_set_text(ui.empty_body, BT(BT_CLAWPAL_PICKS_UP_AGAIN_WHEN_YOU_RE_BACK));
            break;
        }
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
    /* The selected card wears an accent outline and drives the history chart. */
    lv_obj_set_style_border_opa(ui.cpu.button, gpu ? LV_OPA_TRANSP : LV_OPA_COVER, 0);
    lv_obj_set_style_border_opa(ui.gpu.button, gpu ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
    lv_obj_set_style_text_color(ui.cpu.name, color(gpu ? C_TEXT : C_ACCENT), 0);
    lv_obj_set_style_text_color(ui.gpu.name, color(gpu ? C_ACCENT : C_TEXT), 0);
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

static void create_channel(channel_t *channel, const char *name, bool gpu)
{
    channel->button = surface_card(ui.overview, 16);
    lv_obj_add_flag(channel->button, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(channel->button, color(C_PRESSED), LV_STATE_PRESSED);
    lv_obj_set_style_border_color(channel->button, color(C_ACCENT), 0);
    lv_obj_set_style_border_width(channel->button, buddy_theme.border_w, 0);
    lv_obj_set_style_border_opa(channel->button, LV_OPA_TRANSP, 0);
    lv_obj_add_event_cb(channel->button, channel_clicked, LV_EVENT_CLICKED, gpu ? (void *)1 : NULL);
    channel->gauge = usage_gauge(channel->button);
    lv_arc_set_range(channel->gauge, 0, 1000);
    channel->usage = lv_label_create(channel->gauge);
    lv_label_set_text(channel->usage, "--");
    channel->usage_caption = lv_label_create(channel->gauge);
    lv_label_set_text(channel->usage_caption, BT(BT_LOAD));
    lv_obj_set_style_text_font(channel->usage_caption, &buddy_language_font_14, 0);
    lv_obj_set_style_text_color(channel->usage_caption, color(C_MUTED), 0);
    channel->name = text(channel->button, name, 0, 0, 100, &buddy_language_font_20, C_TEXT);
    channel->temperature = text(channel->button, "--", 0, 0, 100, &buddy_language_font_32, C_TEXT);
    channel->temperature_caption = text(channel->button, BT(BT_TEMPERATURE), 0, 0, 100, &buddy_language_font_14, C_MUTED);
    channel->model = text(channel->button, BT(BT_WAITING_FOR_SENSORS), 0, 0, 100, &buddy_language_font_14, C_MUTED);
}

/* Card interior: the load gauge on the left, name, temperature and model beside it. */
static void channel_layout(channel_t *channel, int x, int y, int width, int height, int gauge,
                           const lv_font_t *usage_font, const lv_font_t *temperature_font)
{
    lv_obj_set_pos(channel->button, x, y);
    lv_obj_set_size(channel->button, width, height);
    int pad = (height - gauge) / 2, text_x = pad + gauge + 18, text_w = width - text_x - 14;
    lv_obj_set_pos(channel->gauge, pad, pad);
    lv_obj_set_size(channel->gauge, gauge, gauge);
    lv_obj_set_style_arc_width(channel->gauge, gauge / buddy_theme.gauge_div, LV_PART_MAIN);
    lv_obj_set_style_arc_width(channel->gauge, gauge / buddy_theme.gauge_div, LV_PART_INDICATOR);
    lv_obj_set_style_text_font(channel->usage, usage_font, 0);
    lv_obj_align(channel->usage, LV_ALIGN_CENTER, 0, -8);
    lv_obj_align(channel->usage_caption, LV_ALIGN_CENTER, 0, lv_font_get_line_height(usage_font) / 2 + 2);
    int temperature_h = lv_font_get_line_height(temperature_font), y_text = pad + 2;
    lv_obj_set_pos(channel->name, text_x, y_text);
    lv_obj_set_size(channel->name, text_w, 26);
    lv_obj_set_pos(channel->temperature, text_x, y_text + 28);
    lv_obj_set_size(channel->temperature, text_w, temperature_h);
    lv_obj_set_style_text_font(channel->temperature, temperature_font, 0);
    lv_obj_set_pos(channel->temperature_caption, text_x, y_text + 28 + temperature_h);
    lv_obj_set_size(channel->temperature_caption, text_w, 20);
    /* Shorter compact cards keep the model line and drop the caption. */
    visible(channel->temperature_caption, height >= 130);
    lv_obj_set_pos(channel->model, text_x, height - pad - 20);
    lv_obj_set_size(channel->model, text_w, 20);
}

static lv_obj_t *meter_bar(lv_obj_t *parent)
{
    lv_obj_t *bar = progress_bar(parent, 0, 0, 10);
    lv_obj_set_style_radius(bar, LV_RADIUS_CIRCLE, LV_PART_MAIN);
    lv_obj_set_style_radius(bar, LV_RADIUS_CIRCLE, LV_PART_INDICATOR);
    lv_bar_set_range(bar, 0, 100);
    return bar;
}

/* Memory, GPU memory and network tiles: name, reading, and a usage bar or rates. */
static void tile_layout(unsigned i, int x, int y, int width, int height)
{
    lv_obj_set_pos(ui.tiles[i], x, y);
    lv_obj_set_size(ui.tiles[i], width, height);
    lv_obj_set_pos(ui.tile_names[i], 14, 12);
    lv_obj_set_size(ui.tile_names[i], i == 2 ? width - 28 : width - 28 - 92, 20);
    if (i == 2) {
        int row = (height - 34 - 8) / 2;
        for (unsigned k = 0; k < 2; ++k) {
            lv_obj_t *value = k ? ui.tx : ui.rx;
            lv_obj_set_pos(ui.net_captions[k], 14, 38 + (int)k * row + 4);
            lv_obj_set_size(ui.net_captions[k], 80, 20);
            lv_obj_set_pos(value, 94, 38 + (int)k * row);
            lv_obj_set_size(value, width - 108, 26);
        }
        return;
    }
    lv_obj_t *note = i ? ui.vram_note : ui.memory_percent, *value = i ? ui.vram : ui.memory, *bar = i ? ui.vram_bar : ui.memory_bar;
    lv_obj_set_pos(note, width - 14 - 88, 12);
    lv_obj_set_size(note, 88, 20);
    lv_obj_set_pos(value, 14, 34);
    lv_obj_set_size(value, width - 28, 32);
    lv_obj_set_pos(bar, 14, height - 20);
    lv_obj_set_size(bar, width - 28, 6);
}

/* The history chart and the game panel share one card. */
static void chart_layout(int x, int y, int width, int height)
{
    lv_obj_t *cards[] = { ui.chart_card, ui.game_panel };
    for (unsigned i = 0; i < 2; ++i) {
        lv_obj_set_pos(cards[i], x, y);
        lv_obj_set_size(cards[i], width, height);
    }
    lv_obj_set_pos(ui.chart_title, 14, 10);
    lv_obj_set_size(ui.chart_title, width - 28 - 110, 20);
    lv_obj_set_pos(ui.chart_range, width - 14 - 110, 10);
    lv_obj_set_pos(ui.chart, 14, 34);
    lv_obj_set_size(ui.chart, width - 28, height - 44);
    lv_obj_set_pos(ui.game_title, 14, 10);
    lv_obj_set_size(ui.game_title, width - 28 - 150, 27);
    lv_obj_set_pos(ui.game_status, 14, height - 30);
    lv_obj_set_size(ui.game_status, width - 28 - 150, 20);
    lv_obj_set_pos(ui.game_fps, width - 14 - 150, (height - 70) / 2);
    lv_obj_set_pos(ui.game_fps_unit, width - 14 - 150, (height - 70) / 2 + 52);
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
    lv_obj_set_size(bar, width, buddy_theme.track_h);
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

/* Whether Spotify offers a control right now, ignoring any command in flight. */
static bool music_control_available(unsigned control)
{
    const buddy_music_t *music = &ui.stats.music;
    if (!music_connected() || !music->running || !music->available) return false;
    switch (control) {
    case MUSIC_SHUFFLE_BUTTON: return music->can_shuffle;
    case MUSIC_PREVIOUS_BUTTON: return music->can_previous;
    case MUSIC_PLAY_BUTTON: return music->can_play_pause;
    case MUSIC_NEXT_BUTTON: return music->can_next;
    case MUSIC_REPEAT_BUTTON: return music->can_repeat;
    case MUSIC_SEEK_CONTROL: return music->can_seek && music->duration_s;
    case MUSIC_MUTE_BUTTON: case MUSIC_VOLUME_CONTROL: return music->can_volume;
    default: return false;
    }
}

static bool music_can_control(unsigned control)
{
    return music_control_available(control) && ui.music_control_callback && !ui.music_command_pending;
}

static void playback_time(char *buffer, size_t size, uint32_t seconds)
{
    snprintf(buffer, size, "%lu:%02lu", (unsigned long)(seconds / 60), (unsigned long)(seconds % 60));
}

static void set_music_feedback(const char *message, bool error)
{
    if (!ui.music_feedback) return;
    ui.music_feedback_error = error && message[0];
    lv_obj_set_style_text_color(ui.music_feedback, color(error ? C_WARNING : C_MUTED), 0);
    set_label_if_changed(ui.music_feedback, message);
}

/* One command is in flight at a time. A slider moved meanwhile keeps only its
 * newest value and sends it once Spotify answers; taps are simply ignored. */
static void music_send(unsigned control, uint32_t value)
{
    static const char *const actions[MUSIC_CONTROL_COUNT] = { "shuffle", "previous", "play_pause", "next", "repeat", "mute", "seek", "volume" };
    if (control >= MUSIC_CONTROL_COUNT || !music_control_available(control) || !ui.music_control_callback) return;
    if (ui.music_command_pending) {
        if (control >= MUSIC_BUTTON_COUNT) {
            ui.music_deferred = control + 1U;
            ui.music_deferred_value = value;
        }
        return;
    }
    if (ui.music_control_callback(actions[control], value)) {
        ui.music_command_pending = true;
        ui.music_command_tick = lv_tick_get();
        ui.music_command_control_id = ui.stats.music.control_id;
        set_music_feedback("", false);
    } else {
        set_music_feedback(BT(BT_COULDN_T_SEND_TRY_AGAIN), true);
    }
}

/* The cover, play button and meters take the album's color when the PC sends one. */
static void apply_music_accent(uint32_t accent)
{
    ui.music_accent = accent;
    lv_color_t ink = color(accent);
    lv_obj_set_style_bg_color(ui.music_cover, lv_color_mix(ink, color(C_BG), 118), 0);
    lv_obj_set_style_bg_grad_color(ui.music_cover, lv_color_mix(ink, color(C_BG), 34), 0);
    for (unsigned i = 0; i < 2; ++i) lv_obj_set_style_border_color(ui.music_rings[i], ink, 0);
    lv_obj_set_style_bg_color(ui.music_buttons[MUSIC_PLAY_BUTTON], ink, 0);
    lv_obj_set_style_bg_color(ui.music_buttons[MUSIC_PLAY_BUTTON], lv_color_darken(ink, LV_OPA_30), LV_STATE_PRESSED);
    lv_obj_set_style_bg_color(ui.music_progress, ink, LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(ui.music_volume, ink, LV_PART_INDICATOR);
    for (unsigned i = 0; i < 3; ++i) lv_obj_set_style_bg_color(ui.music_eq_bars[i], ink, 0);
    for (unsigned i = 0; i < 2; ++i) lv_obj_set_style_bg_color(ui.music_toggle_dots[i], ink, 0);
    lv_obj_set_style_bg_color(ui.music_repeat_one, ink, 0);
}

/* Titles wrap up to the layout's line limit; artist and album follow directly.
 * Measure the source text: LONG_DOT rewrites the label's own copy with dots. */
static void music_layout_meta(const char *title)
{
    lv_coord_t width = lv_obj_get_width(ui.music_meta);
    const lv_font_t *title_font = lv_obj_get_style_text_font(ui.music_title, 0);
    lv_coord_t line = lv_font_get_line_height(title_font);
    lv_point_t size;
    lv_txt_get_size(&size, title, title_font, 0, 5, width, LV_TEXT_FLAG_NONE);
    uint32_t lines = (uint32_t)((size.y + 5) / (line + 5));
    if (lines < 1) lines = 1;
    if (lines > ui.music_title_lines) lines = ui.music_title_lines;
    lv_coord_t title_height = (lv_coord_t)(lines * line + (lines - 1) * 5);
    lv_coord_t artist_height = lv_font_get_line_height(lv_obj_get_style_text_font(ui.music_artist, 0));
    lv_coord_t album_height = lv_font_get_line_height(lv_obj_get_style_text_font(ui.music_album, 0));
    lv_obj_set_size(ui.music_title, width, title_height);
    lv_obj_set_pos(ui.music_artist, 0, title_height + 8);
    lv_obj_set_size(ui.music_artist, width, artist_height);
    lv_obj_set_pos(ui.music_album, 0, title_height + 12 + artist_height);
    lv_obj_set_size(ui.music_album, width, album_height);
}

/* The equalizer sits just left of the right-aligned status text. */
static void music_place_eq(void)
{
    const char *status = lv_label_get_text(ui.music_status);
    const lv_font_t *font = lv_obj_get_style_text_font(ui.music_status, 0);
    lv_coord_t box = lv_obj_get_width(ui.music_status);
    lv_coord_t width = lv_txt_get_width(status, (uint32_t)strlen(status), font, 0, LV_TEXT_FLAG_NONE);
    if (width > box) width = box;
    lv_obj_set_pos(ui.music_eq, lv_obj_get_x(ui.music_status) + box - width - 24,
                   lv_obj_get_y(ui.music_status) + (lv_font_get_line_height(font) - 14) / 2);
}

static void music_volume_text(uint32_t volume)
{
    char buffer[8];
    snprintf(buffer, sizeof(buffer), "%lu%%", (unsigned long)volume);
    set_label_if_changed(ui.music_volume_label, buffer);
}

static void refresh_music(void)
{
    if (!ui.music_page) return;
    const buddy_music_t *music = &ui.stats.music;
    bool connected = music_connected(), live = connected && music->running && music->available;
    uint32_t accent = live && music->art_color ? music->art_color : C_ACCENT;
    if (accent != ui.music_accent) apply_music_accent(accent);
    /* Any Windows player can fill this page; only Spotify gets its logo. */
    bool spotify = strcmp(music->source, "Spotify") == 0;
    buddy_app_icons_set_spotify(live && spotify);
    set_label_if_changed(ui.music_heading, live && music->source[0] ? music->source : BT(BT_MUSIC));
    if (BOARD_DISPLAY_WIDTH >= 800) {
        visible(ui.music_logo, live && spotify);
        lv_obj_set_x(ui.music_heading, live && spotify ? 84 : 28);
    }
    bool cover = live && music->art_id && music->art_id == ui.art_id && ui.art_pixels;
    visible(ui.music_art, cover);
    visible(ui.music_buddy, !cover);
    for (unsigned i = 0; i < 2; ++i) visible(ui.music_rings[i], !cover);
    if (cover) {
        lv_img_set_zoom(ui.music_art, (uint16_t)(lv_obj_get_width(ui.music_cover) * 256 / ui.art_frame.header.w));
        lv_obj_center(ui.music_art);
    }
    const char *status = !connected ? BT(BT_PC_OFFLINE) : !music->running ? BT(BT_NOT_RUNNING) :
                         !music->available ? BT(BT_WAITING_FOR_PLAYBACK) : music->playing ? BT(BT_PLAYING) : BT(BT_PAUSED);
    set_label_if_changed(ui.music_status, status);
    lv_obj_set_style_text_color(ui.music_status, color(!connected ? C_WARNING : live && music->playing ? accent : C_MUTED), 0);
    visible(ui.music_eq, live && music->playing);
    music_place_eq();
    const char *title = music->available ? (music->title[0] ? music->title : BT(BT_UNTITLED_TRACK)) :
        !connected ? BT(BT_WAITING_FOR_YOUR_PC) : music->running ? BT(BT_WAITING_FOR_PLAYBACK) : BT(BT_OPEN_SPOTIFY_ON_YOUR_PC);
    music_layout_meta(title);
    set_label_if_changed(ui.music_title, title);
    if (music->available) {
        set_label_if_changed(ui.music_artist, music->artist);
        set_label_if_changed(ui.music_album, music->album);
    } else {
        set_label_if_changed(ui.music_artist, !connected ? BT(BT_PLAYBACK_RETURNS_WHEN_YOUR_PC_RECONNECTS) :
            music->running ? BT(BT_START_A_TRACK_IN_SPOTIFY) : BT(BT_YOUR_MUSIC_WILL_APPEAR_HERE_AUTOMATICALLY));
        set_label_if_changed(ui.music_album, "");
    }
    lv_obj_set_style_text_color(ui.music_artist, color(music->available ? C_TEXT : C_MUTED), 0);

    char buffer[24];
    uint32_t duration = live ? music->duration_s : 0, position = music->position_s;
    if (duration && position > duration) position = duration;
    /* After a seek, show the chosen spot until Spotify reports it (or gives up). */
    if (ui.music_seek_hold) {
        uint32_t gap = position > ui.music_seek_target ? position - ui.music_seek_target : ui.music_seek_target - position;
        if (!live || gap <= 2 || lv_tick_elaps(ui.music_seek_tick) >= MUSIC_HOLD_MS) ui.music_seek_hold = false;
        else position = ui.music_seek_target;
    }
    if (!ui.music_seeking) {
        lv_slider_set_range(ui.music_progress, 0, duration ? (int32_t)duration : 1);
        lv_slider_set_value(ui.music_progress, duration ? (int32_t)position : 0, LV_ANIM_OFF);
        if (connected && music->available) playback_time(buffer, sizeof(buffer), position);
        else snprintf(buffer, sizeof(buffer), "--:--");
        set_label_if_changed(ui.music_elapsed, buffer);
    }
    if (duration) playback_time(buffer, sizeof(buffer), duration);
    else snprintf(buffer, sizeof(buffer), "--:--");
    set_label_if_changed(ui.music_duration, buffer);
    bool seekable = music_control_available(MUSIC_SEEK_CONTROL);
    if (seekable) lv_obj_add_flag(ui.music_progress, LV_OBJ_FLAG_CLICKABLE);
    else lv_obj_clear_flag(ui.music_progress, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_opa(ui.music_progress, seekable ? LV_OPA_COVER : LV_OPA_TRANSP, LV_PART_KNOB);

    set_label_if_changed(ui.music_button_labels[MUSIC_PLAY_BUTTON], live && music->playing ? LV_SYMBOL_PAUSE : LV_SYMBOL_PLAY);
    for (unsigned i = 0; i < MUSIC_BUTTON_COUNT; ++i) button_enabled(ui.music_buttons[i], music_control_available(i));
    visible(ui.music_buttons[MUSIC_SHUFFLE_BUTTON], live && music->can_shuffle);
    visible(ui.music_buttons[MUSIC_REPEAT_BUTTON], live && music->can_repeat);
    lv_obj_set_style_text_color(ui.music_button_labels[MUSIC_SHUFFLE_BUTTON], color(music->shuffle ? accent : C_MUTED), 0);
    lv_obj_set_style_text_color(ui.music_button_labels[MUSIC_REPEAT_BUTTON], color(music->repeat ? accent : C_MUTED), 0);
    visible(ui.music_toggle_dots[0], music->shuffle);
    visible(ui.music_toggle_dots[1], music->repeat != 0);
    visible(ui.music_repeat_one, music->repeat == 1);

    bool mixer = live && music->can_volume;
    visible(ui.music_buttons[MUSIC_MUTE_BUTTON], mixer);
    visible(ui.music_volume, mixer);
    visible(ui.music_volume_label, mixer);
    uint32_t volume = music->volume > 100 ? 100 : music->volume;
    if (ui.music_volume_hold) {
        if (!mixer || volume == ui.music_volume_target || lv_tick_elaps(ui.music_volume_tick) >= MUSIC_HOLD_MS) ui.music_volume_hold = false;
        else volume = ui.music_volume_target;
    }
    if (!ui.music_volume_dragging) {
        lv_slider_set_value(ui.music_volume, (int32_t)volume, LV_ANIM_OFF);
        music_volume_text(volume);
    }
    bool silent = music->muted || volume == 0;
    set_label_if_changed(ui.music_button_labels[MUSIC_MUTE_BUTTON], silent ? LV_SYMBOL_MUTE : volume < 50 ? LV_SYMBOL_VOLUME_MID : LV_SYMBOL_VOLUME_MAX);
    lv_obj_set_style_text_color(ui.music_button_labels[MUSIC_MUTE_BUTTON], color(silent ? C_MUTED : C_TEXT), 0);
    lv_obj_set_style_opa(ui.music_volume, music->muted ? LV_OPA_50 : LV_OPA_COVER, 0);
}

static void music_clicked(lv_event_t *event)
{
    unsigned control = (unsigned)(uintptr_t)lv_event_get_user_data(event);
    if (control >= MUSIC_BUTTON_COUNT || !music_can_control(control)) return;
    music_send(control, 0);
    refresh_music();
}

/* Sliders follow the finger locally and send one command on release. A vertical
 * swipe that scrolls a compact page instead cancels the gesture and restores the value. */
static void music_slider_event(lv_event_t *event)
{
    bool seek = lv_event_get_target(event) == ui.music_progress;
    bool *dragging = seek ? &ui.music_seeking : &ui.music_volume_dragging;
    lv_event_code_t code = lv_event_get_code(event);
    lv_indev_t *pointer = lv_indev_get_act();
    if (*dragging && pointer && lv_indev_get_scroll_obj(pointer)) {
        *dragging = false;
        refresh_music();
        return;
    }
    if (code == LV_EVENT_PRESSED) {
        *dragging = music_control_available(seek ? MUSIC_SEEK_CONTROL : MUSIC_VOLUME_CONTROL);
    } else if (code == LV_EVENT_VALUE_CHANGED && !*dragging) {
        refresh_music();
    } else if (code == LV_EVENT_VALUE_CHANGED) {
        uint32_t value = (uint32_t)lv_slider_get_value(lv_event_get_target(event));
        char buffer[24];
        if (seek) {
            playback_time(buffer, sizeof(buffer), value);
            set_label_if_changed(ui.music_elapsed, buffer);
        } else music_volume_text(value);
    } else if (code == LV_EVENT_PRESS_LOST && *dragging) {
        *dragging = false;
        refresh_music();
    } else if (code == LV_EVENT_RELEASED && *dragging) {
        *dragging = false;
        uint32_t value = (uint32_t)lv_slider_get_value(lv_event_get_target(event));
        if (seek) {
            ui.music_seek_hold = true;
            ui.music_seek_target = value;
            ui.music_seek_tick = lv_tick_get();
        } else {
            ui.music_volume_hold = true;
            ui.music_volume_target = value;
            ui.music_volume_tick = lv_tick_get();
        }
        music_send(seek ? MUSIC_SEEK_CONTROL : MUSIC_VOLUME_CONTROL, value);
        refresh_music();
    }
}

/* Wall-clock seconds, or 0 until SNTP has set a plausible date. */
static int64_t wall_clock(void)
{
    time_t now = time(NULL);
    return now >= 1700000000 ? (int64_t)now : 0;
}

static void duration_text(char *buffer, size_t size, int64_t seconds)
{
    uint64_t minutes = seconds > 0 ? ((uint64_t)seconds + 59U) / 60U : 0;
    if (minutes >= 1440U) snprintf(buffer, size, BT(BT_LU_D_LU_H), (unsigned long)(minutes / 1440U), (unsigned long)(minutes / 60U % 24U));
    else if (minutes >= 60U) snprintf(buffer, size, BT(BT_LU_H_LU_MIN), (unsigned long)(minutes / 60U), (unsigned long)(minutes % 60U));
    else snprintf(buffer, size, BT(BT_LU_MIN), (unsigned long)(minutes ? minutes : 1U));
}

static void hour_text(char *buffer, size_t size, const struct tm *local)
{
    char hour[24];
    strftime(hour, sizeof(hour), view_settings.use_24h ? "%H:%M" : "%I:%M", local);
    snprintf(buffer, size, "%s%s%s", hour, view_settings.use_24h ? "" : " ",
             view_settings.use_24h ? "" : BT(local->tm_hour < 12 ? BT_AM : BT_PM));
}

static void reset_stamp(char *buffer, size_t size, uint32_t resets_at)
{
    time_t timestamp = resets_at;
    struct tm local;
    localtime_r(&timestamp, &local);
    char date[96], hour[40];
    buddy_date(date, sizeof(date), &local, true);
    hour_text(hour, sizeof(hour), &local);
    snprintf(buffer, size, "%s %s", date, hour);
}

/* Projects the window's average use so far. False while it is too early (or the
 * clock is unknown) to say; *runs_out stays 0 when the rest lasts until reset. */
static bool usage_pace(const buddy_codex_window_t *window, int64_t now, int64_t *runs_out)
{
    *runs_out = 0;
    if (!now || !window->resets_at || !window->window_minutes || (int64_t)window->resets_at <= now) return false;
    int64_t length = (int64_t)window->window_minutes * 60, left = (int64_t)window->resets_at - now;
    if (left > length || length - left < length / 10) return false;
    double used = 100.0 - window->remaining_percent;
    if (used <= 0) return true;
    double lasts = window->remaining_percent / (used / (double)(length - left));
    if ((double)now + lasts < (double)window->resets_at) *runs_out = now + (int64_t)lasts;
    return true;
}

/* A 270-degree gauge, open at the bottom and filled clockwise from the lower left. */
#define GAUGE_START 135
#define GAUGE_SWEEP 270

static lv_obj_t *usage_gauge(lv_obj_t *parent)
{
    lv_obj_t *arc = lv_arc_create(parent);
    lv_obj_remove_style_all(arc);
    lv_arc_set_bg_angles(arc, GAUGE_START, (GAUGE_START + GAUGE_SWEEP) % 360);
    lv_arc_set_range(arc, 0, 1000);
    lv_obj_set_style_arc_color(arc, color(C_RULE), LV_PART_MAIN);
    lv_obj_set_style_arc_rounded(arc, true, LV_PART_MAIN);
    lv_obj_set_style_arc_rounded(arc, true, LV_PART_INDICATOR);
    lv_obj_clear_flag(arc, LV_OBJ_FLAG_CLICKABLE);
    return arc;
}

static lv_obj_t *surface_card(lv_obj_t *parent, int radius)
{
    lv_obj_t *card = box(parent, 0, 0, 10, 10);
    lv_obj_set_style_bg_color(card, color(BUDDY_COLOR_SURFACE), 0);
    lv_obj_set_style_bg_opa(card, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(card, radius, 0);
    /* Outlined themes draw the same card as a hairline on the page. */
    if (buddy_theme.outlined_cards) {
        lv_obj_set_style_bg_opa(card, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(card, 1, 0);
        lv_obj_set_style_border_color(card, color(C_RULE), 0);
    }
    return card;
}

/* Lays out one usage card: gauge on the left, name, countdown, reset time and pace beside it. */
static void agent_card_layout(agent_view_t *view, unsigned i, int width, int height, int gauge,
                              const lv_font_t *percent_font, const lv_font_t *name_font,
                              const lv_font_t *countdown_font, const lv_font_t *small_font)
{
    lv_obj_set_size(view->cards[i], width, height);
    int pad = (height - gauge) / 2, arc = gauge / buddy_theme.gauge_div;
    lv_obj_set_pos(view->gauges[i], pad, pad);
    lv_obj_set_size(view->gauges[i], gauge, gauge);
    lv_obj_set_style_arc_width(view->gauges[i], arc, LV_PART_MAIN);
    lv_obj_set_style_arc_width(view->gauges[i], arc, LV_PART_INDICATOR);
    lv_obj_set_style_text_font(view->remaining[i], percent_font, 0);
    lv_obj_set_style_text_font(view->remaining_captions[i], &buddy_language_font_14, 0);
    /* Smaller gauges have no room for the caption inside the ring; the card says "limit". */
    bool caption = gauge >= 100;
    visible(view->remaining_captions[i], caption);
    lv_obj_align(view->remaining[i], LV_ALIGN_CENTER, 0, caption ? -lv_font_get_line_height(&buddy_language_font_14) / 2 : 0);
    lv_obj_align(view->remaining_captions[i], LV_ALIGN_CENTER, 0, lv_font_get_line_height(percent_font) / 2);
    view->gauge_size = gauge;
    lv_obj_t *lines[] = { view->window_names[i], view->countdowns[i], view->resets[i], view->paces[i] };
    const lv_font_t *fonts[] = { name_font, countdown_font, small_font, small_font };
    int text_x = pad + gauge + 18, total = -4;
    for (unsigned k = 0; k < 4; ++k) total += lv_font_get_line_height(fonts[k]) + 4;
    int y = (height - total) / 2;
    for (unsigned k = 0; k < 4; ++k) {
        int line = lv_font_get_line_height(fonts[k]);
        lv_obj_set_pos(lines[k], text_x, y);
        lv_obj_set_size(lines[k], width - text_x - 14, line);
        lv_obj_set_style_text_font(lines[k], fonts[k], 0);
        y += line + 4;
    }
}

/* Task rows: status dot, title, "project · age" and a state chip, either beside
 * the title or, on the narrowest screens, at the end of the detail line. */
static void agent_task_layout(agent_view_t *view, int width, int height, int step,
                              const lv_font_t *title_font, const lv_font_t *detail_font, bool chip_on_details)
{
    int title_h = lv_font_get_line_height(title_font), detail_h = lv_font_get_line_height(detail_font);
    int chip_h = lv_font_get_line_height(&buddy_language_font_14) + 6, y = (height - title_h - 4 - detail_h) / 2;
    view->chip_on_details = chip_on_details;
    for (unsigned i = 0; i < 3; ++i) {
        lv_obj_set_pos(view->task_rows[i], 0, (int)i * step);
        lv_obj_set_size(view->task_rows[i], width, height);
        lv_obj_set_pos(view->task_dots[i], 16, y + (title_h - 10) / 2);
        lv_obj_set_pos(view->task_titles[i], 36, y);
        lv_obj_set_size(view->task_titles[i], width - 52 - (chip_on_details ? 0 : 150), title_h);
        lv_obj_set_style_text_font(view->task_titles[i], title_font, 0);
        lv_obj_set_pos(view->task_details[i], 36, y + title_h + 4);
        lv_obj_set_size(view->task_details[i], width - 52 - (chip_on_details ? 130 : 0), detail_h);
        lv_obj_set_style_text_font(view->task_details[i], detail_font, 0);
        int chip_line = chip_on_details ? y + title_h + 4 : y, chip_line_h = chip_on_details ? detail_h : title_h;
        lv_obj_align(view->task_states[i], LV_ALIGN_TOP_RIGHT, -14, chip_line + (chip_line_h - chip_h) / 2);
    }
}

static void refresh_agent_tasks(agent_view_t *view)
{
    const buddy_codex_activity_t *activity = &view->data->activity;
    bool available = agent_tasks_available(view->data), stale = agent_tasks_stale(view->data);
    unsigned count = available ? activity->item_count : 0;
    if (count > 3) count = 3;
    char buffer[160], age[40];
    for (unsigned i = 0; i < 3; ++i) {
        view->working[i] = false;
        visible(view->task_rows[i], i < count);
        if (i >= count) continue;
        const buddy_codex_task_t *task = &activity->items[i];
        set_label_if_changed(view->task_titles[i], task->title[0] ? task->title : BT(BT_UNTITLED_TASK));
        bool recent_working = strcmp(task->state, "working") == 0 &&
            (uint64_t)task->age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U <= 120U;
        bool attention = strcmp(task->state, "waiting") == 0 || strcmp(task->state, "interrupted") == 0;
        const char *state = recent_working ? (stale ? BT(BT_WAS_WORKING) : BT(BT_RECENTLY_WORKING)) :
            strcmp(task->state, "waiting") == 0 ? BT(BT_WAITING_FOR_YOU) :
            strcmp(task->state, "completed") == 0 ? BT(BT_COMPLETED) :
            strcmp(task->state, "interrupted") == 0 ? BT(BT_INTERRUPTED) :
            strcmp(task->state, "recent") == 0 ? BT(BT_RECENT) : BT(BT_UNKNOWN);
        /* Working takes the product accent, anything that needs you is amber, the rest stays quiet. */
        uint32_t tone = recent_working && !stale ? view->copy->accent : attention && !stale ? C_WARNING : C_MUTED;
        view->working[i] = recent_working && !stale;
        set_label_if_changed(view->task_states[i], state);
        /* The text beside the chip gets whatever width the chip leaves free. */
        lv_coord_t chip = lv_txt_get_width(state, (uint32_t)strlen(state), &buddy_language_font_14, 0, LV_TEXT_FLAG_NONE) + 20;
        lv_obj_t *beside = view->chip_on_details ? view->task_details[i] : view->task_titles[i];
        lv_obj_set_width(beside, lv_obj_get_width(view->task_rows[i]) - 36 - 14 - chip - 12);
        lv_obj_set_style_text_color(view->task_states[i], color(tone), 0);
        lv_obj_set_style_bg_color(view->task_states[i], lv_color_mix(color(tone), color(BUDDY_COLOR_SURFACE), 44), 0);
        lv_obj_set_style_bg_color(view->task_dots[i], color(tone), 0);
        if (!view->working[i]) lv_obj_set_style_bg_opa(view->task_dots[i], LV_OPA_COVER, 0);
        if (strcmp(task->state, "unknown") == 0 && task->age_s >= 31536000U) snprintf(age, sizeof(age), BT(BT_ACTIVITY_TIME_UNKNOWN));
        else activity_age_text(age, sizeof(age), task->age_s);
        snprintf(buffer, sizeof(buffer), "%s \xC2\xB7 %s", task->project[0] ? task->project : BT(BT_NO_PROJECT_RECORDED), age);
        set_label_if_changed(view->task_details[i], buffer);
    }
    visible(view->tasks_empty_title, !count);
    visible(view->tasks_empty_body, !count);
    if (!count) {
        const char *title = available ? BT(BT_NO_RECENT_TASKS_RECORDED) : BT(BT_WAITING_FOR_TASK_ACTIVITY);
        const char *body = available ? BT(BT_THIS_LOCAL_SNAPSHOT_CONTAINS_NO_RECENT_TASK_ENTRIES) :
            BT(BT_RECENT_TASK_RECORDS_WILL_APPEAR_WHEN_THE_PC_CAN);
        if (!available && !music_connected()) {
            title = BT(BT_WAITING_FOR_YOUR_PC);
            body = BT(BT_KEEP_CLAWPAL_RUNNING_IN_YOUR_PC_S_TRAY_RECORDED);
        } else if (!available && (strcmp(activity->status, "closed") == 0 || !view->data->running)) {
            title = BT(view->copy->open_on_pc);
            body = BT(BT_BUDDY_WILL_SHOW_RECENT_LOCAL_TASK_RECORDS_WHEN_THEY);
        } else if (!available && (strcmp(activity->status, "error") == 0 || strcmp(activity->status, "unavailable") == 0)) {
            title = BT(BT_TASK_ACTIVITY_UNAVAILABLE);
            body = BT(BT_THE_PC_COULD_NOT_READ_LOCAL_TASK_RECORDS_BUDDY);
        }
        set_label_if_changed(view->tasks_empty_title, title);
        set_label_if_changed(view->tasks_empty_body, body);
    }
    if (!view->tasks_selected) return;
    if (!available) snprintf(buffer, sizeof(buffer), BT(BT_RECORDED_ACTIVITY));
    else if (stale) snprintf(buffer, sizeof(buffer), BT(BT_CACHED_LU_RECORDED_TASKS), (unsigned long)activity->total_count);
    else snprintf(buffer, sizeof(buffer), BT(BT_LU_RECENTLY_WORKING_LU_RECORDED),
        (unsigned long)activity->working_count, (unsigned long)activity->total_count);
    set_label_if_changed(view->plan, buffer);
    const char *status = !available ? BT(BT_ACTIVITY_UNAVAILABLE) : stale ? BT(BT_CACHED_ACTIVITY) :
        strcmp(activity->status, "partial") == 0 ? BT(BT_PARTIAL_SNAPSHOT) : BT(BT_LOCAL_SNAPSHOT);
    set_label_if_changed(view->status, status);
    lv_obj_set_style_text_color(view->status, color(stale || !available ? C_WARNING : C_MUTED), 0);
    if (available) {
        activity_age_text(age, sizeof(age), activity->age_s);
        snprintf(buffer, sizeof(buffer), BT(BT_S_S_RECENT_LOCAL_RECORDS_ONLY), stale ? BT(BT_CACHED_SNAPSHOT) : BT(BT_SNAPSHOT), age);
    } else snprintf(buffer, sizeof(buffer), BT(BT_NO_LOCAL_TASK_SNAPSHOT_AVAILABLE));
    set_label_if_changed(view->age, buffer);
}

/* The marker sits on the gauge where usage would be if spread evenly over the window. */
static void place_time_mark(agent_view_t *view, unsigned i, const buddy_codex_window_t *window, int64_t now)
{
    int64_t length = (int64_t)window->window_minutes * 60, left = (int64_t)window->resets_at - now;
    bool known = now && window->resets_at && length > 0 && left > 0 && left <= length;
    visible(view->time_marks[i], known);
    if (!known) return;
    int size = view->gauge_size, radius = size / 2 - size / 20;
    int16_t angle = (int16_t)(GAUGE_START + GAUGE_SWEEP * left / length);
    lv_obj_set_pos(view->time_marks[i], size / 2 + radius * lv_trigo_cos(angle) / LV_TRIGO_SIN_MAX - 6,
                   size / 2 + radius * lv_trigo_sin(angle) / LV_TRIGO_SIN_MAX - 6);
}

static void refresh_agent(agent_view_t *view)
{
    if (!view->page) return;
    const buddy_codex_t *data = view->data;
    const agent_copy_t *copy = view->copy;
    bool available = agent_has_windows(data), stale = agent_stale(data);
    if (!view->tab_initialized && !view->tab_manual && agent_tasks_available(data)) {
        view->tasks_selected = data->activity.item_count > 0 && data->activity.working_count > 0 && !agent_tasks_stale(data);
        view->tab_initialized = true;
    }
    set_label_if_changed(view->status, agent_status_text(data, copy));
    lv_obj_set_style_text_color(view->status, color(stale || !available ? C_WARNING : copy->accent), 0);
    char buffer[160], when[96];
    if (data->plan[0]) snprintf(buffer, sizeof(buffer), BT(BT_S_PLAN), data->plan);
    else snprintf(buffer, sizeof(buffer), BT(BT_ACCOUNT_USAGE));
    set_label_if_changed(view->plan, buffer);
    const buddy_codex_window_t *windows[2];
    unsigned count = 0;
    if (available && usage_window_available(&data->primary)) windows[count++] = &data->primary;
    if (available && usage_window_available(&data->secondary)) windows[count++] = &data->secondary;
    int64_t now = wall_clock();
    for (unsigned i = 0; i < 2; ++i) {
        visible(view->cards[i], i < count);
        if (i >= count) continue;
        const buddy_codex_window_t *window = windows[i];
        lv_obj_set_y(view->cards[i], count == 1 ? view->card_single_top : view->card_top + (int)i * view->card_step);
        usage_window_name(buffer, sizeof(buffer), window->window_minutes);
        set_label_if_changed(view->window_names[i], buffer);
        snprintf(buffer, sizeof(buffer), window->remaining_percent < 10.f ? "%.1f%%" : "%.0f%%", window->remaining_percent);
        set_label_if_changed(view->remaining[i], buffer);
        uint32_t level = stale ? C_MUTED : window->remaining_percent < 20.f ? C_WARNING : copy->accent;
        lv_obj_set_style_text_color(view->remaining[i], color(stale ? C_MUTED : C_TEXT), 0);
        lv_obj_set_style_arc_color(view->gauges[i], color(level), LV_PART_INDICATOR);
        lv_arc_set_value(view->gauges[i], (int16_t)lroundf(window->remaining_percent * 10.f));
        place_time_mark(view, i, window, now);
        if (window->resets_at && now && (int64_t)window->resets_at > now) {
            duration_text(when, sizeof(when), (int64_t)window->resets_at - now);
            snprintf(buffer, sizeof(buffer), BT(BT_RESETS_IN_S), when);
            set_label_if_changed(view->countdowns[i], buffer);
            reset_stamp(buffer, sizeof(buffer), window->resets_at);
            set_label_if_changed(view->resets[i], buffer);
        } else {
            if (window->resets_at) {
                reset_stamp(when, sizeof(when), window->resets_at);
                snprintf(buffer, sizeof(buffer), BT(BT_RESETS_VALUE), when);
            } else if (window->reset_text[0]) snprintf(buffer, sizeof(buffer), BT(BT_RESETS_VALUE), window->reset_text);
            else snprintf(buffer, sizeof(buffer), BT(BT_RESET_TIME_UNAVAILABLE));
            set_label_if_changed(view->countdowns[i], buffer);
            set_label_if_changed(view->resets[i], "");
        }
        int64_t runs_out;
        bool pace = !stale && usage_pace(window, now, &runs_out);
        if (window->remaining_percent <= 0.f) {
            set_label_if_changed(view->paces[i], BT(BT_LIMIT_REACHED));
            lv_obj_set_style_text_color(view->paces[i], color(C_WARNING), 0);
        } else if (pace && runs_out) {
            time_t timestamp = (time_t)runs_out;
            struct tm local;
            localtime_r(&timestamp, &local);
            hour_text(when, sizeof(when), &local);
            snprintf(buffer, sizeof(buffer), BT(BT_RUNS_OUT_AROUND_S), when);
            set_label_if_changed(view->paces[i], buffer);
            lv_obj_set_style_text_color(view->paces[i], color(C_WARNING), 0);
        } else {
            set_label_if_changed(view->paces[i], pace ? BT(BT_LASTS_UNTIL_RESET) : "");
            lv_obj_set_style_text_color(view->paces[i], color(copy->accent), 0);
        }
    }
    visible(view->empty_title, !available);
    visible(view->empty_body, !available);
    if (available) {
        uint64_t age = (uint64_t)data->age_s + lv_tick_elaps(ui.last_stats_tick) / 1000U;
        const char *label = stale ? BT(BT_CACHED_UPDATED) : BT(BT_UPDATED);
        if (age < 60) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_S_AGO), label, (unsigned long)age);
        else if (age < 3600) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_MIN_AGO), label, (unsigned long)(age / 60));
        else if (age < 86400) snprintf(buffer, sizeof(buffer), BT(BT_S_LU_H_AGO), label, (unsigned long)(age / 3600));
        else snprintf(buffer, sizeof(buffer), BT(BT_S_LU_D_AGO), label, (unsigned long)(age / 86400));
        set_label_if_changed(view->age, buffer);
    } else {
        set_label_if_changed(view->empty_title, agent_status_text(data, copy));
        const char *body = BT(copy->open_body);
        if (!music_connected()) body = BT(BT_KEEP_CLAWPAL_RUNNING_IN_YOUR_PC_S_TRAY_LIMITS);
        else if (strcmp(data->status, "sign_in") == 0 || strcmp(data->status, "signed_out") == 0 ||
                 strcmp(data->status, "unauthorized") == 0) body = BT(copy->sign_in_body);
        else if (strcmp(data->status, "runtime_missing") == 0) body = BT(copy->update_body);
        else if (strcmp(data->status, "no_limits") == 0) body = BT(copy->no_limits_body);
        else if (strcmp(data->status, "error") == 0 || strcmp(data->status, "failed") == 0)
            body = BT(BT_THE_PC_COULD_NOT_REFRESH_YOUR_ACCOUNT_LIMITS_BUDDY);
        else if (strcmp(data->status, "unavailable") == 0 && data->running)
            body = BT(BT_YOUR_ACCOUNT_LIMITS_ARE_NOT_AVAILABLE_YET_BUDDY_WILL);
        else if (data->running) body = BT(copy->reading_body);
        set_label_if_changed(view->empty_body, body);
        set_label_if_changed(view->age, BT(BT_NO_ACCOUNT_LIMITS_AVAILABLE));
    }
    refresh_agent_tasks(view);
    visible(view->usage_body, !view->tasks_selected);
    visible(view->tasks_body, view->tasks_selected);
    for (unsigned i = 0; i < 2; ++i) {
        bool selected = (i == 1) == view->tasks_selected;
        lv_obj_set_style_bg_opa(view->tabs[i], selected ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
        lv_obj_set_style_text_color(view->tab_labels[i], color(selected ? copy->accent : C_MUTED), 0);
    }
    refresh_app_cards();
}

static void refresh_codex(void) { refresh_agent(&ui.codex); }
static void refresh_claude(void) { refresh_agent(&ui.claude); }

static void agent_tab_clicked(lv_event_t *event)
{
    agent_view_t *view = lv_event_get_user_data(event);
    bool tasks = (uintptr_t)lv_obj_get_user_data(lv_event_get_current_target(event)) == 1;
    view->tab_manual = true;
    view->tab_initialized = true;
    if (tasks == view->tasks_selected) return;
    view->tasks_selected = tasks;
    refresh_agent(view);
    refresh_footer();
    buddy_ui_motion_enter(tasks ? view->tasks_body : view->usage_body, 6, 180, 0);
}

static void create_agent_page(agent_view_t *view, const buddy_codex_t *data, const agent_copy_t *copy)
{
    view->data = data;
    view->copy = copy;
    view->page = box(ui.screen, 0, 0, 800, 440);
    view->logo = lv_img_create(view->page);
    lv_img_set_src(view->logo, copy->logo);
    lv_img_set_zoom(view->logo, 192);
    lv_obj_set_pos(view->logo, 21, 14);
    lv_obj_clear_flag(view->logo, LV_OBJ_FLAG_CLICKABLE);
    view->heading = text(view->page, BT(copy->title), 84, 18, 420, &buddy_language_font_24, C_TEXT);
    view->plan = text(view->page, BT(BT_ACCOUNT_USAGE), 84, 52, 420, &buddy_language_font_16, C_MUTED);
    /* Segmented Usage / Tasks switch. */
    view->tab_track = surface_card(view->page, LV_RADIUS_CIRCLE);
    lv_obj_set_pos(view->tab_track, 532, 20);
    lv_obj_set_size(view->tab_track, 240, 46);
    for (unsigned i = 0; i < 2; ++i) {
        view->tabs[i] = box(view->tab_track, 4 + (int)i * 116, 4, 116, 38);
        lv_obj_add_flag(view->tabs[i], LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_radius(view->tabs[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_bg_color(view->tabs[i], color(C_RULE), 0);
        lv_obj_set_style_bg_color(view->tabs[i], color(C_PRESSED), LV_STATE_PRESSED);
        lv_obj_set_style_bg_opa(view->tabs[i], LV_OPA_COVER, LV_STATE_PRESSED);
        view->tab_labels[i] = text(view->tabs[i], i ? BT(BT_TASKS) : BT(BT_USAGE), 0, 0, 108, &buddy_language_font_16, C_MUTED);
        lv_obj_set_style_text_align(view->tab_labels[i], LV_TEXT_ALIGN_CENTER, 0);
        lv_obj_align(view->tab_labels[i], LV_ALIGN_CENTER, 0, 0);
        lv_obj_set_user_data(view->tabs[i], (void *)(uintptr_t)i);
        lv_obj_add_event_cb(view->tabs[i], agent_tab_clicked, LV_EVENT_CLICKED, view);
    }
    rule(view->page, 28, 84, 744, 1);

    /* The crab, the account status and its freshness share the right column. */
    view->mascot = lv_img_create(view->page);
    lv_obj_set_pos(view->mascot, 636, 116);
    lv_img_set_antialias(view->mascot, false);
    lv_obj_clear_flag(view->mascot, LV_OBJ_FLAG_CLICKABLE);
    view->status = text(view->page, BT(BT_WAITING_FOR_PC), 600, 228, 172, &buddy_language_font_16, C_WARNING);
    lv_label_set_long_mode(view->status, LV_LABEL_LONG_WRAP);
    lv_obj_set_height(view->status, 48);
    lv_obj_set_style_text_align(view->status, LV_TEXT_ALIGN_CENTER, 0);
    view->age = text(view->page, BT(BT_NO_ACCOUNT_LIMITS_AVAILABLE), 600, 280, 172, &buddy_language_font_14, C_MUTED);
    lv_label_set_long_mode(view->age, LV_LABEL_LONG_WRAP);
    lv_obj_set_height(view->age, 64);
    lv_obj_set_style_text_align(view->age, LV_TEXT_ALIGN_CENTER, 0);

    view->usage_body = box(view->page, 28, 100, 548, 300);
    for (unsigned i = 0; i < 2; ++i) {
        view->cards[i] = surface_card(view->usage_body, 16);
        view->gauges[i] = usage_gauge(view->cards[i]);
        view->remaining[i] = lv_label_create(view->gauges[i]);
        lv_label_set_text(view->remaining[i], "--");
        lv_obj_set_style_text_color(view->remaining[i], color(C_TEXT), 0);
        view->remaining_captions[i] = lv_label_create(view->gauges[i]);
        lv_label_set_text(view->remaining_captions[i], BT(BT_GAUGE_LEFT));
        lv_obj_set_style_text_color(view->remaining_captions[i], color(C_MUTED), 0);
        view->time_marks[i] = box(view->gauges[i], 0, 0, 12, 12);
        lv_obj_set_style_radius(view->time_marks[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_bg_color(view->time_marks[i], color(C_TEXT), 0);
        lv_obj_set_style_bg_opa(view->time_marks[i], LV_OPA_COVER, 0);
        lv_obj_set_style_border_color(view->time_marks[i], color(BUDDY_COLOR_SURFACE), 0);
        lv_obj_set_style_border_width(view->time_marks[i], 3, 0);
        view->window_names[i] = text(view->cards[i], BT(BT_USAGE_LIMIT), 0, 0, 100, &buddy_language_font_16, C_MUTED);
        view->countdowns[i] = text(view->cards[i], "", 0, 0, 100, &buddy_language_font_24, C_TEXT);
        view->resets[i] = text(view->cards[i], "", 0, 0, 100, &buddy_language_font_16, C_MUTED);
        view->paces[i] = text(view->cards[i], "", 0, 0, 100, &buddy_language_font_16, copy->accent);
        agent_card_layout(view, i, 548, 140, 116, &buddy_language_font_32, &buddy_language_font_16,
                          &buddy_language_font_24, &buddy_language_font_16);
    }
    view->card_top = 0;
    view->card_step = 152;
    view->card_single_top = 76;
    view->empty_title = text(view->usage_body, BT(BT_WAITING_FOR_PC), 0, 40, 548, &buddy_language_font_32, C_TEXT);
    lv_obj_set_height(view->empty_title, 84);
    view->empty_body = text(view->usage_body, "", 0, 140, 548, &buddy_language_font_20, C_MUTED);
    lv_obj_set_height(view->empty_body, 120);
    lv_label_set_long_mode(view->empty_body, LV_LABEL_LONG_WRAP);

    view->tasks_body = box(view->page, 28, 100, 548, 300);
    for (unsigned i = 0; i < 3; ++i) {
        view->task_rows[i] = surface_card(view->tasks_body, 14);
        view->task_dots[i] = box(view->task_rows[i], 0, 0, 10, 10);
        lv_obj_set_style_radius(view->task_dots[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_bg_opa(view->task_dots[i], LV_OPA_COVER, 0);
        view->task_titles[i] = text(view->task_rows[i], "", 0, 0, 100, &buddy_language_font_20, C_TEXT);
        view->task_details[i] = text(view->task_rows[i], "", 0, 0, 100, &buddy_language_font_16, C_MUTED);
        view->task_states[i] = lv_label_create(view->task_rows[i]);
        lv_obj_set_style_text_font(view->task_states[i], &buddy_language_font_14, 0);
        lv_obj_set_style_bg_opa(view->task_states[i], LV_OPA_COVER, 0);
        lv_obj_set_style_radius(view->task_states[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_pad_hor(view->task_states[i], 10, 0);
        lv_obj_set_style_pad_ver(view->task_states[i], 3, 0);
        lv_label_set_text(view->task_states[i], "");
    }
    agent_task_layout(view, 548, 90, 102, &buddy_language_font_20, &buddy_language_font_16, false);
    view->tasks_empty_title = text(view->tasks_body, BT(BT_WAITING_FOR_TASK_ACTIVITY), 0, 40, 548, &buddy_language_font_32, C_TEXT);
    lv_obj_set_height(view->tasks_empty_title, 84);
    view->tasks_empty_body = text(view->tasks_body, "", 0, 140, 548, &buddy_language_font_20, C_MUTED);
    lv_label_set_long_mode(view->tasks_empty_body, LV_LABEL_LONG_WRAP);
    lv_obj_set_height(view->tasks_empty_body, 120);
    refresh_agent(view);
}

static void create_codex_page(void) { create_agent_page(&ui.codex, &ui.stats.codex, &codex_copy); }
static void create_claude_page(void) { create_agent_page(&ui.claude, &ui.stats.claude, &claude_copy); }

/* Quick durations, including a Pomodoro-length focus block. */
static const uint32_t timer_preset_minutes[4] = { 1U, 5U, 15U, 25U };

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
    lv_obj_set_style_text_color(ui.timer_status, color(ui.timer_completed ? C_WARNING : ui.timer_running ? C_ACCENT : C_MUTED), 0);
    lv_label_set_text(ui.timer_button_labels[0], ui.timer_running ? BT(BT_PAUSE) :
        ui.timer_completed ? BT(BT_START_AGAIN) : ui.timer_started ? BT(BT_RESUME) : BT(BT_START));
    button_enabled(ui.timer_buttons[0], ui.timer_duration_s > 0);
    /* The ring drains with the time left; a finished timer glows full in amber. */
    uint32_t duration = timer_duration_ms();
    uint32_t remaining = ui.timer_remaining_ms < duration ? ui.timer_remaining_ms : duration;
    lv_arc_set_value(ui.timer_ring, ui.timer_completed ? 1000 : duration ? (int16_t)((uint64_t)remaining * 1000U / duration) : 0);
    lv_obj_set_style_arc_color(ui.timer_ring, ui.timer_completed ? color(C_WARNING) : ui.timer_started ? color(C_ACCENT) :
        lv_color_mix(color(C_ACCENT), color(C_BG), 110), LV_PART_INDICATOR);
    int64_t now = wall_clock();
    if (ui.timer_running && now) {
        time_t ends = (time_t)(now + (seconds ? seconds : 0));
        struct tm local;
        localtime_r(&ends, &local);
        char hour[40];
        hour_text(hour, sizeof(hour), &local);
        snprintf(buffer, sizeof(buffer), BT(BT_ENDS_AT_S), hour);
        set_label_if_changed(ui.timer_end, buffer);
    } else set_label_if_changed(ui.timer_end, ui.timer_completed ? BT(BT_TIME_S_UP) : "");
    lv_obj_set_style_text_color(ui.timer_end, color(ui.timer_completed ? C_WARNING : C_MUTED), 0);
    const uint32_t units[] = { ui.timer_duration_s / 3600U, ui.timer_duration_s / 60U % 60U,
                               ui.timer_duration_s % 60U };
    const uint32_t limits[] = { 23U, 59U, 59U };
    bool editable = !ui.timer_started && !ui.timer_running;
    for (unsigned i = 0; i < 3; ++i) {
        snprintf(buffer, sizeof(buffer), "%02lu", (unsigned long)units[i]);
        lv_label_set_text(ui.timer_units[i], buffer);
        button_enabled(ui.timer_adjust[i * 2], editable && units[i] > 0);
        button_enabled(ui.timer_adjust[i * 2 + 1], editable && units[i] < limits[i]);
    }
    for (unsigned i = 0; i < 4; ++i) {
        bool selected = ui.timer_duration_s == timer_preset_minutes[i] * 60U;
        button_enabled(ui.timer_presets[i], editable);
        lv_obj_set_style_bg_color(ui.timer_presets[i], color(selected ? C_RULE : BUDDY_COLOR_SURFACE), 0);
        lv_obj_set_style_text_color(ui.timer_preset_labels[i], color(selected ? C_ACCENT : C_TEXT), 0);
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

static void timer_preset_clicked(lv_event_t *event)
{
    unsigned preset = (unsigned)(uintptr_t)lv_event_get_user_data(event);
    if (ui.timer_running || ui.timer_started || preset >= 4) return;
    ui.timer_duration_s = timer_preset_minutes[preset] * 60U;
    ui.timer_remaining_ms = timer_duration_ms();
    ui.timer_completed = false;
    ui.timer_celebrating = false;
    ui.timer_notify = false;
    refresh_timer();
    refresh_footer();
}

/* Round, label-less transport control; the symbol comes from the built-in icon fallback. */
static lv_obj_t *icon_button(lv_obj_t *parent, const char *symbol, unsigned control)
{
    lv_obj_t *button = box(parent, 0, 0, 48, 48);
    lv_obj_add_flag(button, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_ext_click_area(button, 6);
    lv_obj_set_style_radius(button, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(button, color(C_PRESSED), LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(button, LV_OPA_COVER, LV_STATE_PRESSED);
    lv_obj_t *icon = lv_label_create(button);
    lv_obj_set_style_text_color(icon, color(C_TEXT), 0);
    lv_label_set_text(icon, symbol);
    lv_obj_center(icon);
    lv_obj_clear_flag(icon, LV_OBJ_FLAG_CLICKABLE);
    ui.music_button_labels[control] = icon;
    lv_obj_add_event_cb(button, music_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)control);
    return button;
}

static lv_obj_t *music_slider(lv_obj_t *parent, int32_t maximum)
{
    lv_obj_t *slider = lv_slider_create(parent);
    lv_obj_remove_style_all(slider);
    lv_slider_set_range(slider, 0, maximum);
    lv_obj_set_style_radius(slider, LV_RADIUS_CIRCLE, LV_PART_MAIN);
    lv_obj_set_style_bg_color(slider, color(C_RULE), LV_PART_MAIN);
    lv_obj_set_style_bg_opa(slider, LV_OPA_COVER, LV_PART_MAIN);
    lv_obj_set_style_radius(slider, LV_RADIUS_CIRCLE, LV_PART_INDICATOR);
    lv_obj_set_style_bg_color(slider, color(C_ACCENT), LV_PART_INDICATOR);
    lv_obj_set_style_bg_opa(slider, LV_OPA_COVER, LV_PART_INDICATOR);
    lv_obj_set_style_radius(slider, LV_RADIUS_CIRCLE, LV_PART_KNOB);
    lv_obj_set_style_bg_color(slider, color(C_TEXT), LV_PART_KNOB);
    lv_obj_set_style_bg_opa(slider, LV_OPA_COVER, LV_PART_KNOB);
    lv_obj_set_style_pad_all(slider, 6, LV_PART_KNOB);
    lv_obj_set_style_pad_all(slider, 8, LV_PART_KNOB | LV_STATE_PRESSED);
    /* A thin track still needs a finger-sized target, and vertical swipes still
     * scroll the compact layouts instead of being captured by the slider. */
    lv_obj_set_ext_click_area(slider, 18);
    lv_obj_add_flag(slider, LV_OBJ_FLAG_SCROLL_CHAIN_VER);
    lv_obj_add_event_cb(slider, music_slider_event, LV_EVENT_ALL, NULL);
    return slider;
}

static void music_cover_layout(int x, int y, int size)
{
    lv_obj_set_pos(ui.music_cover, x, y);
    lv_obj_set_size(ui.music_cover, size, size);
    for (unsigned i = 0; i < 2; ++i) {
        int ring = size * (i ? 60 : 84) / 100;
        lv_obj_set_size(ui.music_rings[i], ring, ring);
        lv_obj_center(ui.music_rings[i]);
    }
}

/* Places the transport row around a centre point, with offsets for the skip
 * and toggle buttons; the compact layouts reuse it with smaller sizes. */
static void music_transport_layout(int cx, int cy, int play, int skip, int toggle, int inner, int outer,
                                   const lv_font_t *play_font, const lv_font_t *skip_font, const lv_font_t *toggle_font)
{
    const int offsets[] = { -outer, -inner, 0, inner, outer };
    const int sizes[] = { toggle, skip, play, skip, toggle };
    const lv_font_t *fonts[] = { toggle_font, skip_font, play_font, skip_font, toggle_font };
    for (unsigned i = 0; i <= MUSIC_REPEAT_BUTTON; ++i) {
        lv_obj_set_pos(ui.music_buttons[i], cx + offsets[i] - sizes[i] / 2, cy - sizes[i] / 2);
        lv_obj_set_size(ui.music_buttons[i], sizes[i], sizes[i]);
        lv_obj_set_style_text_font(ui.music_button_labels[i], fonts[i], 0);
    }
    for (unsigned i = 0; i < 2; ++i) lv_obj_set_pos(ui.music_toggle_dots[i], toggle / 2 - 3, toggle - 9);
    lv_obj_set_pos(ui.music_repeat_one, toggle / 2 + 5, toggle / 2 - 19);
}

static void music_volume_layout(int x, int cy, int width, const lv_font_t *font)
{
    lv_obj_set_pos(ui.music_buttons[MUSIC_MUTE_BUTTON], x, cy - 22);
    lv_obj_set_size(ui.music_buttons[MUSIC_MUTE_BUTTON], 44, 44);
    lv_obj_set_style_text_font(ui.music_button_labels[MUSIC_MUTE_BUTTON], font, 0);
    lv_obj_set_pos(ui.music_volume, x + 54, cy - 3);
    lv_obj_set_size(ui.music_volume, width - 54 - 52, 6);
    lv_obj_set_pos(ui.music_volume_label, x + width - 44, cy - 9);
    lv_obj_set_size(ui.music_volume_label, 44, 20);
}

static void create_music_page(void)
{
    ui.music_page = box(ui.screen, 0, 0, 800, 440);
    ui.music_logo = lv_img_create(ui.music_page);
    lv_img_set_src(ui.music_logo, &buddy_logo_spotify);
    lv_img_set_zoom(ui.music_logo, 192);
    lv_obj_set_pos(ui.music_logo, 21, 14);
    lv_obj_clear_flag(ui.music_logo, LV_OBJ_FLAG_CLICKABLE);
    ui.music_heading = text(ui.music_page, BT(BT_MUSIC), 84, 25, 380, &buddy_language_font_24, C_TEXT);
    ui.music_status = text(ui.music_page, BT(BT_WAITING_FOR_PC), 508, 29, 264, &buddy_language_font_16, C_MUTED);
    lv_obj_set_style_text_align(ui.music_status, LV_TEXT_ALIGN_RIGHT, 0);
    ui.music_eq = box(ui.music_page, 0, 0, 18, 14);
    for (unsigned i = 0; i < 3; ++i) {
        ui.music_eq_bars[i] = box(ui.music_eq, (int)i * 7, 8, 4, 6);
        lv_obj_set_style_bg_opa(ui.music_eq_bars[i], LV_OPA_COVER, 0);
        lv_obj_set_style_radius(ui.music_eq_bars[i], 2, 0);
    }
    rule(ui.music_page, 28, 84, 744, 1);

    /* Cover: a tinted record sleeve for the dancing crab. */
    ui.music_cover = box(ui.music_page, 0, 0, 200, 200);
    lv_obj_set_style_radius(ui.music_cover, 18, 0);
    lv_obj_set_style_bg_opa(ui.music_cover, LV_OPA_COVER, 0);
    lv_obj_set_style_bg_grad_dir(ui.music_cover, LV_GRAD_DIR_VER, 0);
    for (unsigned i = 0; i < 2; ++i) {
        ui.music_rings[i] = box(ui.music_cover, 0, 0, 10, 10);
        lv_obj_set_style_radius(ui.music_rings[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_border_width(ui.music_rings[i], buddy_theme.border_w, 0);
        lv_obj_set_style_border_opa(ui.music_rings[i], i ? LV_OPA_50 : LV_OPA_30, 0);
    }
    /* The inner ring doubles as a darker record label behind the crab. */
    lv_obj_set_style_bg_color(ui.music_rings[1], color(C_BG), 0);
    lv_obj_set_style_bg_opa(ui.music_rings[1], LV_OPA_30, 0);
    ui.music_buddy = lv_img_create(ui.music_cover);
    lv_img_set_antialias(ui.music_buddy, false);
    lv_obj_center(ui.music_buddy);
    lv_obj_clear_flag(ui.music_buddy, LV_OBJ_FLAG_CLICKABLE);
    /* The album cover replaces the crab when the PC sends one. */
    lv_obj_set_style_clip_corner(ui.music_cover, true, 0);
    ui.music_art = lv_img_create(ui.music_cover);
    lv_obj_clear_flag(ui.music_art, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_flag(ui.music_art, LV_OBJ_FLAG_HIDDEN);
    music_cover_layout(28, 104, 228);

    ui.music_meta = box(ui.music_page, 284, 104, 488, 150);
    ui.music_title = text(ui.music_meta, BT(BT_WAITING_FOR_YOUR_PC), 0, 0, 488, &buddy_language_font_32, C_TEXT);
    ui.music_artist = text(ui.music_meta, "", 0, 0, 488, &buddy_language_font_20, C_TEXT);
    ui.music_album = text(ui.music_meta, "", 0, 0, 488, &buddy_language_font_16, C_MUTED);
    ui.music_title_lines = 2;

    /* The timeline closes level with the cover's lower edge. */
    ui.music_progress = music_slider(ui.music_page, 1);
    lv_obj_set_pos(ui.music_progress, 284, 296);
    lv_obj_set_size(ui.music_progress, 488, 6);
    ui.music_elapsed = text(ui.music_page, "--:--", 284, 312, 160, &buddy_language_font_14, C_MUTED);
    ui.music_duration = text(ui.music_page, "--:--", 612, 312, 160, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.music_duration, LV_TEXT_ALIGN_RIGHT, 0);

    const char *symbols[] = { LV_SYMBOL_SHUFFLE, LV_SYMBOL_PREV, LV_SYMBOL_PLAY, LV_SYMBOL_NEXT, LV_SYMBOL_LOOP, LV_SYMBOL_VOLUME_MAX };
    for (unsigned i = 0; i < MUSIC_BUTTON_COUNT; ++i) ui.music_buttons[i] = icon_button(ui.music_page, symbols[i], i);
    lv_obj_set_style_bg_opa(ui.music_buttons[MUSIC_PLAY_BUTTON], LV_OPA_COVER, 0);
    lv_obj_set_style_text_color(ui.music_button_labels[MUSIC_PLAY_BUTTON], color(C_BG), 0);
    for (unsigned i = 0; i < 2; ++i) {
        ui.music_toggle_dots[i] = box(ui.music_buttons[i ? MUSIC_REPEAT_BUTTON : MUSIC_SHUFFLE_BUTTON], 0, 0, 6, 6);
        lv_obj_set_style_radius(ui.music_toggle_dots[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_bg_opa(ui.music_toggle_dots[i], LV_OPA_COVER, 0);
    }
    /* Repeat-one badge: a small filled "1" on the repeat icon's shoulder. */
    ui.music_repeat_one = text(ui.music_buttons[MUSIC_REPEAT_BUTTON], "1", 0, 0, 15, &buddy_language_font_14, C_BG);
    lv_obj_set_size(ui.music_repeat_one, 15, 15);
    lv_obj_set_style_text_line_space(ui.music_repeat_one, 0, 0);
    lv_obj_set_style_text_align(ui.music_repeat_one, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_set_style_radius(ui.music_repeat_one, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_opa(ui.music_repeat_one, LV_OPA_COVER, 0);
    music_transport_layout(528, 372, 76, 60, 48, 104, 196, &buddy_language_font_32, &buddy_language_font_24, &buddy_language_font_20);

    ui.music_volume = music_slider(ui.music_page, 100);
    ui.music_volume_label = text(ui.music_page, "", 0, 0, 44, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.music_volume_label, LV_TEXT_ALIGN_RIGHT, 0);
    music_volume_layout(22, 372, 240, &buddy_language_font_20);

    /* Command feedback appears in the quiet band between the track and its timeline. */
    ui.music_feedback = text(ui.music_page, "", 284, 256, 488, &buddy_language_font_14, C_WARNING);
    apply_music_accent(C_ACCENT);
    refresh_music();
}

/* Places the countdown ring; the crab, time and end time sit inside it. */
static void timer_ring_layout(int x, int y, int size, const lv_font_t *time_font, bool mascot)
{
    lv_obj_set_pos(ui.timer_ring, x, y);
    lv_obj_set_size(ui.timer_ring, size, size);
    lv_obj_set_style_arc_width(ui.timer_ring, size / buddy_theme.ring_div, LV_PART_MAIN);
    lv_obj_set_style_arc_width(ui.timer_ring, size / buddy_theme.ring_div, LV_PART_INDICATOR);
    lv_obj_set_style_text_font(ui.timer_time, time_font, 0);
    int time_h = lv_font_get_line_height(time_font);
    mascot = mascot && buddy_theme_has_mascot();
    visible(ui.timer_mascot, mascot);
    lv_obj_align(ui.timer_mascot, LV_ALIGN_CENTER, 0, -time_h / 2 - 44);
    lv_obj_align(ui.timer_time, LV_ALIGN_CENTER, 0, mascot ? time_h / 2 : -10);
    lv_obj_align(ui.timer_end, LV_ALIGN_CENTER, 0, (mascot ? time_h / 2 : -10) + time_h / 2 + 16);
}

static lv_obj_t *timer_chip(lv_obj_t *parent, const char *label, lv_obj_t **caption)
{
    lv_obj_t *chip = box(parent, 0, 0, 10, 10);
    lv_obj_add_flag(chip, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_radius(chip, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(chip, color(BUDDY_COLOR_SURFACE), 0);
    lv_obj_set_style_bg_opa(chip, LV_OPA_COVER, 0);
    lv_obj_set_style_bg_color(chip, color(C_PRESSED), LV_STATE_PRESSED);
    *caption = text(chip, label, 0, 0, 10, &buddy_language_font_16, C_TEXT);
    lv_obj_set_style_text_align(*caption, LV_TEXT_ALIGN_CENTER, 0);
    return chip;
}

/* Stepper rows (caption, minus, value, plus), one per unit. */
static void timer_stepper_rows(int x, int y, int width, int row_height, int step)
{
    for (unsigned i = 0; i < 3; ++i) {
        int top = y + (int)i * step;
        lv_obj_set_pos(ui.timer_captions[i], x, top + (row_height - 22) / 2);
        lv_obj_set_size(ui.timer_captions[i], width - 180, 24);
        lv_obj_set_pos(ui.timer_adjust[i * 2], x + width - 168, top);
        lv_obj_set_pos(ui.timer_adjust[i * 2 + 1], x + width - 52, top);
        lv_obj_set_pos(ui.timer_units[i], x + width - 116, top + (row_height - 30) / 2);
        lv_obj_set_size(ui.timer_units[i], 64, 30);
        for (unsigned j = 0; j < 2; ++j) lv_obj_set_size(ui.timer_adjust[i * 2 + j], 52, row_height);
    }
}

static void timer_presets_layout(int x, int y, int width, int height)
{
    int chip = (width - 3 * 8) / 4;
    for (unsigned i = 0; i < 4; ++i) {
        lv_obj_set_pos(ui.timer_presets[i], x + (int)i * (chip + 8), y);
        lv_obj_set_size(ui.timer_presets[i], chip, height);
        lv_obj_set_width(ui.timer_preset_labels[i], chip - 8);
        lv_obj_align(ui.timer_preset_labels[i], LV_ALIGN_CENTER, 0, 0);
    }
}

static void timer_buttons_layout(int x, int y, int width, int height)
{
    int primary = (width - 12) * 3 / 5;
    lv_obj_set_pos(ui.timer_buttons[0], x, y);
    lv_obj_set_size(ui.timer_buttons[0], primary, height);
    lv_obj_set_pos(ui.timer_buttons[1], x + primary + 12, y);
    lv_obj_set_size(ui.timer_buttons[1], width - primary - 12, height);
    for (unsigned i = 0; i < 2; ++i) {
        lv_obj_set_width(ui.timer_button_labels[i], (i ? width - primary - 12 : primary) - 16);
        lv_obj_align(ui.timer_button_labels[i], LV_ALIGN_CENTER, 0, 0);
    }
}

static void create_timer_page(void)
{
    ui.timer_page = box(ui.screen, 0, 0, 800, 440);
    ui.timer_heading = text(ui.timer_page, BT(BT_TIMER), 28, 26, 420, &buddy_language_font_24, C_TEXT);
    ui.timer_status = text(ui.timer_page, BT(BT_READY), 508, 29, 264, &buddy_language_font_16, C_ACCENT);
    lv_obj_set_style_text_align(ui.timer_status, LV_TEXT_ALIGN_RIGHT, 0);
    rule(ui.timer_page, 28, 84, 744, 1);

    /* A full ring that starts at the top and drains clockwise. */
    ui.timer_ring = lv_arc_create(ui.timer_page);
    lv_obj_remove_style_all(ui.timer_ring);
    lv_arc_set_bg_angles(ui.timer_ring, 0, 360);
    lv_arc_set_rotation(ui.timer_ring, 270);
    lv_arc_set_range(ui.timer_ring, 0, 1000);
    lv_obj_set_style_arc_color(ui.timer_ring, color(C_RULE), LV_PART_MAIN);
    lv_obj_set_style_arc_rounded(ui.timer_ring, true, LV_PART_INDICATOR);
    lv_obj_clear_flag(ui.timer_ring, LV_OBJ_FLAG_CLICKABLE);
    ui.timer_mascot = lv_img_create(ui.timer_ring);
    lv_img_set_antialias(ui.timer_mascot, false);
    lv_obj_clear_flag(ui.timer_mascot, LV_OBJ_FLAG_CLICKABLE);
    ui.timer_time = lv_label_create(ui.timer_ring);
    lv_label_set_text(ui.timer_time, "00:05:00");
    lv_obj_set_style_text_color(ui.timer_time, color(C_TEXT), 0);
    ui.timer_end = lv_label_create(ui.timer_ring);
    lv_label_set_text(ui.timer_end, "");
    lv_obj_set_style_text_font(ui.timer_end, &buddy_language_font_16, 0);
    timer_ring_layout(40, 104, 304, &buddy_language_font_48, true);

    const char *units[] = { BT(BT_HOURS), BT(BT_MINUTES), BT(BT_SECONDS) };
    for (unsigned i = 0; i < 3; ++i) {
        ui.timer_captions[i] = text(ui.timer_page, units[i], 0, 0, 100, &buddy_language_font_16, C_MUTED);
        ui.timer_units[i] = text(ui.timer_page, "00", 0, 0, 64, &buddy_language_font_24, C_TEXT);
        lv_obj_set_style_text_align(ui.timer_units[i], LV_TEXT_ALIGN_CENTER, 0);
        for (unsigned j = 0; j < 2; ++j) {
            unsigned action = i * 2 + j;
            lv_obj_t *caption;
            ui.timer_adjust[action] = action_button(ui.timer_page, j ? LV_SYMBOL_PLUS : LV_SYMBOL_MINUS, 0, 0, 52, 48, &caption);
            lv_obj_set_style_radius(ui.timer_adjust[action], LV_RADIUS_CIRCLE, 0);
            lv_obj_add_event_cb(ui.timer_adjust[action], timer_adjust_clicked, LV_EVENT_PRESSED,
                (void *)(uintptr_t)action);
            lv_obj_add_event_cb(ui.timer_adjust[action], timer_adjust_clicked, LV_EVENT_LONG_PRESSED_REPEAT,
                (void *)(uintptr_t)action);
        }
    }
    timer_stepper_rows(380, 104, 392, 48, 60);

    for (unsigned i = 0; i < 4; ++i) {
        char label[24];
        snprintf(label, sizeof(label), BT(BT_LU_MIN), (unsigned long)timer_preset_minutes[i]);
        ui.timer_presets[i] = timer_chip(ui.timer_page, label, &ui.timer_preset_labels[i]);
        lv_obj_add_event_cb(ui.timer_presets[i], timer_preset_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)i);
    }
    timer_presets_layout(380, 292, 392, 44);

    const char *labels[] = { BT(BT_START), BT(BT_RESET) };
    for (unsigned i = 0; i < 2; ++i) {
        ui.timer_buttons[i] = action_button(ui.timer_page, labels[i], 0, 0, 100, 56, &ui.timer_button_labels[i]);
        lv_obj_set_style_radius(ui.timer_buttons[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_add_event_cb(ui.timer_buttons[i], timer_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)i);
        if (!i) lv_obj_add_event_cb(ui.timer_buttons[i], timer_clicked, LV_EVENT_PRESSED, NULL);
    }
    /* Start is the one filled action on the page. */
    lv_obj_set_style_bg_color(ui.timer_buttons[0], color(C_ACCENT), 0);
    lv_obj_set_style_bg_color(ui.timer_buttons[0], lv_color_darken(color(C_ACCENT), LV_OPA_30), LV_STATE_PRESSED);
    lv_obj_set_style_text_color(ui.timer_button_labels[0], color(C_BG), 0);
    timer_buttons_layout(380, 352, 392, 56);
    refresh_timer();
}

static void draw_weather_icon(lv_event_t *event)
{
    if (!ui.weather.available) return;
    lv_area_t area;
    lv_obj_get_coords(lv_event_get_target(event), &area);
    int scale = lv_obj_get_width(lv_event_get_target(event)) >= 56 ? 2 : 1;
    buddy_weather_icon_draw(lv_event_get_draw_ctx(event), area.x1, area.y1, scale, ui.weather.code, ui.weather.is_day,
                            ui.active_app == BUDDY_APP_HOME ? BUDDY_COLOR_BG : C_CLOCK_BG);
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
    ui.chooser_title = text(ui.chooser, BT(BT_APPS), 28, 20, 744, &buddy_language_font_32, C_TEXT);
    /* Each tile: the app's icon, its name, then a line or two of what it shows now. The smallest
     * screen keeps the icon and name, centred. */
    const chooser_grid_t grid = chooser_grid();
    const bool wide = BOARD_DISPLAY_WIDTH >= 800, tiny = BOARD_DISPLAY_HEIGHT < 400;
    const int inner = grid.width - 2 * grid.pad, name_y = grid.pad + grid.icon + (wide ? 12 : 6);
    for (unsigned i = 0; i < sizeof(app_catalog) / sizeof(app_catalog[0]); ++i) {
        lv_obj_t *tile = surface_card(ui.chooser, wide ? 14 : 12);
        ui.app_rows[i] = tile;
        lv_obj_set_size(tile, grid.width, grid.height);
        /* A finger that slides off a tile is scrolling or changing its mind, not choosing it. */
        lv_obj_add_flag(tile, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_clear_flag(tile, LV_OBJ_FLAG_PRESS_LOCK);
        lv_obj_set_style_bg_color(tile, color(C_PRESSED), LV_STATE_PRESSED);
        lv_obj_set_style_bg_opa(tile, LV_OPA_COVER, LV_STATE_PRESSED);
        lv_obj_add_event_cb(tile, app_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)app_catalog[i].id);
        buddy_app_icon_create(tile, (buddy_app_t)i, tiny ? (grid.width - grid.icon) / 2 : grid.pad, grid.pad, grid.icon);
        ui.app_names[i] = text(tile, BT(app_catalog[i].title), tiny ? 4 : grid.pad, name_y, tiny ? grid.width - 8 : inner,
                               wide ? &buddy_language_font_20 : tiny ? &buddy_language_font_14 : &buddy_language_font_16, C_TEXT);
        if (tiny) lv_obj_set_style_text_align(ui.app_names[i], LV_TEXT_ALIGN_CENTER, 0);
        ui.app_description[i] = text(tile, "", grid.pad, name_y + (wide ? 28 : 22), inner,
                                     wide ? &buddy_language_font_16 : &buddy_language_font_14, C_MUTED);
        lv_obj_set_style_text_line_space(ui.app_description[i], 2, 0);
        lv_obj_set_height(ui.app_description[i], wide ? 46 : 40);
        if (tiny) visible(ui.app_description[i], false);
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

/* Themes without a mascot hide each crab and let the neighbouring content take its place.
 * Objects stay in their creation order, which the compact layout relies on. */
static void theme_layout(void)
{
    lv_obj_t *numbers[] = { ui.clock_large, ui.timer_time, ui.cpu.usage, ui.gpu.usage,
        ui.codex.remaining[0], ui.codex.remaining[1], ui.claude.remaining[0], ui.claude.remaining[1] };
    for (unsigned i = 0; i < sizeof(numbers) / sizeof(numbers[0]); ++i) buddy_theme_numerals(numbers[i]);
    if (buddy_theme_has_mascot()) return;
    const bool compact = BOARD_DISPLAY_WIDTH < 800, tiny = BOARD_DISPLAY_HEIGHT < 400;
    lv_obj_t *crabs[] = { ui.mascot, ui.clock_mascot, ui.timer_mascot, ui.codex.mascot, ui.claude.mascot, ui.transition_image };
    for (unsigned i = 0; i < sizeof(crabs) / sizeof(crabs[0]); ++i) visible(crabs[i], false);
    lv_obj_set_x(ui.monitor_title, compact ? 12 : 28);
    lv_obj_set_x(ui.host, compact ? 12 : 29);
    if (compact && !tiny) lv_obj_set_x(ui.status, 12);
    if (!compact) {
        agent_view_t *views[] = { &ui.codex, &ui.claude };
        for (unsigned i = 0; i < 2; ++i) {
            lv_obj_set_style_text_align(views[i]->status, LV_TEXT_ALIGN_LEFT, 0);
            lv_obj_set_style_text_align(views[i]->age, LV_TEXT_ALIGN_LEFT, 0);
            lv_obj_set_pos(views[i]->status, 604, 112);
            lv_obj_set_pos(views[i]->age, 604, 164);
        }
    }
    /* The record sleeve keeps its rings around a quiet note instead of the dancing crab. */
    lv_img_set_src(ui.music_buddy, LV_SYMBOL_AUDIO);
    lv_obj_set_style_text_font(ui.music_buddy, compact ? &buddy_language_font_32 : &buddy_language_font_48, 0);
    lv_obj_set_style_text_color(ui.music_buddy, color(C_ACCENT), 0);
    lv_obj_center(ui.music_buddy);
}

void buddy_ui_init(void)
{
    if (ui.screen) return;
    buddy_settings_t look = buddy_settings_get();
    buddy_theme_select(look.theme, look.accent);
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
    ui.music_link_title = text(ui.music_link, BT(BT_MUSIC), 12, 29, 210, &buddy_language_font_20, C_TEXT);
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

    ui.overview = box(ui.screen, 28, 96, 744, 340);
    create_channel(&ui.cpu, BT(BT_CPU), false);
    create_channel(&ui.gpu, BT(BT_GPU), true);
    channel_layout(&ui.cpu, 0, 0, 366, 148, 116, &buddy_language_font_32, &buddy_language_font_32);
    channel_layout(&ui.gpu, 378, 0, 366, 148, 116, &buddy_language_font_32, &buddy_language_font_32);
    const char *tile_names[] = { BT(BT_MEMORY), BT(BT_GPU_MEMORY), BT(BT_NETWORK) };
    for (unsigned i = 0; i < 3; ++i) {
        ui.tiles[i] = surface_card(ui.overview, 14);
        ui.tile_names[i] = text(ui.tiles[i], tile_names[i], 0, 0, 100, &buddy_language_font_14, C_MUTED);
    }
    ui.memory = text(ui.tiles[0], "--", 0, 0, 100, &buddy_language_font_24, C_TEXT);
    ui.memory_percent = text(ui.tiles[0], BT(BT_WAITING_FOR_READINGS), 0, 0, 100, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.memory_percent, LV_TEXT_ALIGN_RIGHT, 0);
    ui.memory_bar = meter_bar(ui.tiles[0]);
    ui.vram = text(ui.tiles[1], "--", 0, 0, 100, &buddy_language_font_24, C_TEXT);
    ui.vram_note = text(ui.tiles[1], BT(BT_WAITING_FOR_READINGS), 0, 0, 100, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.vram_note, LV_TEXT_ALIGN_RIGHT, 0);
    ui.vram_bar = meter_bar(ui.tiles[1]);
    ui.net_captions[0] = text(ui.tiles[2], BT(BT_DOWN), 0, 0, 80, &buddy_language_font_14, C_MUTED);
    ui.net_captions[1] = text(ui.tiles[2], BT(BT_UP), 0, 0, 80, &buddy_language_font_14, C_MUTED);
    ui.rx = text(ui.tiles[2], "--", 0, 0, 100, &buddy_language_font_20, C_TEXT);
    ui.tx = text(ui.tiles[2], "--", 0, 0, 100, &buddy_language_font_20, C_TEXT);
    lv_obj_set_style_text_align(ui.rx, LV_TEXT_ALIGN_RIGHT, 0);
    lv_obj_set_style_text_align(ui.tx, LV_TEXT_ALIGN_RIGHT, 0);
    for (unsigned i = 0; i < 3; ++i) tile_layout(i, (int)i * 252, 160, 240, 96);

    ui.chart_card = surface_card(ui.overview, 14);
    ui.chart_title = text(ui.chart_card, BT(BT_CPU_LOAD_60_SECONDS), 0, 0, 100, &buddy_language_font_14, C_MUTED);
    ui.chart_range = text(ui.chart_card, "0 - 100%", 0, 0, 110, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.chart_range, LV_TEXT_ALIGN_RIGHT, 0);
    ui.chart = lv_chart_create(ui.chart_card);
    lv_obj_remove_style_all(ui.chart);
    lv_chart_set_type(ui.chart, LV_CHART_TYPE_LINE);
    lv_chart_set_range(ui.chart, LV_CHART_AXIS_PRIMARY_Y, 0, 100);
    lv_chart_set_point_count(ui.chart, HISTORY_POINTS);
    lv_chart_set_div_line_count(ui.chart, 2, 0);
    lv_obj_set_style_line_color(ui.chart, color(C_RULE), LV_PART_MAIN);
    lv_obj_set_style_line_width(ui.chart, 1, LV_PART_MAIN);
    lv_obj_set_style_line_width(ui.chart, buddy_theme.chart_w, LV_PART_ITEMS);
    lv_obj_set_style_size(ui.chart, 0, LV_PART_INDICATOR);
    lv_obj_clear_flag(ui.chart, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
    ui.cpu_series = lv_chart_add_series(ui.chart, color(C_ACCENT), LV_CHART_AXIS_PRIMARY_Y);
    ui.gpu_series = lv_chart_add_series(ui.chart, color(C_ACCENT), LV_CHART_AXIS_PRIMARY_Y);
    lv_chart_set_all_value(ui.chart, ui.cpu_series, LV_CHART_POINT_NONE);
    lv_chart_set_all_value(ui.chart, ui.gpu_series, LV_CHART_POINT_NONE);

    ui.game_panel = surface_card(ui.overview, 14);
    ui.game_title = text(ui.game_panel, "", 0, 0, 100, &buddy_language_font_20, C_TEXT);
    ui.game_status = text(ui.game_panel, "", 0, 0, 100, &buddy_language_font_14, C_MUTED);
    ui.game_fps = text(ui.game_panel, "--", 0, 0, 150, &buddy_language_font_48, C_ACCENT);
    lv_obj_set_style_text_align(ui.game_fps, LV_TEXT_ALIGN_RIGHT, 0);
    ui.game_fps_unit = text(ui.game_panel, BT(BT_FPS), 0, 0, 150, &buddy_language_font_14, C_MUTED);
    lv_obj_set_style_text_align(ui.game_fps_unit, LV_TEXT_ALIGN_RIGHT, 0);
    chart_layout(0, 268, 744, 72);
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
        lv_obj_t *cell = box(ui.apps_button, 12 + (i % 2) * 7, 19 + (i / 2) * 7, 4, 4);
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
    theme_layout();
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
    /* Away only explains a quiet PC; it ends when Wi-Fi itself is gone. */
    if (state != BUDDY_PC_CONNECTED && state != BUDDY_PC_OFFLINE) ui.pc_away = false;
    if (state != BUDDY_PC_CONNECTED && ui.alert_key[0]) { ui.alert_key[0] = 0; visible(ui.alert.page, false); }
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
        ui.music_deferred = 0;
        set_music_feedback(BT(BT_PC_DISCONNECTED_RECONNECT_TO_CONTROL_SPOTIFY), true);
    }
    refresh_connection();
}

static void channel_reading(channel_t *channel, float usage, float temperature, const char *name)
{
    char buf[32];
    if (isfinite(usage) && usage >= 0) snprintf(buf, sizeof(buf), "%.0f", fminf(usage, 100));
    else snprintf(buf, sizeof(buf), "--");
    lv_label_set_text(channel->usage, buf);
    lv_arc_set_value(channel->gauge, isfinite(usage) ? (int16_t)lroundf(fminf(fmaxf(usage, 0), 100) * 10.f) : 0);
    lv_obj_set_style_arc_color(channel->gauge, color(ui.connection != BUDDY_PC_CONNECTED ? C_MUTED :
        isfinite(usage) && usage >= 90 ? C_WARNING : C_ACCENT), LV_PART_INDICATOR);
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
    unsigned deferred = 0;
    if (ui.music_command_pending && stats->music.control_id != ui.music_command_control_id) {
        ui.music_command_pending = false;
        deferred = ui.music_deferred;
        ui.music_deferred = 0;
        const char *feedback = "";
        if (!stats->music.control_ok) {
            feedback = strcmp(stats->music.control_error, "unsupported") == 0 ? BT(BT_THIS_CONTROL_ISN_T_AVAILABLE_FOR_THIS_TRACK) :
                strcmp(stats->music.control_error, "unavailable") == 0 ? BT(BT_SPOTIFY_ISN_T_READY_START_PLAYBACK_ON_YOUR_PC) :
                BT(BT_COULDN_T_CONTROL_SPOTIFY_TRY_AGAIN);
        }
        set_music_feedback(feedback, true);
    }
    bool away_changed = stats->away != ui.pc_away;
    if (stats->away && !ui.pc_away) ui.away_tick = lv_tick_get();
    if (away_changed) ui.offline_handled = false;
    ui.pc_away = stats->away;
    ui.stats = *stats;
    ui.have_stats = true;
    ui.fresh_sample = true;
    ui.last_stats_tick = lv_tick_get();
    if (deferred) music_send(deferred - 1U, ui.music_deferred_value);
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
    lv_bar_set_value(ui.memory_bar, isfinite(stats->memory_usage) ? (int)fminf(fmaxf(stats->memory_usage, 0), 100) : 0, LV_ANIM_OFF);
    lv_bar_set_value(ui.vram_bar, isfinite(stats->vram_used_mb) && isfinite(stats->vram_total_mb) && stats->vram_total_mb > 0 ?
        (int)fminf(100.f, stats->vram_used_mb / stats->vram_total_mb * 100) : 0, LV_ANIM_OFF);
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
    if (away_changed) refresh_connection();
    maybe_auto_app();
    refresh_alert();
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

bool buddy_ui_restart_safe(void)
{
    return ui.confirm_action == BUDDY_CONFIRM_NONE && ui.install_stage == BUDDY_INSTALL_NONE;
}

static void set_opacity(void *object, int32_t value) { lv_obj_set_style_opa(object, (lv_opa_t)value, 0); }

void buddy_ui_begin_restart(void)
{
    lv_obj_t *shade = box(lv_layer_top(), 0, 0, BOARD_DISPLAY_WIDTH, BOARD_DISPLAY_HEIGHT);
    lv_obj_add_flag(shade, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(shade, color(C_BG), 0);
    lv_obj_set_style_bg_opa(shade, LV_OPA_COVER, 0);
    lv_obj_t *note = text(shade, BT(BT_APPLYING_YOUR_NEW_LOOK), 12, BOARD_DISPLAY_HEIGHT / 2 - 14, BOARD_DISPLAY_WIDTH - 24,
                          BOARD_DISPLAY_HEIGHT < 400 ? &buddy_language_font_16 : &buddy_language_font_20, C_MUTED);
    lv_obj_set_style_text_align(note, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_set_style_opa(shade, LV_OPA_TRANSP, 0);
    lv_anim_t fade; lv_anim_init(&fade); lv_anim_set_var(&fade, shade);
    lv_anim_set_exec_cb(&fade, set_opacity); lv_anim_set_values(&fade, LV_OPA_TRANSP, LV_OPA_COVER);
    lv_anim_set_time(&fade, BUDDY_UI_RESTART_FADE_MS); lv_anim_set_path_cb(&fade, lv_anim_path_ease_out);
    lv_anim_start(&fade);
}

/* ---- Full-screen overlays: the approval prompt, the needs-you alert and the install screen ---- */

/* Sizes for the three screen classes: 800 x 480, 480 x 480 and 320 x 240. The smallest
 * has no room for the crab tile and lets the body give way to the details. */
typedef struct {
    int margin, tile, gap, button_h, row_h, name_w;
    const lv_font_t *eyebrow, *title, *body, *detail, *button;
} prompt_metrics_t;

static prompt_metrics_t prompt_metrics(void)
{
    prompt_metrics_t m;
    if (BOARD_DISPLAY_WIDTH >= 800)
        m = (prompt_metrics_t){ 32, 136, 28, 64, 34, 220, &buddy_language_font_16, &buddy_language_font_32,
            &buddy_language_font_20, &buddy_language_font_20, &buddy_language_font_20 };
    else if (BOARD_DISPLAY_HEIGHT >= 400)
        m = (prompt_metrics_t){ 24, 112, 20, 60, 30, 150, &buddy_language_font_16, &buddy_language_font_24,
            &buddy_language_font_16, &buddy_language_font_16, &buddy_language_font_20 };
    else m = (prompt_metrics_t){ 12, 0, 10, 40, 22, 96, &buddy_language_font_14, &buddy_language_font_20,
        &buddy_language_font_14, &buddy_language_font_14, &buddy_language_font_16 };
    if (!buddy_theme.prompt_tile) m.tile = 0;
    return m;
}

#define PROMPT_LOGO 32

/* Overlays stack by urgency: an install above an approval above an alert, the dim shield above all. */
static void raise_overlays(void)
{
    if (ui.alert.page && ui.alert_key[0]) lv_obj_move_foreground(ui.alert.page);
    if (ui.confirm.page && ui.confirm_action != BUDDY_CONFIRM_NONE) lv_obj_move_foreground(ui.confirm.page);
    if (ui.install_page && ui.install_stage != BUDDY_INSTALL_NONE) lv_obj_move_foreground(ui.install_page);
    lv_obj_move_foreground(ui.dim_overlay);
}

static void show_overlay(lv_obj_t *page)
{
    visible(page, true);
    raise_overlays();
    ui.mascot_state = BUDDY_MASCOT_STATE_COUNT;
    lv_disp_trig_activity(NULL);
    refresh_display_power();
}

static void prompt_button_primary(lv_obj_t *button, lv_obj_t *label, uint32_t ink)
{
    lv_obj_set_style_bg_color(button, color(ink), 0);
    lv_obj_set_style_bg_color(button, lv_color_darken(color(ink), LV_OPA_30), LV_STATE_PRESSED);
    lv_obj_set_style_text_color(label, color(C_BG), 0);
}

/* The crab in a tile, an eyebrow, a title and a body; a details card above a quiet
 * secondary and a filled primary button. Secondary passes NULL to `clicked`, primary 1. */
static void create_prompt(prompt_view_t *view, lv_event_cb_t clicked)
{
    const prompt_metrics_t m = prompt_metrics();
    const int width = BOARD_DISPLAY_WIDTH, height = BOARD_DISPLAY_HEIGHT;
    view->page = box(ui.screen, 0, 0, width, height);
    /* Clickable so presses never reach the app underneath. */
    lv_obj_add_flag(view->page, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(view->page, color(C_BG), 0);
    lv_obj_set_style_bg_opa(view->page, LV_OPA_COVER, 0);
    if (m.tile) {
        view->tile = surface_card(view->page, 20);
        lv_obj_set_pos(view->tile, m.margin, m.margin);
        lv_obj_set_size(view->tile, m.tile, m.tile);
        lv_obj_set_style_border_width(view->tile, buddy_theme.border_w, 0);
        lv_obj_set_style_border_color(view->tile, color(C_RULE), 0);
        view->mascot = lv_img_create(view->tile);
        lv_img_set_antialias(view->mascot, false);
        lv_obj_set_size(view->mascot, 96, 96);
        lv_obj_center(view->mascot);
        lv_obj_clear_flag(view->mascot, LV_OBJ_FLAG_CLICKABLE);
    }
    int x = m.tile ? m.margin + m.tile + m.gap : m.margin, column = width - x - m.margin;
    view->logo = lv_img_create(view->page);
    lv_img_set_zoom(view->logo, 256 * PROMPT_LOGO / 56);
    lv_obj_clear_flag(view->logo, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_flag(view->logo, LV_OBJ_FLAG_HIDDEN);
    view->eyebrow = text(view->page, "", x, m.margin, column, m.eyebrow, C_ACCENT);
    view->title = text(view->page, "", x, m.margin, column, m.title, C_TEXT);
    lv_label_set_long_mode(view->title, LV_LABEL_LONG_WRAP);
    view->body = text(view->page, "", x, m.margin, column, m.body, C_MUTED);
    view->details = surface_card(view->page, 14);
    lv_obj_set_width(view->details, width - m.margin * 2);
    for (unsigned i = 0; i < PROMPT_ROWS; ++i) {
        view->names[i] = text(view->details, "", 16, 0, m.name_w - 16, m.detail, C_MUTED);
        view->values[i] = text(view->details, "", m.name_w, 0, width - m.margin * 2 - m.name_w - 16, m.detail, C_TEXT);
        lv_obj_set_style_text_align(view->values[i], LV_TEXT_ALIGN_RIGHT, 0);
    }
    /* Time left to answer, shrinking towards the automatic denial. */
    view->track = box(view->page, m.margin, 0, width - m.margin * 2, 4);
    lv_obj_set_style_bg_color(view->track, color(C_RULE), 0);
    lv_obj_set_style_bg_opa(view->track, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(view->track, 2, 0);
    view->countdown = box(view->track, 0, 0, width - m.margin * 2, 4);
    lv_obj_set_style_bg_color(view->countdown, color(C_ACCENT), 0);
    lv_obj_set_style_bg_opa(view->countdown, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(view->countdown, 2, 0);
    /* The quiet choice sits where a stray tap is most likely; the primary one needs a deliberate press. */
    const int button_w = (width - m.margin * 2 - 16) / 2, button_y = height - m.margin - m.button_h;
    view->secondary = action_button(view->page, "", m.margin, button_y, button_w, m.button_h, &view->secondary_label);
    view->primary = action_button(view->page, "", m.margin + button_w + 16, button_y, button_w, m.button_h, &view->primary_label);
    lv_obj_set_style_radius(view->secondary, 12, 0);
    lv_obj_set_style_radius(view->primary, 12, 0);
    lv_obj_set_style_text_font(view->secondary_label, m.button, 0);
    lv_obj_set_style_text_font(view->primary_label, m.button, 0);
    lv_obj_add_event_cb(view->secondary, clicked, LV_EVENT_CLICKED, NULL);
    lv_obj_add_event_cb(view->primary, clicked, LV_EVENT_CLICKED, (void *)1);
    lv_obj_add_flag(view->page, LV_OBJ_FLAG_HIDDEN);
}

static void prompt_row(prompt_view_t *view, const char *name, const char *value)
{
    if (view->rows >= PROMPT_ROWS || !value || !value[0]) return;
    lv_label_set_text(view->names[view->rows], name);
    lv_label_set_text(view->values[view->rows], value);
    ++view->rows;
}

/* Stacks the text column after its contents are set; details and the countdown sit just
 * above the buttons, and the body shortens (or hides) to fit between them. */
static void prompt_layout(prompt_view_t *view, bool countdown)
{
    const prompt_metrics_t m = prompt_metrics();
    const int height = BOARD_DISPLAY_HEIGHT;
    const int x = m.tile ? m.margin + m.tile + m.gap : m.margin;
    const int button_y = height - m.margin - m.button_h;
    const bool logo = !lv_obj_has_flag(view->logo, LV_OBJ_FLAG_HIDDEN);
    const int eyebrow_line = lv_font_get_line_height(m.eyebrow), head = logo ? LV_MAX(PROMPT_LOGO, eyebrow_line) : eyebrow_line;
    lv_obj_set_pos(view->logo, x + PROMPT_LOGO / 2 - 28, m.margin + head / 2 - 28);
    lv_obj_set_pos(view->eyebrow, x + (logo ? PROMPT_LOGO + 10 : 0), m.margin + (head - eyebrow_line) / 2);
    lv_obj_set_width(view->eyebrow, BOARD_DISPLAY_WIDTH - m.margin - lv_obj_get_x(view->eyebrow));
    lv_obj_set_y(view->title, m.margin + head + 8);
    lv_obj_update_layout(view->page);
    const int body_y = lv_obj_get_y(view->title) + lv_obj_get_height(view->title) + 8;
    for (unsigned i = 0; i < PROMPT_ROWS; ++i) {
        visible(view->names[i], i < view->rows);
        visible(view->values[i], i < view->rows);
        lv_obj_set_y(view->names[i], 12 + (int)i * m.row_h);
        lv_obj_set_y(view->values[i], 12 + (int)i * m.row_h);
    }
    const int details_h = view->rows ? (int)view->rows * m.row_h + 24 - (m.row_h - lv_font_get_line_height(m.detail)) : 0;
    const int track_y = button_y - 14;
    const int details_y = (countdown ? track_y - 12 : button_y - 14) - details_h;
    visible(view->details, view->rows > 0);
    lv_obj_set_pos(view->details, m.margin, details_y);
    lv_obj_set_height(view->details, details_h);
    visible(view->track, countdown);
    lv_obj_set_y(view->track, track_y);
    const int body_bottom = (view->rows ? details_y : track_y) - 12;
    const int line = lv_font_get_line_height(m.body);
    const bool body = lv_label_get_text(view->body)[0] && body_bottom - body_y >= line;
    visible(view->body, body);
    lv_obj_set_y(view->body, body_y);
    lv_label_set_long_mode(view->body, LV_LABEL_LONG_DOT);
    lv_obj_set_height(view->body, body ? (body_bottom - body_y) / (line + 5) * (line + 5) : 0);
}

static void close_alert(void)
{
    /* Stays dismissed until that task stops waiting, so it can alert again next time. */
    snprintf(ui.alert_dismissed, sizeof(ui.alert_dismissed), "%s", ui.alert_key);
    ui.alert_key[0] = 0;
    visible(ui.alert.page, false);
    refresh_display_power();
}

static void alert_clicked(lv_event_t *event)
{
    bool focus = lv_event_get_user_data(event) != NULL;
    buddy_app_t app = ui.alert_app;
    close_alert();
    /* The PC brings that window forward; a refused request simply leaves it where it is. */
    if (focus && ui.music_control_callback) ui.music_control_callback("focus", (uint32_t)app);
}

/* The first task in the order shown on the agent pages that waits for an answer. */
static const buddy_codex_task_t *waiting_task(const buddy_codex_t *agent)
{
    for (uint32_t i = 0; i < agent->activity.item_count && i < 3; ++i)
        if (strcmp(agent->activity.items[i].state, "waiting") == 0) return &agent->activity.items[i];
    return NULL;
}

static void ago_text(char *buffer, size_t size, uint32_t seconds)
{
    if (seconds < 60) snprintf(buffer, size, BT(BT_LU_S_AGO), (unsigned long)seconds);
    else if (seconds < 3600) snprintf(buffer, size, BT(BT_LU_MIN_AGO), (unsigned long)(seconds / 60));
    else snprintf(buffer, size, BT(BT_LU_H_AGO), (unsigned long)(seconds / 3600));
}

/* Shows the alert over any app while a task waits; the first tap is the user's choice. */
static void refresh_alert(void)
{
    const buddy_codex_t *agents[] = { &ui.stats.claude, &ui.stats.codex };
    const buddy_app_t apps[] = { BUDDY_APP_CLAUDE, BUDDY_APP_CODEX };
    const buddy_codex_task_t *task = NULL;
    buddy_app_t app = BUDDY_APP_CLAUDE;
    for (unsigned i = 0; !task && i < 2; ++i)
        if (music_connected() && (task = waiting_task(agents[i])) != NULL) app = apps[i];
    char key[sizeof(ui.alert_key)] = "";
    if (task) snprintf(key, sizeof(key), "%d|%.63s|%.31s", (int)app, task->title, task->project);
    if (!task || strcmp(key, ui.alert_dismissed) != 0) ui.alert_dismissed[0] = 0;
    bool show = task && !ui.alert_dismissed[0] && ui.connection != BUDDY_PROVISIONING;
    if (!show) {
        if (ui.alert_key[0]) { ui.alert_key[0] = 0; visible(ui.alert.page, false); refresh_display_power(); }
        return;
    }
    char asked[40];
    ago_text(asked, sizeof(asked), task->age_s);
    if (strcmp(key, ui.alert_key) == 0) {
        set_label_if_changed(ui.alert.values[ui.alert.rows - 1], asked);
        return;
    }
    if (!ui.alert.page) create_prompt(&ui.alert, alert_clicked);
    snprintf(ui.alert_key, sizeof(ui.alert_key), "%s", key);
    ui.alert_app = app;
    const agent_copy_t *copy = app == BUDDY_APP_CLAUDE ? &claude_copy : &codex_copy;
    lv_img_set_src(ui.alert.logo, copy->logo);
    visible(ui.alert.logo, true);
    lv_label_set_text(ui.alert.eyebrow, BT(app == BUDDY_APP_CLAUDE ? BT_CLAUDE_CODE_NEEDS_YOU : BT_CODEX_NEEDS_YOU));
    lv_obj_set_style_text_color(ui.alert.eyebrow, color(copy->accent), 0);
    lv_label_set_text(ui.alert.title, task->title[0] ? task->title : BT(BT_IT_IS_WAITING_FOR_YOUR_PERMISSION_OR_ANSWER));
    lv_label_set_text(ui.alert.body, task->title[0] ? BT(BT_IT_IS_WAITING_FOR_YOUR_PERMISSION_OR_ANSWER) : "");
    ui.alert.rows = 0;
    prompt_row(&ui.alert, BT(BT_PROJECT_LABEL), task->project);
    prompt_row(&ui.alert, BT(BT_ASKED_LABEL), asked);
    lv_label_set_text(ui.alert.secondary_label, BT(BT_NOT_NOW));
    lv_label_set_text(ui.alert.primary_label, BT(BT_SHOW_ON_PC));
    prompt_button_primary(ui.alert.primary, ui.alert.primary_label, copy->accent);
    if (ui.alert.tile) lv_obj_set_style_border_color(ui.alert.tile, color(C_WARNING), 0);
    prompt_layout(&ui.alert, false);
    show_overlay(ui.alert.page);
}

void buddy_ui_set_album_art(uint32_t id, uint16_t size, uint8_t *pixels)
{
    if (!pixels) return;
    if (!ui.music_art || !id || size == 0) { free(pixels); return; }
#if LV_COLOR_16_SWAP
    /* The companion sends little-endian RGB565. */
    for (size_t i = 0; i + 1 < (size_t)size * size * 2; i += 2) { uint8_t low = pixels[i]; pixels[i] = pixels[i + 1]; pixels[i + 1] = low; }
#endif
    lv_img_cache_invalidate_src(&ui.art_frame);
    free(ui.art_pixels);
    ui.art_pixels = pixels;
    ui.art_id = id;
    ui.art_frame = (lv_img_dsc_t) {
        .header = { .cf = LV_IMG_CF_TRUE_COLOR, .always_zero = 0, .w = size, .h = size },
        .data_size = (uint32_t)size * size * 2, .data = pixels,
    };
    lv_img_set_src(ui.music_art, &ui.art_frame);
    lv_img_set_antialias(ui.music_art, true);
    refresh_music();
}

static void confirm_answer(bool approved)
{
    buddy_confirm_action_t action = ui.confirm_action;
    if (action == BUDDY_CONFIRM_NONE) return;
    ui.confirm_action = BUDDY_CONFIRM_NONE;
    visible(ui.confirm.page, false);
    /* The install screen names the version that was approved. */
    if (approved && action == BUDDY_CONFIRM_FIRMWARE) snprintf(ui.install_detail, sizeof(ui.install_detail), "%s", ui.confirm_detail);
    if (ui.confirm_callback) ui.confirm_callback(action, approved);
    refresh_display_power();
}

static void confirm_clicked(lv_event_t *event)
{
    confirm_answer(lv_event_get_user_data(event) != NULL);
}

static void confirm_tick(lv_timer_t *timer)
{
    (void)timer;
    if (ui.confirm_action != BUDDY_CONFIRM_NONE && lv_tick_elaps(ui.confirm_started) >= CONFIRM_TIMEOUT_MS) confirm_answer(false);
    /* A failed install explains itself, then steps aside; the old firmware keeps running. */
    if (ui.install_stage == BUDDY_INSTALL_FAILED && lv_tick_elaps(ui.install_tick) >= INSTALL_FAILURE_MS) buddy_ui_set_install(BUDDY_INSTALL_NONE, 0, 0);
}

void buddy_ui_set_confirm_callback(buddy_confirm_callback_t callback)
{
    ui.confirm_callback = callback;
}

void buddy_ui_request_confirmation(buddy_confirm_action_t action, const char *detail)
{
    if (action != BUDDY_CONFIRM_FIRMWARE && action != BUDDY_CONFIRM_WIFI) return;
    if (!ui.confirm.page) create_prompt(&ui.confirm, confirm_clicked);
    if (!ui.overlay_timer) ui.overlay_timer = lv_timer_create(confirm_tick, 1000, NULL);
    if (!detail) detail = "";
    if (ui.confirm_action == action && strcmp(detail, ui.confirm_detail) == 0) return;
    if (ui.confirm_action != action) ui.confirm_started = lv_tick_get();
    ui.confirm_action = action;
    snprintf(ui.confirm_detail, sizeof(ui.confirm_detail), "%s", detail);
    bool firmware = action == BUDDY_CONFIRM_FIRMWARE;
    lv_label_set_text(ui.confirm.eyebrow, BT(firmware ? BT_FIRMWARE_UPDATE : BT_WI_FI_CHANGE));
    lv_label_set_text(ui.confirm.title, BT(firmware ? BT_CONFIRM_FIRMWARE_TITLE : BT_CONFIRM_WIFI_TITLE));
    lv_label_set_text(ui.confirm.body, BT(firmware ? BT_CONFIRM_FIRMWARE_BODY : BT_CONFIRM_WIFI_BODY));
    ui.confirm.rows = 0;
    if (firmware) {
        prompt_row(&ui.confirm, BT(BT_INSTALLED_LABEL), esp_app_get_description()->version);
        prompt_row(&ui.confirm, BT(BT_NEW_LABEL), detail);
    } else prompt_row(&ui.confirm, BT(BT_NETWORK_LABEL), detail);
    /* The request carries no PC name; the paired PC's own telemetry does. */
    if (ui.have_stats) prompt_row(&ui.confirm, BT(BT_FROM_LABEL), ui.stats.hostname);
    lv_label_set_text(ui.confirm.secondary_label, BT(BT_CONFIRM_DENY));
    lv_label_set_text(ui.confirm.primary_label, BT(BT_CONFIRM_ALLOW));
    prompt_button_primary(ui.confirm.primary, ui.confirm.primary_label, C_ACCENT);
    lv_obj_set_width(ui.confirm.countdown, lv_obj_get_width(ui.confirm.track));
    prompt_layout(&ui.confirm, true);
    show_overlay(ui.confirm.page);
}

/* The carrying crab walks along the track as the image arrives; the percentage and the
 * stage sit under it. The 320 x 240 screen drops the eyebrow and uses the original-size crab. */
static void create_install_page(void)
{
    const int width = BOARD_DISPLAY_WIDTH, height = BOARD_DISPLAY_HEIGHT;
    const bool wide = width >= 800, compact = height < 300;
    const int margin = wide ? 32 : compact ? 12 : 24;
    ui.install_page = box(ui.screen, 0, 0, width, height);
    lv_obj_add_flag(ui.install_page, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(ui.install_page, color(C_BG), 0);
    lv_obj_set_style_bg_opa(ui.install_page, LV_OPA_COVER, 0);
    const lv_font_t *small = compact ? &buddy_language_font_14 : &buddy_language_font_16;
    const lv_font_t *title_font = wide ? &buddy_language_font_32 : compact ? &buddy_language_font_20 : &buddy_language_font_24;
    if (!compact) text(ui.install_page, BT(BT_FIRMWARE_UPDATE), margin, margin, width / 2, small, C_ACCENT);
    const int title_y = compact ? margin : margin + lv_font_get_line_height(small) + 6;
    ui.install_version = text(ui.install_page, "", width / 2, compact ? title_y + (lv_font_get_line_height(title_font) - lv_font_get_line_height(small)) / 2 : margin,
                              width / 2 - margin, small, C_MUTED);
    lv_obj_set_style_text_align(ui.install_version, LV_TEXT_ALIGN_RIGHT, 0);
    ui.install_title = text(ui.install_page, "", margin, title_y, width - margin * 2 - (compact ? 64 : 0), title_font, C_TEXT);
    const int track_w = width - margin * 2 - (wide ? 96 : 0), track_h = buddy_theme.track_h * (compact ? 2 : 3);
    const int track_x = (width - track_w) / 2, track_y = wide ? 250 : compact ? 112 : 244;
    ui.install_crab = !buddy_theme_has_mascot() ? 0 : compact ? 48 : 96;
    ui.install_track = box(ui.install_page, track_x, track_y, track_w, track_h);
    lv_obj_set_style_bg_color(ui.install_track, color(C_RULE), 0);
    lv_obj_set_style_bg_opa(ui.install_track, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(ui.install_track, LV_RADIUS_CIRCLE, 0);
    ui.install_fill = box(ui.install_track, 0, 0, 0, track_h);
    lv_obj_set_style_bg_opa(ui.install_fill, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(ui.install_fill, LV_RADIUS_CIRCLE, 0);
    if (ui.install_crab) {
        ui.install_mascot = lv_img_create(ui.install_page);
        lv_img_set_antialias(ui.install_mascot, false);
        lv_obj_set_size(ui.install_mascot, ui.install_crab, ui.install_crab);
        lv_obj_clear_flag(ui.install_mascot, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_y(ui.install_mascot, track_y - ui.install_crab + ui.install_crab / 16);
    }
    const lv_font_t *percent_font = compact ? &buddy_language_font_24 : &buddy_language_font_48;
    const int percent_y = track_y + track_h + (compact ? 6 : 18);
    ui.install_percent = text(ui.install_page, "", margin, percent_y, width - margin * 2, percent_font, C_TEXT);
    buddy_theme_numerals(ui.install_percent);
    lv_obj_set_style_text_align(ui.install_percent, LV_TEXT_ALIGN_CENTER, 0);
    const lv_font_t *stage_font = compact ? &buddy_language_font_14 : &buddy_language_font_20;
    ui.install_stage_label = text(ui.install_page, "", margin, percent_y + lv_font_get_line_height(percent_font) + 2,
                                  width - margin * 2, stage_font, C_MUTED);
    lv_obj_set_style_text_align(ui.install_stage_label, LV_TEXT_ALIGN_CENTER, 0);
    lv_label_set_long_mode(ui.install_stage_label, LV_LABEL_LONG_WRAP);
    /* Positions read back only after a layout pass; before it every label reports y = 0. */
    lv_obj_update_layout(ui.install_page);
    ui.install_stage_y = lv_obj_get_y(ui.install_stage_label);
    /* The small screen wraps the note onto two lines right under the stage. */
    const int stage_bottom = percent_y + lv_font_get_line_height(percent_font) + 2 + lv_font_get_line_height(stage_font);
    ui.install_note = text(ui.install_page, BT(BT_KEEP_CLAWPAL_POWERED), margin,
                           compact ? stage_bottom + 6 : height - margin - lv_font_get_line_height(small), width - margin * 2, small, C_MUTED);
    lv_label_set_long_mode(ui.install_note, LV_LABEL_LONG_WRAP);
    lv_obj_set_style_text_align(ui.install_note, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_add_flag(ui.install_page, LV_OBJ_FLAG_HIDDEN);
    if (!ui.overlay_timer) ui.overlay_timer = lv_timer_create(confirm_tick, 1000, NULL);
}

void buddy_ui_set_install(buddy_install_stage_t stage, uint32_t received, uint32_t total)
{
    if (!ui.screen) return;
    if (stage == BUDDY_INSTALL_NONE) {
        ui.install_stage = BUDDY_INSTALL_NONE;
        if (ui.install_page) visible(ui.install_page, false);
        refresh_display_power();
        return;
    }
    if (!ui.install_page) create_install_page();
    bool shown = ui.install_stage != BUDDY_INSTALL_NONE;
    ui.install_stage = stage;
    ui.install_tick = lv_tick_get();
    bool failed = stage == BUDDY_INSTALL_FAILED;
    uint32_t percent = stage == BUDDY_INSTALL_CHECKING || stage == BUDDY_INSTALL_RESTARTING ? 100 :
                       total ? (uint32_t)((uint64_t)received * 100 / total) : 0;
    if (percent > 100) percent = 100;
    const int track_w = lv_obj_get_width(ui.install_track);
    lv_obj_set_width(ui.install_fill, LV_MAX(lv_obj_get_height(ui.install_track), track_w * (int)percent / 100));
    lv_obj_set_style_bg_color(ui.install_fill, color(failed ? C_WARNING : C_ACCENT), 0);
    if (ui.install_mascot) lv_obj_set_x(ui.install_mascot, lv_obj_get_x(ui.install_track) + (track_w - ui.install_crab) * (int)percent / 100);
    char buffer[16];
    snprintf(buffer, sizeof(buffer), "%lu%%", (unsigned long)percent);
    set_label_if_changed(ui.install_percent, buffer);
    set_label_if_changed(ui.install_title, BT(failed ? BT_UPDATE_STOPPED : BT_INSTALLING_UPDATE));
    set_label_if_changed(ui.install_stage_label, BT(failed ? BT_UPDATE_FAILED_CURRENT_KEPT : stage == BUDDY_INSTALL_CHECKING ? BT_CHECKING_THE_SIGNATURE :
        stage == BUDDY_INSTALL_RESTARTING ? BT_RESTARTING_INTO_THE_NEW_VERSION : BT_RECEIVING_THE_UPDATE));
    lv_obj_set_style_text_color(ui.install_stage_label, color(failed ? C_WARNING : C_MUTED), 0);
    set_label_if_changed(ui.install_version, ui.install_detail);
    visible(ui.install_note, !failed);
    visible(ui.install_percent, !failed);
    /* A failure has no percentage; its explanation takes that space and may wrap. */
    lv_obj_set_y(ui.install_stage_label, failed ? lv_obj_get_y(ui.install_percent) : ui.install_stage_y);
    if (!shown) show_overlay(ui.install_page);
    else refresh_display_power();
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
        .pc_away = ui.pc_away,
        .needs_you = ui.alert_key[0] != 0,
        .install_stage = ui.install_stage,
        .game_fps = music_connected() && ui.stats.game.active ? ui.stats.game.fps : NAN,
    };
}
