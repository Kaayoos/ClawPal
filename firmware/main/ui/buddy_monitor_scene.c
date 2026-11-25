#include "buddy_monitor_scene.h"
#include "buddy_theme.h"
#include <math.h>

#define CELLS BUDDY_MONITOR_SCENE_CELLS
/* Moves a cell at a time on the crab's beat, like the Printer app. */
#define STEP_MS 140

static struct {
    lv_obj_t *art;
    int scale;
    uint32_t step;
    bool live, have;
    float cpu, cpu_t, gpu, gpu_t, memory, vram;
    double network;
} sc;

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

static bool known(float value) { return isfinite(value) && value >= 0; }

/* How far a part has warmed: nothing at 45 C and below, all the way at 85 C and above. */
static uint8_t heat(float celsius)
{
    if (!isfinite(celsius) || celsius <= 45) return 0;
    if (celsius >= 85) return 255;
    return (uint8_t)((celsius - 45) * 255 / 40);
}

/* Steps per blade change: a busy fan turns every step, an idle one every fourth. */
static uint32_t turn(float load)
{
    return !known(load) ? 0 : load >= 60 ? 1 : load >= 25 ? 2 : 4;
}

/* A square fan with open corners; its blades alternate between a plus and a cross to spin. */
static void fan(const canvas_t *c, int x, int y, int size, uint32_t frame, lv_color_t ring, lv_color_t blade, lv_color_t hub)
{
    int r = size / 2;
    cells(c, x + 1, y, size - 2, 1, ring);
    cells(c, x + 1, y + size - 1, size - 2, 1, ring);
    cells(c, x, y + 1, 1, size - 2, ring);
    cells(c, x + size - 1, y + 1, 1, size - 2, ring);
    for (int i = 1; i < r; ++i) {
        if (frame % 2U == 0) {
            cells(c, x + r, y + r - i, 1, 1, blade);
            cells(c, x + r, y + r + i, 1, 1, blade);
            cells(c, x + r - i, y + r, 1, 1, blade);
            cells(c, x + r + i, y + r, 1, 1, blade);
        } else {
            cells(c, x + r - i, y + r - i, 1, 1, blade);
            cells(c, x + r + i, y + r + i, 1, 1, blade);
            cells(c, x + r + i, y + r - i, 1, 1, blade);
            cells(c, x + r - i, y + r + i, 1, 1, blade);
        }
    }
    cells(c, x + r, y + r, 1, 1, hub);
}

static void draw(lv_event_t *event)
{
    lv_area_t a;
    lv_obj_get_coords(lv_event_get_target(event), &a);
    const canvas_t c = { lv_event_get_draw_ctx(event), a.x1, a.y1, sc.scale };
    const bool live = sc.live && sc.have;
    const uint32_t step = lv_tick_get() / STEP_MS;

    /* Offline, the case is drawn in the rule's grey with every light out. */
    lv_color_t body = live ? blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_RULE, 120) : hex(BUDDY_COLOR_RULE);
    lv_color_t lit = live ? blend(BUDDY_COLOR_TEXT, BUDDY_COLOR_MUTED, 110) : blend(BUDDY_COLOR_MUTED, BUDDY_COLOR_RULE, 90);
    lv_color_t shade = live ? hex(BUDDY_COLOR_RULE) : blend(BUDDY_COLOR_RULE, BUDDY_COLOR_BG, 140);
    lv_color_t inside = blend(BUDDY_COLOR_SURFACE, BUDDY_COLOR_BG, 110);
    lv_color_t board = blend(BUDDY_COLOR_RULE, BUDDY_COLOR_SURFACE, 110);
    lv_color_t accent = live ? hex(BUDDY_COLOR_ACCENT) : shade;
    lv_color_t dim = live ? blend(BUDDY_COLOR_ACCENT, BUDDY_COLOR_SURFACE, 70) : shade;

    /* Its shadow and feet. */
    cells(&c, 3, 35, 30, 1, blend(BUDDY_COLOR_RULE, BUDDY_COLOR_BG, 110));
    cells(&c, 5, 33, 4, 2, shade);
    cells(&c, 27, 33, 4, 2, shade);
    /* The case, lit from the top left; its corners stay open so it reads rounded. */
    cells(&c, 4, 1, 28, 1, lit);
    cells(&c, 3, 2, 30, 31, body);
    cells(&c, 3, 2, 1, 31, lit);
    cells(&c, 32, 2, 1, 31, shade);
    cells(&c, 4, 32, 28, 1, shade);
    /* Through the open side: the motherboard on the far wall. */
    cells(&c, 5, 3, 21, 28, inside);
    cells(&c, 6, 4, 19, 17, board);

    /* The CPU cooler: a fan on its block, turning with load and warming with temperature. */
    uint32_t cpu_turn = live ? turn(sc.cpu) : 0, gpu_turn = live ? turn(sc.gpu) : 0;
    cells(&c, 8, 6, 9, 9, shade);
    fan(&c, 9, 7, 7, cpu_turn ? step / cpu_turn : 0,
        live ? lv_color_mix(hex(BUDDY_COLOR_WARNING), body, heat(sc.cpu_t)) : shade, lit, inside);

    /* Four RAM sticks light from the first one up as memory fills. */
    enum { STICKS = 4, STICK = 9 };
    int ram = live && known(sc.memory) ? (int)lroundf(fminf(sc.memory, 100) * STICKS * STICK / 100) : 0;
    for (int i = 0; i < STICKS; ++i) {
        int x = 19 + i * 2, n = ram - i * STICK;
        n = n < 0 ? 0 : n > STICK ? STICK : n;
        cells(&c, x, 5, 1, STICK - n, shade);
        cells(&c, x, 5 + STICK - n, 1, n, accent);
    }

    /* The graphics card: two fans out of step with each other, and its memory along the spine. */
    lv_color_t gpu_ring = live ? lv_color_mix(hex(BUDDY_COLOR_WARNING), shade, heat(sc.gpu_t)) : shade;
    cells(&c, 6, 17, 20, 7, body);
    cells(&c, 6, 17, 20, 1, lit);
    cells(&c, 6, 23, 20, 1, shade);
    uint32_t gpu_frame = gpu_turn ? step / gpu_turn : 0;
    fan(&c, 8, 18, 5, gpu_frame, gpu_ring, lit, inside);
    fan(&c, 17, 18, 5, gpu_frame + (gpu_turn ? 1U : 0U), gpu_ring, lit, inside);
    int vram = live && known(sc.vram) ? (int)lroundf(fminf(sc.vram, 100) * 16 / 100) : 0;
    cells(&c, 7, 24, 16, 1, shade);
    cells(&c, 7, 24, vram, 1, dim);

    /* The power supply's cover along the floor. */
    cells(&c, 5, 26, 21, 5, body);
    cells(&c, 5, 26, 21, 1, lit);
    for (int x = 7; x < 24; x += 2) cells(&c, x, 28, 1, 2, shade);

    /* The front panel: vents, the power light, and an activity light that blinks with traffic. */
    cells(&c, 26, 3, 1, 28, shade);
    for (int y = 10; y < 30; y += 2) cells(&c, 28, y, 3, 1, shade);
    cells(&c, 28, 4, 3, 1, accent);
    uint32_t period = !live || !isfinite(sc.network) ? 0 : sc.network >= 1e6 ? 2 : sc.network >= 1e4 ? 4 : 0;
    cells(&c, 28, 6, 3, 1, period && step % period < period / 2 ? accent : shade);
}

static void animate(lv_timer_t *timer)
{
    (void)timer;
    uint32_t step = lv_tick_get() / STEP_MS;
    /* Only a live PC moves, only while it is on screen and only when the next cell is due. */
    if (step != sc.step && sc.art && sc.live && sc.have && lv_obj_is_visible(sc.art)) lv_obj_invalidate(sc.art);
    sc.step = step;
}

lv_obj_t *buddy_monitor_scene_create(lv_obj_t *parent)
{
    sc.art = lv_obj_create(parent);
    lv_obj_remove_style_all(sc.art);
    lv_obj_clear_flag(sc.art, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_event_cb(sc.art, draw, LV_EVENT_DRAW_MAIN, NULL);
    sc.scale = 4;
    lv_obj_set_size(sc.art, CELLS * sc.scale, CELLS * sc.scale);
    lv_timer_create(animate, STEP_MS / 2, NULL);
    return sc.art;
}

void buddy_monitor_scene_place(int x, int y, int scale)
{
    if (!sc.art) return;
    sc.scale = scale < 1 ? 1 : scale;
    lv_obj_set_pos(sc.art, x, y);
    lv_obj_set_size(sc.art, CELLS * sc.scale, CELLS * sc.scale);
}

void buddy_monitor_scene_update(const buddy_stats_t *stats, bool live)
{
    if (stats) {
        sc.have = true;
        sc.cpu = stats->cpu_usage; sc.cpu_t = stats->cpu_temperature;
        sc.gpu = stats->gpu_usage; sc.gpu_t = stats->gpu_temperature;
        sc.memory = stats->memory_usage;
        sc.vram = known(stats->vram_used_mb) && isfinite(stats->vram_total_mb) && stats->vram_total_mb > 0 ?
            stats->vram_used_mb / stats->vram_total_mb * 100 : NAN;
        double rx = stats->network_rx_bps, tx = stats->network_tx_bps;
        sc.network = (isfinite(rx) && rx > 0 ? rx : 0) + (isfinite(tx) && tx > 0 ? tx : 0);
    }
    sc.live = live;
    if (sc.art) lv_obj_invalidate(sc.art);
}
