#include "buddy_printer_page.h"
#include "buddy_printer_watch.h"
#include "buddy_settings.h"
#include "buddy_theme.h"
#include "buddy_mascot.h"
#include "buddy_clock_font.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "board.h"
#include "esp_heap_caps.h"
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

#define WIDTH BOARD_DISPLAY_WIDTH
#define BODY (BOARD_DISPLAY_HEIGHT - 44)
#define WIDE (BOARD_DISPLAY_WIDTH >= 800)
#define TINY (BOARD_DISPLAY_HEIGHT < 400)
/* The wide screen doubles the crab, like the weather page; smaller ones use it as drawn. */
#define CRAB (WIDE ? 96 : 48)
#define CRAB_BYTES (64 + 96 * 96 / 2)
/* The drawing moves at about 15 frames a second while it has something to show. */
#define ANIMATION_MS 66

/* A stage on the left with the printer drawn at work (and the crab watching, in looks that have
 * it); on the right the state, a large percentage, the time, a progress bar and two temperature
 * tiles. Positions per screen class: 800 x 480, 480 x 480 and 320 x 240. */
typedef struct {
    int margin, head_y, stage_x, stage_y, stage_w, stage_h, col_x, col_w;
    int pill_y, file_y, number_y, time_y, done_y, bar_y, bar_h, layer_y, tiles_y, tile_h, chip_w, chip_h;
    const lv_font_t *head, *pill, *file, *number, *unit, *time, *small, *tile_value;
} geometry_t;

static geometry_t geometry(void)
{
    if (WIDE) return (geometry_t){ 28, 26, 28, 84, 360, 344, 412, 360,
        84, 120, 146, 248, 282, 318, 12, 338, 370, 58, 96, 36,
        &buddy_language_font_24, &buddy_language_font_16, &buddy_language_font_20, &buddy_clock_font, &buddy_language_font_32,
        &buddy_language_font_24, &buddy_language_font_16, &buddy_language_font_20 };
    if (!TINY) return (geometry_t){ 12, 10, 12, 50, 196, 236, 222, WIDTH - 234,
        50, 82, 104, 168, 196, 300, 10, 316, 346, 62, 80, 32,
        &buddy_language_font_20, &buddy_language_font_14, &buddy_language_font_16, &buddy_language_font_48, &buddy_language_font_20,
        &buddy_language_font_20, &buddy_language_font_14, &buddy_language_font_20 };
    return (geometry_t){ 12, 4, 12, 30, 120, 158, 142, WIDTH - 154,
        30, 92, 50, 112, 112, 180, 5, 132, 152, 0, 64, 26,
        &buddy_language_font_16, &buddy_language_font_14, &buddy_language_font_14, &buddy_language_font_32, &buddy_language_font_16,
        &buddy_language_font_14, &buddy_language_font_14, &buddy_language_font_14 };
}

static struct {
    lv_obj_t *page, *head, *chip, *chip_label, *stage, *art, *crab;
    lv_obj_t *pill, *dot, *pill_label, *file, *number, *unit, *time, *done, *bar, *layer, *temps_line;
    lv_obj_t *tiles[2], *tile_names[2], *tile_values[2], *heat[2], *heat_fill[2];
    lv_obj_t *empty_title, *empty_body;
    uint8_t *crab_pixels;
    lv_img_dsc_t crab_frame;
    buddy_mascot_state_t crab_state;
    buddy_printers_t printers;
    bool connected;
    uint8_t selected; /* id of the printer on screen; 0 follows the PC's order */
    char status_text[64];
} pr;

static lv_obj_t *box(lv_obj_t *parent, int x, int y, int w, int h)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_size(obj, w, h);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE);
    return obj;
}

static lv_obj_t *card(lv_obj_t *parent, int x, int y, int w, int h, int radius)
{
    lv_obj_t *obj = box(parent, x, y, w, h);
    lv_obj_set_style_bg_color(obj, lv_color_hex(BUDDY_COLOR_SURFACE), 0);
    lv_obj_set_style_bg_opa(obj, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(obj, buddy_theme_round(radius), 0);
    /* Outlined looks draw the same card as a hairline on the page. */
    if (buddy_theme.outlined_cards) {
        lv_obj_set_style_bg_opa(obj, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(obj, 1, 0);
        lv_obj_set_style_border_color(obj, lv_color_hex(BUDDY_COLOR_RULE), 0);
    }
    return obj;
}

static lv_obj_t *label(lv_obj_t *parent, int x, int y, int w, const lv_font_t *font, uint32_t ink)
{
    lv_obj_t *obj = lv_label_create(parent);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_width(obj, w);
    lv_obj_set_style_text_font(obj, font, 0);
    lv_obj_set_style_text_color(obj, lv_color_hex(ink), 0);
    lv_obj_set_style_text_line_space(obj, 4, 0);
    lv_label_set_long_mode(obj, LV_LABEL_LONG_DOT);
    lv_label_set_text(obj, "");
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static lv_obj_t *bar(lv_obj_t *parent, int x, int y, int w, int h, int range)
{
    lv_obj_t *obj = lv_bar_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_size(obj, w, h);
    lv_bar_set_range(obj, 0, range);
    lv_obj_set_style_bg_color(obj, lv_color_hex(BUDDY_COLOR_RULE), LV_PART_MAIN);
    lv_obj_set_style_bg_opa(obj, LV_OPA_COVER, LV_PART_MAIN);
    lv_obj_set_style_bg_opa(obj, LV_OPA_COVER, LV_PART_INDICATOR);
    lv_obj_set_style_radius(obj, buddy_theme_round(LV_RADIUS_CIRCLE), LV_PART_MAIN);
    lv_obj_set_style_radius(obj, buddy_theme_round(LV_RADIUS_CIRCLE), LV_PART_INDICATOR);
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

static void set_ink(lv_obj_t *obj, uint32_t ink)
{
    if (obj) lv_obj_set_style_text_color(obj, lv_color_hex(ink), 0);
}

static const buddy_printer_t *shown(void)
{
    if (!pr.connected || !pr.printers.count) return NULL;
    for (unsigned i = 0; i < pr.printers.count; ++i)
        if (pr.printers.items[i].id == pr.selected) return &pr.printers.items[i];
    return &pr.printers.items[0];
}

const char *buddy_printer_state_text(const buddy_printer_t *p)
{
    if (!p) return "";
    if (p->state == BUDDY_PRINT_PAUSED && p->reason == BUDDY_PRINT_REASON_FILAMENT) return BT(BT_PRINT_OUT_OF_FILAMENT);
    if (p->state == BUDDY_PRINT_PAUSED && p->reason == BUDDY_PRINT_REASON_ERROR) return BT(BT_PRINT_NEEDS_ATTENTION);
    switch (p->state) {
    case BUDDY_PRINT_IDLE: return BT(BT_PRINT_READY);
    case BUDDY_PRINT_PREPARING: return BT(BT_PRINT_PREPARING);
    case BUDDY_PRINT_PRINTING: return BT(BT_PRINT_PRINTING);
    case BUDDY_PRINT_PAUSED: return BT(BT_PRINT_PAUSED);
    case BUDDY_PRINT_FINISHED: return BT(BT_PRINT_FINISHED);
    case BUDDY_PRINT_FAILED: return BT(BT_PRINT_STOPPED);
    default: return BT(BT_PRINT_OFFLINE);
    }
}

void buddy_printer_done_at(char *buffer, size_t size, const buddy_printer_t *p)
{
    buffer[0] = 0;
    time_t now = time(NULL);
    if (!p || !p->remaining_s || now < 1700000000 || (p->state != BUDDY_PRINT_PRINTING && p->state != BUDDY_PRINT_PREPARING)) return;
    time_t done = now + (time_t)p->remaining_s;
    struct tm local;
    localtime_r(&done, &local);
    bool hours24 = buddy_settings_get().use_24h;
    char hour[16], stamp[64];
    strftime(hour, sizeof(hour), hours24 ? "%H:%M" : "%I:%M", &local);
    /* A print that ends tomorrow or later names its day too. */
    struct tm today;
    localtime_r(&now, &today);
    if (local.tm_yday != today.tm_yday || local.tm_year != today.tm_year) {
        char day[16];
        strftime(day, sizeof(day), "%d.%m", &local);
        snprintf(stamp, sizeof(stamp), "%s %s%s%s", day, hour, hours24 ? "" : " ", hours24 ? "" : BT(local.tm_hour < 12 ? BT_AM : BT_PM));
    } else snprintf(stamp, sizeof(stamp), "%s%s%s", hour, hours24 ? "" : " ", hours24 ? "" : BT(local.tm_hour < 12 ? BT_AM : BT_PM));
    snprintf(buffer, size, BT(BT_PRINT_DONE_AT_S), stamp);
}

static void duration(char *buffer, size_t size, uint32_t seconds)
{
    uint32_t minutes = (seconds + 59U) / 60U;
    if (minutes >= 1440U) snprintf(buffer, size, BT(BT_LU_D_LU_H), (unsigned long)(minutes / 1440U), (unsigned long)(minutes / 60U % 24U));
    else if (minutes >= 60U) snprintf(buffer, size, BT(BT_LU_H_LU_MIN), (unsigned long)(minutes / 60U), (unsigned long)(minutes % 60U));
    else snprintf(buffer, size, BT(BT_LU_MIN), (unsigned long)minutes);
}

static void temperature(char *buffer, size_t size, float now, float target)
{
    if (!isfinite(now)) snprintf(buffer, size, "--");
    else if (isfinite(target) && target >= 1.f) snprintf(buffer, size, "%.0f\xC2\xB0 / %.0f\xC2\xB0", now, target);
    else snprintf(buffer, size, "%.0f\xC2\xB0", now);
}

static uint32_t state_ink(const buddy_printer_t *p)
{
    if (!p || p->state == BUDDY_PRINT_OFFLINE || p->state == BUDDY_PRINT_IDLE) return BUDDY_COLOR_MUTED;
    if (p->state == BUDDY_PRINT_FAILED || (p->state == BUDDY_PRINT_PAUSED && p->reason != BUDDY_PRINT_REASON_USER)) return BUDDY_COLOR_WARNING;
    return BUDDY_COLOR_ACCENT;
}

/* ---- The printer at work ---- */

static lv_color_t mix(uint32_t a, uint32_t b, uint8_t amount) { return lv_color_mix(lv_color_hex(a), lv_color_hex(b), amount); }

/* 0..1..0 over `period` milliseconds. */
static float swing(uint32_t period, uint32_t offset)
{
    float t = (float)((lv_tick_get() + offset) % period) / (float)period;
    return t < 0.5f ? t * 2.f : 2.f - t * 2.f;
}

/* The part's outline, a little vase: width at height t (0 at the bed, 1 at the top). */
static float part_width(float t)
{
    return 0.58f + 0.30f * sinf(3.14159f * (0.18f + 0.78f * t)) - 0.12f * t * t;
}

static void fill(lv_draw_ctx_t *ctx, int x1, int y1, int x2, int y2, lv_color_t ink, lv_opa_t opa, int radius)
{
    if (x2 < x1 || y2 < y1) return;
    lv_draw_rect_dsc_t rect;
    lv_draw_rect_dsc_init(&rect);
    rect.bg_color = ink;
    rect.bg_opa = opa;
    rect.radius = buddy_theme_round((lv_coord_t)radius);
    lv_area_t area = { (lv_coord_t)x1, (lv_coord_t)y1, (lv_coord_t)x2, (lv_coord_t)y2 };
    lv_draw_rect(ctx, &rect, &area);
}

static void stroke(lv_draw_ctx_t *ctx, int x1, int y1, int x2, int y2, lv_color_t ink, int width, lv_opa_t opa)
{
    lv_draw_line_dsc_t line;
    lv_draw_line_dsc_init(&line);
    line.color = ink;
    line.width = (lv_coord_t)width;
    line.opa = opa;
    line.round_start = line.round_end = !buddy_theme.square;
    lv_point_t a = { (lv_coord_t)x1, (lv_coord_t)y1 }, b = { (lv_coord_t)x2, (lv_coord_t)y2 };
    lv_draw_line(ctx, &line, &a, &b);
}

static void ring(lv_draw_ctx_t *ctx, int x, int y, int radius, lv_color_t ink, int width, uint16_t start, uint16_t end)
{
    lv_draw_arc_dsc_t arc;
    lv_draw_arc_dsc_init(&arc);
    arc.color = ink;
    arc.width = (lv_coord_t)width;
    arc.rounded = !buddy_theme.square;
    lv_point_t centre = { (lv_coord_t)x, (lv_coord_t)y };
    lv_draw_arc(ctx, &arc, &centre, (uint16_t)radius, start, end);
}

static void draw_printer(lv_event_t *event)
{
    lv_draw_ctx_t *ctx = lv_event_get_draw_ctx(event);
    lv_area_t a;
    lv_obj_get_coords(lv_event_get_target(event), &a);
    const int w = lv_area_get_width(&a), h = lv_area_get_height(&a);
    const buddy_printer_t *p = shown();
    const uint8_t state = p ? p->state : BUDDY_PRINT_OFFLINE;
    const bool live = p && state != BUDDY_PRINT_OFFLINE;
    const int line = w >= 240 ? 3 : 2;
    const uint32_t frame_ink = live ? BUDDY_COLOR_MUTED : BUDDY_COLOR_RULE;
    const lv_color_t accent = lv_color_hex(live ? state_ink(p) == BUDDY_COLOR_MUTED ? BUDDY_COLOR_ACCENT : state_ink(p) : BUDDY_COLOR_RULE);

    /* The frame: two uprights, a top with the X rail under it, and the bed near the bottom. */
    const int left = a.x1 + w * 10 / 100, right = a.x1 + w * 90 / 100, top = a.y1 + h * 14 / 100, bottom = a.y1 + h * 92 / 100;
    const int rail = top + h * 6 / 100, bed = a.y1 + h * 80 / 100, bed_h = LV_MAX(4, h / 40);
    fill(ctx, left, top, right, top + line * 2, lv_color_hex(frame_ink), LV_OPA_COVER, 2);
    stroke(ctx, left + line / 2, top, left + line / 2, bottom, lv_color_hex(frame_ink), line, LV_OPA_COVER);
    stroke(ctx, right - line / 2, top, right - line / 2, bottom, lv_color_hex(frame_ink), line, LV_OPA_COVER);
    fill(ctx, left - line, bottom, right + line, bottom + line * 2, lv_color_hex(frame_ink), LV_OPA_COVER, 2);
    /* The spool on top, with the filament running down to the head. */
    const int spool_r = LV_MAX(6, w * 7 / 100), spool_x = right - spool_r - line * 2, spool_y = top - spool_r - line;
    const bool runout = p && state == BUDDY_PRINT_PAUSED && p->reason == BUDDY_PRINT_REASON_FILAMENT;
    ring(ctx, spool_x, spool_y, spool_r, lv_color_hex(frame_ink), line, 0, 360);
    if (!runout && live) ring(ctx, spool_x, spool_y, spool_r - line * 2, mix(state_ink(p) == BUDDY_COLOR_MUTED ? BUDDY_COLOR_ACCENT : state_ink(p), BUDDY_COLOR_SURFACE, 150), line * 2, 0, 360);
    else if (runout) ring(ctx, spool_x, spool_y, spool_r - line * 2, lv_color_hex(BUDDY_COLOR_WARNING), line, 0, 360);
    /* The bed warms in colour as it heats. */
    float warmth = p && isfinite(p->bed) ? LV_CLAMP(0.f, (p->bed - 30.f) / 50.f, 1.f) : 0.f;
    lv_color_t bed_ink = lv_color_mix(lv_color_hex(BUDDY_COLOR_WARNING), lv_color_hex(frame_ink), (uint8_t)(warmth * 200));
    fill(ctx, left + w * 6 / 100, bed, right - w * 6 / 100, bed + bed_h, bed_ink, LV_OPA_COVER, 2);

    /* The part grows with the print, one band per layer. */
    const int cx = (left + right) / 2, max_w = (right - left) * 62 / 100, max_h = (bed - rail) * 58 / 100;
    const int layer_px = LV_MAX(2, h / 60);
    int progress = !p ? 0 : state == BUDDY_PRINT_FINISHED ? 100 : (state == BUDDY_PRINT_PRINTING || state == BUDDY_PRINT_PAUSED || state == BUDDY_PRINT_FAILED) ? p->progress : 0;
    int height = max_h * progress / 100;
    lv_color_t part_ink = live ? accent : lv_color_hex(BUDDY_COLOR_RULE);
    lv_color_t part_dark = lv_color_mix(part_ink, lv_color_hex(BUDDY_COLOR_SURFACE), 190);
    int part_top = bed, top_left = cx, top_right = cx;
    for (int y = 0, band = 0; y + layer_px <= height; y += layer_px, ++band) {
        int half = (int)(max_w * part_width((float)y / (float)max_h)) / 2;
        part_top = bed - y - layer_px;
        top_left = cx - half; top_right = cx + half;
        fill(ctx, cx - half, part_top, cx + half, bed - y - 1, band % 2 ? part_dark : part_ink, LV_OPA_COVER, 0);
    }

    /* The head: on the layer it prints, parked under the rail otherwise. */
    const int head_w = LV_MAX(14, w * 16 / 100), head_h = LV_MAX(10, h * 9 / 100), nozzle = LV_MAX(4, head_h / 3);
    int head_x = cx, head_y = rail + line * 2;
    bool hot = p && isfinite(p->nozzle) && p->nozzle >= 150.f;
    if (live && state == BUDDY_PRINT_PRINTING) {
        int half = (int)(max_w * part_width((float)height / (float)max_h)) / 2;
        head_x = cx - half + (int)(swing(1800, 0) * (float)(half * 2));
        head_y = part_top - head_h - nozzle - 1;
        /* The layer being laid down trails behind the nozzle. */
        fill(ctx, cx - half, part_top - layer_px, head_x, part_top - 1, part_ink, LV_OPA_COVER, 0);
    } else if (live && state == BUDDY_PRINT_PREPARING) {
        /* Levelling: the head sweeps slowly over the empty bed. */
        head_x = left + w * 18 / 100 + (int)(swing(3200, 0) * (float)(right - left - w * 36 / 100));
        head_y = bed - head_h - nozzle - 2 - (int)(swing(800, 0) * (float)(h / 30));
    } else if (live && state == BUDDY_PRINT_FINISHED) head_x = left + head_w / 2 + w * 4 / 100;
    else if (live && state == BUDDY_PRINT_PAUSED) head_x = right - head_w / 2 - w * 4 / 100;
    /* The gantry the head rides on spans the frame at its height. */
    stroke(ctx, left, head_y + head_h / 2, right, head_y + head_h / 2, lv_color_hex(frame_ink), line, LV_OPA_COVER);
    /* Filament from the spool to the head. */
    if (live && !runout) {
        lv_color_t thread = lv_color_mix(part_ink, lv_color_hex(BUDDY_COLOR_SURFACE), 160);
        stroke(ctx, spool_x, spool_y + spool_r, spool_x, rail - line, thread, LV_MAX(1, line - 1), LV_OPA_COVER);
        stroke(ctx, spool_x, rail - line, head_x + head_w / 4, head_y, thread, LV_MAX(1, line - 1), LV_OPA_COVER);
    }
    fill(ctx, head_x - head_w / 2, head_y, head_x + head_w / 2, head_y + head_h, lv_color_hex(live ? BUDDY_COLOR_TEXT : BUDDY_COLOR_RULE), LV_OPA_COVER, 3);
    fill(ctx, head_x - head_w / 2 + head_w / 5, head_y + head_h / 3, head_x + head_w / 2 - head_w / 5, head_y + head_h / 3 + LV_MAX(2, head_h / 6),
         lv_color_hex(BUDDY_COLOR_SURFACE), LV_OPA_COVER, 1);
    fill(ctx, head_x - nozzle / 2, head_y + head_h, head_x + nozzle / 2, head_y + head_h + nozzle, lv_color_hex(live ? BUDDY_COLOR_MUTED : BUDDY_COLOR_RULE), LV_OPA_COVER, 1);
    if (hot && live) {
        /* A hot nozzle glows at its tip. */
        lv_opa_t glow = state == BUDDY_PRINT_PRINTING ? (lv_opa_t)(150 + swing(600, 0) * 105) : LV_OPA_COVER;
        fill(ctx, head_x - nozzle / 2 - 1, head_y + head_h + nozzle - 2, head_x + nozzle / 2 + 1, head_y + head_h + nozzle + 1,
             lv_color_hex(BUDDY_COLOR_WARNING), glow, LV_RADIUS_CIRCLE);
    }

    /* What the state adds: pause bars, sparkles on a finished part, tangles on a failed one. */
    if (live && state == BUDDY_PRINT_PAUSED) {
        lv_opa_t opa = (lv_opa_t)(110 + swing(1400, 0) * 145);
        int bar_w = LV_MAX(4, w / 30), bar_h = LV_MAX(14, h / 9), y = LV_MIN(part_top, bed) - bar_h - h / 12;
        fill(ctx, cx - bar_w * 2, y, cx - bar_w, y + bar_h, lv_color_hex(state_ink(p)), opa, 2);
        fill(ctx, cx + bar_w, y, cx + bar_w * 2, y + bar_h, lv_color_hex(state_ink(p)), opa, 2);
    } else if (live && state == BUDDY_PRINT_FINISHED) {
        const int spots[3][3] = { { -60, -26, 0 }, { 58, -14, 500 }, { 8, -44, 1000 } };
        for (unsigned i = 0; i < 3; ++i) {
            float t = swing(1500, (uint32_t)spots[i][2]);
            int x = cx + spots[i][0] * max_w / 120, y = part_top + spots[i][1] * h / 200, r = LV_MAX(2, (int)(t * (float)(w / 28)));
            stroke(ctx, x - r, y, x + r, y, lv_color_hex(BUDDY_COLOR_ACCENT), LV_MAX(1, line - 1), (lv_opa_t)(80 + t * 175));
            stroke(ctx, x, y - r, x, y + r, lv_color_hex(BUDDY_COLOR_ACCENT), LV_MAX(1, line - 1), (lv_opa_t)(80 + t * 175));
        }
    } else if (live && state == BUDDY_PRINT_FAILED) {
        int r = LV_MAX(4, w / 22), y = part_top - r / 2;
        lv_color_t tangle = lv_color_hex(BUDDY_COLOR_WARNING);
        ring(ctx, top_left + r, y, r, tangle, LV_MAX(1, line - 1), 180, 360);
        ring(ctx, (top_left + top_right) / 2, y - r / 2, r + r / 2, tangle, LV_MAX(1, line - 1), 200, 20);
        ring(ctx, top_right - r, y + r / 3, r, tangle, LV_MAX(1, line - 1), 150, 340);
    }
}

/* The crab watching the printer, in looks that have it. */
static buddy_mascot_state_t crab_state(const buddy_printer_t *p)
{
    if (!p) return pr.connected ? BUDDY_MASCOT_IDLE : BUDDY_MASCOT_SLEEPING;
    switch (p->state) {
    case BUDDY_PRINT_PRINTING: return BUDDY_MASCOT_TYPING;
    case BUDDY_PRINT_PREPARING: return BUDDY_MASCOT_WORKING;
    case BUDDY_PRINT_PAUSED: return BUDDY_MASCOT_THINKING;
    case BUDDY_PRINT_FINISHED: return BUDDY_MASCOT_HAPPY;
    case BUDDY_PRINT_FAILED: return BUDDY_MASCOT_ERROR;
    case BUDDY_PRINT_IDLE: return BUDDY_MASCOT_IDLE;
    default: return BUDDY_MASCOT_SLEEPING;
    }
}

static void feed_crab(void)
{
    if (!pr.crab) return;
    unsigned frame = (lv_tick_get() / BUDDY_MASCOT_FRAME_MS) % BUDDY_MASCOT_FRAME_COUNT;
    const lv_img_dsc_t *source = &buddy_mascot_frames[pr.crab_state][frame];
    if (!pr.crab_pixels) { lv_img_set_src(pr.crab, source); return; }
    /* LVGL's indexed decoder reads by line, so the doubled crab is expanded by hand, as elsewhere. */
    memcpy(pr.crab_pixels, source->data, 64);
    for (unsigned y = 0; y < 48; ++y) {
        uint8_t *row = pr.crab_pixels + 64 + y * 2 * 48;
        for (unsigned x = 0; x < 24; ++x) {
            uint8_t pair = source->data[64 + y * 24 + x];
            row[x * 2] = (pair & 0xf0) | (pair >> 4);
            row[x * 2 + 1] = (pair & 0x0f) | (pair << 4);
        }
        memcpy(row + 48, row, 48);
    }
    lv_img_cache_invalidate_src(&pr.crab_frame);
    lv_img_set_src(pr.crab, &pr.crab_frame);
    lv_obj_invalidate(pr.crab);
}

static void animate(lv_timer_t *timer)
{
    (void)timer;
    if (!pr.page || lv_obj_has_flag(pr.page, LV_OBJ_FLAG_HIDDEN)) return;
    const buddy_printer_t *p = shown();
    /* Only a printer that is doing something moves; an idle one stays still. */
    if (p && (p->state == BUDDY_PRINT_PRINTING || p->state == BUDDY_PRINT_PREPARING || p->state == BUDDY_PRINT_PAUSED || p->state == BUDDY_PRINT_FINISHED))
        lv_obj_invalidate(pr.art);
    if (pr.dot) lv_obj_set_style_bg_opa(pr.dot, p && p->state == BUDDY_PRINT_PRINTING && (lv_tick_get() / 700U) % 2U ? LV_OPA_40 : LV_OPA_COVER, 0);
    if (buddy_theme_has_mascot()) feed_crab();
}

/* ---- The readings ---- */

static void place_unit(void)
{
    lv_obj_update_layout(pr.number);
    int x = lv_obj_get_x(pr.number) + lv_obj_get_width(pr.number) + (TINY ? 2 : 4);
    lv_obj_set_pos(pr.unit, x, lv_obj_get_y(pr.number) + (WIDE ? 14 : TINY ? 4 : 6));
}

static void heat(unsigned i, float now, float target, float scale)
{
    if (!pr.heat_fill[i]) return;
    float level = isfinite(now) ? LV_CLAMP(0.f, now / scale, 1.f) : 0.f;
    int track = lv_obj_get_height(pr.heat[i]);
    int fill_h = LV_MAX(track / 8, (int)(level * (float)track));
    lv_obj_set_height(pr.heat_fill[i], fill_h);
    lv_obj_set_y(pr.heat_fill[i], track - fill_h);
    bool warm = isfinite(now) && isfinite(target) && target >= 1.f;
    lv_obj_set_style_bg_color(pr.heat_fill[i], lv_color_hex(warm ? BUDDY_COLOR_WARNING : BUDDY_COLOR_MUTED), 0);
}

static void refresh(void)
{
    if (!pr.page) return;
    const buddy_printer_t *p = shown();
    bool empty = !p;
    lv_obj_t *readings[] = { pr.pill, pr.file, pr.number, pr.unit, pr.time, pr.done, pr.bar, pr.layer, pr.tiles[0], pr.tiles[1], pr.temps_line };
    for (unsigned i = 0; i < sizeof(readings) / sizeof(readings[0]); ++i) visible(readings[i], !empty);
    visible(pr.empty_title, empty);
    visible(pr.empty_body, empty);
    visible(pr.chip, pr.connected && pr.printers.count > 1);
    buddy_mascot_state_t crab = crab_state(p);
    if (crab != pr.crab_state) { pr.crab_state = crab; if (buddy_theme_has_mascot()) feed_crab(); }
    lv_obj_invalidate(pr.art);
    if (empty) {
        set_text(pr.head, BT(BT_PRINTER));
        set_text(pr.empty_title, pr.connected ? BT(BT_PRINT_NO_PRINTERS_TITLE) : BT(BT_PC_OFFLINE));
        set_text(pr.empty_body, pr.connected ? BT(BT_PRINT_NO_PRINTERS_BODY) : BT(BT_PRINT_PC_OFFLINE_BODY));
        return;
    }
    char buffer[96], stamp[64];
    set_text(pr.head, p->name);
    if (pr.printers.count > 1) {
        snprintf(buffer, sizeof(buffer), "%u / %u  " LV_SYMBOL_RIGHT, (unsigned)(p - pr.printers.items) + 1, (unsigned)pr.printers.count);
        set_text(pr.chip_label, buffer);
    }
    /* The state as a pill: a dot in the state's colour, which breathes while printing. */
    uint32_t ink = state_ink(p);
    set_text(pr.pill_label, buddy_printer_state_text(p));
    set_ink(pr.pill_label, ink);
    lv_obj_set_style_bg_color(pr.dot, lv_color_hex(ink), 0);
    lv_obj_update_layout(pr.pill_label);
    lv_obj_set_width(pr.pill, lv_obj_get_x(pr.pill_label) + lv_obj_get_width(pr.pill_label) + (TINY ? 8 : 14));

    bool working = buddy_print_active(p->state) || p->state == BUDDY_PRINT_FINISHED || p->state == BUDDY_PRINT_FAILED;
    bool preparing = p->state == BUDDY_PRINT_PREPARING;
    int progress = p->state == BUDDY_PRINT_FINISHED ? 100 : working && !preparing ? p->progress : 0;
    if (working && !preparing) snprintf(buffer, sizeof(buffer), "%d", progress);
    else snprintf(buffer, sizeof(buffer), "--");
    set_text(pr.number, buffer);
    set_ink(pr.number, working ? BUDDY_COLOR_TEXT : BUDDY_COLOR_MUTED);
    visible(pr.unit, working && !preparing);
    set_text(pr.unit, "%");
    place_unit();
    set_text(pr.file, p->file[0] ? p->file : working ? BT(BT_PRINT_PRINTING) : BT(BT_PRINT_NOTHING_PRINTING));
    set_ink(pr.file, p->file[0] ? BUDDY_COLOR_TEXT : BUDDY_COLOR_MUTED);

    /* Time left and when it will be done; once over, the state says it instead. */
    buddy_printer_done_at(stamp, sizeof(stamp), p);
    if (buddy_print_active(p->state) && p->remaining_s) {
        char left[48];
        duration(left, sizeof(left), p->remaining_s);
        if (TINY) snprintf(buffer, sizeof(buffer), "%s%s%s", left, stamp[0] ? "  \xC2\xB7  " : "", stamp[0] ? strchr(stamp, ' ') ? strrchr(stamp, ' ') + 1 : stamp : "");
        else snprintf(buffer, sizeof(buffer), "%s", left);
        set_text(pr.time, buffer);
        set_text(pr.done, TINY ? "" : stamp);
    } else {
        set_text(pr.time, (working && !buddy_print_active(p->state)) || preparing ? buddy_printer_state_text(p) : "--");
        set_text(pr.done, "");
    }
    set_ink(pr.time, buddy_print_active(p->state) ? BUDDY_COLOR_TEXT : ink);
    lv_bar_set_value(pr.bar, progress, LV_ANIM_OFF);
    lv_obj_set_style_bg_color(pr.bar, lv_color_hex(ink == BUDDY_COLOR_MUTED ? BUDDY_COLOR_ACCENT : ink), LV_PART_INDICATOR);
    if (p->layers && (p->layer || p->state == BUDDY_PRINT_FINISHED)) snprintf(buffer, sizeof(buffer), "%s %lu / %lu", BT(BT_PRINT_LAYER), (unsigned long)p->layer, (unsigned long)p->layers);
    else buffer[0] = 0;
    set_text(pr.layer, buffer);

    /* Nozzle and bed, each with a heat bar that fills as it warms. */
    char nozzle[32], bed[32];
    temperature(nozzle, sizeof(nozzle), p->nozzle, p->nozzle_target);
    temperature(bed, sizeof(bed), p->bed, p->bed_target);
    if (TINY) {
        snprintf(buffer, sizeof(buffer), "%s %.0f\xC2\xB0  %s %.0f\xC2\xB0", BT(BT_PRINT_NOZZLE), isfinite(p->nozzle) ? p->nozzle : 0.f,
                 BT(BT_PRINT_BED), isfinite(p->bed) ? p->bed : 0.f);
        set_text(pr.temps_line, isfinite(p->nozzle) || isfinite(p->bed) ? buffer : "");
    } else {
        set_text(pr.tile_values[0], nozzle);
        set_text(pr.tile_values[1], bed);
        heat(0, p->nozzle, p->nozzle_target, 300.f);
        heat(1, p->bed, p->bed_target, 120.f);
    }
}

static void next_printer(lv_event_t *event)
{
    (void)event;
    const buddy_printer_t *p = shown();
    if (!p || pr.printers.count < 2) return;
    pr.selected = pr.printers.items[((unsigned)(p - pr.printers.items) + 1) % pr.printers.count].id;
    refresh();
}

lv_obj_t *buddy_printer_page_create(lv_obj_t *parent)
{
    const geometry_t g = geometry();
    pr.page = box(parent, 0, 0, WIDE ? 800 : WIDTH, WIDE ? 440 : BODY);
    pr.head = label(pr.page, g.margin, g.head_y, WIDTH - 2 * g.margin - g.chip_w - 12, g.head, BUDDY_COLOR_TEXT);
    /* More than one printer: a chip in the corner steps through them. */
    pr.chip = box(pr.page, WIDTH - g.margin - g.chip_w, g.head_y - (TINY ? 2 : 4), g.chip_w, g.chip_h);
    lv_obj_add_flag(pr.chip, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_radius(pr.chip, buddy_theme_round(LV_RADIUS_CIRCLE), 0);
    lv_obj_set_style_bg_color(pr.chip, lv_color_hex(BUDDY_COLOR_PRESSED), 0);
    lv_obj_set_style_bg_color(pr.chip, lv_color_hex(BUDDY_COLOR_RULE), LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(pr.chip, LV_OPA_COVER, 0);
    pr.chip_label = label(pr.chip, 0, 0, g.chip_w, TINY ? &buddy_language_font_14 : &buddy_language_font_16, BUDDY_COLOR_TEXT);
    lv_obj_set_style_text_align(pr.chip_label, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(pr.chip_label, LV_ALIGN_CENTER, 0, 0);
    lv_obj_add_event_cb(pr.chip, next_printer, LV_EVENT_CLICKED, NULL);
    visible(pr.chip, false);

    /* The stage: the printer drawn at work, and the crab beside it. */
    pr.stage = card(pr.page, g.stage_x, g.stage_y, g.stage_w, g.stage_h, WIDE ? 18 : 14);
    const bool crab = buddy_theme_has_mascot();
    int art_w = crab ? g.stage_w - CRAB * (WIDE ? 104 : 80) / 96 : g.stage_w;
    pr.art = box(pr.stage, crab && WIDE ? 0 : 0, 0, art_w, g.stage_h);
    lv_obj_add_event_cb(pr.art, draw_printer, LV_EVENT_DRAW_MAIN, NULL);
    pr.crab_state = BUDDY_MASCOT_STATE_COUNT;
    if (crab) {
        pr.crab = lv_img_create(pr.stage);
        lv_img_set_antialias(pr.crab, false);
        lv_obj_clear_flag(pr.crab, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_pos(pr.crab, g.stage_w - CRAB - (WIDE ? 6 : 2), g.stage_h - CRAB - (WIDE ? 10 : 4));
        if (WIDE) {
            /* Internal RAM stays free for Wi-Fi and TLS; the frame falls back there only without PSRAM. */
            pr.crab_pixels = heap_caps_malloc(CRAB_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
            if (!pr.crab_pixels) pr.crab_pixels = heap_caps_malloc(CRAB_BYTES, MALLOC_CAP_8BIT);
            pr.crab_frame = (lv_img_dsc_t){ .header = { .cf = LV_IMG_CF_INDEXED_4BIT, .w = 96, .h = 96 }, .data_size = CRAB_BYTES, .data = pr.crab_pixels };
        }
    }

    /* The state pill. */
    pr.pill = box(pr.page, g.col_x, g.pill_y, 120, TINY ? 20 : WIDE ? 30 : 24);
    lv_obj_set_style_radius(pr.pill, buddy_theme_round(LV_RADIUS_CIRCLE), 0);
    lv_obj_set_style_bg_color(pr.pill, lv_color_hex(BUDDY_COLOR_SURFACE), 0);
    lv_obj_set_style_bg_opa(pr.pill, TINY ? LV_OPA_TRANSP : LV_OPA_COVER, 0);
    int dot = TINY ? 8 : 10, pill_h = TINY ? 20 : WIDE ? 30 : 24;
    pr.dot = box(pr.pill, TINY ? 0 : 12, (pill_h - dot) / 2, dot, dot);
    lv_obj_set_style_radius(pr.dot, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_opa(pr.dot, LV_OPA_COVER, 0);
    pr.pill_label = label(pr.pill, (TINY ? 0 : 12) + dot + 8, (pill_h - lv_font_get_line_height(g.pill)) / 2, g.col_w - 40, g.pill, BUDDY_COLOR_ACCENT);
    lv_obj_set_width(pr.pill_label, LV_SIZE_CONTENT);

    pr.file = label(pr.page, g.col_x, g.file_y, g.col_w, g.file, BUDDY_COLOR_TEXT);
    pr.number = label(pr.page, g.col_x - (WIDE ? 4 : 0), g.number_y, g.col_w, g.number, BUDDY_COLOR_TEXT);
    lv_label_set_long_mode(pr.number, LV_LABEL_LONG_CLIP);
    lv_obj_set_width(pr.number, LV_SIZE_CONTENT);
    pr.unit = label(pr.page, 0, 0, 60, g.unit, BUDDY_COLOR_MUTED);
    lv_obj_set_width(pr.unit, LV_SIZE_CONTENT);
    pr.time = label(pr.page, g.col_x, g.time_y, g.col_w, g.time, BUDDY_COLOR_TEXT);
    pr.done = label(pr.page, g.col_x, g.done_y, g.col_w, g.small, BUDDY_COLOR_MUTED);
    int bar_x = TINY ? g.margin : WIDE ? g.col_x : g.margin, bar_w = TINY ? WIDTH - 2 * g.margin : WIDE ? g.col_w : WIDTH - 2 * g.margin;
    pr.bar = bar(pr.page, bar_x, g.bar_y, bar_w, g.bar_h, 100);
    pr.layer = label(pr.page, TINY ? g.col_x : bar_x, g.layer_y, TINY ? g.col_w : bar_w, g.small, BUDDY_COLOR_MUTED);
    if (TINY) {
        pr.temps_line = label(pr.page, g.col_x, g.tiles_y, g.col_w, g.small, BUDDY_COLOR_MUTED);
    } else {
        const buddy_text_key_t names[2] = { BT_PRINT_NOZZLE, BT_PRINT_BED };
        int tile_x = WIDE ? g.col_x : g.margin, tiles_w = WIDE ? g.col_w : WIDTH - 2 * g.margin, tile_w = (tiles_w - 10) / 2;
        for (unsigned i = 0; i < 2; ++i) {
            pr.tiles[i] = card(pr.page, tile_x + (int)i * (tile_w + 10), g.tiles_y, tile_w, g.tile_h, 12);
            pr.tile_names[i] = label(pr.tiles[i], 14, WIDE ? 7 : 8, tile_w - 40, &buddy_language_font_14, BUDDY_COLOR_MUTED);
            set_text(pr.tile_names[i], BT(names[i]));
            pr.tile_values[i] = label(pr.tiles[i], 14, WIDE ? 26 : 30, tile_w - 40, g.tile_value, BUDDY_COLOR_TEXT);
            buddy_theme_numerals(pr.tile_values[i]);
            /* A small thermometer at the tile's edge. */
            int track_h = g.tile_h - 20;
            pr.heat[i] = box(pr.tiles[i], tile_w - 20, 10, 6, track_h);
            lv_obj_set_style_radius(pr.heat[i], buddy_theme_round(LV_RADIUS_CIRCLE), 0);
            lv_obj_set_style_bg_color(pr.heat[i], lv_color_hex(BUDDY_COLOR_RULE), 0);
            lv_obj_set_style_bg_opa(pr.heat[i], LV_OPA_COVER, 0);
            pr.heat_fill[i] = box(pr.heat[i], 0, track_h, 6, 0);
            lv_obj_set_style_radius(pr.heat_fill[i], buddy_theme_round(LV_RADIUS_CIRCLE), 0);
            lv_obj_set_style_bg_opa(pr.heat_fill[i], LV_OPA_COVER, 0);
        }
    }
    /* Nothing to show yet: the idle printer stays on stage, the words sit beside it. */
    pr.empty_title = label(pr.page, g.col_x, g.pill_y + (WIDE ? 24 : 0), g.col_w,
                           WIDE ? &buddy_language_font_32 : TINY ? &buddy_language_font_16 : &buddy_language_font_24, BUDDY_COLOR_TEXT);
    lv_label_set_long_mode(pr.empty_title, LV_LABEL_LONG_WRAP);
    pr.empty_body = label(pr.page, g.col_x, g.pill_y + (WIDE ? 120 : TINY ? 44 : 76), g.col_w,
                          WIDE ? &buddy_language_font_20 : TINY ? &buddy_language_font_14 : &buddy_language_font_16, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(pr.empty_body, LV_LABEL_LONG_WRAP);

    buddy_theme_numerals(pr.number);
    buddy_theme_type(pr.page);
    buddy_theme_heading(pr.head);
    lv_obj_add_flag(pr.page, LV_OBJ_FLAG_HIDDEN);
    lv_timer_create(animate, ANIMATION_MS, NULL);
    refresh();
    return pr.page;
}

void buddy_printer_page_update(const buddy_printers_t *printers, bool connected)
{
    if (printers) pr.printers = *printers;
    pr.connected = connected && pr.printers.present;
    refresh();
}

void buddy_printer_page_show(uint8_t id)
{
    pr.selected = id;
    refresh();
}

const char *buddy_printer_page_status(void)
{
    const buddy_printer_t *p = shown();
    if (!pr.connected) snprintf(pr.status_text, sizeof(pr.status_text), "%s", BT(BT_PC_OFFLINE));
    else if (!p) snprintf(pr.status_text, sizeof(pr.status_text), "%s", BT(BT_PRINT_NO_PRINTERS));
    else if (p->state == BUDDY_PRINT_PRINTING) snprintf(pr.status_text, sizeof(pr.status_text), BT(BT_PRINT_PRINTING_LU), (unsigned long)p->progress);
    else snprintf(pr.status_text, sizeof(pr.status_text), "%s", buddy_printer_state_text(p));
    return pr.status_text;
}
