#include "buddy_weather_units.h"
#include "buddy_theme.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "buddy_home.h"
#include "board.h"
#include "buddy_app_icons.h"
#include "buddy_home_logos.h"
#include "buddy_clock_font.h"
#include "buddy_music_font.h"
#include "buddy_mascot.h"
#include "esp_heap_caps.h"
#include "esp_random.h"
#include <math.h>
#include <stdio.h>
#include <string.h>

#define HOME_BG BUDDY_COLOR_BG
#define HOME_INK BUDDY_COLOR_TEXT
#define HOME_SECONDARY BUDDY_COLOR_MUTED
#define HOME_ACCENT BUDDY_COLOR_ACCENT
#define HOME_SEAM BUDDY_COLOR_RULE
#define CRAB_SCALE (BOARD_DISPLAY_WIDTH>=800?6:BOARD_DISPLAY_HEIGHT<400?2:4)
#define HOME_CRAB_Y (BOARD_DISPLAY_WIDTH>=800?117:0)
#define CRAB_SIZE (48 * CRAB_SCALE)
#define CRAB_BYTES (64 + CRAB_SIZE * CRAB_SIZE / 2)
#define HOME_ACTIVITY_MS (15U * 60U * 1000U)

typedef struct { buddy_text_key_t lines[5]; uint8_t count; } home_quotes_t;
static const home_quotes_t quotes[BUDDY_MASCOT_STATE_COUNT] = {
    [BUDDY_MASCOT_TYPING] = {{BT_CLICK_CLACK_CRAB_BUSINESS, BT_CLAW_CLAW, BT_MAKING_THE_PIXELS_BEHAVE, BT_CRABBY_BUSINESS}, 4},
    [BUDDY_MASCOT_IDLE] = {{BT_JUST_CRABBIN_AROUND, BT_NO_THOUGHTS_ONLY_CRAB, BT_STANDING_BY_PROFESSIONALLY, BT_ENJOYING_A_BRIEF_MOMENT_OF_NOTHING}, 4},
    [BUDDY_MASCOT_SLEEPING] = {{BT_RECHARGING_THE_CLAWS, BT_SHHH_CRAB_IS_EEPING, BT_DREAMING_OF_WARMER_WATERS, BT_UNAVAILABLE_FOR_CRAB_REASONS, BT_JUST_FIVE_MORE_MINUTES}, 5},
    [BUDDY_MASCOT_CARRYING] = {{BT_TAKING_THIS_SOMEWHERE_SAFE, BT_CARRYING_YOUR_VERY_IMPORTANT_STUFF, BT_HEAVY_LIFTING_IN_PROGRESS, BT_DON_T_WORRY_I_VE_GOT_IT, BT_ONE_FILE_AT_A_TIME}, 5},
    [BUDDY_MASCOT_MUSIC] = {{BT_CURRENTLY_UNAVAILABLE_VIBING, BT_THIS_ONE_HAS_A_GOOD_BEAT, BT_MAXIMUM_CRAB_VIBES, BT_PRETENDING_THIS_IS_A_MUSIC_VIDEO, BT_DO_NOT_DISTURB_THE_GROOVE}, 5},
    [BUDDY_MASCOT_THINKING] = {{BT_CONSULTING_THE_CRAB_BRAIN, BT_HMM_CLAWSOME_QUESTION, BT_CONNECTING_A_FEW_TINY_DOTS, BT_THINKING_VERY_CRABFULLY, BT_THERE_S_AN_IDEA_IN_HERE_SOMEWHERE}, 5},
    [BUDDY_MASCOT_WORKING] = {{BT_ON_IT, BT_DOING_TINY_CRAB_THINGS, BT_GIVE_ME_A_SEC_I_M_COOKING, BT_BUSY_MAKING_SOMETHING_HAPPEN, BT_WORKING_MY_LITTLE_CLAWS_OFF}, 5},
};

static struct {
    lv_obj_t *page, *time, *date, *crab, *greeting, *wifi;
    lv_obj_t *statuses[BUDDY_APP_COUNT], *rows[BUDDY_APP_COUNT], *titles[BUDDY_APP_COUNT], *weather_icon;
    lv_img_dsc_t frame;
    uint8_t *pixels;
    uint32_t reaction_tick, taps, activity_tick, frame_tick;
    buddy_mascot_state_t activity_state;
    const char *activity_caption, *music_caption;
    uint8_t last_quotes[BUDDY_MASCOT_STATE_COUNT];
    bool reacting, connected, wifi_ready, music_open, codex_working;
} home;

static lv_obj_t *plain(lv_obj_t *parent, int x, int y, int w, int h)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_set_pos(obj, x, y); lv_obj_set_size(obj, w, h);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
    return obj;
}
static lv_obj_t *label(lv_obj_t *parent, const char *value, int x, int y, int w, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *obj = lv_label_create(parent);
    lv_obj_set_pos(obj, x, y); lv_obj_set_width(obj, w);
    lv_obj_set_style_text_font(obj, font, 0); lv_obj_set_style_text_color(obj, lv_color_hex(color), 0);
    lv_label_set_long_mode(obj, LV_LABEL_LONG_DOT); lv_label_set_text(obj, value);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    return obj;
}
static void set_text(lv_obj_t *obj, const char *text)
{
    if (strcmp(lv_label_get_text(obj), text) != 0) lv_label_set_text(obj, text);
}
static const char *random_caption(buddy_mascot_state_t state)
{
    unsigned count = quotes[state].count;
    unsigned previous = home.last_quotes[state];
    unsigned next = esp_random() % (count - (previous < count ? 1U : 0U));
    if (previous < count && next >= previous) ++next;
    home.last_quotes[state] = next;
    return BT(quotes[state].lines[next]);
}
static void show_activity_caption(void)
{
    lv_obj_set_style_text_font(home.greeting, BOARD_DISPLAY_WIDTH<800?&buddy_language_font_16:&buddy_language_font_20, 0);
    set_text(home.greeting, buddy_retranslate(home.music_open ? home.music_caption : home.activity_caption));
}
static void choose_activity(void)
{
    if (home.music_open) {
        home.music_caption = random_caption(BUDDY_MASCOT_MUSIC);
    } else {
        static const buddy_mascot_state_t states[] = { BUDDY_MASCOT_TYPING, BUDDY_MASCOT_IDLE,
            BUDDY_MASCOT_SLEEPING, BUDDY_MASCOT_CARRYING, BUDDY_MASCOT_THINKING, BUDDY_MASCOT_WORKING };
        buddy_mascot_state_t candidates[8]; unsigned count = 0;
        for (unsigned i = 0; i < sizeof(states) / sizeof(states[0]); ++i)
            if (states[i] != home.activity_state) candidates[count++] = states[i];
        /* Actual recorded Codex work gives keyboard activity extra weight. */
        if (home.codex_working && home.activity_state != BUDDY_MASCOT_TYPING) {
            candidates[count++] = BUDDY_MASCOT_TYPING;
            candidates[count++] = BUDDY_MASCOT_TYPING;
        }
        home.activity_state = candidates[esp_random() % count];
        home.activity_caption = random_caption(home.activity_state);
        home.frame_tick = lv_tick_get();
    }
    home.activity_tick = lv_tick_get();
    if (!home.reacting) show_activity_caption();
}
static void move_crab(void *object, int32_t y) { lv_obj_set_y(object, (lv_coord_t)y); }
static void pet_crab(lv_event_t *event)
{
    (void)event;
    home.reacting = true; home.reaction_tick = lv_tick_get(); ++home.taps;
    const char *replies[] = { BT(BT_HEY_THAT_S_ME), BT(BT_A_LITTLE_CRAB_HELLO), BT(BT_YOU_MADE_MY_DAY) };
    lv_obj_set_style_text_font(home.greeting, BOARD_DISPLAY_WIDTH<800?&buddy_language_font_20:&buddy_language_font_24, 0);
    set_text(home.greeting, replies[(home.taps - 1) % 3]);
    lv_anim_del(home.crab, move_crab);
    lv_anim_t hop; lv_anim_init(&hop); lv_anim_set_var(&hop, home.crab);
    lv_anim_set_exec_cb(&hop, move_crab); lv_anim_set_values(&hop, HOME_CRAB_Y, HOME_CRAB_Y-8);
    lv_anim_set_time(&hop, 160); lv_anim_set_playback_time(&hop, 240);
    lv_anim_set_path_cb(&hop, lv_anim_path_ease_out); lv_anim_start(&hop);
    buddy_home_tick();
}

static void draw_wifi(lv_event_t *event)
{
    lv_draw_ctx_t *ctx = lv_event_get_draw_ctx(event);
    lv_area_t bounds; lv_obj_get_coords(lv_event_get_target(event), &bounds);
    lv_point_t centre = {bounds.x1 + 18, bounds.y1 + 26};
    lv_draw_arc_dsc_t arc; lv_draw_arc_dsc_init(&arc);
    arc.color = lv_color_hex(home.wifi_ready ? HOME_ACCENT : HOME_SECONDARY); arc.width = 3; arc.rounded = 1;
    lv_draw_arc(ctx, &arc, &centre, 20, 225, 315);
    lv_draw_arc(ctx, &arc, &centre, 12, 225, 315);
    lv_draw_rect_dsc_t dot; lv_draw_rect_dsc_init(&dot); dot.bg_color = arc.color; dot.radius = LV_RADIUS_CIRCLE;
    lv_area_t point = {centre.x - 2, centre.y - 2, centre.x + 2, centre.y + 2}; lv_draw_rect(ctx, &dot, &point);
}

lv_obj_t *buddy_home_create(lv_obj_t *screen, lv_event_cb_t open_app, lv_event_cb_t weather_icon)
{
    home.page = plain(screen, 0, 0, BOARD_DISPLAY_WIDTH, BOARD_DISPLAY_HEIGHT-44);
    lv_obj_set_style_bg_color(home.page, lv_color_hex(HOME_BG), 0); lv_obj_set_style_bg_opa(home.page, LV_OPA_COVER, 0);
    home.time = label(home.page, "--:--", 28, 28, 395, &buddy_clock_font, HOME_INK);
    lv_obj_set_style_text_align(home.time, LV_TEXT_ALIGN_CENTER, 0);
    home.date = label(home.page, BT(BT_WAITING_FOR_TIME), 32, 118, 387, &buddy_language_font_24, HOME_SECONDARY);
    lv_obj_set_style_text_align(home.date, LV_TEXT_ALIGN_CENTER, 0);
    home.pixels = heap_caps_malloc(CRAB_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    home.frame = (lv_img_dsc_t){.header = {.cf = LV_IMG_CF_INDEXED_4BIT, .w = CRAB_SIZE, .h = CRAB_SIZE}, .data_size = CRAB_BYTES, .data = home.pixels};
    home.crab = lv_img_create(home.page); lv_obj_set_pos(home.crab, 78, 117); lv_img_set_antialias(home.crab, false);
    lv_obj_clear_flag(home.crab, LV_OBJ_FLAG_CLICKABLE);
    /* The generous pet target covers the visible crab, including transparent pixels. */
    lv_obj_t *pet = plain(home.page, 66, 180, 312, 211); lv_obj_add_flag(pet, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(pet, pet_crab, LV_EVENT_CLICKED, NULL);
    memset(home.last_quotes, UINT8_MAX, sizeof(home.last_quotes));
    home.activity_state = BUDDY_MASCOT_IDLE;
    home.activity_tick = home.frame_tick = lv_tick_get();
    home.activity_caption = random_caption(home.activity_state);
    home.greeting = label(home.page, home.activity_caption, 20, 400, 415, &buddy_language_font_20, HOME_INK);
    lv_obj_set_height(home.greeting, 31);
    lv_obj_set_style_text_align(home.greeting, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_t *seam = plain(home.page, 441, 60, 1, 358);
    lv_obj_set_style_bg_color(seam, lv_color_hex(HOME_SEAM), 0); lv_obj_set_style_bg_opa(seam, LV_OPA_COVER, 0);
    home.wifi = plain(home.page, 744, 9, 38, 32); lv_obj_add_event_cb(home.wifi, draw_wifi, LV_EVENT_DRAW_MAIN, NULL);
    const char *names[] = { BT(BT_PC_MONITOR), BT(BT_CLOCK), BT(BT_SPOTIFY), BT(BT_TIMER), BT(BT_CODEX), BT(BT_HOME), BT(BT_WEATHER), BT(BT_CLAUDE) };
    for (unsigned app = 0; app < BUDDY_APP_COUNT; ++app) {
        if (app == BUDDY_APP_HOME) continue;
        lv_obj_t *row = plain(home.page, 464, 67, 316, 78); home.rows[app] = row;
        lv_obj_add_flag(row, LV_OBJ_FLAG_CLICKABLE); lv_obj_set_style_radius(row, 10, 0);
        lv_obj_set_style_bg_color(row, lv_color_hex(BUDDY_COLOR_PRESSED), LV_STATE_PRESSED); lv_obj_set_style_bg_opa(row, LV_OPA_COVER, LV_STATE_PRESSED);
        lv_obj_add_event_cb(row, open_app, LV_EVENT_CLICKED, (void *)(uintptr_t)app);
        lv_obj_t *icon = plain(row, 0, 2, 66, 66);
        lv_obj_set_style_border_width(icon, 1, 0); lv_obj_set_style_border_color(icon, lv_color_hex(HOME_SEAM), 0); lv_obj_set_style_radius(icon, 12, 0);
        if (app == BUDDY_APP_WEATHER) {
            home.weather_icon = plain(icon, 5, 5, 56, 56);
            lv_obj_add_event_cb(home.weather_icon, weather_icon, LV_EVENT_DRAW_MAIN, NULL);
        } else buddy_app_icon_create(icon,(buddy_app_t)app,5,5,56);
        home.titles[app] = label(row, names[app], 82, 4, 234, &buddy_language_font_24, HOME_INK);
        home.statuses[app] = label(row, BT(BT_WAITING), 82, 38, 234, &buddy_language_font_20, HOME_SECONDARY);
        lv_obj_set_height(home.statuses[app], 27);
    }
    if(BOARD_DISPLAY_WIDTH<800){
        int w=BOARD_DISPLAY_WIDTH;bool tiny=BOARD_DISPLAY_HEIGHT<400;
        lv_obj_add_flag(home.page,LV_OBJ_FLAG_SCROLLABLE|LV_OBJ_FLAG_CLICKABLE);lv_obj_set_scroll_dir(home.page,LV_DIR_VER);
        lv_obj_set_style_bg_color(home.page,lv_color_hex(HOME_ACCENT),LV_PART_SCROLLBAR);lv_obj_set_style_width(home.page,3,LV_PART_SCROLLBAR);
        lv_obj_set_pos(home.time,12,tiny?4:30);lv_obj_set_width(home.time,tiny?w-120:260);lv_obj_set_style_text_font(home.time,&buddy_language_font_48,0);
        lv_obj_set_pos(home.date,12,tiny?65:96);lv_obj_set_width(home.date,tiny?w-116:260);lv_obj_set_style_text_font(home.date,&buddy_language_font_16,0);
        lv_obj_set_pos(home.crab,w-CRAB_SIZE-12,HOME_CRAB_Y);lv_obj_set_pos(pet,w-CRAB_SIZE-12,0);lv_obj_set_size(pet,CRAB_SIZE,CRAB_SIZE);
        lv_obj_set_pos(home.greeting,12,tiny?94:190);lv_obj_set_width(home.greeting,w-24);lv_obj_set_height(home.greeting,30);lv_obj_set_style_text_font(home.greeting,&buddy_language_font_16,0);
        lv_obj_add_flag(seam,LV_OBJ_FLAG_HIDDEN);lv_obj_add_flag(home.wifi,LV_OBJ_FLAG_HIDDEN);
        for(unsigned app=0;app<BUDDY_APP_COUNT;++app)if(home.rows[app]){lv_obj_set_x(home.rows[app],12);lv_obj_set_width(home.rows[app],w-24);lv_obj_set_width(home.titles[app],w-110);lv_obj_set_style_text_font(home.titles[app],&buddy_language_font_20,0);lv_obj_set_width(home.statuses[app],w-110);lv_obj_set_style_text_font(home.statuses[app],&buddy_language_font_16,0);}
    }
    buddy_settings_t settings = buddy_settings_get(); buddy_home_apply_settings(&settings);
    buddy_home_tick();
    return home.page;
}

void buddy_home_update(const buddy_stats_t *stats, bool connected, bool wifi, bool codex_cached,
                       const char *time, const char *date, const buddy_weather_t *weather)
{
    if (!home.page) return;
    home.connected = connected;
    home.codex_working = connected && stats && ((stats->codex.activity.available &&
        !stats->codex.activity.stale && stats->codex.activity.working_count > 0) ||
        (stats->claude.activity.available && !stats->claude.activity.stale && stats->claude.activity.working_count > 0));
    bool music_open = connected && stats && stats->music.running;
    if (home.music_open != music_open) {
        home.music_open = music_open;
        home.activity_tick = home.frame_tick = lv_tick_get();
        if (music_open) home.music_caption = random_caption(BUDDY_MASCOT_MUSIC);
        if (!home.reacting) show_activity_caption();
    }
    if (home.wifi_ready != wifi) { home.wifi_ready = wifi; lv_obj_invalidate(home.wifi); }
    set_text(home.time, time); set_text(home.date, date);
    set_text(home.statuses[0], connected ? BT(BT_ONLINE) : wifi ? BT(BT_PC_OFFLINE) : BT(BT_CONNECTING_5F04AE));
    char value[80];
    const buddy_codex_window_t *window = stats && stats->codex.primary.available ? &stats->codex.primary : stats ? &stats->codex.secondary : NULL;
    if (connected && stats && stats->codex.available && window && window->available && isfinite(window->remaining_percent))
        snprintf(value, sizeof(value), BT(BT_0F_LEFT_S), window->remaining_percent, codex_cached ? BT(BT_CACHED) : "");
    else snprintf(value, sizeof(value), "%s", connected && stats && stats->codex.running ? BT(BT_LIMITS_UNAVAILABLE) : BT(BT_NOT_RUNNING));
    set_text(home.statuses[BUDDY_APP_CODEX], value);
    window = stats && stats->claude.primary.available ? &stats->claude.primary : stats ? &stats->claude.secondary : NULL;
    if (connected && stats && stats->claude.available && window && window->available && isfinite(window->remaining_percent))
        snprintf(value, sizeof(value), BT(BT_0F_LEFT_S), window->remaining_percent, stats->claude.stale ? BT(BT_CACHED) : "");
    else snprintf(value, sizeof(value), "%s", connected && stats && stats->claude.running ? BT(BT_LIMITS_UNAVAILABLE) : BT(BT_NOT_RUNNING));
    set_text(home.statuses[BUDDY_APP_CLAUDE], value);
    set_text(home.statuses[2], connected && stats && stats->music.available && stats->music.playing ?
        (stats->music.title[0] ? stats->music.title : BT(BT_PLAYING)) : BT(BT_NOT_PLAYING));
    if (weather->available) {
        char temperature[24];buddy_settings_t settings=buddy_settings_get();
        buddy_weather_format_temperature(temperature,sizeof(temperature),weather->temperature_c,settings.weather_fahrenheit);
        snprintf(value,sizeof(value),"%s%s",temperature,weather->stale?BT(BT_CACHED):"");
    }
    else snprintf(value, sizeof(value), BT(BT_UNAVAILABLE));
    set_text(home.statuses[BUDDY_APP_WEATHER], value);
    set_text(home.statuses[BUDDY_APP_CLOCK], time);
    buddy_ui_state_t ui = buddy_ui_get_state();
    snprintf(value, sizeof(value), "%lu:%02lu%s", (unsigned long)(ui.timer_remaining_s / 60), (unsigned long)(ui.timer_remaining_s % 60), ui.timer_running ? BT(BT_RUNNING_12D2CF) : BT(BT_READY_5BA178));
    set_text(home.statuses[BUDDY_APP_TIMER], value); lv_obj_invalidate(home.weather_icon);
}

void buddy_home_apply_settings(const buddy_settings_t *settings)
{
    if (!home.page) return;
    for (unsigned app = 0; app < BUDDY_APP_COUNT; ++app) if (home.rows[app]) lv_obj_add_flag(home.rows[app], LV_OBJ_FLAG_HIDDEN);
    for (unsigned i = 0; i < settings->home_count; ++i) {
        unsigned app = settings->home_order[i];
        if (app >= BUDDY_APP_COUNT || app == BUDDY_APP_HOME || !(settings->enabled_apps & (1U << app))) continue;
        lv_obj_t *row = home.rows[app];
        lv_obj_set_y(row,BOARD_DISPLAY_WIDTH<800?(BOARD_DISPLAY_HEIGHT<400?124:228)+(int)i*76:(settings->home_count > 4 ? 49 + (int)i * 76 : 67 + (int)i * 89));
        lv_obj_set_height(row, settings->home_count > 4 ? 70 : 78);
        lv_obj_clear_flag(row, LV_OBJ_FLAG_HIDDEN);
    }
}

void buddy_home_tick(void)
{
    if (!home.page || lv_obj_has_flag(home.page, LV_OBJ_FLAG_HIDDEN)) return;
    uint32_t elapsed = lv_tick_elaps(home.reaction_tick);
    if (home.reacting && elapsed >= 4800) {
        home.reacting = false;
        show_activity_caption();
    }
    if (!home.reacting && lv_tick_elaps(home.activity_tick) >= HOME_ACTIVITY_MS) choose_activity();
    unsigned frame = ((home.reacting ? elapsed : lv_tick_elaps(home.frame_tick)) / BUDDY_MASCOT_FRAME_MS) % BUDDY_MASCOT_FRAME_COUNT;
    buddy_mascot_state_t state = home.reacting ? BUDDY_MASCOT_HAPPY :
        home.music_open ? BUDDY_MASCOT_MUSIC : home.activity_state;
    /* Sleeping/thinking contain bubbles above the crab. Keep every supplied
     * frame below the date and above the caption without changing its pixels. */
    if (!home.reacting) {
        int y = BOARD_DISPLAY_WIDTH<800?HOME_CRAB_Y:(state == BUDDY_MASCOT_SLEEPING ? 135 : state == BUDDY_MASCOT_THINKING ? 129 : 117);
        if (lv_obj_get_y(home.crab) != y) lv_obj_set_y(home.crab, y);
    }
    const lv_img_dsc_t *source = &buddy_mascot_frames[state][frame];
    if (!home.pixels) { lv_img_set_src(home.crab, source); return; }
    memcpy(home.pixels, source->data, 64);
    for (unsigned y = 0; y < 48; ++y) {
        uint8_t *row = home.pixels + 64 + y * CRAB_SCALE * (CRAB_SIZE / 2);
        for (unsigned x = 0; x < 48; ++x) {
            unsigned pair = source->data[64 + y * 24 + x / 2];
            unsigned pixel = x % 2 ? pair & 15 : pair >> 4;
            for (unsigned s = 0; s < CRAB_SCALE / 2; ++s) row[x * (CRAB_SCALE / 2) + s] = (pixel << 4) | pixel;
        }
        for (unsigned s = 1; s < CRAB_SCALE; ++s) memcpy(row + s * (CRAB_SIZE / 2), row, CRAB_SIZE / 2);
    }
    lv_img_cache_invalidate_src(&home.frame); lv_img_set_src(home.crab, &home.frame); lv_obj_invalidate(home.crab);
}
