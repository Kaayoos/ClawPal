#include "buddy_lyrics_view.h"
#include "buddy_theme.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "board.h"
#include "esp_heap_caps.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Each line lights up a little before it is sung: a late line is noticed, an early one isn't. */
#define LEAD_MS 150
/* The reel's slide: quick, and eased so it settles rather than stops. */
#define SLIDE_MS 260
#define TICK_MS 30
/* One line before the current one and the rest after it, as many as fit. */
#define SLOTS 7
#define GAP 10

static struct {
    lv_obj_t *panel, *heading, *window, *reel, *slots[SLOTS], *message, *plain;
    lv_timer_t *timer;
    buddy_lyrics_t *lyrics;
    buddy_lyrics_sync_t sync;
    const lv_font_t *font;
    char title[192], artist[128];
    int shown;        /* the line lit now: -1 before the first, -2 when the reel must be set again */
    int line_h, anchor, width;
    bool open, heading_on;
    uint32_t plain_id; /* the plain document now in the scroller, 0 for none */
} lv;

static lv_obj_t *label(lv_obj_t *parent, const lv_font_t *font, uint32_t color)
{
    lv_obj_t *obj = lv_label_create(parent);
    lv_label_set_text(obj, "");
    lv_obj_set_style_text_font(obj, font, 0);
    lv_obj_set_style_text_color(obj, lv_color_hex(color), 0);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static lv_obj_t *plain_box(lv_obj_t *parent)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
    return obj;
}

static void set_text(lv_obj_t *obj, const char *text)
{
    if (strcmp(lv_label_get_text(obj), text) != 0) lv_label_set_text(obj, text);
}

static void show(lv_obj_t *obj, bool visible)
{
    if (visible) lv_obj_clear_flag(obj, LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(obj, LV_OBJ_FLAG_HIDDEN);
}

static bool document_ready(void)
{
    return lv.lyrics && lv.lyrics->count && lv.lyrics->id == lv.sync.id &&
           (lv.sync.status == BUDDY_LYRICS_SYNCED || lv.sync.status == BUDDY_LYRICS_PLAIN);
}

/* A line's words; a pause, an unreadable line and the wait before the first are a music note. */
static const char *line_text(int index)
{
    if (!lv.lyrics || index < -1 || index >= (int)lv.lyrics->count) return "";
    if (index == -1) return LV_SYMBOL_AUDIO;
    const buddy_lyric_line_t *line = &lv.lyrics->lines[index];
    const char *text = lv.lyrics->text + line->offset;
    return line->missing || !text[0] ? LV_SYMBOL_AUDIO : text;
}

/* Where the song is now on this display's clock, counting on from the PC's reference point. */
static uint32_t position_now(void)
{
    if (!lv.sync.anchor) return 0;
    int64_t position = lv.sync.position_ms;
    if (lv.sync.playing) position += (int32_t)(lv_tick_get() - lv.sync.at_ms);
    return position < 0 ? 0 : position > BUDDY_LYRICS_MAX_MS ? BUDDY_LYRICS_MAX_MS : (uint32_t)position;
}

/* The last line whose time has come, or -1 before the first. */
static int line_at(uint32_t ms)
{
    int low = 0, high = (int)lv.lyrics->count - 1, found = -1;
    while (low <= high) {
        int middle = (low + high) / 2;
        if (lv.lyrics->lines[middle].time_ms <= ms) { found = middle; low = middle + 1; }
        else high = middle - 1;
    }
    return found;
}

/* A line takes one or two rows of type; longer ones end with dots. */
static int line_height(const char *text)
{
    lv_point_t size;
    lv_txt_get_size(&size, text, lv.font, 0, 4, lv.width, LV_TEXT_FLAG_NONE);
    int two = 2 * lv.line_h + 4;
    return size.y <= lv.line_h ? lv.line_h : two;
}

/* Lays the reel out around line `index`, the current one at the anchor. Returns where line
 * `index + 1` sits, which is how far the reel slides when it comes. */
static int arrange(int index)
{
    int heights[SLOTS], y[SLOTS];
    for (int r = 0; r < SLOTS; ++r) heights[r] = line_height(line_text(index - 1 + r));
    y[1] = lv.anchor;
    y[0] = y[1] - GAP - heights[0];
    for (int r = 2; r < SLOTS; ++r) y[r] = y[r - 1] + heights[r - 1] + GAP;
    static const lv_opa_t fade[SLOTS] = { LV_OPA_50, LV_OPA_COVER, LV_OPA_COVER, LV_OPA_70, LV_OPA_50, LV_OPA_40, LV_OPA_30 };
    for (int r = 0; r < SLOTS; ++r) {
        lv_obj_t *slot = lv.slots[r];
        set_text(slot, line_text(index - 1 + r));
        lv_obj_set_pos(slot, 0, y[r]);
        lv_obj_set_size(slot, lv.width, heights[r]);
        /* Colour and text opacity only: dimming a whole object would draw it through a layer. */
        lv_obj_set_style_text_color(slot, lv_color_hex(r == 1 ? BUDDY_COLOR_TEXT : BUDDY_COLOR_MUTED), 0);
        lv_obj_set_style_text_opa(slot, fade[r], 0);
    }
    return y[2] - y[1];
}

static void slide(void *obj, int32_t value) { lv_obj_set_style_translate_y(obj, (lv_coord_t)value, 0); }

/* The reel moves up by `from` pixels into place; a jump back moves it down. */
static void glide(int from)
{
    uint32_t time = SLIDE_MS * buddy_theme.motion_pct / 100U;
    lv_anim_del(lv.reel, slide);
    if (!time || !from) { lv_obj_set_style_translate_y(lv.reel, 0, 0); return; }
    lv_obj_set_style_translate_y(lv.reel, (lv_coord_t)from, 0);
    lv_anim_t anim;
    lv_anim_init(&anim);
    lv_anim_set_var(&anim, lv.reel);
    lv_anim_set_exec_cb(&anim, slide);
    lv_anim_set_values(&anim, from, 0);
    lv_anim_set_time(&anim, time);
    lv_anim_set_path_cb(&anim, lv_anim_path_ease_out);
    lv_anim_start(&anim);
}

static void draw_synced(bool animate)
{
    int index = line_at(position_now() + LEAD_MS);
    if (index == lv.shown) return;
    int previous = lv.shown;
    lv.shown = index;
    int step = arrange(index);
    /* The next line slides into place; a seek moves the reel a little the same way. */
    int from = !animate || previous == -2 ? 0 : index == previous + 1 ? step : index > previous ? lv.line_h : -lv.line_h;
    glide(from);
}

static void tick(lv_timer_t *timer)
{
    (void)timer;
    if (lv.open && document_ready() && lv.lyrics->synced) draw_synced(true);
}

/* Plain lyrics: every line at once, in a column the finger scrolls. */
static void fill_plain(void)
{
    if (lv.plain_id == lv.lyrics->id) return;
    lv.plain_id = lv.lyrics->id;
    size_t size = 1;
    for (unsigned i = 0; i < lv.lyrics->count; ++i) size += strlen(lv.lyrics->text + lv.lyrics->lines[i].offset) + 8;
    char *text = heap_caps_malloc(size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (!text) text = malloc(size);
    if (!text) return;
    size_t used = 0;
    for (unsigned i = 0; i < lv.lyrics->count; ++i) {
        const buddy_lyric_line_t *line = &lv.lyrics->lines[i];
        /* A verse break stays a blank line; only an unreadable one becomes a note. */
        const char *words = line->missing ? LV_SYMBOL_AUDIO : lv.lyrics->text + line->offset;
        size_t length = strlen(words);
        memcpy(text + used, words, length);
        used += length;
        if (i + 1 < lv.lyrics->count) text[used++] = '\n';
    }
    text[used] = 0;
    lv_label_set_text(lv.plain, text);
    free(text);
    lv_obj_scroll_to_y(lv.window, 0, LV_ANIM_OFF);
}

static void refresh(void)
{
    if (!lv.panel) return;
    bool ready = document_ready(), plain = ready && !lv.lyrics->synced, synced = ready && lv.lyrics->synced;
    show(lv.reel, synced);
    show(lv.plain, plain);
    show(lv.message, !ready);
    if (plain) {
        lv_obj_add_flag(lv.window, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
        fill_plain();
    } else {
        lv_obj_clear_flag(lv.window, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
        lv_obj_scroll_to_y(lv.window, 0, LV_ANIM_OFF);
        lv.plain_id = 0;
    }
    char heading[352];
    if (plain) snprintf(heading, sizeof(heading), "%s", BT(BT_LYRICS_NOT_SYNCED));
    else if (lv.artist[0]) snprintf(heading, sizeof(heading), "%s  \xC2\xB7  %s", lv.title, lv.artist);
    else snprintf(heading, sizeof(heading), "%s", lv.title);
    set_text(lv.heading, heading);
    if (!ready) {
        lv.shown = -2;
        set_text(lv.message, lv.sync.status == BUDDY_LYRICS_NONE ? BT(BT_NO_LYRICS_FOUND) :
            lv.sync.status == BUDDY_LYRICS_INSTRUMENTAL ? BT(BT_LYRICS_INSTRUMENTAL) :
            lv.sync.status == BUDDY_LYRICS_UNSUPPORTED ? BT(BT_LYRICS_UNSUPPORTED) :
            lv.sync.status == BUDDY_LYRICS_UNAVAILABLE ? BT(BT_LYRICS_UNAVAILABLE) : BT(BT_LOOKING_FOR_LYRICS));
    } else if (synced) draw_synced(lv.open);
}

void buddy_lyrics_view_create(lv_obj_t *page)
{
    lv.shown = -2;
    lv.font = &buddy_language_font_32;
    lv.panel = plain_box(page);
    lv_obj_add_flag(lv.panel, LV_OBJ_FLAG_HIDDEN);
    lv.heading = label(lv.panel, &buddy_language_font_16, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(lv.heading, LV_LABEL_LONG_DOT);
    /* The window clips the reel; at either end of plain lyrics a swipe carries on into the page. */
    lv.window = plain_box(lv.panel);
    lv_obj_add_flag(lv.window, LV_OBJ_FLAG_SCROLL_CHAIN_VER);
    lv_obj_set_scroll_dir(lv.window, LV_DIR_VER);
    lv_obj_set_scrollbar_mode(lv.window, LV_SCROLLBAR_MODE_ACTIVE);
    lv.reel = plain_box(lv.window);
    for (int r = 0; r < SLOTS; ++r) {
        lv.slots[r] = label(lv.reel, lv.font, BUDDY_COLOR_MUTED);
        lv_label_set_long_mode(lv.slots[r], LV_LABEL_LONG_DOT);
        lv_obj_set_style_text_line_space(lv.slots[r], 4, 0);
    }
    lv.message = label(lv.window, &buddy_language_font_24, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(lv.message, LV_LABEL_LONG_WRAP);
    lv.plain = label(lv.window, &buddy_language_font_20, BUDDY_COLOR_TEXT);
    lv_label_set_long_mode(lv.plain, LV_LABEL_LONG_WRAP);
    lv_obj_set_style_text_line_space(lv.plain, 6, 0);
    show(lv.plain, false);
    show(lv.reel, false);
}

void buddy_lyrics_view_place(int x, int y, int width, int height, const lv_font_t *font, bool heading)
{
    if (!lv.panel) return;
    bool wide = BOARD_DISPLAY_WIDTH >= 800;
    const lv_font_t *small = wide ? &buddy_language_font_16 : &buddy_language_font_14;
    lv.font = font;
    lv.heading_on = heading;
    lv.width = width;
    lv_obj_set_pos(lv.panel, x, y);
    lv_obj_set_size(lv.panel, width, height);
    int heading_h = heading ? lv_font_get_line_height(small) + 8 : 0;
    show(lv.heading, heading);
    lv_obj_set_style_text_font(lv.heading, small, 0);
    lv_obj_set_pos(lv.heading, 0, 0);
    lv_obj_set_size(lv.heading, width, lv_font_get_line_height(small));
    lv_obj_set_pos(lv.window, 0, heading_h);
    lv_obj_set_size(lv.window, width, height - heading_h);
    lv_obj_set_size(lv.reel, width, height - heading_h);
    for (int r = 0; r < SLOTS; ++r) lv_obj_set_style_text_font(lv.slots[r], font, 0);
    /* Heavy looks set the big lines in their heavy type, as they do every display size. */
    buddy_theme_type(lv.reel);
    lv.font = lv_obj_get_style_text_font(lv.slots[0], 0);
    lv.line_h = lv_font_get_line_height(lv.font);
    /* The line being sung sits a third of the way down, with the one before it above. A view
     * too short for that (the smallest screen's) starts at the line being sung, and the one
     * before slides out above it. */
    int room = height - heading_h;
    lv.anchor = room >= 4 * lv.line_h ? LV_MAX(lv.line_h + GAP, room / 3) : 2;
    const lv_font_t *message = wide ? &buddy_language_font_24 : BOARD_DISPLAY_HEIGHT < 400 ? &buddy_language_font_16 : &buddy_language_font_20;
    lv_obj_set_style_text_font(lv.message, message, 0);
    lv_obj_set_pos(lv.message, 0, 0);
    lv_obj_set_width(lv.message, width);
    lv_obj_set_style_text_font(lv.plain, wide ? &buddy_language_font_20 : BOARD_DISPLAY_HEIGHT < 400 ? &buddy_language_font_14 : &buddy_language_font_16, 0);
    lv_obj_set_pos(lv.plain, 0, 0);
    lv_obj_set_width(lv.plain, width - 8);
    lv.shown = -2;
    lv.plain_id = 0;
    refresh();
}

void buddy_lyrics_view_set_document(buddy_lyrics_t *lyrics)
{
    free(lv.lyrics);
    lv.lyrics = lyrics;
    lv.shown = -2;
    lv.plain_id = 0;
    refresh();
}

void buddy_lyrics_view_update(const buddy_lyrics_sync_t *sync, const char *title, const char *artist)
{
    bool changed = sync->id != lv.sync.id || sync->status != lv.sync.status || sync->anchor != lv.sync.anchor ||
                   strcmp(title, lv.title) != 0 || strcmp(artist, lv.artist) != 0;
    /* A new song starts its reel afresh; a new reference point only re-times it. */
    if (sync->id != lv.sync.id) lv.shown = -2;
    lv.sync = *sync;
    snprintf(lv.title, sizeof(lv.title), "%s", title);
    snprintf(lv.artist, sizeof(lv.artist), "%s", artist);
    if (changed) refresh();
}

void buddy_lyrics_view_set_open(bool open)
{
    if (!lv.panel || open == lv.open) return;
    lv.open = open;
    show(lv.panel, open);
    /* The timer lives only once lyrics are first opened, and rests while they are closed. */
    if (open && !lv.timer) lv.timer = lv_timer_create(tick, TICK_MS, NULL);
    if (lv.timer) {
        if (open) lv_timer_resume(lv.timer);
        else lv_timer_pause(lv.timer);
    }
    lv.shown = -2;
    refresh();
}

bool buddy_lyrics_view_is_open(void) { return lv.open; }
bool buddy_lyrics_view_ready(void) { return document_ready(); }
