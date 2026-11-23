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

#define WIDE (BOARD_DISPLAY_WIDTH >= 800)
#define TINY (BOARD_DISPLAY_HEIGHT < 400)
/* Each line shows a little before it is sung: a late line is noticed, an early one isn't. */
#define LEAD_MS 120
#define FADE_MS 160
#define TICK_MS 30

static struct {
    lv_obj_t *panel, *heading, *current, *next, *plain, *credit;
    lv_timer_t *timer;
    buddy_lyrics_t *lyrics;
    buddy_lyrics_sync_t sync;
    char title[192], artist[128];
    int shown; /* the line on screen, -1 before the first, -2 when it must be drawn again */
    bool open, credit_hidden, message;
    uint32_t mode_id; /* the plain document now in the scroller, 0 for none */
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

static void set_text(lv_obj_t *obj, const char *text)
{
    if (strcmp(lv_label_get_text(obj), text) != 0) lv_label_set_text(obj, text);
}

static void show(lv_obj_t *obj, bool visible)
{
    if (visible) lv_obj_clear_flag(obj, LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(obj, LV_OBJ_FLAG_HIDDEN);
}

static void fade(void *obj, int32_t value) { lv_obj_set_style_opa(obj, (lv_opa_t)value, 0); }

/* A new line rises out of nothing; looks that keep still simply swap it. */
static void fade_in(lv_obj_t *obj)
{
    uint32_t time = FADE_MS * buddy_theme.motion_pct / 100U;
    lv_anim_del(obj, fade);
    if (!time) { lv_obj_set_style_opa(obj, LV_OPA_COVER, 0); return; }
    lv_obj_set_style_opa(obj, LV_OPA_TRANSP, 0);
    lv_anim_t anim;
    lv_anim_init(&anim);
    lv_anim_set_var(&anim, obj);
    lv_anim_set_exec_cb(&anim, fade);
    lv_anim_set_values(&anim, LV_OPA_TRANSP, LV_OPA_COVER);
    lv_anim_set_time(&anim, time);
    lv_anim_set_path_cb(&anim, lv_anim_path_ease_out);
    lv_anim_start(&anim);
}

/* A line's words, or a music note for a pause or a line the fonts can't draw. */
static const char *line_text(int index)
{
    if (!lv.lyrics || index < 0 || index >= lv.lyrics->count) return "";
    const buddy_lyric_line_t *line = &lv.lyrics->lines[index];
    const char *text = lv.lyrics->text + line->offset;
    return line->missing || !text[0] ? LV_SYMBOL_AUDIO : text;
}

static bool document_ready(void)
{
    return lv.lyrics && lv.lyrics->count && lv.lyrics->id == lv.sync.id &&
           (lv.sync.status == BUDDY_LYRICS_SYNCED || lv.sync.status == BUDDY_LYRICS_PLAIN);
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

static void draw_synced(void)
{
    int index = line_at(position_now() + LEAD_MS);
    if (index == lv.shown) return;
    lv.shown = index;
    lv_obj_set_style_text_color(lv.current, lv_color_hex(BUDDY_COLOR_TEXT), 0);
    set_text(lv.current, index < 0 ? LV_SYMBOL_AUDIO : line_text(index));
    set_text(lv.next, line_text(index + 1));
    fade_in(lv.current);
    fade_in(lv.next);
}

static void tick(lv_timer_t *timer)
{
    (void)timer;
    if (lv.open && !lv.message && document_ready() && lv.lyrics->synced) draw_synced();
}

/* Plain lyrics: every line at once, in a column the finger scrolls. */
static void fill_plain(void)
{
    if (lv.mode_id == lv.lyrics->id) return;
    lv.mode_id = lv.lyrics->id;
    size_t size = 1;
    for (unsigned i = 0; i < lv.lyrics->count; ++i) size += strlen(line_text((int)i)) + 1;
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
    lv_obj_scroll_to_y(lv.panel, 0, LV_ANIM_OFF);
}

static void refresh(void)
{
    if (!lv.panel) return;
    bool ready = document_ready();
    bool plain = ready && !lv.lyrics->synced;
    lv.message = !ready;
    show(lv.plain, plain);
    show(lv.current, !plain);
    show(lv.next, !plain && ready);
    if (plain) {
        lv_obj_add_flag(lv.panel, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
        fill_plain();
    } else {
        lv_obj_clear_flag(lv.panel, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
        lv_obj_scroll_to_y(lv.panel, 0, LV_ANIM_OFF);
        lv.mode_id = 0;
    }
    char heading[352];
    if (lv.artist[0]) snprintf(heading, sizeof(heading), "%s  \xC2\xB7  %s", lv.title, lv.artist);
    else snprintf(heading, sizeof(heading), "%s", lv.title);
    if (plain) snprintf(heading, sizeof(heading), "%s", BT(BT_LYRICS_NOT_SYNCED));
    set_text(lv.heading, heading);
    if (!ready) {
        lv.shown = -2;
        const char *message = lv.sync.status == BUDDY_LYRICS_NONE ? BT(BT_NO_LYRICS_FOUND) :
            lv.sync.status == BUDDY_LYRICS_INSTRUMENTAL ? BT(BT_LYRICS_INSTRUMENTAL) :
            lv.sync.status == BUDDY_LYRICS_UNSUPPORTED ? BT(BT_LYRICS_UNSUPPORTED) :
            lv.sync.status == BUDDY_LYRICS_UNAVAILABLE ? BT(BT_LYRICS_UNAVAILABLE) : BT(BT_LOOKING_FOR_LYRICS);
        lv_anim_del(lv.current, fade);
        lv_obj_set_style_opa(lv.current, LV_OPA_COVER, 0);
        lv_obj_set_style_text_color(lv.current, lv_color_hex(BUDDY_COLOR_MUTED), 0);
        set_text(lv.current, message);
        set_text(lv.next, "");
    } else if (!plain) draw_synced();
    show(lv.credit, lv.open && !lv.credit_hidden);
}

void buddy_lyrics_view_create(lv_obj_t *page)
{
    lv.shown = -2;
    lv.panel = lv_obj_create(page);
    lv_obj_remove_style_all(lv.panel);
    lv_obj_clear_flag(lv.panel, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
    /* At either end of the lyrics, a swipe carries on into the page on the smaller screens. */
    lv_obj_add_flag(lv.panel, LV_OBJ_FLAG_SCROLL_CHAIN_VER | LV_OBJ_FLAG_HIDDEN);
    lv_obj_set_scrollbar_mode(lv.panel, LV_SCROLLBAR_MODE_ACTIVE);
    lv_obj_set_scroll_dir(lv.panel, LV_DIR_VER);
    lv.heading = label(lv.panel, &buddy_language_font_16, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(lv.heading, LV_LABEL_LONG_DOT);
    lv.current = label(lv.panel, &buddy_language_font_32, BUDDY_COLOR_TEXT);
    lv_label_set_long_mode(lv.current, LV_LABEL_LONG_DOT);
    lv.next = label(lv.panel, &buddy_language_font_20, BUDDY_COLOR_MUTED);
    lv_label_set_long_mode(lv.next, LV_LABEL_LONG_DOT);
    lv.plain = label(lv.panel, &buddy_language_font_20, BUDDY_COLOR_TEXT);
    lv_label_set_long_mode(lv.plain, LV_LABEL_LONG_WRAP);
    lv_obj_add_flag(lv.plain, LV_OBJ_FLAG_HIDDEN);
    lv.credit = label(page, &buddy_language_font_14, BUDDY_COLOR_MUTED);
    lv_label_set_text(lv.credit, BT(BT_LYRICS_CREDIT));
    lv_label_set_long_mode(lv.credit, LV_LABEL_LONG_DOT);
    lv_obj_add_flag(lv.credit, LV_OBJ_FLAG_HIDDEN);
}

/* Heading, current line and next line, top to bottom; the current line may take two lines of
 * type on the larger screens. */
void buddy_lyrics_view_place(int x, int y, int width, int height, int credit_x, int credit_y, int credit_width)
{
    if (!lv.panel) return;
    const lv_font_t *heading = WIDE ? &buddy_language_font_16 : &buddy_language_font_14;
    const lv_font_t *current = WIDE ? &buddy_language_font_32 : TINY ? &buddy_language_font_20 : &buddy_language_font_24;
    const lv_font_t *next = WIDE ? &buddy_language_font_20 : TINY ? &buddy_language_font_14 : &buddy_language_font_16;
    const lv_font_t *plain = WIDE ? &buddy_language_font_20 : TINY ? &buddy_language_font_14 : &buddy_language_font_16;
    lv_obj_set_pos(lv.panel, x, y);
    lv_obj_set_size(lv.panel, width, height);
    lv_obj_set_style_text_font(lv.heading, heading, 0);
    lv_obj_set_style_text_font(lv.current, current, 0);
    lv_obj_set_style_text_font(lv.next, next, 0);
    lv_obj_set_style_text_font(lv.plain, plain, 0);
    lv_obj_set_style_text_line_space(lv.current, 4, 0);
    lv_obj_set_style_text_line_space(lv.next, 3, 0);
    lv_obj_set_style_text_line_space(lv.plain, 6, 0);
    int heading_h = TINY ? 0 : lv_font_get_line_height(heading);
    int current_lines = TINY ? 1 : 2, next_lines = TINY ? 1 : 2;
    int current_h = current_lines * lv_font_get_line_height(current) + (current_lines - 1) * 4;
    int next_h = next_lines * lv_font_get_line_height(next) + (next_lines - 1) * 3;
    int gap = TINY ? 4 : WIDE ? 14 : 10;
    show(lv.heading, !TINY);
    lv_obj_set_pos(lv.heading, 0, 0);
    lv_obj_set_size(lv.heading, width, heading_h);
    lv_obj_set_pos(lv.current, 0, heading_h + (TINY ? 0 : gap));
    lv_obj_set_size(lv.current, width, current_h);
    lv_obj_set_pos(lv.next, 0, heading_h + (TINY ? 0 : gap) + current_h + gap);
    lv_obj_set_size(lv.next, width, next_h);
    lv_obj_set_pos(lv.plain, 0, heading_h + (TINY ? 0 : gap));
    lv_obj_set_width(lv.plain, width - 8);
    lv_obj_set_pos(lv.credit, credit_x, credit_y);
    lv_obj_set_size(lv.credit, credit_width, lv_font_get_line_height(&buddy_language_font_14));
    lv_obj_set_style_text_align(lv.credit, WIDE ? LV_TEXT_ALIGN_LEFT : LV_TEXT_ALIGN_CENTER, 0);
    /* Heavy looks set the big line in their heavy type, as they do every display size. */
    buddy_theme_type(lv.panel);
}

void buddy_lyrics_view_set_document(buddy_lyrics_t *lyrics)
{
    free(lv.lyrics);
    lv.lyrics = lyrics;
    lv.shown = -2;
    lv.mode_id = 0;
    refresh();
}

void buddy_lyrics_view_update(const buddy_lyrics_sync_t *sync, const char *title, const char *artist)
{
    bool changed = sync->id != lv.sync.id || sync->status != lv.sync.status || sync->anchor != lv.sync.anchor ||
                   strcmp(title, lv.title) != 0 || strcmp(artist, lv.artist) != 0;
    /* A new reference point re-times the line: a seek or skip shows its new line at once. */
    if (sync->anchor != lv.sync.anchor) lv.shown = -2;
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

void buddy_lyrics_view_set_credit_hidden(bool hidden)
{
    if (!lv.credit || hidden == lv.credit_hidden) return;
    lv.credit_hidden = hidden;
    show(lv.credit, lv.open && !hidden);
}
