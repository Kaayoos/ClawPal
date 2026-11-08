#include "buddy_shortcuts_page.h"
#include "buddy_shortcut_data.h"
#include "buddy_app_icons.h"
#include "buddy_theme.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "buddy_ui_motion.h"
#include "board.h"
#include "esp_heap_caps.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define WIDTH BOARD_DISPLAY_WIDTH
#define BODY (BOARD_DISPLAY_HEIGHT - 44)
#define WIDE (BOARD_DISPLAY_WIDTH >= 800)
#define TINY (BOARD_DISPLAY_HEIGHT < 400)
#define SLOTS BUDDY_SHORTCUT_SLOTS
/* Quick replies stay silent; a slow one dims its tile, and none at all is reported. */
#define SENDING_DELAY_MS 700
#define REPLY_TIMEOUT_MS 5000
#define FLASH_MS 650
#define MESSAGE_MS 3200

static struct {
    lv_obj_t *page, *heading, *status, *rule, *empty, *empty_art, *heading2, *counter2, *end;
    lv_obj_t *tiles[SLOTS], *icons[SLOTS], *images[SLOTS], *labels[SLOTS], *checks[SLOTS];
    lv_img_dsc_t image_dsc[SLOTS];
    uint8_t *pixels[SLOTS];
    buddy_shortcut_deck_t deck;
    bool have_deck;
    buddy_shortcut_send_t send;
    bool connected, have_stats, present, away, playing;
    uint32_t rev, on, control_id;
    bool pending, dimmed, repeated;
    unsigned pending_slot;
    uint32_t pending_tick, pending_control;
    int flash_slot;
    uint32_t flash_tick;
    const char *message;
    bool message_error;
    uint32_t message_tick;
    unsigned views;
} sc = {.flash_slot = -1};

static lv_obj_t *box(lv_obj_t *parent, int x, int y, int width, int height)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_size(obj, width, height);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE);
    return obj;
}

static lv_obj_t *label(lv_obj_t *parent, const char *value, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *obj = lv_label_create(parent);
    lv_label_set_long_mode(obj, LV_LABEL_LONG_DOT);
    lv_label_set_text(obj, value);
    lv_obj_set_style_text_font(obj, font, 0);
    lv_obj_set_style_text_color(obj, lv_color_hex(color), 0);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static void place(lv_obj_t *obj, int x, int y, int width, int height)
{
    lv_obj_set_pos(obj, x, y);
    lv_obj_set_size(obj, width, height);
}

static void visible(lv_obj_t *obj, bool show)
{
    if (show) lv_obj_clear_flag(obj, LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(obj, LV_OBJ_FLAG_HIDDEN);
}

static void set_text(lv_obj_t *obj, const char *value)
{
    if (strcmp(lv_label_get_text(obj), value)) lv_label_set_text(obj, value);
}

static const buddy_shortcut_tile_t *tile_at(unsigned slot)
{
    return sc.have_deck && slot < sc.deck.size && sc.deck.tiles[slot].used ? &sc.deck.tiles[slot] : NULL;
}

static uint32_t tile_color(const buddy_shortcut_tile_t *tile)
{
    return tile->color && tile->color < BUDDY_SHORTCUT_COLOR_COUNT ? buddy_shortcut_colors[tile->color] : BUDDY_COLOR_ACCENT;
}

static bool tile_on(unsigned slot)
{
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    return tile && tile->toggle && sc.have_stats && sc.present && sc.rev == sc.deck.rev && ((sc.on >> slot) & 1U);
}

/* The PC answers only for the deck it last sent, and only while it is here. */
static bool deck_live(void)
{
    return sc.connected && sc.have_stats && sc.present && sc.have_deck && sc.rev == sc.deck.rev;
}

static const char *unavailable_reason(const buddy_shortcut_tile_t *tile)
{
    if (!sc.connected || !sc.have_stats) return BT(BT_PC_OFFLINE);
    if (!sc.present) return BT(BT_UPDATE_CLAWPAL_ON_YOUR_PC);
    if (!sc.have_deck || sc.rev != sc.deck.rev) return BT(BT_WAITING_FOR_YOUR_PC);
    if (sc.away && !tile->locked_ok) return BT(BT_PC_IS_LOCKED);
    return NULL;
}

/* The icon a tile shows right now: its lit twin while on, and pause while media plays. */
static unsigned shown_icon(unsigned slot)
{
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    if (!tile || tile->icon >= BUDDY_SHORTCUT_ICON_COUNT) return BUDDY_SHORTCUT_ICON_GRID;
    if (tile->icon == BUDDY_SHORTCUT_ICON_PLAY_PAUSE && sc.have_stats)
        return sc.playing ? BUDDY_SHORTCUT_ICON_PAUSE : BUDDY_SHORTCUT_ICON_PLAY;
    int on = buddy_shortcut_icons[tile->icon].on;
    return tile_on(slot) && on >= 0 ? (unsigned)on : tile->icon;
}

static uint32_t icon_ink(unsigned slot)
{
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    if (!tile) return BUDDY_COLOR_MUTED;
    return tile_on(slot) && !buddy_theme.outlined_cards ? BUDDY_COLOR_BG : tile_color(tile);
}

static void draw_icon(lv_event_t *event)
{
    lv_obj_t *obj = lv_event_get_target(event);
    unsigned slot = (unsigned)(uintptr_t)lv_event_get_user_data(event);
    if (!tile_at(slot) || (sc.pixels[slot] && tile_at(slot)->app_icon)) return;
    lv_draw_ctx_t *ctx = lv_event_get_draw_ctx(event);
    lv_area_t a;
    lv_obj_get_coords(obj, &a);
    int size = lv_obj_get_width(obj);
    lv_color_t ink = lv_color_hex(icon_ink(slot));
    lv_draw_line_dsc_t line;
    lv_draw_line_dsc_init(&line);
    line.color = ink;
    line.width = size >= 40 ? 3 : 2;
    line.round_start = line.round_end = 1;
    lv_draw_rect_dsc_t rect;
    lv_draw_rect_dsc_init(&rect);
    rect.bg_opa = LV_OPA_TRANSP;
    rect.border_color = ink;
    rect.border_width = line.width;
    lv_draw_arc_dsc_t arc;
    lv_draw_arc_dsc_init(&arc);
    arc.color = ink;
    arc.width = line.width;
    arc.rounded = 1;
    const buddy_shortcut_icon_t *icon = &buddy_shortcut_icons[shown_icon(slot)];
#define X(v) (a.x1 + (v) * size / 100)
#define Y(v) (a.y1 + (v) * size / 100)
    for (unsigned s = 0; s < icon->count; ++s) {
        const buddy_shortcut_shape_t *shape = &buddy_shortcut_shapes[icon->first + s];
        const int16_t *v = &buddy_shortcut_values[shape->first];
        if (shape->kind == 'l' || shape->kind == 'p') {
            for (unsigned i = 0; i + 3 < shape->count; i += 2) {
                lv_point_t p = {X(v[i]), Y(v[i + 1])}, q = {X(v[i + 2]), Y(v[i + 3])};
                lv_draw_line(ctx, &line, &p, &q);
            }
        } else if (shape->kind == 'b' || shape->kind == 'f') {
            lv_area_t b = {X(v[0]), Y(v[1]), X(v[0] + v[2]), Y(v[1] + v[3])};
            rect.radius = v[4] < 0 ? LV_RADIUS_CIRCLE : v[4] * size / 100;
            rect.bg_opa = shape->kind == 'f' ? LV_OPA_COVER : LV_OPA_TRANSP;
            rect.bg_color = ink;
            rect.border_width = shape->kind == 'f' ? 0 : line.width;
            lv_draw_rect(ctx, &rect, &b);
        } else if (shape->kind == 'a') {
            lv_point_t centre = {X(v[0]), Y(v[1])};
            lv_draw_arc(ctx, &arc, &centre, (uint16_t)(v[2] * size / 100 + line.width / 2), (uint16_t)v[3], (uint16_t)v[4]);
        }
    }
#undef X
#undef Y
}

static void restore_border(lv_obj_t *tile, bool on, uint32_t color)
{
    bool outlined = buddy_theme.outlined_cards;
    lv_obj_set_style_border_opa(tile, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(tile, outlined ? (on ? 2 : 1) : 0, 0);
    lv_obj_set_style_border_color(tile, lv_color_hex(outlined && on ? color : BUDDY_COLOR_RULE), 0);
}

/* A tile's whole look follows from the deck, the PC's state and any pending tap. */
static void style_tile(unsigned slot)
{
    lv_obj_t *obj = sc.tiles[slot];
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    if (!tile) {
        /* An empty slot in a set-up deck is a faint outline, so the grid still reads as a deck. */
        bool slot_shown = sc.have_deck && slot < sc.deck.size && sc.empty && lv_obj_has_flag(sc.empty, LV_OBJ_FLAG_HIDDEN);
        /* Outlined looks draw real tiles as outlines too, so their empty slots are fainter still. */
        visible(obj, slot_shown);
        lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_bg_opa(obj, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(obj, 1, 0);
        lv_obj_set_style_border_color(obj, lv_color_hex(BUDDY_COLOR_RULE), 0);
        lv_obj_set_style_border_opa(obj, buddy_theme.outlined_cards ? LV_OPA_40 : LV_OPA_70, 0);
        lv_obj_set_style_opa(obj, LV_OPA_COVER, 0);
        visible(sc.labels[slot], false);
        visible(sc.images[slot], false);
        visible(sc.checks[slot], false);
        return;
    }
    visible(obj, true);
    lv_obj_add_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    visible(sc.labels[slot], tile->label[0] != 0);
    bool on = tile_on(slot), outlined = buddy_theme.outlined_cards;
    uint32_t color = tile_color(tile);
    lv_color_t fill = !on ? lv_color_hex(BUDDY_COLOR_SURFACE) :
        outlined ? lv_color_mix(lv_color_hex(color), lv_color_hex(BUDDY_COLOR_BG), 52) : lv_color_hex(color);
    lv_obj_set_style_bg_color(obj, fill, 0);
    lv_obj_set_style_bg_opa(obj, outlined && !on ? LV_OPA_TRANSP : LV_OPA_COVER, 0);
    if (sc.flash_slot != (int)slot) restore_border(obj, on, color);
    lv_obj_set_style_text_color(sc.labels[slot], lv_color_hex(on && !outlined ? BUDDY_COLOR_BG : BUDDY_COLOR_TEXT), 0);
    set_text(sc.labels[slot], on && tile->label_on[0] ? tile->label_on : tile->label);
    bool image = tile->app_icon && sc.pixels[slot];
    visible(sc.images[slot], image);
    lv_obj_set_style_opa(sc.icons[slot], sc.pending && sc.dimmed && sc.pending_slot == slot ? LV_OPA_50 : LV_OPA_COVER, 0);
    lv_obj_set_style_opa(obj, unavailable_reason(tile) ? LV_OPA_40 : LV_OPA_COVER, 0);
    lv_obj_invalidate(sc.icons[slot]);
}

static const char *home_status(bool *warning)
{
    *warning = true;
    if (!sc.connected || !sc.have_stats) return BT(BT_PC_OFFLINE);
    if (!sc.present) return BT(BT_UPDATE_CLAWPAL_ON_YOUR_PC);
    *warning = false;
    if (!sc.have_deck || sc.rev != sc.deck.rev) return BT(BT_WAITING_FOR_YOUR_PC);
    if (sc.away) { *warning = true; return BT(BT_PC_IS_LOCKED); }
    const char *muted = NULL;
    bool any = false;
    for (unsigned slot = 0; slot < sc.deck.size; ++slot) {
        const buddy_shortcut_tile_t *tile = tile_at(slot);
        if (!tile) continue;
        any = true;
        if (!tile_on(slot)) continue;
        if (tile->icon == BUDDY_SHORTCUT_ICON_MIC) return BT(BT_MIC_MUTED);
        if (tile->icon == BUDDY_SHORTCUT_ICON_SPEAKER) muted = BT(BT_SOUND_MUTED);
    }
    if (muted) return muted;
    return any ? BT(BT_READY) : BT(BT_ADD_SHORTCUTS_IN_CLAWPAL_ON_YOUR_PC);
}

const char *buddy_shortcuts_page_status(void)
{
    bool warning;
    return home_status(&warning);
}

static void refresh(void)
{
    if (!sc.page) return;
    bool any = false;
    for (unsigned slot = 0; slot < SLOTS; ++slot) any |= tile_at(slot) != NULL;
    /* With a deck in hand the grid stays, dimmed, while the PC is away; otherwise say why it is empty. */
    visible(sc.empty, !any);
    visible(sc.empty_art, !any);
    for (unsigned slot = 0; slot < SLOTS; ++slot) style_tile(slot);
    bool warning;
    const char *status = home_status(&warning);
    if (!any) set_text(sc.empty, sc.have_deck && deck_live() ? BT(BT_ADD_SHORTCUTS_IN_CLAWPAL_ON_YOUR_PC) : status);
    const char *line = sc.message ? sc.message : any && status != BT(BT_READY) ? status : "";
    bool alert = sc.message ? sc.message_error : warning;
    char counter[16] = "";
    if (sc.views > 1 && !line[0]) {
        snprintf(counter, sizeof(counter), BT(BT_U_OF_U_PAGES), 1U, sc.views);
        line = counter;
    }
    /* The header: the page name, then its state or a reply in the room the name leaves. On the
     * smallest screen a reply needs the whole line, so it stands in for the name for a moment,
     * in each view's header so it is read wherever the tile was. */
    lv_point_t name;
    lv_txt_get_size(&name, BT(BT_SHORTCUTS), lv_obj_get_style_text_font(sc.heading, 0), 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
    int pad = WIDE ? 28 : 12, room = LV_MAX(60, WIDTH - pad * 2 - name.x - 16);
    bool takeover = TINY && sc.message;
    lv_obj_t *headings[] = {sc.heading, sc.heading2}, *corners[] = {sc.status, sc.counter2};
    for (unsigned i = 0; i < 2; ++i) {
        if (!headings[i]) continue;
        set_text(headings[i], takeover ? sc.message : BT(BT_SHORTCUTS));
        lv_obj_set_style_text_color(headings[i], lv_color_hex(!takeover ? BUDDY_COLOR_TEXT : sc.message_error ? BUDDY_COLOR_WARNING : BUDDY_COLOR_ACCENT), 0);
        lv_obj_set_x(corners[i], WIDTH - pad - room);
        lv_obj_set_width(corners[i], room);
        visible(corners[i], !takeover && (i == 0 || sc.views > 1));
    }
    set_text(sc.status, line);
    lv_obj_set_style_text_color(sc.status, lv_color_hex(alert ? BUDDY_COLOR_WARNING : line == counter ? BUDDY_COLOR_MUTED : BUDDY_COLOR_ACCENT), 0);
    if (sc.counter2) {
        snprintf(counter, sizeof(counter), BT(BT_U_OF_U_PAGES), 2U, sc.views);
        set_text(sc.counter2, counter);
    }
}

static void show_message(const char *message, bool error)
{
    sc.message = message;
    sc.message_error = error;
    sc.message_tick = lv_tick_get();
}

static void flash_ready(lv_anim_t *anim)
{
    unsigned slot = (unsigned)(uintptr_t)anim->user_data;
    if (sc.flash_slot == (int)slot) sc.flash_slot = -1;
    visible(sc.checks[slot], false);
    if (tile_at(slot)) restore_border(sc.tiles[slot], tile_on(slot), tile_color(tile_at(slot)));
}

static void flash_opa(void *obj, int32_t value) { lv_obj_set_style_border_opa(obj, (lv_opa_t)value, 0); }

/* A ring in the tile's colour, or the warning colour, that fades; a tick marks success. */
static void flash(unsigned slot, bool ok)
{
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    if (!tile) return;
    lv_obj_t *obj = sc.tiles[slot];
    if (sc.flash_slot >= 0 && sc.flash_slot != (int)slot) {
        lv_anim_del(sc.tiles[sc.flash_slot], flash_opa);
        visible(sc.checks[sc.flash_slot], false);
        unsigned previous = (unsigned)sc.flash_slot;
        sc.flash_slot = -1;
        style_tile(previous);
    }
    sc.flash_slot = (int)slot;
    sc.flash_tick = lv_tick_get();
    lv_anim_del(obj, flash_opa);
    lv_obj_set_style_border_width(obj, buddy_theme.border_w + 1, 0);
    lv_obj_set_style_border_color(obj, lv_color_hex(ok ? tile_color(tile) : BUDDY_COLOR_WARNING), 0);
    lv_obj_set_style_border_opa(obj, LV_OPA_COVER, 0);
    if (ok) {
        lv_obj_set_style_bg_color(sc.checks[slot], lv_color_hex(tile_color(tile)), 0);
        visible(sc.checks[slot], true);
    }
    uint32_t time = (uint32_t)FLASH_MS * buddy_theme.motion_pct / 100U;
    if (!time) return;
    lv_anim_t anim;
    lv_anim_init(&anim);
    lv_anim_set_var(&anim, obj);
    lv_anim_set_exec_cb(&anim, flash_opa);
    lv_anim_set_values(&anim, LV_OPA_COVER, LV_OPA_TRANSP);
    lv_anim_set_delay(&anim, 250);
    lv_anim_set_time(&anim, time);
    lv_anim_set_path_cb(&anim, lv_anim_path_ease_in);
    anim.user_data = (void *)(uintptr_t)slot;
    lv_anim_set_ready_cb(&anim, flash_ready);
    lv_anim_start(&anim);
}

static void run(unsigned slot)
{
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    if (!tile) return;
    const char *reason = unavailable_reason(tile);
    if (reason) {
        show_message(reason, true);
        flash(slot, false);
        refresh();
        return;
    }
    if (sc.pending) return;
    if (!sc.send || !sc.send("shortcut", buddy_shortcut_control_value(sc.deck.rev, slot))) {
        show_message(BT(BT_COULDN_T_SEND_TRY_AGAIN), true);
        flash(slot, false);
        refresh();
        return;
    }
    sc.pending = true;
    sc.dimmed = false;
    sc.pending_slot = slot;
    sc.pending_tick = lv_tick_get();
    sc.pending_control = sc.control_id;
    sc.message = NULL;
    refresh();
}

static void tile_event(lv_event_t *event)
{
    unsigned slot = (unsigned)(uintptr_t)lv_event_get_user_data(event);
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    if (!tile) return;
    if (lv_event_get_code(event) == LV_EVENT_LONG_PRESSED_REPEAT) {
        if (!tile->repeat) return;
        sc.repeated = true;
        run(slot);
        return;
    }
    /* A release after repeating steps is not one more step. */
    if (sc.repeated) { sc.repeated = false; return; }
    run(slot);
}

static void grid_shape(unsigned size, unsigned *columns, unsigned *rows)
{
    *columns = size == 6 || (TINY && size == 12) ? 3 : 4;
    *rows = (size + *columns - 1) / *columns;
}

static const lv_font_t *label_font(int width) { return width < 120 || TINY ? &buddy_language_font_14 : &buddy_language_font_16; }
static bool side_by_side(int width, int height) { return width >= 150 && height < 96; }

static int text_width(const char *text, const lv_font_t *font)
{
    lv_point_t size;
    lv_txt_get_size(&size, text, font, 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
    return size.x;
}

/* Two lines when a label is too long for one, but only if every word fits a line:
 * splitting a word reads worse than ending in dots. */
static int label_lines(const char *text, const lv_font_t *font, int width)
{
    if (!text[0]) return 0;
    if (text_width(text, font) <= width) return 1;
    char word[BUDDY_SHORTCUT_LABEL_BYTES + 1];
    for (const char *p = text; *p;) {
        while (*p == ' ') ++p;
        size_t n = strcspn(p, " ");
        if (!n) break;
        memcpy(word, p, n);
        word[n] = 0;
        if (text_width(word, font) > width) return 1;
        p += n;
    }
    return 2;
}

/* Tall tiles stack the icon over the label; short, wide ones sit them side by side.
 * Every tile in a grid shares one icon size and label line, whatever its own label needs. */
static void layout_tile(unsigned slot, int width, int height, int lines)
{
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    bool named = tile && tile->label[0];
    const lv_font_t *font = label_font(width);
    int line = lv_font_get_line_height(font);
    lv_obj_set_style_text_font(sc.labels[slot], font, 0);
    lv_obj_t *icon = sc.icons[slot];
    if (!named) {
        int size = LV_CLAMP(28, (width < height ? width : height) * 55 / 100, 64);
        place(icon, (width - size) / 2, (height - size) / 2, size, size);
        visible(sc.labels[slot], false);
    } else if (side_by_side(width, height)) {
        /* Side by side, a narrow tile trades a little icon and type size for the whole label. */
        bool narrow = width < 200;
        if (narrow) { font = &buddy_language_font_14; line = lv_font_get_line_height(font); lv_obj_set_style_text_font(sc.labels[slot], font, 0); }
        int size = LV_CLAMP(24, height - (narrow ? 38 : 30), 40), text = (narrow ? 12 : 14) + size + (narrow ? 10 : 12);
        place(icon, narrow ? 12 : 14, (height - size) / 2, size, size);
        place(sc.labels[slot], text, (height - line) / 2, width - text - 10, line);
        lv_obj_set_style_text_align(sc.labels[slot], LV_TEXT_ALIGN_LEFT, 0);
    } else {
        int label_h = LV_MAX(1, lines) * line;
        int gap = lines > 1 ? 3 : 6, size = LV_CLAMP(20, height - label_h - (lines > 1 ? 14 : 28), WIDE ? 52 : 44), top = (height - size - gap - label_h) / 2;
        place(icon, (width - size) / 2, top, size, size);
        /* A label that needs one line of a two-line grid stays one line, with dots if its word is too long. */
        int own = LV_MAX(label_lines(tile->label, font, width - 10), label_lines(tile->label_on, font, width - 10));
        place(sc.labels[slot], 5, top + size + gap, width - 10, LV_MIN(own, LV_MAX(1, lines)) * line);
        lv_obj_set_style_text_align(sc.labels[slot], LV_TEXT_ALIGN_CENTER, 0);
    }
    lv_img_set_zoom(sc.images[slot], (uint16_t)(256 * lv_obj_get_width(icon) / BUDDY_SHORTCUT_ICON_SIZE));
    int badge = TINY ? 16 : 20;
    place(sc.checks[slot], width - badge - 6, 6, badge, badge);
}

static void layout(void)
{
    unsigned size = sc.have_deck ? sc.deck.size : 12, columns, rows;
    grid_shape(size, &columns, &rows);
    int x0, y0, area_w, area_h, gap;
    unsigned rows_per_view = rows;
    if (WIDE) { x0 = 28; y0 = 100; area_w = 744; area_h = 324; gap = 14; }
    else if (!TINY) { x0 = 12; y0 = 50; area_w = WIDTH - 24; area_h = BODY - 58; gap = 10; }
    else { x0 = 8; y0 = 28; area_w = WIDTH - 16; area_h = BODY - 32; gap = 6; rows_per_view = 2; }
    bool any = false;
    for (unsigned slot = 0; slot < SLOTS; ++slot) any |= tile_at(slot) != NULL;
    /* An empty deck is one view holding its explanation. */
    sc.views = TINY && any ? (rows + 1) / 2 : 1;
    int tile_w = (area_w - gap * ((int)columns - 1)) / (int)columns;
    int tile_h = (area_h - gap * ((int)rows_per_view - 1)) / (int)rows_per_view;
    int lines = 1;
    if (!side_by_side(tile_w, tile_h))
        for (unsigned slot = 0; slot < SLOTS; ++slot) {
            const buddy_shortcut_tile_t *tile = tile_at(slot);
            if (!tile) continue;
            lines = LV_MAX(lines, label_lines(tile->label, label_font(tile_w), tile_w - 10));
            lines = LV_MAX(lines, label_lines(tile->label_on, label_font(tile_w), tile_w - 10));
        }
    for (unsigned slot = 0; slot < SLOTS; ++slot) {
        unsigned row = slot / columns, column = slot % columns, view = row / rows_per_view;
        int y = (int)view * BODY + y0 + (int)(row % rows_per_view) * (tile_h + gap);
        place(sc.tiles[slot], x0 + (int)column * (tile_w + gap), y, tile_w, tile_h);
        layout_tile(slot, tile_w, tile_h, lines);
        lv_obj_set_style_radius(sc.tiles[slot], WIDE ? 14 : TINY ? 10 : 12, 0);
    }
    if (TINY) {
        /* One swipe moves one view; a spacer lets the last view reach the top. */
        bool more = sc.views > 1;
        visible(sc.heading2, more);
        visible(sc.counter2, more);
        visible(sc.end, more);
        if (more) {
            lv_obj_add_flag(sc.page, LV_OBJ_FLAG_SCROLLABLE);
            lv_obj_set_pos(sc.end, 0, 2 * BODY - 2);
        } else {
            lv_obj_scroll_to_y(sc.page, 0, LV_ANIM_OFF);
            lv_obj_clear_flag(sc.page, LV_OBJ_FLAG_SCROLLABLE);
        }
    }
}

lv_obj_t *buddy_shortcuts_page_create(lv_obj_t *parent)
{
    lv_obj_t *page = sc.page = box(parent, 0, 0, WIDTH, BODY);
    lv_obj_set_style_bg_color(page, lv_color_hex(BUDDY_COLOR_BG), 0);
    lv_obj_set_style_bg_opa(page, LV_OPA_COVER, 0);
    const lv_font_t *heading_font = WIDE ? &buddy_language_font_24 : TINY ? &buddy_language_font_14 : &buddy_language_font_20;
    const lv_font_t *status_font = WIDE ? &buddy_language_font_16 : &buddy_language_font_14;
    int pad = WIDE ? 28 : 12, top = WIDE ? 26 : TINY ? 6 : 12, status_w = WIDE ? 264 : TINY ? 150 : 220;
    sc.heading = label(page, BT(BT_SHORTCUTS), heading_font, BUDDY_COLOR_TEXT);
    place(sc.heading, pad, top, WIDTH - pad * 2, lv_font_get_line_height(heading_font));
    sc.status = label(page, "", status_font, BUDDY_COLOR_ACCENT);
    lv_obj_set_style_text_align(sc.status, LV_TEXT_ALIGN_RIGHT, 0);
    place(sc.status, WIDTH - pad - status_w, top + (WIDE ? 3 : TINY ? 0 : 3), status_w, lv_font_get_line_height(status_font));
    if (WIDE) {
        sc.rule = box(page, 28, 84, 744, 1);
        lv_obj_set_style_bg_color(sc.rule, lv_color_hex(BUDDY_COLOR_RULE), 0);
        lv_obj_set_style_bg_opa(sc.rule, LV_OPA_COVER, 0);
    }
    if (TINY) {
        sc.heading2 = label(page, BT(BT_SHORTCUTS), heading_font, BUDDY_COLOR_TEXT);
        place(sc.heading2, pad, BODY + top, WIDTH - pad * 2, lv_font_get_line_height(heading_font));
        sc.counter2 = label(page, "", status_font, BUDDY_COLOR_MUTED);
        lv_obj_set_style_text_align(sc.counter2, LV_TEXT_ALIGN_RIGHT, 0);
        place(sc.counter2, WIDTH - pad - status_w, BODY + top, status_w, lv_font_get_line_height(status_font));
        sc.end = box(page, 0, 2 * BODY - 2, 2, 2);
        lv_obj_add_flag(page, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_scroll_dir(page, LV_DIR_VER);
        lv_obj_set_scrollbar_mode(page, LV_SCROLLBAR_MODE_OFF);
        lv_obj_t *stops[] = {box(page, 0, 0, 2, 2), box(page, 0, BODY, 2, 2)};
        for (unsigned i = 0; i < 2; ++i) lv_obj_add_flag(stops[i], LV_OBJ_FLAG_SNAPPABLE);
        lv_obj_set_scroll_snap_y(page, LV_SCROLL_SNAP_START);
        lv_obj_add_flag(page, LV_OBJ_FLAG_SCROLL_ONE);
    }
    int art = WIDE ? 72 : TINY ? 40 : 56;
    sc.empty_art = buddy_app_icon_create(page, BUDDY_APP_SHORTCUTS, (WIDTH - art) / 2, WIDE ? 170 : TINY ? 48 : 130, art);
    sc.empty = label(page, "", WIDE ? &buddy_language_font_20 : &buddy_language_font_16, BUDDY_COLOR_MUTED);
    lv_obj_set_style_text_align(sc.empty, LV_TEXT_ALIGN_CENTER, 0);
    place(sc.empty, pad, (WIDE ? 170 : TINY ? 48 : 130) + art + (TINY ? 10 : 18), WIDTH - pad * 2, 52);
    lv_label_set_long_mode(sc.empty, LV_LABEL_LONG_WRAP);

    static lv_style_transition_dsc_t press;
    static const lv_style_prop_t props[] = {LV_STYLE_TRANSFORM_WIDTH, LV_STYLE_TRANSFORM_HEIGHT, 0};
    lv_style_transition_dsc_init(&press, props, lv_anim_path_ease_out, (uint32_t)90 * buddy_theme.motion_pct / 100U, 0, NULL);
    for (unsigned slot = 0; slot < SLOTS; ++slot) {
        lv_obj_t *tile = sc.tiles[slot] = box(page, 0, 0, 10, 10);
        lv_obj_add_flag(tile, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_HIDDEN);
        /* A finger that slides off cancels the tap. */
        lv_obj_clear_flag(tile, LV_OBJ_FLAG_PRESS_LOCK);
        lv_obj_set_style_bg_color(tile, lv_color_hex(BUDDY_COLOR_PRESSED), LV_STATE_PRESSED);
        lv_obj_set_style_bg_opa(tile, LV_OPA_COVER, LV_STATE_PRESSED);
        lv_obj_set_style_transform_width(tile, -3, LV_STATE_PRESSED);
        lv_obj_set_style_transform_height(tile, -3, LV_STATE_PRESSED);
        if (buddy_theme.motion_pct) lv_obj_set_style_transition(tile, &press, 0);
        lv_obj_add_event_cb(tile, tile_event, LV_EVENT_CLICKED, (void *)(uintptr_t)slot);
        lv_obj_add_event_cb(tile, tile_event, LV_EVENT_LONG_PRESSED_REPEAT, (void *)(uintptr_t)slot);
        sc.icons[slot] = box(tile, 0, 0, 10, 10);
        lv_obj_add_event_cb(sc.icons[slot], draw_icon, LV_EVENT_DRAW_MAIN, (void *)(uintptr_t)slot);
        sc.images[slot] = lv_img_create(sc.icons[slot]);
        lv_img_set_pivot(sc.images[slot], 0, 0);
        lv_obj_clear_flag(sc.images[slot], LV_OBJ_FLAG_CLICKABLE);
        visible(sc.images[slot], false);
        sc.labels[slot] = label(tile, "", &buddy_language_font_16, BUDDY_COLOR_TEXT);
        lv_obj_t *check = sc.checks[slot] = box(tile, 0, 0, 20, 20);
        lv_obj_set_style_radius(check, LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_bg_opa(check, LV_OPA_COVER, 0);
        lv_obj_t *mark = label(check, LV_SYMBOL_OK, &buddy_language_font_14, BUDDY_COLOR_BG);
        lv_obj_center(mark);
        visible(check, false);
    }
    layout();
    refresh();
    return page;
}

void buddy_shortcuts_page_set_sender(buddy_shortcut_send_t send) { sc.send = send; }

void buddy_shortcuts_page_set_deck(const buddy_shortcut_deck_t *deck)
{
    bool same = sc.have_deck && sc.deck.rev == deck->rev;
    /* App icons belong to the deck they were sent for. */
    for (unsigned slot = 0; slot < SLOTS && !same; ++slot) {
        if (!sc.pixels[slot]) continue;
        lv_img_set_src(sc.images[slot], NULL);
        visible(sc.images[slot], false);
        free(sc.pixels[slot]);
        sc.pixels[slot] = NULL;
    }
    sc.deck = *deck;
    sc.have_deck = true;
    if (sc.pending && !same) sc.pending = false;
    if (!sc.page) return;
    layout();
    refresh();
}

bool buddy_shortcuts_page_set_icon(uint32_t rev, unsigned slot, uint8_t *pixels)
{
    const buddy_shortcut_tile_t *tile = tile_at(slot);
    if (!sc.page || !tile || !tile->app_icon || rev != sc.deck.rev) {
        free(pixels);
        return false;
    }
    lv_img_set_src(sc.images[slot], NULL);
    free(sc.pixels[slot]);
    sc.pixels[slot] = pixels;
    sc.image_dsc[slot] = (lv_img_dsc_t){
        .header = {.cf = LV_IMG_CF_TRUE_COLOR_ALPHA, .w = BUDDY_SHORTCUT_ICON_SIZE, .h = BUDDY_SHORTCUT_ICON_SIZE},
        .data_size = BUDDY_SHORTCUT_ICON_BYTES, .data = pixels,
    };
    lv_img_cache_invalidate_src(&sc.image_dsc[slot]);
    lv_img_set_src(sc.images[slot], &sc.image_dsc[slot]);
    lv_img_set_zoom(sc.images[slot], (uint16_t)(256 * lv_obj_get_width(sc.icons[slot]) / BUDDY_SHORTCUT_ICON_SIZE));
    style_tile(slot);
    return true;
}

void buddy_shortcuts_page_update(const buddy_stats_t *stats)
{
    /* Telemetry arrives every second; the tiles are restyled only when something they show changed. */
    bool changed = !sc.have_stats || sc.present != stats->shortcuts.present || sc.rev != stats->shortcuts.rev ||
        sc.on != stats->shortcuts.on || sc.away != stats->away || sc.playing != stats->music.playing;
    sc.have_stats = true;
    sc.present = stats->shortcuts.present;
    sc.rev = stats->shortcuts.rev;
    sc.on = stats->shortcuts.on;
    sc.away = stats->away;
    sc.playing = stats->music.playing;
    if (sc.pending && stats->music.control_id != sc.pending_control) {
        changed = true;
        sc.pending = false;
        const char *error = stats->music.control_error;
        if (stats->music.control_ok) flash(sc.pending_slot, true);
        else {
            show_message(strcmp(error, "locked") == 0 ? BT(BT_PC_IS_LOCKED) :
                         strcmp(error, "changed") == 0 ? BT(BT_SHORTCUTS_CHANGED_TRY_AGAIN) : BT(BT_COULDN_T_RUN_THAT_SHORTCUT), true);
            flash(sc.pending_slot, false);
        }
    }
    sc.control_id = stats->music.control_id;
    if (changed) refresh();
}

void buddy_shortcuts_page_set_connected(bool connected)
{
    if (connected == sc.connected) return;
    sc.connected = connected;
    if (!connected) {
        sc.have_stats = false;
        if (sc.pending) {
            sc.pending = false;
            show_message(BT(BT_PC_OFFLINE), true);
        }
    }
    refresh();
}

bool buddy_shortcuts_page_pending(void) { return sc.pending; }

void buddy_shortcuts_page_tick(void)
{
    bool changed = false;
    if (sc.pending && lv_tick_elaps(sc.pending_tick) >= REPLY_TIMEOUT_MS) {
        sc.pending = false;
        show_message(BT(BT_NO_REPLY_FROM_YOUR_PC), true);
        flash(sc.pending_slot, false);
        changed = true;
    } else if (sc.pending && !sc.dimmed && lv_tick_elaps(sc.pending_tick) >= SENDING_DELAY_MS) {
        sc.dimmed = true;
        changed = true;
    }
    if (sc.flash_slot >= 0 && lv_tick_elaps(sc.flash_tick) >= FLASH_MS + 300U) {
        unsigned slot = (unsigned)sc.flash_slot;
        lv_anim_del(sc.tiles[slot], flash_opa);
        visible(sc.checks[slot], false);
        sc.flash_slot = -1;
        changed = true;
    }
    if (sc.message && lv_tick_elaps(sc.message_tick) >= MESSAGE_MS) {
        sc.message = NULL;
        changed = true;
    }
    if (changed) refresh();
}
