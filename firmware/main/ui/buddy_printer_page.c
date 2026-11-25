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
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

#define WIDTH BOARD_DISPLAY_WIDTH
#define BODY (BOARD_DISPLAY_HEIGHT - 44)
#define WIDE (BOARD_DISPLAY_WIDTH >= 800)
#define TINY (BOARD_DISPLAY_HEIGHT < 400)
/* The printer is pixel art on the crab's own grid: 48 x 48 cells, four pixels a cell on the
 * wide screen like the weather crab, three on the square one and two on the small one. */
#define CELLS 48
#define SCALE (WIDE ? 4 : TINY ? 2 : 3)
#define ART (CELLS * SCALE)
/* The drawing steps a cell at a time, like the crab's own frames. */
#define STEP_MS 140

/* Laid out like Weather's Now view: a heading with a rule under it (and a switch when there
 * is more than one printer), a large reading with the file and what comes next, the printer
 * at work on the right, a progress bar and three tiles. */
static struct {
    lv_obj_t *page, *crab, *title, *subtitle, *rule, *track, *tabs[BUDDY_PRINTERS], *tab_labels[BUDDY_PRINTERS];
    lv_obj_t *art, *number, *unit, *file, *line, *bar, *empty_title, *empty_body;
    lv_obj_t *tiles[3], *tile_names[3], *values[3], *units[3];
    int top; /* where the reading starts; the square screen moves it down for the switch */
    unsigned tab_count, crab_frame;
    uint32_t step;
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

static lv_obj_t *card(lv_obj_t *parent, int radius)
{
    lv_obj_t *obj = box(parent, 0, 0, 10, 10);
    lv_obj_set_style_bg_color(obj, lv_color_hex(BUDDY_COLOR_SURFACE), 0);
    lv_obj_set_style_bg_opa(obj, LV_OPA_COVER, 0);
    if (buddy_theme.outlined_cards) {
        lv_obj_set_style_bg_opa(obj, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(obj, 1, 0);
        lv_obj_set_style_border_color(obj, lv_color_hex(BUDDY_COLOR_RULE), 0);
    }
    lv_obj_set_style_radius(obj, buddy_theme_round(radius), 0);
    return obj;
}

static lv_obj_t *label(lv_obj_t *parent, int x, int y, int w, const lv_font_t *font, uint32_t ink)
{
    lv_obj_t *obj = lv_label_create(parent);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_width(obj, w);
    lv_obj_set_style_text_font(obj, font, 0);
    lv_obj_set_style_text_color(obj, lv_color_hex(ink), 0);
    lv_label_set_long_mode(obj, LV_LABEL_LONG_DOT);
    lv_label_set_text(obj, "");
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static void place(lv_obj_t *obj, int x, int y, int w, int h)
{
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_size(obj, w, h);
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

/* A smaller unit just after a reading, sharing its baseline area like Weather's degrees. */
static void after(lv_obj_t *value, lv_obj_t *unit, int gap, int drop)
{
    lv_obj_update_layout(value);
    lv_obj_set_pos(unit, lv_obj_get_x(value) + lv_obj_get_width(value) + gap, lv_obj_get_y(value) + drop);
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

static bool attention(const buddy_printer_t *p)
{
    return p && (p->state == BUDDY_PRINT_FAILED || (p->state == BUDDY_PRINT_PAUSED && p->reason != BUDDY_PRINT_REASON_USER));
}

/* The state's own colour: warning when the printer wants someone, accent while it works. */
static uint32_t state_ink(const buddy_printer_t *p)
{
    if (attention(p)) return BUDDY_COLOR_WARNING;
    if (p && (p->state == BUDDY_PRINT_PRINTING || p->state == BUDDY_PRINT_PREPARING || p->state == BUDDY_PRINT_FINISHED)) return BUDDY_COLOR_ACCENT;
    return BUDDY_COLOR_MUTED;
}

/* ---- The printer, in pixels ---- */

typedef struct {
    lv_draw_ctx_t *ctx;
    int x, y, s;
} canvas_t;

static void cells(const canvas_t *c, int x, int y, int w, int h, lv_color_t ink, lv_opa_t opa)
{
    if (w <= 0 || h <= 0) return;
    lv_draw_rect_dsc_t rect;
    lv_draw_rect_dsc_init(&rect);
    rect.bg_color = ink;
    rect.bg_opa = opa;
    lv_area_t area = { (lv_coord_t)(c->x + x * c->s), (lv_coord_t)(c->y + y * c->s),
                       (lv_coord_t)(c->x + (x + w) * c->s - 1), (lv_coord_t)(c->y + (y + h) * c->s - 1) };
    lv_draw_rect(c->ctx, &rect, &area);
}

static lv_color_t hex(uint32_t ink) { return lv_color_hex(ink); }
static lv_color_t blend(uint32_t a, uint32_t b, uint8_t amount) { return lv_color_mix(lv_color_hex(a), lv_color_hex(b), amount); }

/* Back and forth over `span` cells, one cell per step. */
static int bounce(uint32_t step, int span)
{
    if (span <= 1) return 0;
    int period = 2 * (span - 1), at = (int)(step % (uint32_t)period);
    return at < span ? at : period - at;
}

/* The part is the little tug every printer is tested with, one row of cells per slice,
 * bottom row first and bow to the right. */
#define PART_ROWS 18
#define PART_W 22
static const char *const PART[PART_ROWS] = {
    "....##############....",
    "...################...",
    "..##################..",
    "..###################.",
    ".####################.",
    ".#####################",
    "######################",
    "##.....##########...##",
    "##.....##.....###...#.",
    ".......##.....###.....",
    ".......##.....###.....",
    ".......##########.....",
    ".......##########.....",
    "......############....",
    "........###...........",
    "........###...........",
    "........###...........",
    ".......#####..........",
};

/* The first and last filled cell of a slice. */
static void extent(int row, int *first, int *last)
{
    const char *line = PART[row];
    *first = PART_W; *last = -1;
    for (int i = 0; i < PART_W; ++i) if (line[i] == '#') { if (i < *first) *first = i; *last = i; }
}

/* One slice, cells `from` to `to` of it; the ends of each run catch the light and the shade. */
static void slice(const canvas_t *c, int x, int y, int row, int from, int to, lv_color_t ink, lv_color_t light, lv_color_t dark, bool shaded)
{
    const char *line = PART[row];
    for (int i = from; i <= to && i < PART_W; ++i) {
        if (line[i] != '#') continue;
        int run = i;
        while (run + 1 <= to && run + 1 < PART_W && line[run + 1] == '#') ++run;
        cells(c, x + i, y, run - i + 1, 1, ink, LV_OPA_COVER);
        if (shaded) {
            if (i == 0 || line[i - 1] != '#') cells(c, x + i, y, 1, 1, light, LV_OPA_COVER);
            if (run + 1 >= PART_W || line[run + 1] != '#') cells(c, x + run, y, 1, 1, dark, LV_OPA_COVER);
        }
        i = run;
    }
}

#define BED_ROW 33
#define PART_X (24 - PART_W / 2)

static void draw_head(const canvas_t *c, int x, int y, lv_color_t shell, lv_color_t shade, lv_color_t inside, lv_color_t tip)
{
    cells(c, x, y, 6, 4, shell, LV_OPA_COVER);
    cells(c, x, y + 3, 6, 1, shade, LV_OPA_COVER);
    cells(c, x + 2, y + 1, 2, 1, inside, LV_OPA_COVER); /* the fan */
    cells(c, x + 2, y + 4, 2, 1, shade, LV_OPA_COVER);  /* the nozzle */
    cells(c, x + 2, y + 5, 2, 1, tip, LV_OPA_COVER);
}

static void draw_printer(lv_event_t *event)
{
    lv_area_t a;
    lv_obj_get_coords(lv_event_get_target(event), &a);
    buddy_printer_draw(lv_event_get_draw_ctx(event), a.x1, a.y1, SCALE, shown());
}

void buddy_printer_draw(lv_draw_ctx_t *ctx, int x, int y, int scale, const buddy_printer_t *p)
{
    const canvas_t c = { ctx, x, y, scale };
    const uint8_t state = p ? p->state : BUDDY_PRINT_OFFLINE;
    const bool live = p && state != BUDDY_PRINT_OFFLINE;
    const uint32_t step = lv_tick_get() / STEP_MS;

    /* A printer that can't be reached is drawn in the rule's grey, without any light. */
    lv_color_t body = live ? blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_RULE, 120) : hex(BUDDY_COLOR_RULE);
    lv_color_t lit = live ? blend(BUDDY_COLOR_TEXT, BUDDY_COLOR_MUTED, 110) : blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_RULE, 90);
    lv_color_t shade = live ? hex(BUDDY_COLOR_RULE) : blend(BUDDY_COLOR_RULE, BUDDY_COLOR_BG, 140);
    lv_color_t inside = blend(BUDDY_COLOR_SURFACE, BUDDY_COLOR_BG, 110);
    lv_color_t accent = hex(live ? BUDDY_COLOR_ACCENT : BUDDY_COLOR_RULE);

    /* Its shadow and feet. */
    cells(&c, 6, 46, 36, 1, blend(BUDDY_COLOR_RULE, BUDDY_COLOR_BG, 110), LV_OPA_COVER);
    cells(&c, 8, 44, 4, 2, shade, LV_OPA_COVER);
    cells(&c, 36, 44, 4, 2, shade, LV_OPA_COVER);
    /* Lid, sides and base around a dark chamber; the corners stay open so it reads rounded. */
    cells(&c, 5, 2, 38, 1, lit, LV_OPA_COVER);
    cells(&c, 4, 3, 40, 3, body, LV_OPA_COVER);
    cells(&c, 4, 6, 4, 32, body, LV_OPA_COVER);
    cells(&c, 40, 6, 4, 32, body, LV_OPA_COVER);
    cells(&c, 4, 3, 1, 40, lit, LV_OPA_COVER);
    cells(&c, 43, 3, 1, 40, shade, LV_OPA_COVER);
    cells(&c, 4, 38, 40, 6, body, LV_OPA_COVER);
    cells(&c, 5, 43, 38, 1, shade, LV_OPA_COVER);
    cells(&c, 8, 6, 32, 32, inside, LV_OPA_COVER);
    cells(&c, 8, 6, 32, 1, blend(BUDDY_COLOR_BG, BUDDY_COLOR_SURFACE, 160), LV_OPA_COVER);

    /* The light on the lid: steady while all is well, blinking while it waits for someone. */
    bool blink = (step / 4U) % 2U == 0U;
    lv_color_t light = shade;
    if (live && (state == BUDDY_PRINT_PRINTING || state == BUDDY_PRINT_FINISHED)) light = accent;
    else if (live && state == BUDDY_PRINT_PREPARING) light = blink ? accent : shade;
    else if (live && state == BUDDY_PRINT_PAUSED) light = blink ? hex(state_ink(p) == BUDDY_COLOR_MUTED ? BUDDY_COLOR_ACCENT : state_ink(p)) : shade;
    else if (live && state == BUDDY_PRINT_FAILED) light = hex(BUDDY_COLOR_WARNING);
    else if (live) light = blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_RULE, 200);
    cells(&c, 36, 3, 3, 2, light, LV_OPA_COVER);

    /* How far along it is. */
    bool working = live && (state == BUDDY_PRINT_PRINTING || state == BUDDY_PRINT_PAUSED || state == BUDDY_PRINT_FAILED);
    int progress = !live ? 0 : state == BUDDY_PRINT_FINISHED ? 100 : working ? LV_CLAMP(0, p->progress, 100) : 0;

    /* The little screen on the front fills with the print. */
    int lit_cells = progress * 8 / 100;
    cells(&c, 8, 40, 8, 2, inside, LV_OPA_COVER);
    if (live) cells(&c, 8, 40, lit_cells, 2, blend(BUDDY_COLOR_ACCENT, BUDDY_COLOR_SURFACE, 200), LV_OPA_COVER);
    cells(&c, 36, 40, 3, 2, lit, LV_OPA_COVER);

    /* The bed glows warmer as it heats, and stands on its lift. */
    float warmth = live && isfinite(p->bed) ? LV_CLAMP(0.f, (p->bed - 30.f) / 50.f, 1.f) : 0.f;
    cells(&c, 22, BED_ROW + 2, 4, 3, shade, LV_OPA_COVER);
    cells(&c, 11, BED_ROW + 1, 26, 1, shade, LV_OPA_COVER);
    cells(&c, 10, BED_ROW, 28, 1, lv_color_mix(hex(BUDDY_COLOR_WARNING), live ? hex(BUDDY_COLOR_MUTED) : shade, (uint8_t)(warmth * 190.f)), LV_OPA_COVER);

    /* The part, one row of cells per slice, lit from the left. */
    int rows = progress * PART_ROWS / 100;
    lv_color_t part = accent, part_dark = blend(live ? BUDDY_COLOR_ACCENT : BUDDY_COLOR_RULE, BUDDY_COLOR_BG, 150);
    lv_color_t part_light = blend(BUDDY_COLOR_TEXT, live ? BUDDY_COLOR_ACCENT : BUDDY_COLOR_RULE, 70);
    lv_color_t part_line = blend(BUDDY_COLOR_ACCENT, BUDDY_COLOR_BG, 225);
    for (int r = 0; r < rows; ++r)
        slice(&c, PART_X, BED_ROW - 1 - r, r, 0, PART_W - 1, r % 2 ? part_line : part, part_light, part_dark, live);
    int top = BED_ROW - rows; /* the first free row above the part */

    /* The head: laying down the next slice, sweeping the bed, or parked out of the way. */
    bool hot = live && isfinite(p->nozzle) && p->nozzle >= 150.f;
    lv_color_t tip = hot ? hex(BUDDY_COLOR_WARNING) : shade;
    int head_x = 21, head_y = 8;
    if (live && state == BUDDY_PRINT_PRINTING) {
        int first, last;
        extent(LV_MIN(rows, PART_ROWS - 1), &first, &last);
        int span = last - first + 1, at = bounce(step, span);
        head_x = PART_X + first + at - 2;
        head_y = top - 7; /* the nozzle tip sits just above the slice it lays */
        /* The slice in progress trails behind the nozzle. */
        bool rightward = (int)(step % (uint32_t)LV_MAX(1, 2 * (span - 1))) < span;
        if (rows < PART_ROWS) {
            if (rightward) slice(&c, PART_X, top - 1, rows, first, first + at, part, part_light, part_dark, false);
            else slice(&c, PART_X, top - 1, rows, first + at, last, part, part_light, part_dark, false);
        }
        if (hot && (step % 2U)) tip = blend(BUDDY_COLOR_WARNING, BUDDY_COLOR_TEXT, 170);
    } else if (live && state == BUDDY_PRINT_PREPARING) {
        head_x = 10 + bounce(step / 2U, 23);
        head_y = BED_ROW - 1 - 5;
    } else if (live && state == BUDDY_PRINT_PAUSED) head_x = 32;
    else if (live && state == BUDDY_PRINT_FINISHED) head_x = 10;
    /* The gantry spans the chamber at the head's height. */
    cells(&c, 8, head_y + 1, 32, 1, shade, LV_OPA_COVER);
    draw_head(&c, head_x, head_y, lit, shade, inside, tip);

    /* A finished part twinkles; a failed one has come loose into a tangle. */
    if (live && state == BUDDY_PRINT_FINISHED) {
        static const int8_t stars[3][2] = { { 11, 23 }, { 36, 19 }, { 31, 12 } };
        for (unsigned i = 0; i < 3; ++i) {
            if ((step / 3U + i) % 3U == 0U) continue;
            lv_color_t ink = (step / 3U + i) % 3U == 1U ? hex(BUDDY_COLOR_TEXT) : accent;
            cells(&c, stars[i][0], stars[i][1] - 1, 1, 3, ink, LV_OPA_COVER);
            cells(&c, stars[i][0] - 1, stars[i][1], 3, 1, ink, LV_OPA_COVER);
        }
    } else if (live && state == BUDDY_PRINT_FAILED) {
        static const int8_t tangle[][2] = { { -5, 1 }, { -4, 2 }, { -3, 2 }, { -2, 1 }, { -2, 0 }, { -3, -1 }, { -1, -1 }, { 0, -2 }, { 1, -2 },
                                            { 2, -1 }, { 2, 0 }, { 1, 1 }, { 3, 1 }, { 4, 0 }, { 5, -1 }, { 5, -2 }, { 4, -3 }, { -4, -2 } };
        for (unsigned i = 0; i < sizeof(tangle) / sizeof(tangle[0]); ++i)
            cells(&c, 24 + tangle[i][0], top - 3 + tangle[i][1], 1, 1, i % 3 ? part : part_light, LV_OPA_COVER);
    }

}

/* The crab beside the heading, in looks that have it, follows the print. */
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

static void feed_crab(bool force)
{
    if (!pr.crab) return;
    unsigned frame = (lv_tick_get() / BUDDY_MASCOT_FRAME_MS) % BUDDY_MASCOT_FRAME_COUNT;
    if (!force && frame == pr.crab_frame) return;
    pr.crab_frame = frame;
    lv_img_set_src(pr.crab, &buddy_mascot_frames[pr.crab_state][frame]);
}

static bool moving(const buddy_printer_t *p)
{
    return p && (p->state == BUDDY_PRINT_PRINTING || p->state == BUDDY_PRINT_PREPARING || p->state == BUDDY_PRINT_PAUSED || p->state == BUDDY_PRINT_FINISHED);
}

static void animate(lv_timer_t *timer)
{
    (void)timer;
    if (!pr.page || lv_obj_has_flag(pr.page, LV_OBJ_FLAG_HIDDEN)) return;
    uint32_t step = lv_tick_get() / STEP_MS;
    /* Only a printer that is doing something moves, and only when the next cell is due. */
    if (step != pr.step && moving(shown())) lv_obj_invalidate(pr.art);
    pr.step = step;
    feed_crab(false);
}

/* ---- Layout ---- */

static void layout_tabs(unsigned count)
{
    pr.tab_count = count;
    bool tabs = count > 1 && !TINY;
    visible(pr.track, tabs);
    if (!WIDE && !TINY) {
        /* The square screen puts the switch under the heading, where the rule would be. */
        pr.top = tabs ? 124 : 88;
        visible(pr.rule, !tabs && !buddy_theme.square);
    }
    if (!tabs) return;
    int track_w = lv_obj_get_width(pr.track), track_h = lv_obj_get_height(pr.track);
    int tab_w = (track_w - 8) / (int)count;
    for (unsigned i = 0; i < BUDDY_PRINTERS; ++i) {
        visible(pr.tabs[i], i < count);
        if (i >= count) continue;
        place(pr.tabs[i], 4 + (int)i * tab_w, 4, tab_w - (i + 1 < count ? 2 : 0), track_h - 8);
        lv_obj_set_width(pr.tab_labels[i], tab_w - 16);
        lv_obj_align(pr.tab_labels[i], LV_ALIGN_CENTER, 0, 0);
    }
}

/* Positions of the reading, the art, the bar and the tiles below the heading. */
static void layout_body(void)
{
    if (WIDE) {
        const int x = 28, y = 96;
        lv_obj_set_pos(pr.number, x, y + 10);
        place(pr.file, x, y + 98, 528, 30);
        place(pr.line, x, y + 134, 528, 56);
        lv_obj_set_pos(pr.art, x + 744 - ART, y - 6);
        place(pr.bar, x, y + 200, 744, 6);
        for (int i = 0; i < 3; ++i) {
            place(pr.tiles[i], x + i * 252, y + 222, 240, 96);
            place(pr.tile_names[i], 16, 14, 208, 22);
            lv_obj_set_pos(pr.values[i], 16, 42);
        }
        place(pr.empty_title, x, y + 20, 500, 84);
        place(pr.empty_body, x, y + 110, 500, 90);
        return;
    }
    const int x = 12, w = WIDTH - 24;
    if (TINY) {
        lv_obj_set_pos(pr.number, x, 40);
        place(pr.file, x, 98, w, 20);
        place(pr.line, x, 118, w, 20);
        lv_obj_set_pos(pr.art, x + w - ART, 2);
        place(pr.bar, x, 142, w, 5);
        int tile_w = (w - 8) / 3;
        for (int i = 0; i < 3; ++i) {
            place(pr.tiles[i], x + i * (tile_w + 4), 154, tile_w, 40);
            place(pr.tile_names[i], 8, 3, tile_w - 12, 18);
            lv_obj_set_pos(pr.values[i], 8, 19);
        }
        place(pr.empty_title, x, 48, w - ART - 4, 46);
        place(pr.empty_body, x, 102, w, 80);
        return;
    }
    const int y = pr.top, text_w = w - ART - 12;
    lv_obj_set_pos(pr.number, x, y);
    place(pr.file, x, y + 86, text_w, 26);
    place(pr.line, x, y + 114, text_w, 44);
    lv_obj_set_pos(pr.art, x + w - ART, y);
    place(pr.bar, x, y + 166, w, 6);
    int tile_w = (w - 16) / 3;
    for (int i = 0; i < 3; ++i) {
        place(pr.tiles[i], x + i * (tile_w + 8), y + 184, tile_w, 84);
        place(pr.tile_names[i], 12, 10, tile_w - 20, 20);
        lv_obj_set_pos(pr.values[i], 12, 36);
    }
    place(pr.empty_title, x, y + 8, text_w, 64);
    place(pr.empty_body, x, y + 76, text_w, 110);
}

/* ---- The readings ---- */

static void temperature(lv_obj_t *value, lv_obj_t *unit, float now, float target)
{
    char buffer[24];
    if (isfinite(now)) snprintf(buffer, sizeof(buffer), "%.0f\xC2\xB0", now);
    else snprintf(buffer, sizeof(buffer), "--");
    set_text(value, buffer);
    /* Where it is heading, when it is heating; the small screen has room for the reading only. */
    if (!TINY && isfinite(now) && isfinite(target) && target >= 1.f) snprintf(buffer, sizeof(buffer), "/ %.0f\xC2\xB0", target);
    else buffer[0] = 0;
    set_text(unit, buffer);
}

static void refresh(void)
{
    if (!pr.page) return;
    const buddy_printer_t *p = shown();
    bool empty = !p;
    unsigned count = pr.connected ? pr.printers.count : 0;
    if (count != pr.tab_count) { layout_tabs(count); layout_body(); }

    lv_obj_t *readings[] = { pr.number, pr.unit, pr.file, pr.line, pr.bar, pr.tiles[0], pr.tiles[1], pr.tiles[2] };
    for (unsigned i = 0; i < sizeof(readings) / sizeof(readings[0]); ++i) visible(readings[i], !empty);
    visible(pr.empty_title, empty);
    visible(pr.empty_body, empty);
    buddy_mascot_state_t crab = crab_state(p);
    if (crab != pr.crab_state) { pr.crab_state = crab; feed_crab(true); }
    lv_obj_invalidate(pr.art);

    char buffer[128];
    if (empty) {
        set_text(pr.title, BT(BT_PRINTER));
        set_text(pr.subtitle, pr.connected ? BT(BT_PRINT_NO_PRINTERS) : BT(BT_PC_OFFLINE));
        set_ink(pr.subtitle, BUDDY_COLOR_MUTED);
        set_text(pr.empty_title, pr.connected ? BT(BT_PRINT_NO_PRINTERS_TITLE) : BT(BT_PC_OFFLINE));
        set_text(pr.empty_body, pr.connected ? BT(BT_PRINT_NO_PRINTERS_BODY) : BT(BT_PRINT_PC_OFFLINE_BODY));
        return;
    }

    /* The heading names the printer and says how it is; the switch picks another. */
    unsigned index = (unsigned)(p - pr.printers.items);
    set_text(pr.title, p->name);
    if (TINY && count > 1) snprintf(buffer, sizeof(buffer), "%s  \xC2\xB7  %u/%u", buddy_printer_state_text(p), index + 1, count);
    else snprintf(buffer, sizeof(buffer), "%s", buddy_printer_state_text(p));
    set_text(pr.subtitle, buffer);
    set_ink(pr.subtitle, state_ink(p));
    for (unsigned i = 0; i < BUDDY_PRINTERS && !TINY; ++i) {
        if (i >= count) continue;
        bool on = i == index;
        set_text(pr.tab_labels[i], pr.printers.items[i].name);
        lv_obj_set_style_bg_opa(pr.tabs[i], on ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
        set_ink(pr.tab_labels[i], on ? (buddy_theme.cased ? BUDDY_COLOR_FRAME_INK : BUDDY_COLOR_ACCENT) : BUDDY_COLOR_MUTED);
    }

    /* The large reading: how far along, like the temperature on Weather. */
    bool preparing = p->state == BUDDY_PRINT_PREPARING;
    bool working = p->state == BUDDY_PRINT_PRINTING || p->state == BUDDY_PRINT_PAUSED || p->state == BUDDY_PRINT_FINISHED || p->state == BUDDY_PRINT_FAILED;
    int progress = p->state == BUDDY_PRINT_FINISHED ? 100 : working ? LV_CLAMP(0, p->progress, 100) : 0;
    if (working) snprintf(buffer, sizeof(buffer), "%d", progress);
    else snprintf(buffer, sizeof(buffer), "--");
    set_text(pr.number, buffer);
    set_ink(pr.number, working ? BUDDY_COLOR_TEXT : BUDDY_COLOR_MUTED);
    set_text(pr.unit, working ? "%" : "");
    after(pr.number, pr.unit, WIDE ? 6 : 4, WIDE ? 4 : TINY ? 2 : 4);

    set_text(pr.file, p->file[0] ? p->file : p->state == BUDDY_PRINT_OFFLINE ? "--" : BT(BT_PRINT_NOTHING_PRINTING));
    set_ink(pr.file, p->file[0] ? BUDDY_COLOR_TEXT : BUDDY_COLOR_MUTED);

    /* Under the file: when it will be done, or what the printer is waiting for. */
    buffer[0] = 0;
    if ((p->state == BUDDY_PRINT_PRINTING || preparing) && p->remaining_s) {
        char left[48], stamp[64];
        duration(left, sizeof(left), p->remaining_s);
        buddy_printer_done_at(stamp, sizeof(stamp), p);
        int n = snprintf(buffer, sizeof(buffer), BT(BT_PRINT_LEFT_S), left);
        if (stamp[0] && n > 0 && (size_t)n < sizeof(buffer)) snprintf(buffer + n, sizeof(buffer) - (size_t)n, "  \xC2\xB7  %s", stamp);
    } else if (p->state == BUDDY_PRINT_PAUSED) {
        snprintf(buffer, sizeof(buffer), "%s", BT(p->reason == BUDDY_PRINT_REASON_FILAMENT ? BT_PRINT_FILAMENT_BODY :
                                                  p->reason == BUDDY_PRINT_REASON_ERROR ? BT_PRINT_ERROR_BODY : BT_PRINT_PAUSED_BODY));
    } else if (p->state == BUDDY_PRINT_FINISHED) snprintf(buffer, sizeof(buffer), "%s", BT(BT_PRINT_FINISHED_BODY));
    else if (p->state == BUDDY_PRINT_FAILED && p->reason != BUDDY_PRINT_REASON_USER) snprintf(buffer, sizeof(buffer), "%s", BT(BT_PRINT_FAILED_BODY));
    set_text(pr.line, buffer);

    lv_bar_set_value(pr.bar, progress, LV_ANIM_OFF);
    uint32_t ink = state_ink(p);
    lv_obj_set_style_bg_color(pr.bar, lv_color_hex(ink == BUDDY_COLOR_MUTED ? BUDDY_COLOR_ACCENT : ink), LV_PART_INDICATOR);

    /* Layer, nozzle and bed. */
    if (p->layer || (p->state == BUDDY_PRINT_FINISHED && p->layers)) snprintf(buffer, sizeof(buffer), "%lu", (unsigned long)(p->layer ? p->layer : p->layers));
    else snprintf(buffer, sizeof(buffer), "--");
    set_text(pr.values[0], buffer);
    if (p->layers && strcmp(lv_label_get_text(pr.values[0]), "--")) snprintf(buffer, sizeof(buffer), "/ %lu", (unsigned long)p->layers);
    else buffer[0] = 0;
    set_text(pr.units[0], buffer);
    temperature(pr.values[1], pr.units[1], p->nozzle, p->nozzle_target);
    temperature(pr.values[2], pr.units[2], p->bed, p->bed_target);
    for (unsigned i = 0; i < 3; ++i) after(pr.values[i], pr.units[i], WIDE ? 8 : 5, WIDE ? 10 : TINY ? 1 : 6);
}

static void show_printer(unsigned index)
{
    if (index >= pr.printers.count) return;
    pr.selected = pr.printers.items[index].id;
    refresh();
}

static void tab_clicked(lv_event_t *event)
{
    show_printer((unsigned)(uintptr_t)lv_event_get_user_data(event));
}

/* On the small screen, where no switch fits, a tap on the heading steps to the next printer. */
static void heading_clicked(lv_event_t *event)
{
    (void)event;
    const buddy_printer_t *p = shown();
    if (!p || pr.printers.count < 2) return;
    show_printer(((unsigned)(p - pr.printers.items) + 1) % pr.printers.count);
}

lv_obj_t *buddy_printer_page_create(lv_obj_t *parent)
{
    pr.page = box(parent, 0, 0, WIDTH, BODY);
    const bool crab = buddy_theme_has_mascot() && !TINY;
    const int margin = WIDE ? 28 : 12;
    const int text_x = crab ? margin + (WIDE ? 60 : 56) : margin;

    /* The heading, like PC Monitor's: the crab, the printer's name and how it is. */
    pr.crab_state = BUDDY_MASCOT_STATE_COUNT;
    if (crab) {
        pr.crab = lv_img_create(pr.page);
        lv_img_set_antialias(pr.crab, false);
        lv_obj_set_pos(pr.crab, margin, WIDE ? 17 : 6);
        lv_obj_clear_flag(pr.crab, LV_OBJ_FLAG_CLICKABLE);
    }
    int title_w = (WIDE ? 440 : WIDTH - margin) - text_x;
    if (TINY) title_w = WIDTH - 2 * margin - ART - 8;
    pr.rule = box(pr.page, margin, WIDE ? 84 : 76, WIDTH - 2 * margin, 1);
    lv_obj_set_style_bg_color(pr.rule, lv_color_hex(BUDDY_COLOR_RULE), 0);
    lv_obj_set_style_bg_opa(pr.rule, LV_OPA_COVER, 0);
    visible(pr.rule, !TINY && !buddy_theme.square);

    /* The switch between printers, the same segmented control as Weather's views. */
    pr.track = card(pr.page, LV_RADIUS_CIRCLE);
    if (WIDE) place(pr.track, 448, 20, 324, 46);
    else place(pr.track, margin, 70, WIDTH - 2 * margin, 44);
    for (unsigned i = 0; i < BUDDY_PRINTERS; ++i) {
        lv_obj_t *tab = pr.tabs[i] = box(pr.track, 0, 0, 10, 10);
        lv_obj_add_flag(tab, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_radius(tab, buddy_theme_round(LV_RADIUS_CIRCLE), 0);
        lv_obj_set_style_bg_color(tab, lv_color_hex(buddy_theme.cased ? BUDDY_COLOR_ACCENT : BUDDY_COLOR_RULE), 0);
        lv_obj_set_style_bg_color(tab, lv_color_hex(BUDDY_COLOR_PRESSED), LV_STATE_PRESSED);
        lv_obj_set_style_bg_opa(tab, LV_OPA_COVER, LV_STATE_PRESSED);
        lv_obj_add_event_cb(tab, tab_clicked, LV_EVENT_CLICKED, (void *)(uintptr_t)i);
        pr.tab_labels[i] = label(tab, 0, 0, 80, &buddy_language_font_16, BUDDY_COLOR_MUTED);
        lv_obj_set_style_text_align(pr.tab_labels[i], LV_TEXT_ALIGN_CENTER, 0);
    }
    visible(pr.track, false);

    lv_obj_t *heading = box(pr.page, text_x, 0, title_w, WIDE ? 80 : TINY ? 42 : 66);
    pr.title = label(heading, 0, WIDE ? 18 : TINY ? 4 : 8, title_w, TINY ? &buddy_language_font_16 : &buddy_language_font_24, BUDDY_COLOR_TEXT);
    pr.subtitle = label(heading, 0, WIDE ? 52 : TINY ? 22 : 40, title_w, TINY ? &buddy_language_font_14 : &buddy_language_font_16, BUDDY_COLOR_MUTED);
    if (TINY) {
        lv_obj_add_flag(heading, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_add_event_cb(heading, heading_clicked, LV_EVENT_CLICKED, NULL);
    }

    /* The reading. */
    pr.number = label(pr.page, 0, 0, 10, WIDE || !TINY ? &buddy_clock_font : &buddy_language_font_48, BUDDY_COLOR_TEXT);
    lv_label_set_long_mode(pr.number, LV_LABEL_LONG_CLIP);
    lv_obj_set_width(pr.number, LV_SIZE_CONTENT);
    pr.unit = label(pr.page, 0, 0, 10, WIDE ? &buddy_language_font_32 : TINY ? &buddy_language_font_20 : &buddy_language_font_24, BUDDY_COLOR_MUTED);
    lv_obj_set_width(pr.unit, LV_SIZE_CONTENT);
    pr.file = label(pr.page, 0, 0, 10, WIDE ? &buddy_language_font_24 : TINY ? &buddy_language_font_14 : &buddy_language_font_20, BUDDY_COLOR_TEXT);
    pr.line = label(pr.page, 0, 0, 10, WIDE ? &buddy_language_font_20 : TINY ? &buddy_language_font_14 : &buddy_language_font_16, BUDDY_COLOR_MUTED);
    if (!TINY) lv_label_set_long_mode(pr.line, LV_LABEL_LONG_WRAP);

    /* The printer at work. */
    pr.art = box(pr.page, 0, 0, ART, ART);
    lv_obj_add_event_cb(pr.art, draw_printer, LV_EVENT_DRAW_MAIN, NULL);
    if (TINY) {
        lv_obj_add_flag(pr.art, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_add_event_cb(pr.art, heading_clicked, LV_EVENT_CLICKED, NULL);
    }

    pr.bar = lv_bar_create(pr.page);
    lv_obj_remove_style_all(pr.bar);
    lv_bar_set_range(pr.bar, 0, 100);
    lv_obj_set_style_bg_color(pr.bar, lv_color_hex(BUDDY_COLOR_RULE), LV_PART_MAIN);
    lv_obj_set_style_bg_opa(pr.bar, LV_OPA_COVER, LV_PART_MAIN);
    lv_obj_set_style_bg_opa(pr.bar, LV_OPA_COVER, LV_PART_INDICATOR);
    lv_obj_set_style_radius(pr.bar, buddy_theme_round(LV_RADIUS_CIRCLE), LV_PART_MAIN);
    lv_obj_set_style_radius(pr.bar, buddy_theme_round(LV_RADIUS_CIRCLE), LV_PART_INDICATOR);
    lv_obj_clear_flag(pr.bar, LV_OBJ_FLAG_CLICKABLE);

    /* Three tiles, like Weather's. */
    const buddy_text_key_t names[3] = { BT_PRINT_LAYER, BT_PRINT_NOZZLE, BT_PRINT_BED };
    const lv_font_t *value_font = WIDE ? &buddy_language_font_32 : TINY ? &buddy_language_font_16 : &buddy_language_font_24;
    const lv_font_t *unit_font = WIDE ? &buddy_language_font_20 : &buddy_language_font_14;
    for (unsigned i = 0; i < 3; ++i) {
        pr.tiles[i] = card(pr.page, TINY ? 10 : 14);
        pr.tile_names[i] = label(pr.tiles[i], 0, 0, 10, WIDE ? &buddy_language_font_16 : &buddy_language_font_14, BUDDY_COLOR_MUTED);
        set_text(pr.tile_names[i], BT(names[i]));
        pr.values[i] = label(pr.tiles[i], 0, 0, 10, value_font, BUDDY_COLOR_TEXT);
        lv_label_set_long_mode(pr.values[i], LV_LABEL_LONG_CLIP);
        lv_obj_set_width(pr.values[i], LV_SIZE_CONTENT);
        buddy_theme_numerals(pr.values[i]);
        pr.units[i] = label(pr.tiles[i], 0, 0, 10, unit_font, BUDDY_COLOR_MUTED);
        lv_obj_set_width(pr.units[i], LV_SIZE_CONTENT);
    }

    /* Nothing to show yet: the printer stands idle and the words sit beside it. */
    pr.empty_title = label(pr.page, 0, 0, 10, WIDE ? &buddy_language_font_32 : TINY ? &buddy_language_font_16 : &buddy_language_font_24, BUDDY_COLOR_TEXT);
    lv_label_set_long_mode(pr.empty_title, LV_LABEL_LONG_WRAP);
    pr.empty_body = label(pr.page, 0, 0, 10, WIDE ? &buddy_language_font_20 : TINY ? &buddy_language_font_14 : &buddy_language_font_16, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(pr.empty_body, LV_LABEL_LONG_WRAP);

    buddy_theme_numerals(pr.number);
    buddy_theme_type(pr.page);
    buddy_theme_heading(pr.title);
    pr.top = 88;
    pr.tab_count = BUDDY_PRINTERS + 1;
    lv_obj_add_flag(pr.page, LV_OBJ_FLAG_HIDDEN);
    lv_timer_create(animate, STEP_MS / 2, NULL);
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
