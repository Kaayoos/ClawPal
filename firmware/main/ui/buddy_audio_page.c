#include "buddy_audio_page.h"
#include "buddy_shortcuts.h"
#include "buddy_shortcut_data.h"
#include "buddy_shortcuts_page.h"
#include "buddy_app_icons.h"
#include "buddy_theme.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "board.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define WIDTH BOARD_DISPLAY_WIDTH
#define BODY (BOARD_DISPLAY_HEIGHT - 44)
#define WIDE (BOARD_DISPLAY_WIDTH >= 800)
#define TINY (BOARD_DISPLAY_HEIGHT < 400)
#define CHANNELS BUDDY_MIXER_CHANNELS
/* A finger's own move outranks telemetry for a while: the PC's next frames may predate it. */
#define HOLD_MS 2000
/* Moves are sent at most this often while a fader is dragged; the last one always goes. */
#define SEND_EVERY_MS 80
#define FLUSH_MS 40
/* A press becomes a drag only once it clearly moves up or down. */
#define DRAG_START_PX 8
#define ICON_CACHE 12

/* Fixed proportions per screen; a strip is built from top to bottom in this order. */
typedef struct {
    int x0, y0, area_w, area_h, gap, per_view, max_w, radius;
    int icon_y, icon, name_y, value_y, well_top, well_bottom, well_w, button_w, button_h, button_bottom;
    const lv_font_t *name_font, *value_font;
} geometry_t;

static geometry_t geometry(void)
{
    if (WIDE) return (geometry_t){ 28, 16, 744, BODY - 28, 12, 5, 156, 16,
        18, 36, 62, 86, 128, 72, 58, 84, 44, 16, &buddy_language_font_16, &buddy_language_font_24 };
    if (!TINY) return (geometry_t){ 12, 12, WIDTH - 24, BODY - 24, 10, 4, 130, 14,
        16, 32, 54, 74, 108, 66, 50, 72, 40, 16, &buddy_language_font_14, &buddy_language_font_20 };
    return (geometry_t){ 8, 6, WIDTH - 16, BODY - 12, 6, 4, 80, 10,
        8, 22, -1, 36, 58, 42, 34, 50, 28, 6, NULL, &buddy_language_font_14 };
}

typedef struct {
    buddy_mixer_channel_t channel; /* what the PC said, or the finger's move while held */
    int shown;                     /* the fill as drawn, eased towards channel.volume */
    uint32_t hold_tick;
    bool held, send_volume, send_mute;
} strip_state_t;

static struct {
    lv_obj_t *page, *row, *empty, *empty_art;
    lv_obj_t *strips[CHANNELS], *icons[CHANNELS], *images[CHANNELS], *names[CHANNELS], *values[CHANNELS];
    lv_obj_t *wells[CHANNELS], *buttons[CHANNELS], *glyphs[CHANNELS];
    strip_state_t state[CHANNELS];
    unsigned count;
    bool connected, have_stats, present;
    buddy_audio_send_t send;
    lv_timer_t *flush_timer;
    uint32_t last_send;
    int drag, strip_w, well_h;
    bool dragging, drag_lost;
    lv_point_t press;
    int drag_start_y, drag_start_volume;
    struct { int16_t id; uint8_t *pixels; lv_img_dsc_t dsc; } icon_cache[ICON_CACHE];
    char status[48];
} au = {.drag = -1};

static lv_obj_t *box(lv_obj_t *parent, int x, int y, int width, int height)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_size(obj, width, height);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE);
    return obj;
}

static lv_obj_t *label(lv_obj_t *parent, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *obj = lv_label_create(parent);
    lv_label_set_long_mode(obj, LV_LABEL_LONG_DOT);
    lv_label_set_text(obj, "");
    lv_obj_set_style_text_font(obj, font, 0);
    lv_obj_set_style_text_color(obj, lv_color_hex(color), 0);
    lv_obj_set_style_text_align(obj, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static void visible(lv_obj_t *obj, bool show)
{
    if (!obj) return;
    if (show) lv_obj_clear_flag(obj, LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(obj, LV_OBJ_FLAG_HIDDEN);
}

static void set_text(lv_obj_t *obj, const char *value)
{
    if (obj && strcmp(lv_label_get_text(obj), value)) lv_label_set_text(obj, value);
}

static const buddy_mixer_channel_t *channel_at(int index)
{
    return index >= 0 && (unsigned)index < au.count ? &au.state[index].channel : NULL;
}

static uint32_t channel_color(const buddy_mixer_channel_t *channel)
{
    return channel->color && channel->color < BUDDY_SHORTCUT_COLOR_COUNT ? buddy_shortcut_colors[channel->color] : BUDDY_COLOR_ACCENT;
}

static const char *channel_name(const buddy_mixer_channel_t *channel)
{
    switch (channel->kind) {
    case BUDDY_MIXER_OUTPUT: return BT(BT_MASTER);
    case BUDDY_MIXER_MIC: return BT(BT_MIC);
    case BUDDY_MIXER_SYSTEM: return BT(BT_SYSTEM_SOUNDS);
    default: return channel->name;
    }
}

static lv_img_dsc_t *cached_icon(unsigned id)
{
    for (unsigned i = 0; i < ICON_CACHE; ++i)
        if (au.icon_cache[i].pixels && au.icon_cache[i].id == (int16_t)id) return &au.icon_cache[i].dsc;
    return NULL;
}

/* The speaker's cone and waves fill only the left 12-78 of its 100-unit square; nudge it
 * so it sits in the middle of its box. The muted speaker and the microphone are balanced. */
static lv_area_t centred(const lv_area_t *area, unsigned icon)
{
    lv_area_t a = *area;
    if (icon == BUDDY_SHORTCUT_ICON_SPEAKER) lv_area_move(&a, lv_area_get_width(area) * 5 / 100, 0);
    return a;
}

/* The well: a rounded slot the channel's colour fills from the bottom, with a grip at the
 * fill's edge. A muted channel keeps its level, drawn in grey. */
static void draw_well(lv_event_t *event)
{
    lv_obj_t *obj = lv_event_get_target(event);
    int index = (int)(uintptr_t)lv_event_get_user_data(event);
    const buddy_mixer_channel_t *channel = channel_at(index);
    if (!channel) return;
    lv_draw_ctx_t *ctx = lv_event_get_draw_ctx(event);
    lv_area_t a;
    lv_obj_get_coords(obj, &a);
    int width = lv_area_get_width(&a), height = lv_area_get_height(&a), radius = width * 32 / 100;
    bool outlined = buddy_theme.outlined_cards;
    lv_draw_rect_dsc_t rect;
    lv_draw_rect_dsc_init(&rect);
    rect.radius = radius;
    rect.bg_color = lv_color_hex(outlined ? BUDDY_COLOR_SURFACE : BUDDY_COLOR_BG);
    rect.bg_opa = LV_OPA_COVER;
    lv_draw_rect(ctx, &rect, &a);
    int fill = LV_CLAMP(0, au.state[index].shown, 100) * height / 100;
    if (fill <= 0) return;
    lv_area_t level = {a.x1, a.y2 - fill + 1, a.x2, a.y2};
    /* Clip to the well's rounded shape, so a low level hugs its corners instead of poking out. */
    lv_draw_mask_radius_param_t mask;
    lv_draw_mask_radius_init(&mask, &a, radius, false);
    int16_t mask_id = lv_draw_mask_add(&mask, NULL);
    lv_draw_rect_dsc_init(&rect);
    rect.radius = 0;
    rect.bg_color = lv_color_hex(channel->muted ? BUDDY_COLOR_MUTED : channel_color(channel));
    rect.bg_opa = channel->muted ? LV_OPA_40 : LV_OPA_COVER;
    lv_draw_rect(ctx, &rect, &level);
    lv_draw_mask_remove_id(mask_id);
    lv_draw_mask_free_param(&mask);
    if (fill < 16) return;
    lv_draw_line_dsc_t line;
    lv_draw_line_dsc_init(&line);
    line.color = lv_color_hex(BUDDY_COLOR_BG);
    line.opa = LV_OPA_50;
    line.width = TINY ? 3 : 4;
    line.round_start = line.round_end = 1;
    int centre = (a.x1 + a.x2) / 2, half = LV_MAX(6, width * 22 / 100), y = level.y1 + (TINY ? 6 : 9);
    lv_point_t p = {centre - half, y}, q = {centre + half, y};
    lv_draw_line(ctx, &line, &p, &q);
}

/* The channel's face: the PC's own app icon when it sent one, a line icon for the output,
 * microphone and Windows' sounds, and otherwise the app's initial on its colour. */
static void draw_icon(lv_event_t *event)
{
    lv_obj_t *obj = lv_event_get_target(event);
    int index = (int)(uintptr_t)lv_event_get_user_data(event);
    const buddy_mixer_channel_t *channel = channel_at(index);
    if (!channel) return;
    lv_draw_ctx_t *ctx = lv_event_get_draw_ctx(event);
    lv_area_t a;
    lv_obj_get_coords(obj, &a);
    lv_color_t ink = lv_color_hex(channel_color(channel));
    if (channel->kind != BUDDY_MIXER_APP) {
        unsigned icon = channel->kind == BUDDY_MIXER_MIC ? (channel->muted ? BUDDY_SHORTCUT_ICON_MIC_OFF : BUDDY_SHORTCUT_ICON_MIC) :
            channel->kind == BUDDY_MIXER_SYSTEM ? BUDDY_SHORTCUT_ICON_BELL :
            channel->muted ? BUDDY_SHORTCUT_ICON_SPEAKER_OFF : BUDDY_SHORTCUT_ICON_SPEAKER;
        lv_area_t at = centred(&a, icon);
        buddy_shortcut_icon_draw(ctx, &at, icon, ink);
        return;
    }
    if (cached_icon(channel->id)) return;
    int size = lv_area_get_width(&a);
    lv_draw_rect_dsc_t rect;
    lv_draw_rect_dsc_init(&rect);
    rect.radius = size / 4;
    rect.bg_color = ink;
    rect.bg_opa = LV_OPA_COVER;
    lv_draw_rect(ctx, &rect, &a);
    char initial[5] = "";
    const unsigned char *name = (const unsigned char *)channel->name;
    size_t length = name[0] < 0x80 ? 1 : name[0] < 0xE0 ? 2 : name[0] < 0xF0 ? 3 : 4;
    if (strlen(channel->name) < length) length = 0;
    memcpy(initial, name, length);
    if (length == 1 && initial[0] >= 'a' && initial[0] <= 'z') initial[0] = (char)(initial[0] - 'a' + 'A');
    lv_draw_label_dsc_t text;
    lv_draw_label_dsc_init(&text);
    text.font = size >= 32 ? &buddy_language_font_20 : &buddy_language_font_14;
    text.color = lv_color_hex(BUDDY_COLOR_BG);
    text.align = LV_TEXT_ALIGN_CENTER;
    lv_area_t line = a;
    int line_h = lv_font_get_line_height(text.font);
    line.y1 = a.y1 + (size - line_h) / 2;
    line.y2 = line.y1 + line_h - 1;
    lv_draw_label(ctx, &text, &line, initial, NULL);
}

static void draw_glyph(lv_event_t *event)
{
    lv_obj_t *obj = lv_event_get_target(event);
    int index = (int)(uintptr_t)lv_event_get_user_data(event);
    const buddy_mixer_channel_t *channel = channel_at(index);
    if (!channel) return;
    lv_area_t a;
    lv_obj_get_coords(obj, &a);
    unsigned icon = channel->kind == BUDDY_MIXER_MIC ? (channel->muted ? BUDDY_SHORTCUT_ICON_MIC_OFF : BUDDY_SHORTCUT_ICON_MIC) :
        channel->muted ? BUDDY_SHORTCUT_ICON_SPEAKER_OFF : BUDDY_SHORTCUT_ICON_SPEAKER;
    lv_area_t at = centred(&a, icon);
    buddy_shortcut_icon_draw(lv_event_get_draw_ctx(event), &at, icon, lv_color_hex(channel->muted ? BUDDY_COLOR_WARNING : BUDDY_COLOR_MUTED));
}

/* Everything one strip shows follows from its channel and the finger's last move. */
static void style_strip(int index)
{
    const buddy_mixer_channel_t *channel = channel_at(index);
    if (!channel) return;
    char value[16];
    if (channel->muted) snprintf(value, sizeof(value), "%s", BT(BT_MUTED));
    else snprintf(value, sizeof(value), "%u%%", (unsigned)channel->volume);
    set_text(au.values[index], value);
    lv_obj_set_style_text_color(au.values[index], lv_color_hex(channel->muted ? BUDDY_COLOR_WARNING : BUDDY_COLOR_TEXT), 0);
    set_text(au.names[index], channel_name(channel));
    lv_img_dsc_t *icon = channel->kind == BUDDY_MIXER_APP ? cached_icon(channel->id) : NULL;
    if (icon) {
        if (lv_img_get_src(au.images[index]) != icon) {
            lv_img_set_src(au.images[index], icon);
            /* A new source re-centres the pivot; the zoom scales from the corner. */
            lv_img_set_pivot(au.images[index], 0, 0);
        }
        lv_img_set_zoom(au.images[index], (uint16_t)(256 * lv_obj_get_width(au.icons[index]) / BUDDY_SHORTCUT_ICON_SIZE));
    }
    visible(au.images[index], icon != NULL);
    lv_obj_set_style_opa(au.icons[index], channel->muted ? LV_OPA_50 : LV_OPA_COVER, 0);
    lv_obj_t *button = au.buttons[index];
    lv_obj_set_style_border_color(button, lv_color_hex(channel->muted ? BUDDY_COLOR_WARNING : BUDDY_COLOR_RULE), 0);
    lv_obj_set_style_bg_color(button, channel->muted ? lv_color_mix(lv_color_hex(BUDDY_COLOR_WARNING), lv_color_hex(BUDDY_COLOR_BG), 56) :
        lv_color_hex(buddy_theme.outlined_cards ? BUDDY_COLOR_BG : BUDDY_COLOR_SURFACE), 0);
    lv_obj_invalidate(au.icons[index]);
    lv_obj_invalidate(au.wells[index]);
    lv_obj_invalidate(au.glyphs[index]);
}

static void set_shown(void *var, int32_t value)
{
    int index = (int)(intptr_t)var - 1;
    if (index < 0 || index >= CHANNELS) return;
    au.state[index].shown = value;
    lv_obj_invalidate(au.wells[index]);
}

/* The fill eases to a level the PC reports; a finger's move is drawn where the finger is. */
static void show_level(int index, bool ease)
{
    strip_state_t *state = &au.state[index];
    void *var = (void *)(intptr_t)(index + 1);
    lv_anim_del(var, set_shown);
    uint32_t time = 220U * buddy_theme.motion_pct / 100U;
    if (!ease || !time || state->shown == state->channel.volume) {
        set_shown(var, state->channel.volume);
        return;
    }
    lv_anim_t anim;
    lv_anim_init(&anim);
    lv_anim_set_var(&anim, var);
    lv_anim_set_exec_cb(&anim, set_shown);
    lv_anim_set_values(&anim, state->shown, state->channel.volume);
    lv_anim_set_time(&anim, time);
    lv_anim_set_path_cb(&anim, lv_anim_path_ease_out);
    lv_anim_start(&anim);
}

static const char *unavailable(void)
{
    if (!au.connected || !au.have_stats) return BT(BT_PC_OFFLINE);
    if (!au.present) return BT(BT_UPDATE_CLAWPAL_ON_YOUR_PC);
    if (!au.count) return BT(BT_NO_SOUND_DEVICES_ON_YOUR_PC);
    return NULL;
}

const char *buddy_audio_page_status(void)
{
    const char *reason = unavailable();
    if (reason) return reason;
    for (unsigned i = 0; i < au.count; ++i) {
        const buddy_mixer_channel_t *channel = &au.state[i].channel;
        if (channel->kind != BUDDY_MIXER_OUTPUT) continue;
        if (channel->muted) return BT(BT_SOUND_MUTED);
        snprintf(au.status, sizeof(au.status), BT(BT_VOLUME_U), (unsigned)channel->volume);
        return au.status;
    }
    return BT(BT_NO_SOUND_DEVICES_ON_YOUR_PC);
}

static void row_scrolls(bool allowed);

/* Strips share the row evenly up to a comfortable width. More channels than fit keep a
 * narrower width that leaves part of the next strip in view, which says the row scrolls. */
static void layout(void)
{
    if (!au.page) return;
    const geometry_t g = geometry();
    const char *reason = unavailable();
    visible(au.row, !reason);
    visible(au.empty, reason != NULL);
    visible(au.empty_art, reason != NULL);
    if (reason) {
        set_text(au.empty, reason);
        for (unsigned i = 0; i < CHANNELS; ++i) visible(au.strips[i], false);
        return;
    }
    int count = (int)au.count, width, x = 0;
    bool scrolls = count > g.per_view;
    if (scrolls) width = (g.area_w - g.gap * g.per_view) * 10 / (g.per_view * 10 + 4);
    else {
        width = LV_MIN(g.max_w, (g.area_w - g.gap * (count - 1)) / count);
        x = (g.area_w - (width * count + g.gap * (count - 1))) / 2;
    }
    au.strip_w = width;
    int well_w = LV_MIN(g.well_w, width * 46 / 100), height = g.area_h;
    au.well_h = height - g.well_top - g.well_bottom;
    for (unsigned i = 0; i < CHANNELS; ++i) {
        lv_obj_t *strip = au.strips[i];
        visible(strip, (int)i < count);
        if ((int)i >= count) continue;
        lv_obj_set_pos(strip, x + (int)i * (width + g.gap), 0);
        lv_obj_set_size(strip, width, height);
        lv_obj_set_pos(au.icons[i], (width - g.icon) / 2, g.icon_y);
        lv_obj_set_size(au.icons[i], g.icon, g.icon);
        if (au.names[i]) {
            lv_obj_set_pos(au.names[i], 6, g.name_y);
            lv_obj_set_size(au.names[i], width - 12, lv_font_get_line_height(g.name_font));
        }
        lv_obj_set_pos(au.values[i], 4, g.value_y);
        lv_obj_set_size(au.values[i], width - 8, lv_font_get_line_height(g.value_font));
        lv_obj_set_pos(au.wells[i], (width - well_w) / 2, g.well_top);
        lv_obj_set_size(au.wells[i], well_w, au.well_h);
        int button_w = LV_MIN(g.button_w, width - 12);
        lv_obj_set_pos(au.buttons[i], (width - button_w) / 2, height - g.button_bottom - g.button_h);
        lv_obj_set_size(au.buttons[i], button_w, g.button_h);
        int glyph = g.button_h * 55 / 100;
        lv_obj_set_pos(au.glyphs[i], (button_w - glyph) / 2, (g.button_h - glyph) / 2);
        lv_obj_set_size(au.glyphs[i], glyph, glyph);
        if (scrolls) lv_obj_add_flag(strip, LV_OBJ_FLAG_SNAPPABLE);
        else lv_obj_clear_flag(strip, LV_OBJ_FLAG_SNAPPABLE);
        style_strip((int)i);
    }
    if (!scrolls) lv_obj_scroll_to_x(au.row, 0, LV_ANIM_OFF);
    row_scrolls(au.drag < 0 || !au.dragging);
}

/* One command at a time goes out; mute first, then the newest level, no faster than
 * SEND_EVERY_MS. A refused send stays marked and goes on the next tick. */
static void flush(lv_timer_t *timer)
{
    (void)timer;
    bool waiting = false;
    for (unsigned i = 0; i < au.count; ++i) {
        strip_state_t *state = &au.state[i];
        if (state->held && lv_tick_elaps(state->hold_tick) >= HOLD_MS && !state->send_mute && !state->send_volume && au.drag != (int)i)
            state->held = false;
        waiting |= state->held;
    }
    for (unsigned i = 0; i < au.count && au.send; ++i) {
        strip_state_t *state = &au.state[i];
        if (state->send_mute) {
            if (au.send("mixer_mute", BUDDY_MIXER_MUTE_VALUE(state->channel.id, state->channel.muted))) {
                state->send_mute = false;
                au.last_send = lv_tick_get();
            }
            return;
        }
    }
    if (lv_tick_elaps(au.last_send) < SEND_EVERY_MS) return;
    for (unsigned i = 0; i < au.count && au.send; ++i) {
        strip_state_t *state = &au.state[i];
        if (!state->send_volume) continue;
        if (au.send("mixer_volume", BUDDY_MIXER_VOLUME_VALUE(state->channel.id, state->channel.volume))) {
            state->send_volume = false;
            au.last_send = lv_tick_get();
        }
        return;
    }
    if (!waiting && au.flush_timer) lv_timer_pause(au.flush_timer);
}

static void hold(int index)
{
    au.state[index].held = true;
    au.state[index].hold_tick = lv_tick_get();
    if (au.flush_timer) lv_timer_resume(au.flush_timer);
}

static void set_volume(int index, int volume)
{
    strip_state_t *state = &au.state[index];
    volume = LV_CLAMP(0, volume, 100);
    hold(index);
    if (state->channel.volume == volume) return;
    state->channel.volume = (uint8_t)volume;
    /* Moving a muted fader up is asking to hear it again; the PC unmutes it too. */
    if (volume > 0) state->channel.muted = false;
    state->send_volume = true;
    show_level(index, false);
    style_strip(index);
}

/* The row scrolls only when it holds more strips than fit, and never under a dragged fader. */
static void row_scrolls(bool allowed)
{
    if (allowed && (int)au.count > geometry().per_view) lv_obj_add_flag(au.row, LV_OBJ_FLAG_SCROLLABLE);
    else lv_obj_clear_flag(au.row, LV_OBJ_FLAG_SCROLLABLE);
}

static int well_volume(int index, int y)
{
    lv_area_t a;
    lv_obj_get_coords(au.wells[index], &a);
    return (a.y2 - y) * 100 / LV_MAX(1, lv_area_get_height(&a));
}

static void strip_event(lv_event_t *event)
{
    int index = (int)(uintptr_t)lv_event_get_user_data(event);
    if (!channel_at(index)) return;
    lv_event_code_t code = lv_event_get_code(event);
    lv_indev_t *indev = lv_indev_get_act();
    lv_point_t point = {0, 0};
    if (indev) lv_indev_get_point(indev, &point);
    if (code == LV_EVENT_PRESSED) {
        au.drag = index;
        au.dragging = au.drag_lost = false;
        au.press = point;
        return;
    }
    if (au.drag != index) return;
    if (code == LV_EVENT_PRESSING) {
        int dy = au.press.y - point.y, dx = au.press.x - point.x;
        if (!au.dragging) {
            if (LV_ABS(dy) < DRAG_START_PX || LV_ABS(dy) <= LV_ABS(dx)) return;
            au.dragging = true;
            row_scrolls(false);
            au.drag_start_y = point.y;
            au.drag_start_volume = au.state[index].channel.volume;
        }
        set_volume(index, au.drag_start_volume + (au.drag_start_y - point.y) * 100 / LV_MAX(1, au.well_h));
    } else if (code == LV_EVENT_PRESS_LOST) {
        /* The row took the swipe to scroll. */
        au.drag_lost = true;
        if (au.dragging) hold(index);
        au.drag = -1;
        row_scrolls(true);
    } else if (code == LV_EVENT_RELEASED) {
        if (!au.dragging && !au.drag_lost && indev && !lv_indev_get_scroll_obj(indev)) {
            /* A tap in the well sets its level there. */
            lv_area_t a;
            lv_obj_get_coords(au.wells[index], &a);
            a.x1 -= 10;
            a.x2 += 10;
            if (_lv_area_is_point_on(&a, &point, 0)) set_volume(index, well_volume(index, point.y));
        } else if (au.dragging) hold(index);
        au.drag = -1;
        au.dragging = false;
        row_scrolls(true);
    }
}

static void mute_event(lv_event_t *event)
{
    int index = (int)(uintptr_t)lv_event_get_user_data(event);
    strip_state_t *state = &au.state[index];
    if (!channel_at(index)) return;
    state->channel.muted = !state->channel.muted;
    state->send_mute = true;
    hold(index);
    style_strip(index);
    flush(NULL);
}

lv_obj_t *buddy_audio_page_create(lv_obj_t *parent)
{
    const geometry_t g = geometry();
    lv_obj_t *page = au.page = box(parent, 0, 0, WIDTH, BODY);
    lv_obj_set_style_bg_color(page, lv_color_hex(BUDDY_COLOR_BG), 0);
    lv_obj_set_style_bg_opa(page, LV_OPA_COVER, 0);
    au.row = box(page, g.x0, g.y0, g.area_w, g.area_h);
    lv_obj_set_scroll_dir(au.row, LV_DIR_HOR);
    lv_obj_set_scrollbar_mode(au.row, LV_SCROLLBAR_MODE_OFF);
    lv_obj_set_scroll_snap_x(au.row, LV_SCROLL_SNAP_START);
    /* Strips stay whole at the row's edges; nothing draws past its rounded corners. */
    lv_obj_add_flag(au.row, LV_OBJ_FLAG_CLICKABLE);
    int art = WIDE ? 72 : TINY ? 40 : 56, art_y = WIDE ? 130 : TINY ? 40 : 130;
    au.empty_art = buddy_app_icon_create(page, BUDDY_APP_AUDIO, (WIDTH - art) / 2, art_y, art);
    au.empty = label(page, WIDE ? &buddy_language_font_20 : &buddy_language_font_16, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(au.empty, LV_LABEL_LONG_WRAP);
    int pad = WIDE ? 28 : 12;
    lv_obj_set_pos(au.empty, pad, art_y + art + (TINY ? 10 : 18));
    lv_obj_set_size(au.empty, WIDTH - pad * 2, 52);
    bool outlined = buddy_theme.outlined_cards;
    for (unsigned i = 0; i < CHANNELS; ++i) {
        void *index = (void *)(uintptr_t)i;
        lv_obj_t *strip = au.strips[i] = box(au.row, 0, 0, 10, 10);
        lv_obj_add_flag(strip, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_style_radius(strip, g.radius, 0);
        lv_obj_set_style_bg_color(strip, lv_color_hex(BUDDY_COLOR_SURFACE), 0);
        lv_obj_set_style_bg_opa(strip, outlined ? LV_OPA_TRANSP : LV_OPA_COVER, 0);
        lv_obj_set_style_border_width(strip, outlined ? 1 : 0, 0);
        lv_obj_set_style_border_color(strip, lv_color_hex(BUDDY_COLOR_RULE), 0);
        lv_obj_add_event_cb(strip, strip_event, LV_EVENT_PRESSED, index);
        lv_obj_add_event_cb(strip, strip_event, LV_EVENT_PRESSING, index);
        lv_obj_add_event_cb(strip, strip_event, LV_EVENT_RELEASED, index);
        lv_obj_add_event_cb(strip, strip_event, LV_EVENT_PRESS_LOST, index);
        au.icons[i] = box(strip, 0, 0, g.icon, g.icon);
        lv_obj_add_event_cb(au.icons[i], draw_icon, LV_EVENT_DRAW_MAIN, index);
        au.images[i] = lv_img_create(au.icons[i]);
        lv_img_set_pivot(au.images[i], 0, 0);
        lv_obj_clear_flag(au.images[i], LV_OBJ_FLAG_CLICKABLE);
        visible(au.images[i], false);
        if (g.name_font) au.names[i] = label(strip, g.name_font, BUDDY_COLOR_MUTED);
        au.values[i] = label(strip, g.value_font, BUDDY_COLOR_TEXT);
        au.wells[i] = box(strip, 0, 0, 10, 10);
        lv_obj_add_event_cb(au.wells[i], draw_well, LV_EVENT_DRAW_MAIN, index);
        lv_obj_t *button = au.buttons[i] = box(strip, 0, 0, 10, 10);
        lv_obj_add_flag(button, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_clear_flag(button, LV_OBJ_FLAG_PRESS_LOCK);
        lv_obj_set_style_radius(button, WIDE ? 12 : TINY ? 8 : 10, 0);
        lv_obj_set_style_bg_opa(button, LV_OPA_COVER, 0);
        lv_obj_set_style_border_width(button, TINY ? 1 : 2, 0);
        lv_obj_set_style_bg_color(button, lv_color_hex(BUDDY_COLOR_PRESSED), LV_STATE_PRESSED);
        lv_obj_add_event_cb(button, mute_event, LV_EVENT_CLICKED, index);
        au.glyphs[i] = box(button, 0, 0, 10, 10);
        lv_obj_add_event_cb(au.glyphs[i], draw_glyph, LV_EVENT_DRAW_MAIN, index);
    }
    for (unsigned i = 0; i < ICON_CACHE; ++i) au.icon_cache[i].id = -1;
    au.flush_timer = lv_timer_create(flush, FLUSH_MS, NULL);
    lv_timer_pause(au.flush_timer);
    layout();
    return page;
}

void buddy_audio_page_set_sender(buddy_audio_send_t send) { au.send = send; }

static bool same_channels(const buddy_mixer_t *mixer)
{
    if (mixer->count != au.count) return false;
    for (unsigned i = 0; i < au.count; ++i) {
        const buddy_mixer_channel_t *a = &mixer->channels[i], *b = &au.state[i].channel;
        if (a->id != b->id || a->kind != b->kind || a->color != b->color || strcmp(a->name, b->name)) return false;
    }
    return true;
}

void buddy_audio_page_update(const buddy_stats_t *stats)
{
    const buddy_mixer_t *mixer = &stats->mixer;
    bool was_present = au.have_stats && au.present;
    au.have_stats = true;
    au.present = mixer->present;
    if (same_channels(mixer) && was_present == mixer->present) {
        for (unsigned i = 0; i < au.count; ++i) {
            strip_state_t *state = &au.state[i];
            if (state->held) continue;
            const buddy_mixer_channel_t *fresh = &mixer->channels[i];
            if (fresh->volume == state->channel.volume && fresh->muted == state->channel.muted) continue;
            state->channel.volume = fresh->volume;
            state->channel.muted = fresh->muted;
            show_level((int)i, true);
            style_strip((int)i);
        }
        return;
    }
    /* The list changed: keep each surviving channel's held move, by ID, wherever it moved to. */
    strip_state_t next[CHANNELS];
    memset(next, 0, sizeof(next));
    for (unsigned i = 0; i < mixer->count; ++i) {
        next[i].channel = mixer->channels[i];
        next[i].shown = mixer->channels[i].volume;
        for (unsigned j = 0; j < au.count; ++j) {
            if (au.state[j].channel.id != mixer->channels[i].id) continue;
            if (au.state[j].held) {
                next[i] = au.state[j];
                next[i].channel.kind = mixer->channels[i].kind;
                next[i].channel.color = mixer->channels[i].color;
                memcpy(next[i].channel.name, mixer->channels[i].name, sizeof(next[i].channel.name));
            }
            next[i].shown = au.state[j].shown;
            break;
        }
    }
    if (au.drag >= 0) au.drag = -1;
    for (unsigned i = 0; i < CHANNELS; ++i) lv_anim_del((void *)(intptr_t)(i + 1), set_shown);
    memcpy(au.state, next, sizeof(next));
    au.count = mixer->count;
    layout();
    for (unsigned i = 0; i < au.count; ++i) show_level((int)i, true);
}

void buddy_audio_page_set_connected(bool connected)
{
    if (connected == au.connected) return;
    au.connected = connected;
    if (!connected) {
        au.have_stats = false;
        au.drag = -1;
        for (unsigned i = 0; i < au.count; ++i) au.state[i].held = au.state[i].send_mute = au.state[i].send_volume = false;
    }
    layout();
}

bool buddy_audio_page_set_icon(unsigned id, uint8_t *pixels)
{
    if (!au.page || id > BUDDY_MIXER_MAX_ID) {
        free(pixels);
        return false;
    }
    /* The same ID's icon is replaced; otherwise a free entry, then one no channel shows now. */
    int slot = -1;
    for (unsigned i = 0; i < ICON_CACHE && slot < 0; ++i) if (au.icon_cache[i].pixels && au.icon_cache[i].id == (int16_t)id) slot = (int)i;
    for (unsigned i = 0; i < ICON_CACHE && slot < 0; ++i) if (!au.icon_cache[i].pixels) slot = (int)i;
    for (unsigned i = 0; i < ICON_CACHE && slot < 0; ++i) {
        bool shown = false;
        for (unsigned c = 0; c < au.count; ++c) shown |= au.state[c].channel.id == au.icon_cache[i].id;
        if (!shown) slot = (int)i;
    }
    if (slot < 0) {
        free(pixels);
        return false;
    }
    lv_img_dsc_t *dsc = &au.icon_cache[slot].dsc;
    for (unsigned c = 0; c < CHANNELS; ++c) if (lv_img_get_src(au.images[c]) == dsc) {
        lv_img_set_src(au.images[c], NULL);
        visible(au.images[c], false);
    }
    free(au.icon_cache[slot].pixels);
    au.icon_cache[slot].id = (int16_t)id;
    au.icon_cache[slot].pixels = pixels;
    *dsc = (lv_img_dsc_t){
        .header = {.cf = LV_IMG_CF_TRUE_COLOR_ALPHA, .w = BUDDY_SHORTCUT_ICON_SIZE, .h = BUDDY_SHORTCUT_ICON_SIZE},
        .data_size = BUDDY_SHORTCUT_ICON_BYTES, .data = pixels,
    };
    lv_img_cache_invalidate_src(dsc);
    for (unsigned c = 0; c < au.count; ++c) if (au.state[c].channel.id == id) style_strip((int)c);
    return true;
}
