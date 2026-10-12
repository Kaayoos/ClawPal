#include "buddy_theme.h"
#include "buddy_app_icons.h"
#include "buddy_home_logos.h"

static void draw_icon(lv_event_t *event)
{
    lv_obj_t *obj=lv_event_get_target(event);
    lv_draw_ctx_t *ctx=lv_event_get_draw_ctx(event);
    buddy_app_t app=(buddy_app_t)(uintptr_t)lv_event_get_user_data(event);
    lv_area_t a;lv_obj_get_coords(obj,&a);
    int size=lv_obj_get_width(obj);
    lv_draw_line_dsc_t line;lv_draw_line_dsc_init(&line);
    line.color=lv_color_hex(app==BUDDY_APP_WEATHER?BUDDY_COLOR_WEATHER_APP:BUDDY_COLOR_ACCENT);line.width=size>=48?3:2;line.round_start=line.round_end=1;
    lv_draw_rect_dsc_t rect;lv_draw_rect_dsc_init(&rect);
    rect.bg_opa=LV_OPA_TRANSP;rect.border_color=line.color;rect.border_width=line.width;
#define POINT(x,y) (lv_point_t){a.x1+(x)*size/100,a.y1+(y)*size/100}
#define LINE(x1,y1,x2,y2) do {lv_point_t p=POINT(x1,y1),q=POINT(x2,y2);lv_draw_line(ctx,&line,&p,&q);}while(0)
#define BOX(x,y,w,h,r) do {lv_area_t b={a.x1+(x)*size/100,a.y1+(y)*size/100,a.x1+((x)+(w))*size/100,a.y1+((y)+(h))*size/100};rect.radius=r;lv_draw_rect(ctx,&rect,&b);}while(0)
    if(app==BUDDY_APP_MONITOR){BOX(12,17,76,51,3);LINE(50,70,50,84);LINE(31,86,69,86);}
    else if(app==BUDDY_APP_HOME){LINE(12,45,50,13);LINE(50,13,88,45);LINE(23,38,23,85);LINE(77,38,77,85);LINE(23,85,77,85);LINE(43,85,43,60);LINE(43,60,59,60);LINE(59,60,59,85);}
    else if(app==BUDDY_APP_WEATHER){BOX(13,12,38,38,LV_RADIUS_CIRCLE);rect.bg_opa=LV_OPA_COVER;rect.bg_color=lv_color_hex(BUDDY_COLOR_BG);BOX(20,47,64,34,16);BOX(34,33,36,42,18);rect.border_width=0;BOX(27,57,47,20,0);}
    else {BOX(17,19,66,66,LV_RADIUS_CIRCLE);LINE(50,52,50,32);LINE(50,52,67,60);if(app==BUDDY_APP_TIMER){LINE(40,8,60,8);LINE(50,8,50,16);LINE(79,17,87,25);}}
#undef BOX
#undef LINE
#undef POINT
}

lv_obj_t *buddy_app_icon_create(lv_obj_t *parent,buddy_app_t app,int x,int y,int size)
{
    lv_obj_t *icon=lv_obj_create(parent);lv_obj_remove_style_all(icon);
    lv_obj_set_pos(icon,x,y);lv_obj_set_size(icon,size,size);
    lv_obj_clear_flag(icon,LV_OBJ_FLAG_CLICKABLE|LV_OBJ_FLAG_SCROLLABLE);
    if(app==BUDDY_APP_MUSIC || app==BUDDY_APP_CODEX || app==BUDDY_APP_CLAUDE){
        lv_obj_t *image=lv_img_create(icon);lv_img_set_src(image,app==BUDDY_APP_MUSIC?&buddy_logo_spotify:app==BUDDY_APP_CLAUDE?&buddy_logo_claude:&buddy_logo_codex);
        lv_img_set_pivot(image,0,0);lv_img_set_zoom(image,(uint16_t)(256*size/56));
        lv_obj_clear_flag(image,LV_OBJ_FLAG_CLICKABLE);
    }else lv_obj_add_event_cb(icon,draw_icon,LV_EVENT_DRAW_MAIN,(void *)(uintptr_t)app);
    return icon;
}
