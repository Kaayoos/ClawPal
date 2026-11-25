#include "buddy_alert_scene.h"
#include "buddy_printer_page.h"
#include "buddy_theme.h"
#include <string.h>

/* The crab's beat, like the Printer and Monitor pictures. */
#define STEP_MS 140

typedef struct {
    buddy_alert_scene_t kind;
    uint32_t ink, step;
    int scale;
    buddy_printer_t printer;
    lv_timer_t *timer;
} scene_t;

typedef struct {
    lv_draw_ctx_t *ctx;
    int x, y, s;
} canvas_t;

static void cells(const canvas_t *c, int x, int y, int w, int h, lv_color_t ink)
{
    if (w <= 0 || h <= 0) return;
    lv_draw_rect_dsc_t rect;
    lv_draw_rect_dsc_init(&rect);
    rect.bg_color = ink;
    rect.bg_opa = LV_OPA_COVER;
    lv_area_t area = { (lv_coord_t)(c->x + x * c->s), (lv_coord_t)(c->y + y * c->s),
                       (lv_coord_t)(c->x + (x + w) * c->s - 1), (lv_coord_t)(c->y + (y + h) * c->s - 1) };
    lv_draw_rect(c->ctx, &rect, &area);
}

static lv_color_t hex(uint32_t ink) { return lv_color_hex(ink); }
static lv_color_t blend(uint32_t a, uint32_t b, uint8_t amount) { return lv_color_mix(lv_color_hex(a), lv_color_hex(b), amount); }

static int bounce(uint32_t step, int span)
{
    if (span <= 1) return 0;
    int period = 2 * (span - 1), at = (int)(step % (uint32_t)period);
    return at < span ? at : period - at;
}

typedef struct {
    lv_color_t body, lit, shade, inside, floor, ink;
} palette_t;

static palette_t palette(void)
{
    lv_color_t inside = blend(BUDDY_COLOR_SURFACE, BUDDY_COLOR_BG, 110);
    return (palette_t){
        .body = blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_RULE, 120),
        .lit = blend(BUDDY_COLOR_TEXT, BUDDY_COLOR_MUTED, 110),
        .shade = hex(BUDDY_COLOR_RULE),
        .inside = inside,
        .floor = blend(BUDDY_COLOR_RULE, BUDDY_COLOR_BG, 110),
        .ink = lv_color_mix(hex(BUDDY_COLOR_MUTED), inside, 110),
    };
}

/* A box lit from the top left with open corners, as every object in these pictures is built. */
static void block(const canvas_t *c, const palette_t *p, int x, int y, int w, int h)
{
    cells(c, x + 1, y, w - 2, 1, p->lit);
    cells(c, x, y + 1, w, h - 1, p->body);
    cells(c, x, y + 1, 1, h - 2, p->lit);
    cells(c, x + w - 1, y + 1, 1, h - 2, p->shade);
    cells(c, x + 1, y + h - 1, w - 2, 1, p->shade);
}

/* An agent needs you: its question waits on the monitor, the answer blinking, a bubble bobbing. */
static void draw_ask(const canvas_t *c, uint32_t step, uint32_t agent_ink)
{
    const palette_t p = palette();
    lv_color_t agent = hex(agent_ink);
    cells(c, 8, 46, 32, 1, p.floor);
    cells(c, 21, 37, 6, 5, p.shade);
    block(c, &p, 14, 42, 20, 3);
    block(c, &p, 3, 9, 42, 28);
    cells(c, 6, 12, 36, 22, p.inside);
    /* What it was doing, as lines of text. */
    cells(c, 8, 14, 20, 1, p.ink);
    cells(c, 8, 16, 27, 1, p.ink);
    cells(c, 8, 18, 14, 1, p.ink);
    /* The question in the agent's colour, with a quiet choice and the highlighted one. */
    cells(c, 9, 21, 30, 11, lv_color_mix(agent, hex(BUDDY_COLOR_BG), 40));
    cells(c, 9, 21, 30, 1, agent);
    cells(c, 9, 31, 30, 1, agent);
    cells(c, 9, 21, 1, 11, agent);
    cells(c, 38, 21, 1, 11, agent);
    cells(c, 12, 23, 18, 1, p.lit);
    cells(c, 12, 25, 11, 1, p.ink);
    cells(c, 12, 28, 10, 2, p.shade);
    cells(c, 25, 28, 11, 2, (step / 4U) % 2U == 0U ? agent : lv_color_mix(agent, hex(BUDDY_COLOR_BG), 120));
    /* A speech bubble with an exclamation mark, bobbing a cell over the monitor's corner. */
    int bx = 31, by = 2 - bounce(step / 3U, 2);
    lv_color_t bubble = hex(BUDDY_COLOR_TEXT);
    cells(c, bx + 1, by, 12, 1, bubble);
    cells(c, bx, by + 1, 14, 7, bubble);
    cells(c, bx + 1, by + 8, 12, 1, bubble);
    cells(c, bx + 2, by + 9, 3, 1, bubble);
    cells(c, bx + 2, by + 10, 1, 1, bubble);
    cells(c, bx + 6, by + 2, 2, 4, agent);
    cells(c, bx + 6, by + 7, 2, 1, agent);
}

/* A firmware approval: the new chip hovers over its socket, settling in and lifting again. */
static void draw_firmware(const canvas_t *c, uint32_t step)
{
    const palette_t p = palette();
    lv_color_t accent = hex(BUDDY_COLOR_ACCENT), faded = blend(BUDDY_COLOR_ACCENT, BUDDY_COLOR_BG, 120);
    cells(c, 6, 46, 36, 1, p.floor);
    block(c, &p, 5, 35, 38, 10);
    for (int x = 8; x < 41; x += 3) cells(c, x, 41, 2, 1, p.shade);
    cells(c, 16, 33, 16, 3, p.shade);
    cells(c, 17, 34, 14, 1, p.inside);
    int y = 13 + bounce(step / 2U, 6);
    cells(c, 14, y, 20, 14, p.shade);
    cells(c, 15, y + 1, 18, 12, blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_RULE, 90));
    cells(c, 15, y + 1, 18, 1, p.lit);
    for (int i = 0; i < 5; ++i) cells(c, 16 + i * 4, y + 14, 2, 2, p.lit);
    cells(c, 20, y + 5, 8, 4, accent);
    /* An arrow points it home, blinking. */
    lv_color_t arrow = (step / 4U) % 2U == 0U ? accent : faded;
    int ay = 2 + (int)((step / 2U) % 4U);
    cells(c, 23, ay, 2, 5, arrow);
    cells(c, 20, ay + 5, 8, 1, arrow);
    cells(c, 21, ay + 6, 6, 1, arrow);
    cells(c, 22, ay + 7, 4, 1, arrow);
}

/* A Wi-Fi change: a router sends its waves out one ring after another. */
static const char *const WAVES[] = {
    "......333333333333......",
    "....3333........3333....",
    "...33..............33...",
    "..33....22222222....33..",
    "......2222....2222......",
    ".....22..........22.....",
    "..........1111..........",
    "........11111111........",
    "........11....11........",
    "........................",
    "..........0000..........",
    "..........0000..........",
};

static void draw_wifi(const canvas_t *c, uint32_t step)
{
    const palette_t p = palette();
    lv_color_t accent = hex(BUDDY_COLOR_ACCENT), sent = blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_BG, 150);
    lv_color_t waiting = blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_BG, 80);
    cells(c, 6, 46, 36, 1, p.floor);
    cells(c, 12, 25, 2, 8, p.lit);
    cells(c, 34, 25, 2, 8, p.lit);
    block(c, &p, 8, 33, 32, 10);
    cells(c, 10, 43, 4, 2, p.shade);
    cells(c, 34, 43, 4, 2, p.shade);
    for (int i = 0; i < 4; ++i) cells(c, 12 + i * 4, 38, 2, 1, (step / 2U + (uint32_t)i) % 4U ? accent : p.shade);
    /* Ring 1 goes out first; rings already sent stay muted, those still to come stay dim. */
    int wave = (int)((step / 3U) % 4U);
    for (unsigned r = 0; r < sizeof(WAVES) / sizeof(WAVES[0]); ++r) {
        const char *line = WAVES[r];
        for (int i = 0; line[i]; ++i) {
            if (line[i] == '.') continue;
            int ring = line[i] - '0', run = i;
            while (line[run + 1] == line[i]) ++run;
            lv_color_t ink = ring == 0 ? accent : ring == wave ? accent : ring < wave ? sent : waiting;
            cells(c, 12 + i, 8 + (int)r, run - i + 1, 1, ink);
            i = run;
        }
    }
}

static void draw(lv_event_t *event)
{
    lv_obj_t *obj = lv_event_get_target(event);
    scene_t *s = lv_obj_get_user_data(obj);
    if (!s) return;
    lv_area_t a;
    lv_obj_get_coords(obj, &a);
    const canvas_t c = { lv_event_get_draw_ctx(event), a.x1, a.y1, s->scale };
    const uint32_t step = lv_tick_get() / STEP_MS;
    switch (s->kind) {
    case BUDDY_ALERT_SCENE_PRINTER: buddy_printer_draw(c.ctx, c.x, c.y, c.s, &s->printer); break;
    case BUDDY_ALERT_SCENE_FIRMWARE: draw_firmware(&c, step); break;
    case BUDDY_ALERT_SCENE_WIFI: draw_wifi(&c, step); break;
    default: draw_ask(&c, step, s->ink); break;
    }
}

static void animate(lv_timer_t *timer)
{
    lv_obj_t *obj = timer->user_data;
    scene_t *s = lv_obj_get_user_data(obj);
    uint32_t step = lv_tick_get() / STEP_MS;
    /* A stopped print or an idle printer stays still; everything else moves a cell per step. */
    bool still = s->kind == BUDDY_ALERT_SCENE_PRINTER && s->printer.state != BUDDY_PRINT_PAUSED && s->printer.state != BUDDY_PRINT_FINISHED;
    if (step != s->step && !still && lv_obj_is_visible(obj)) lv_obj_invalidate(obj);
    s->step = step;
}

static void deleted(lv_event_t *event)
{
    scene_t *s = lv_obj_get_user_data(lv_event_get_target(event));
    if (!s) return;
    lv_timer_del(s->timer);
    lv_mem_free(s);
}

lv_obj_t *buddy_alert_scene_create(lv_obj_t *parent, int scale)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_clear_flag(obj, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_size(obj, BUDDY_ALERT_SCENE_CELLS * scale, BUDDY_ALERT_SCENE_CELLS * scale);
    scene_t *s = lv_mem_alloc(sizeof(scene_t));
    LV_ASSERT_MALLOC(s);
    memset(s, 0, sizeof(*s));
    s->scale = scale;
    s->ink = BUDDY_COLOR_ACCENT;
    s->timer = lv_timer_create(animate, STEP_MS / 2, obj);
    lv_obj_set_user_data(obj, s);
    lv_obj_add_event_cb(obj, draw, LV_EVENT_DRAW_MAIN, NULL);
    lv_obj_add_event_cb(obj, deleted, LV_EVENT_DELETE, NULL);
    return obj;
}

void buddy_alert_scene_set_scale(lv_obj_t *scene, int scale)
{
    scene_t *s = scene ? lv_obj_get_user_data(scene) : NULL;
    if (!s || scale < 1 || scale == s->scale) return;
    s->scale = scale;
    lv_obj_set_size(scene, BUDDY_ALERT_SCENE_CELLS * scale, BUDDY_ALERT_SCENE_CELLS * scale);
}

void buddy_alert_scene_set(lv_obj_t *scene, buddy_alert_scene_t kind, uint32_t ink, const buddy_printer_t *printer)
{
    scene_t *s = scene ? lv_obj_get_user_data(scene) : NULL;
    if (!s) return;
    s->kind = kind;
    s->ink = ink;
    if (printer) s->printer = *printer;
    else memset(&s->printer, 0, sizeof(s->printer));
    lv_obj_invalidate(scene);
}
