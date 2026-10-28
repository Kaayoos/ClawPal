#include "buddy_weather_icon.h"
#include "buddy_theme.h"

static void weather_disc(lv_draw_ctx_t *ctx, int x, int y, int radius, uint32_t ink)
{
    lv_draw_rect_dsc_t style;
    lv_draw_rect_dsc_init(&style);
    style.bg_color = lv_color_hex(ink);
    style.bg_opa = LV_OPA_COVER;
    style.radius = LV_RADIUS_CIRCLE;
    lv_area_t area = {x - radius, y - radius, x + radius, y + radius};
    lv_draw_rect(ctx, &style, &area);
}

static void weather_stroke(lv_draw_ctx_t *ctx, int x, int y, int scale, int x1, int y1, int x2, int y2)
{
    lv_draw_line_dsc_t style;
    lv_draw_line_dsc_init(&style);
    style.color = lv_color_hex(BUDDY_COLOR_WEATHER_MARK);
    style.width = 2 * scale;
    style.round_start = style.round_end = 1;
    lv_point_t start = {x + x1 * scale, y + y1 * scale}, end = {x + x2 * scale, y + y2 * scale};
    lv_draw_line(ctx, &style, &start, &end);
}

/* Small native vector marks drawn on a 26 x 26 grid times scale; no bitmap buffers. */
void buddy_weather_icon_draw(lv_draw_ctx_t *ctx, int x, int y, int scale, int code, bool is_day, uint32_t background)
{
    if (code <= 1) {
        if (!is_day) {
            weather_disc(ctx, x + 13 * scale, y + 12 * scale, 9 * scale, BUDDY_COLOR_WEATHER_MARK);
            weather_disc(ctx, x + 17 * scale, y + 8 * scale, 8 * scale, background);
        } else {
            weather_disc(ctx, x + 13 * scale, y + 12 * scale, 5 * scale, BUDDY_COLOR_WEATHER_MARK);
            static const int8_t rays[][4] = {
                {13,1,13,3}, {13,21,13,23}, {2,12,4,12}, {22,12,24,12},
                {5,4,7,6}, {19,18,21,20}, {5,20,7,18}, {19,6,21,4},
            };
            for (unsigned i = 0; i < sizeof(rays) / sizeof(rays[0]); ++i)
                weather_stroke(ctx, x, y, scale, rays[i][0], rays[i][1], rays[i][2], rays[i][3]);
        }
        return;
    }
    if (code == 45 || code == 48) {
        weather_stroke(ctx, x, y, scale, 3, 7, 23, 7);
        weather_stroke(ctx, x, y, scale, 6, 13, 25, 13);
        weather_stroke(ctx, x, y, scale, 2, 19, 21, 19);
        return;
    }
    bool precipitation = code >= 51;
    int cloud_y = precipitation ? 10 : 14;
    weather_disc(ctx, x + 7 * scale, y + cloud_y * scale, 5 * scale, BUDDY_COLOR_WEATHER_MARK);
    weather_disc(ctx, x + 14 * scale, y + (cloud_y - 3) * scale, 7 * scale, BUDDY_COLOR_WEATHER_MARK);
    weather_disc(ctx, x + 21 * scale, y + cloud_y * scale, 5 * scale, BUDDY_COLOR_WEATHER_MARK);
    weather_stroke(ctx, x, y, scale, 7, cloud_y + 4, 21, cloud_y + 4);
    if (code >= 95) {
        weather_stroke(ctx, x, y, scale, 15, 16, 11, 21);
        weather_stroke(ctx, x, y, scale, 11, 21, 16, 21);
        weather_stroke(ctx, x, y, scale, 16, 21, 13, 25);
    } else if ((code >= 71 && code <= 77) || code == 85 || code == 86) {
        for (int offset = 7; offset <= 21; offset += 14) {
            weather_stroke(ctx, x, y, scale, offset - 2, 21, offset + 2, 21);
            weather_stroke(ctx, x, y, scale, offset, 19, offset, 23);
        }
    } else if (precipitation) {
        weather_stroke(ctx, x, y, scale, 8, 19, 6, 23);
        weather_stroke(ctx, x, y, scale, 15, 19, 13, 23);
        weather_stroke(ctx, x, y, scale, 22, 19, 20, 23);
    }
}
