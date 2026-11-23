#include "buddy_printer_page.h"
#include "buddy_printer_watch.h"
#include "buddy_settings.h"
#include "buddy_theme.h"
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
#define ROWS 4

/* Where everything sits on each screen class: 800 x 480, 480 x 480 and 320 x 240. */
typedef struct {
    int margin, head_y, status_y, rule_y, ring_x, ring_y, ring, col_x, col_y, file_h, rows_y, row_h, rows_x, rows_w, chip_w, chip_h;
    const lv_font_t *head, *status, *percent, *sub, *file, *row_name, *row_value;
} geometry_t;

static geometry_t geometry(void)
{
    if (WIDE) return (geometry_t){ 28, 26, 29, 84, 40, 104, 304, 380, 104, 64, 196, 44, 380, 392, 96, 36,
        &buddy_language_font_24, &buddy_language_font_16, &buddy_language_font_48, &buddy_language_font_16,
        &buddy_language_font_24, &buddy_language_font_20, &buddy_language_font_20 };
    if (!TINY) return (geometry_t){ 12, 8, 38, -1, 12, 68, 188, 212, 68, 60, 268, 42, 12, WIDTH - 24, 80, 32,
        &buddy_language_font_20, &buddy_language_font_14, &buddy_language_font_48, &buddy_language_font_14,
        &buddy_language_font_20, &buddy_language_font_16, &buddy_language_font_20 };
    return (geometry_t){ 12, 4, 26, -1, 12, 52, 112, 136, 52, 20, 76, 22, 136, WIDTH - 148, 64, 28,
        &buddy_language_font_16, &buddy_language_font_14, &buddy_language_font_24, &buddy_language_font_14,
        &buddy_language_font_14, &buddy_language_font_14, &buddy_language_font_14 };
}

static struct {
    lv_obj_t *page, *head, *status, *chip, *chip_label, *ring, *percent, *sub, *file;
    lv_obj_t *names[ROWS], *values[ROWS], *rules[ROWS];
    /* Square screens set the time and the layer large beside the ring, the temperatures below. */
    lv_obj_t *empty_title, *empty_body;
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
    lv_obj_set_style_text_color(obj, lv_color_hex(ink), 0);
}

static const buddy_printer_t *shown(void)
{
    if (!pr.printers.count) return NULL;
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
    if (!pr.connected || p->state == BUDDY_PRINT_OFFLINE || p->state == BUDDY_PRINT_IDLE) return BUDDY_COLOR_MUTED;
    if (p->state == BUDDY_PRINT_FAILED || (p->state == BUDDY_PRINT_PAUSED && p->reason != BUDDY_PRINT_REASON_USER)) return BUDDY_COLOR_WARNING;
    return BUDDY_COLOR_ACCENT;
}

static void refresh(void)
{
    if (!pr.page) return;
    const buddy_printer_t *p = pr.connected ? shown() : NULL;
    bool empty = !p;
    visible(pr.empty_title, empty);
    visible(pr.empty_body, empty);
    lv_obj_t *parts[] = { pr.ring, pr.sub, pr.file, pr.status };
    for (unsigned i = 0; i < sizeof(parts) / sizeof(parts[0]); ++i) visible(parts[i], !empty);
    for (unsigned i = 0; i < ROWS; ++i) { visible(pr.names[i], !empty); visible(pr.values[i], !empty); visible(pr.rules[i], !empty && !buddy_theme.square); }
    visible(pr.chip, pr.connected && pr.printers.count > 1);
    if (empty) {
        set_text(pr.head, BT(BT_PRINTER));
        set_text(pr.empty_title, pr.connected ? BT(BT_PRINT_NO_PRINTERS_TITLE) : BT(BT_PC_OFFLINE));
        set_text(pr.empty_body, pr.connected ? BT(BT_PRINT_NO_PRINTERS_BODY) : BT(BT_PRINT_PC_OFFLINE_BODY));
        return;
    }
    char buffer[96];
    set_text(pr.head, p->name);
    set_text(pr.status, buddy_printer_state_text(p));
    set_ink(pr.status, state_ink(p));
    if (pr.printers.count > 1) {
        unsigned index = (unsigned)(p - pr.printers.items);
        snprintf(buffer, sizeof(buffer), "%u / %u  " LV_SYMBOL_RIGHT, index + 1, (unsigned)pr.printers.count);
        set_text(pr.chip_label, buffer);
    }
    /* The ring fills with the print; a finished print stays full, a stopped one keeps where it got to. */
    bool working = buddy_print_active(p->state) || p->state == BUDDY_PRINT_FINISHED || p->state == BUDDY_PRINT_FAILED;
    int progress = p->state == BUDDY_PRINT_FINISHED ? 100 : working ? p->progress : 0;
    lv_arc_set_value(pr.ring, (int16_t)(progress * 10));
    lv_color_t ink = lv_color_hex(state_ink(p));
    if (p->state == BUDDY_PRINT_PREPARING) ink = lv_color_mix(lv_color_hex(BUDDY_COLOR_ACCENT), lv_color_hex(BUDDY_COLOR_BG), 110);
    lv_obj_set_style_arc_color(pr.ring, ink, LV_PART_INDICATOR);
    if (working) snprintf(buffer, sizeof(buffer), "%d%%", progress);
    else snprintf(buffer, sizeof(buffer), "--");
    set_text(pr.percent, buffer);
    set_ink(pr.percent, working ? BUDDY_COLOR_TEXT : BUDDY_COLOR_MUTED);
    buddy_printer_done_at(buffer, sizeof(buffer), p);
    if (!buffer[0]) snprintf(buffer, sizeof(buffer), "%s", buddy_printer_state_text(p));
    set_text(pr.sub, buffer);
    set_ink(pr.sub, buddy_print_active(p->state) && p->state != BUDDY_PRINT_PAUSED ? BUDDY_COLOR_MUTED : state_ink(p));
    set_text(pr.file, p->file[0] ? p->file : buddy_print_active(p->state) ? BT(BT_PRINT_PRINTING) : BT(BT_PRINT_NOTHING_PRINTING));
    set_ink(pr.file, p->file[0] ? BUDDY_COLOR_TEXT : BUDDY_COLOR_MUTED);
    /* Time left, layer, nozzle, bed. */
    if (buddy_print_active(p->state) && p->remaining_s) duration(buffer, sizeof(buffer), p->remaining_s);
    else snprintf(buffer, sizeof(buffer), "--");
    set_text(pr.values[0], buffer);
    if (p->layers && (working || p->layer)) snprintf(buffer, sizeof(buffer), "%lu / %lu", (unsigned long)p->layer, (unsigned long)p->layers);
    else snprintf(buffer, sizeof(buffer), "--");
    set_text(pr.values[1], buffer);
    temperature(buffer, sizeof(buffer), p->nozzle, p->nozzle_target);
    set_text(pr.values[2], buffer);
    temperature(buffer, sizeof(buffer), p->bed, p->bed_target);
    set_text(pr.values[3], buffer);
}

static void next_printer(lv_event_t *event)
{
    (void)event;
    const buddy_printer_t *p = shown();
    if (!p || pr.printers.count < 2) return;
    unsigned index = (unsigned)(p - pr.printers.items);
    pr.selected = pr.printers.items[(index + 1) % pr.printers.count].id;
    refresh();
}

lv_obj_t *buddy_printer_page_create(lv_obj_t *parent)
{
    const geometry_t g = geometry();
    pr.page = box(parent, 0, 0, WIDE ? 800 : WIDTH, WIDE ? 440 : BODY);
    pr.head = label(pr.page, g.margin, g.head_y, WIDE ? 440 : WIDTH - 2 * g.margin - g.chip_w - 8, g.head, BUDDY_COLOR_TEXT);
    pr.status = label(pr.page, WIDE ? 470 : g.margin, g.status_y, WIDE ? 302 : WIDTH - 2 * g.margin, g.status, BUDDY_COLOR_ACCENT);
    if (WIDE) lv_obj_set_style_text_align(pr.status, LV_TEXT_ALIGN_RIGHT, 0);
    if (g.rule_y >= 0 && !buddy_theme.square) {
        lv_obj_t *line = box(pr.page, g.margin, g.rule_y, WIDTH - 2 * g.margin, 1);
        lv_obj_set_style_bg_color(line, lv_color_hex(BUDDY_COLOR_RULE), 0);
        lv_obj_set_style_bg_opa(line, LV_OPA_COVER, 0);
    }
    /* More than one printer: a chip in the corner steps through them. */
    pr.chip = box(pr.page, WIDTH - g.margin - g.chip_w, WIDE ? 388 : g.head_y, g.chip_w, g.chip_h);
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

    /* The ring starts at the top and fills clockwise, like the timer's. */
    pr.ring = lv_arc_create(pr.page);
    lv_obj_remove_style_all(pr.ring);
    lv_arc_set_bg_angles(pr.ring, 0, 360);
    lv_arc_set_rotation(pr.ring, 270);
    lv_arc_set_range(pr.ring, 0, 1000);
    lv_arc_set_value(pr.ring, 0);
    lv_obj_set_pos(pr.ring, g.ring_x, g.ring_y);
    lv_obj_set_size(pr.ring, g.ring, g.ring);
    lv_obj_set_style_arc_width(pr.ring, g.ring / buddy_theme.ring_div, LV_PART_MAIN);
    lv_obj_set_style_arc_width(pr.ring, g.ring / buddy_theme.ring_div, LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(pr.ring, lv_color_hex(BUDDY_COLOR_RULE), LV_PART_MAIN);
    lv_obj_set_style_arc_rounded(pr.ring, !buddy_theme.square, LV_PART_INDICATOR);
    lv_obj_clear_flag(pr.ring, LV_OBJ_FLAG_CLICKABLE);
    int inner = g.ring - 2 * (g.ring / buddy_theme.ring_div) - 8;
    pr.percent = label(pr.ring, 0, 0, inner, g.percent, BUDDY_COLOR_TEXT);
    lv_obj_set_style_text_align(pr.percent, LV_TEXT_ALIGN_CENTER, 0);
    buddy_theme_numerals(pr.percent);
    int percent_h = lv_font_get_line_height(g.percent);
    /* The smallest ring holds the percentage alone; what it means sits just under it. */
    lv_obj_align(pr.percent, LV_ALIGN_CENTER, 0, TINY ? 0 : -lv_font_get_line_height(g.sub) / 2 - 2);
    pr.sub = TINY ? label(pr.page, g.ring_x - 8, g.ring_y + g.ring + 2, g.ring + 16, g.sub, BUDDY_COLOR_MUTED) : label(pr.ring, 0, 0, inner, g.sub, BUDDY_COLOR_MUTED);
    lv_obj_set_style_text_align(pr.sub, LV_TEXT_ALIGN_CENTER, 0);
    if (!TINY) lv_obj_align(pr.sub, LV_ALIGN_CENTER, 0, percent_h / 2 + 2);

    /* The file, then name and value rows: time left, layer, nozzle and bed. */
    pr.file = label(pr.page, g.col_x, g.col_y, WIDTH - g.col_x - g.margin, g.file, BUDDY_COLOR_TEXT);
    if (!TINY) { lv_label_set_long_mode(pr.file, LV_LABEL_LONG_DOT); lv_obj_set_height(pr.file, g.file_h); }
    const buddy_text_key_t names[ROWS] = { BT_PRINT_TIME_LEFT, BT_PRINT_LAYER, BT_PRINT_NOZZLE, BT_PRINT_BED };
    for (unsigned i = 0; i < ROWS; ++i) {
        int row = !WIDE && !TINY ? (int)i - 2 : (int)i;
        int x = g.rows_x, y = g.rows_y + row * g.row_h, w = g.rows_w, value_w = w * 3 / 5;
        /* The square screen stacks time and layer beside the ring, name over a larger value. */
        bool stacked = !WIDE && !TINY && i < 2;
        if (stacked) { x = g.col_x; w = WIDTH - g.col_x - g.margin; y = g.col_y + g.file_h + 8 + (int)i * 60; }
        pr.names[i] = label(pr.page, x, y, stacked ? w : w - value_w, stacked ? &buddy_language_font_14 : g.row_name, BUDDY_COLOR_MUTED);
        set_text(pr.names[i], BT(names[i]));
        pr.values[i] = label(pr.page, stacked ? x : x + w - value_w, stacked ? y + 18 : y, stacked ? w : value_w,
                             stacked ? &buddy_language_font_24 : g.row_value, BUDDY_COLOR_TEXT);
        if (!stacked) lv_obj_set_style_text_align(pr.values[i], LV_TEXT_ALIGN_RIGHT, 0);
        buddy_theme_numerals(pr.values[i]);
        pr.rules[i] = NULL;
        if (!TINY && !stacked) {
            pr.rules[i] = box(pr.page, x, y + g.row_h - 9, w, 1);
            lv_obj_set_style_bg_color(pr.rules[i], lv_color_hex(BUDDY_COLOR_RULE), 0);
            lv_obj_set_style_bg_opa(pr.rules[i], LV_OPA_COVER, 0);
        }
    }
    /* Nothing to show: no printer set up, or no PC. */
    pr.empty_title = label(pr.page, g.margin, WIDE ? 140 : TINY ? 44 : 120, WIDTH - 2 * g.margin,
                           WIDE ? &buddy_language_font_32 : TINY ? &buddy_language_font_20 : &buddy_language_font_24, BUDDY_COLOR_TEXT);
    pr.empty_body = label(pr.page, g.margin, WIDE ? 196 : TINY ? 76 : 164, WIDE ? 600 : WIDTH - 2 * g.margin,
                          WIDE ? &buddy_language_font_20 : TINY ? &buddy_language_font_14 : &buddy_language_font_16, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(pr.empty_body, LV_LABEL_LONG_WRAP);
    buddy_theme_type(pr.page);
    buddy_theme_heading(pr.head);
    lv_obj_add_flag(pr.page, LV_OBJ_FLAG_HIDDEN);
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
    const buddy_printer_t *p = pr.connected ? shown() : NULL;
    if (!pr.connected) snprintf(pr.status_text, sizeof(pr.status_text), "%s", BT(BT_PC_OFFLINE));
    else if (!p) snprintf(pr.status_text, sizeof(pr.status_text), "%s", BT(BT_PRINT_NO_PRINTERS));
    else if (p->state == BUDDY_PRINT_PRINTING) snprintf(pr.status_text, sizeof(pr.status_text), BT(BT_PRINT_PRINTING_LU), (unsigned long)p->progress);
    else snprintf(pr.status_text, sizeof(pr.status_text), "%s", buddy_printer_state_text(p));
    return pr.status_text;
}
