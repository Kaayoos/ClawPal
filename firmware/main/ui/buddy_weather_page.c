#include "buddy_weather_units.h"
#include "buddy_theme.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "buddy_weather_page.h"
#include "buddy_weather_icon.h"
#include "board.h"
#include "buddy_weather_animation.h"
#include "buddy_clock_font.h"
#include "buddy_settings.h"
#include "buddy_ui_motion.h"
#include "esp_heap_caps.h"
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

#define SCALE (BOARD_DISPLAY_HEIGHT<400?2:4)
#define SIZE (48*SCALE)
#define BYTES (64+SIZE*SIZE/2)
#define INK BUDDY_COLOR_TEXT
#define MUTED BUDDY_COLOR_MUTED
#define ACCENT BUDDY_COLOR_ACCENT
#define RULE BUDDY_COLOR_RULE
#define SURFACE BUDDY_COLOR_SURFACE
#define CARDS 6
#define WIDE (BOARD_DISPLAY_WIDTH>=800)
#define TINY (BOARD_DISPLAY_HEIGHT<400)
enum { VIEW_NOW, VIEW_HOURS, VIEW_DAYS };
static struct {
    lv_obj_t *page,*city,*note,*tab_track,*tabs[3],*tab_labels[3];
    lv_obj_t *now,*temperature,*unit,*description,*range,*crab,*tiles[4],*tile_names[4],*values[4];
    lv_obj_t *forecast,*cards[CARDS],*times[CARDS],*icons[CARDS],*temperatures[CARDS],*probabilities[CARDS],*rain[CARDS];
    lv_obj_t *previous,*next,*page_range,*empty;
    unsigned view,offset[3];
    buddy_weather_t weather;
    buddy_mascot_state_t state;
    uint8_t *pixels;
    lv_img_dsc_t frame;
} weather_ui;

static void refresh(void);
static lv_obj_t *box(lv_obj_t *parent,int x,int y,int width,int height)
{
    lv_obj_t *obj=lv_obj_create(parent);lv_obj_remove_style_all(obj);
    lv_obj_set_pos(obj,x,y);lv_obj_set_size(obj,width,height);
    lv_obj_clear_flag(obj,LV_OBJ_FLAG_CLICKABLE|LV_OBJ_FLAG_SCROLLABLE);return obj;
}
static lv_obj_t *card(lv_obj_t *parent,int radius)
{
    lv_obj_t *obj=box(parent,0,0,10,10);
    lv_obj_set_style_bg_color(obj,lv_color_hex(SURFACE),0);lv_obj_set_style_bg_opa(obj,LV_OPA_COVER,0);
    lv_obj_set_style_radius(obj,radius,0);return obj;
}
static void visible(lv_obj_t *obj,bool show)
{
    if(show)lv_obj_clear_flag(obj,LV_OBJ_FLAG_HIDDEN);
    else {buddy_ui_motion_cancel(obj);lv_obj_add_flag(obj,LV_OBJ_FLAG_HIDDEN);}
}
static lv_obj_t *label(lv_obj_t *parent,const char *value,int x,int y,int width,const lv_font_t *font,uint32_t color)
{
    lv_obj_t *obj=lv_label_create(parent);lv_obj_set_pos(obj,x,y);lv_obj_set_width(obj,width);
    lv_label_set_long_mode(obj,LV_LABEL_LONG_DOT);lv_label_set_text(obj,value);
    lv_obj_set_style_text_font(obj,font,0);lv_obj_set_style_text_color(obj,lv_color_hex(color),0);
    lv_obj_clear_flag(obj,LV_OBJ_FLAG_CLICKABLE);return obj;
}
static void place(lv_obj_t *obj,int x,int y,int width,int height,const lv_font_t *font)
{
    lv_obj_set_pos(obj,x,y);lv_obj_set_size(obj,width,height);if(font)lv_obj_set_style_text_font(obj,font,0);
}
static void set_text(lv_obj_t *label,const char *value)
{
    if(strcmp(lv_label_get_text(label),value))lv_label_set_text(label,value);
}
static void enable(lv_obj_t *obj,bool enabled)
{
    if(enabled)lv_obj_clear_state(obj,LV_STATE_DISABLED);else lv_obj_add_state(obj,LV_STATE_DISABLED);
    lv_obj_set_style_opa(obj,enabled?LV_OPA_COVER:LV_OPA_40,0);
}

/* Each forecast card's icon keeps its condition in user data: code * 2 + is_day, or -1. */
static void draw_icon(lv_event_t *event)
{
    lv_obj_t *icon=lv_event_get_target(event);intptr_t value=(intptr_t)lv_obj_get_user_data(icon);
    if(value<0)return;
    lv_area_t area;lv_obj_get_coords(icon,&area);
    buddy_weather_icon_draw(lv_event_get_draw_ctx(event),area.x1,area.y1,lv_obj_get_width(icon)>=78?3:2,(int)(value/2),value%2,SURFACE);
}
static void set_icon(lv_obj_t *icon,int code,bool is_day)
{
    intptr_t value=code<0?-1:(intptr_t)code*2+(is_day?1:0);
    if((intptr_t)lv_obj_get_user_data(icon)!=value){lv_obj_set_user_data(icon,(void *)value);lv_obj_invalidate(icon);}
}

static unsigned columns(void){return WIDE?(weather_ui.view==VIEW_DAYS?5:CARDS):TINY?3:(weather_ui.view==VIEW_DAYS?5:4);}

/* Forecast cards: time, icon, temperature, rain chance and a bar for that chance. */
static void layout_cards(int width,int height)
{
    unsigned count=columns();int gap=WIDE?12:8,cw=(width-gap*((int)count-1))/(int)count;
    const lv_font_t *temp_font=WIDE?&buddy_language_font_24:TINY?&buddy_language_font_16:&buddy_language_font_20;
    int icon=WIDE?78:52;
    for(unsigned i=0;i<CARDS;i++){
        place(weather_ui.cards[i],(int)i*(cw+gap),0,cw,height,NULL);
        place(weather_ui.times[i],4,12,cw-8,22,cw<90?&buddy_language_font_14:&buddy_language_font_16);
        place(weather_ui.icons[i],(cw-icon)/2,height*18/100,icon,icon,NULL);
        place(weather_ui.temperatures[i],4,height*18/100+icon+8,cw-8,lv_font_get_line_height(temp_font),temp_font);
        place(weather_ui.probabilities[i],4,height-48,cw-8,22,TINY?&buddy_language_font_14:&buddy_language_font_16);
        place(weather_ui.rain[i],14,height-20,cw-28,6,NULL);
    }
}

static void layout(void)
{
    int w=BOARD_DISPLAY_WIDTH;bool now=weather_ui.view==VIEW_NOW;
    visible(weather_ui.now,now);visible(weather_ui.forecast,!now);
    for(unsigned i=0;i<3;i++){
        lv_obj_set_style_bg_opa(weather_ui.tabs[i],i==weather_ui.view?LV_OPA_COVER:LV_OPA_TRANSP,0);
        lv_obj_set_style_text_color(weather_ui.tab_labels[i],lv_color_hex(i==weather_ui.view?ACCENT:MUTED),0);
    }
    if(WIDE){layout_cards(744,236);place(weather_ui.previous,0,252,48,44,NULL);place(weather_ui.next,696,252,48,44,NULL);place(weather_ui.page_range,60,264,624,22,NULL);return;}
    int cards=TINY?168:200,top=cards+12;
    layout_cards(w-24,cards);
    place(weather_ui.previous,0,top,48,44,NULL);place(weather_ui.next,w-72,top,48,44,NULL);place(weather_ui.page_range,56,top+12,w-136,22,NULL);
}

static void compact_weather(void)
{
    if(WIDE)return;
    int w=BOARD_DISPLAY_WIDTH;
    lv_obj_add_flag(weather_ui.page,LV_OBJ_FLAG_SCROLLABLE|LV_OBJ_FLAG_CLICKABLE);lv_obj_set_scroll_dir(weather_ui.page,LV_DIR_VER);lv_obj_set_style_bg_color(weather_ui.page,lv_color_hex(ACCENT),LV_PART_SCROLLBAR);lv_obj_set_style_width(weather_ui.page,3,LV_PART_SCROLLBAR);
    place(weather_ui.city,12,TINY?6:8,w-24,30,TINY?&buddy_language_font_20:&buddy_language_font_24);
    place(weather_ui.note,12,TINY?32:40,w-24,20,&buddy_language_font_14);
    int track=TINY?58:66,tab=(w-24-8)/3;place(weather_ui.tab_track,12,track,w-24,44,NULL);
    for(int i=0;i<3;i++){place(weather_ui.tabs[i],4+i*tab,4,tab,36,NULL);lv_obj_set_width(weather_ui.tab_labels[i],tab-8);lv_obj_align(weather_ui.tab_labels[i],LV_ALIGN_CENTER,0,0);}
    int top=track+56,text=w-24-SIZE-12;
    place(weather_ui.now,12,top,w-24,TINY?260:400,NULL);place(weather_ui.forecast,12,top,w-24,TINY?240:270,NULL);
    lv_obj_set_style_text_font(weather_ui.temperature,TINY?&buddy_language_font_48:&buddy_clock_font,0);lv_obj_set_pos(weather_ui.temperature,0,4);
    lv_obj_set_style_text_font(weather_ui.unit,TINY?&buddy_language_font_20:&buddy_language_font_32,0);
    place(weather_ui.description,0,TINY?62:90,text,28,TINY?&buddy_language_font_16:&buddy_language_font_20);
    place(weather_ui.range,0,TINY?86:122,text,24,TINY?&buddy_language_font_14:&buddy_language_font_16);
    lv_obj_set_pos(weather_ui.crab,w-24-SIZE,TINY?-4:-8);
    int tile_w=(w-24-10)/2,tile_h=TINY?72:84,tiles_top=TINY?120:196;
    for(int i=0;i<4;i++){place(weather_ui.tiles[i],(i%2)*(tile_w+10),tiles_top+(i/2)*(tile_h+10),tile_w,tile_h,NULL);
        place(weather_ui.tile_names[i],12,10,tile_w-24,20,&buddy_language_font_14);place(weather_ui.values[i],12,TINY?34:40,tile_w-24,30,TINY?&buddy_language_font_20:&buddy_language_font_24);}
    place(weather_ui.empty,0,60,w-24,100,&buddy_language_font_20);
}

static void tab_clicked(lv_event_t *event)
{
    unsigned view=(unsigned)(uintptr_t)lv_event_get_user_data(event);
    if(view==weather_ui.view)return;
    weather_ui.view=view;layout();refresh();
    buddy_ui_motion_enter(view==VIEW_NOW?weather_ui.now:weather_ui.forecast,6,180,0);
}
static void page_clicked(lv_event_t *event)
{
    unsigned *offset=&weather_ui.offset[weather_ui.view],step=columns();
    if((uintptr_t)lv_event_get_user_data(event))*offset+=step;
    else *offset=*offset>=step?*offset-step:0;
    refresh();buddy_ui_motion_enter(weather_ui.forecast,4,150,0);
}
static lv_obj_t *arrow(lv_obj_t *parent,const char *symbol,uintptr_t id)
{
    lv_obj_t *obj=card(parent,LV_RADIUS_CIRCLE);lv_obj_add_flag(obj,LV_OBJ_FLAG_CLICKABLE);lv_obj_set_ext_click_area(obj,6);
    lv_obj_set_style_bg_color(obj,lv_color_hex(BUDDY_COLOR_PRESSED),LV_STATE_PRESSED);
    lv_obj_add_event_cb(obj,page_clicked,LV_EVENT_CLICKED,(void *)id);
    lv_obj_t *icon=label(obj,symbol,0,0,24,&buddy_language_font_20,ACCENT);lv_obj_set_style_text_align(icon,LV_TEXT_ALIGN_CENTER,0);lv_obj_center(icon);
    return obj;
}

lv_obj_t *buddy_weather_page_create(lv_obj_t *parent)
{
    lv_obj_t *page=weather_ui.page=box(parent,0,0,BOARD_DISPLAY_WIDTH,BOARD_DISPLAY_HEIGHT-44);
    lv_obj_set_style_bg_color(page,lv_color_hex(BUDDY_COLOR_BG),0);lv_obj_set_style_bg_opa(page,LV_OPA_COVER,0);
    weather_ui.city=label(page,buddy_weather_city(),28,18,400,&buddy_language_font_24,INK);
    weather_ui.note=label(page,BT(BT_WAITING_FOR_WI_FI),28,52,400,&buddy_language_font_16,MUTED);
    /* Segmented Now / 24 hours / 5 days switch, matching the account pages. */
    weather_ui.tab_track=card(page,LV_RADIUS_CIRCLE);place(weather_ui.tab_track,448,20,324,46,NULL);
    const char *tabs[]={BT(BT_NOW),BT(BT_24_HOURS),BT(BT_5_DAYS)};
    for(unsigned i=0;i<3;i++){
        lv_obj_t *tab=weather_ui.tabs[i]=box(weather_ui.tab_track,4+(int)i*106,4,104,38);lv_obj_add_flag(tab,LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_radius(tab,LV_RADIUS_CIRCLE,0);lv_obj_set_style_bg_color(tab,lv_color_hex(RULE),0);
        lv_obj_set_style_bg_color(tab,lv_color_hex(BUDDY_COLOR_PRESSED),LV_STATE_PRESSED);lv_obj_set_style_bg_opa(tab,LV_OPA_COVER,LV_STATE_PRESSED);
        lv_obj_add_event_cb(tab,tab_clicked,LV_EVENT_CLICKED,(void *)(uintptr_t)i);
        weather_ui.tab_labels[i]=label(tab,tabs[i],0,0,96,&buddy_language_font_16,MUTED);
        lv_obj_set_style_text_align(weather_ui.tab_labels[i],LV_TEXT_ALIGN_CENTER,0);lv_obj_align(weather_ui.tab_labels[i],LV_ALIGN_CENTER,0,0);
    }
    lv_obj_t *rule=box(page,28,84,744,1);lv_obj_set_style_bg_color(rule,lv_color_hex(RULE),0);lv_obj_set_style_bg_opa(rule,LV_OPA_COVER,0);

    /* Now: a large reading, today's range and the animated crab, with four detail tiles. */
    weather_ui.now=box(page,28,96,744,340);
    weather_ui.temperature=label(weather_ui.now,"--",0,10,400,&buddy_clock_font,INK);lv_label_set_long_mode(weather_ui.temperature,LV_LABEL_LONG_CLIP);
    lv_obj_set_width(weather_ui.temperature,LV_SIZE_CONTENT);
    weather_ui.unit=label(weather_ui.now,"",0,14,80,&buddy_language_font_32,MUTED);
    weather_ui.description=label(weather_ui.now,BT(BT_FINDING_YOUR_FORECAST),0,98,500,&buddy_language_font_24,INK);
    weather_ui.range=label(weather_ui.now,"",0,134,500,&buddy_language_font_20,MUTED);
    weather_ui.crab=lv_img_create(weather_ui.now);lv_img_set_antialias(weather_ui.crab,false);lv_obj_clear_flag(weather_ui.crab,LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_pos(weather_ui.crab,744-SIZE-8,-6);
    weather_ui.pixels=heap_caps_malloc(BYTES,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    weather_ui.frame=(lv_img_dsc_t){.header={.cf=LV_IMG_CF_INDEXED_4BIT,.w=SIZE,.h=SIZE},.data_size=BYTES,.data=weather_ui.pixels};
    const char *names[]={BT(BT_FEELS_LIKE),BT(BT_HUMIDITY),BT(BT_WIND),BT(BT_PRECIPITATION)};
    for(unsigned i=0;i<4;i++){
        weather_ui.tiles[i]=card(weather_ui.now,14);place(weather_ui.tiles[i],(int)i*189,204,177,104,NULL);
        weather_ui.tile_names[i]=label(weather_ui.tiles[i],names[i],14,14,149,&buddy_language_font_16,MUTED);
        weather_ui.values[i]=label(weather_ui.tiles[i],"--",14,48,149,&buddy_language_font_32,INK);
    }

    /* Forecast: a row of cards, paged with arrows. */
    weather_ui.forecast=box(page,28,100,744,300);
    for(unsigned i=0;i<CARDS;i++){
        lv_obj_t *c=weather_ui.cards[i]=card(weather_ui.forecast,16);
        weather_ui.times[i]=label(c,"",0,0,10,&buddy_language_font_16,MUTED);lv_obj_set_style_text_align(weather_ui.times[i],LV_TEXT_ALIGN_CENTER,0);
        weather_ui.icons[i]=box(c,0,0,78,78);lv_obj_set_user_data(weather_ui.icons[i],(void *)(intptr_t)-1);
        lv_obj_add_event_cb(weather_ui.icons[i],draw_icon,LV_EVENT_DRAW_MAIN,NULL);
        weather_ui.temperatures[i]=label(c,"",0,0,10,&buddy_language_font_24,INK);lv_obj_set_style_text_align(weather_ui.temperatures[i],LV_TEXT_ALIGN_CENTER,0);
        weather_ui.probabilities[i]=label(c,"",0,0,10,&buddy_language_font_16,ACCENT);lv_obj_set_style_text_align(weather_ui.probabilities[i],LV_TEXT_ALIGN_CENTER,0);
        weather_ui.rain[i]=lv_bar_create(c);lv_obj_remove_style_all(weather_ui.rain[i]);lv_bar_set_range(weather_ui.rain[i],0,100);
        lv_obj_set_style_radius(weather_ui.rain[i],LV_RADIUS_CIRCLE,LV_PART_MAIN);lv_obj_set_style_radius(weather_ui.rain[i],LV_RADIUS_CIRCLE,LV_PART_INDICATOR);
        lv_obj_set_style_bg_color(weather_ui.rain[i],lv_color_hex(RULE),LV_PART_MAIN);lv_obj_set_style_bg_opa(weather_ui.rain[i],LV_OPA_COVER,LV_PART_MAIN);
        lv_obj_set_style_bg_color(weather_ui.rain[i],lv_color_hex(BUDDY_COLOR_WEATHER_APP),LV_PART_INDICATOR);lv_obj_set_style_bg_opa(weather_ui.rain[i],LV_OPA_COVER,LV_PART_INDICATOR);
    }
    weather_ui.previous=arrow(weather_ui.forecast,LV_SYMBOL_LEFT,0);
    weather_ui.next=arrow(weather_ui.forecast,LV_SYMBOL_RIGHT,1);
    weather_ui.page_range=label(weather_ui.forecast,"",0,0,10,&buddy_language_font_14,MUTED);lv_obj_set_style_text_align(weather_ui.page_range,LV_TEXT_ALIGN_CENTER,0);
    weather_ui.empty=label(weather_ui.forecast,BT(BT_FORECAST_IS_LOADING),16,100,712,&buddy_language_font_20,MUTED);lv_obj_set_style_text_align(weather_ui.empty,LV_TEXT_ALIGN_CENTER,0);
    weather_ui.view=VIEW_NOW;weather_ui.state=BUDDY_MASCOT_WORKING;
    compact_weather();layout();refresh();buddy_weather_page_tick();return page;
}

static void hour_label(char *value,size_t size,int64_t timestamp,bool use_24h)
{
    time_t stamp=(time_t)timestamp;struct tm local;localtime_r(&stamp,&local);
    strftime(value,size,use_24h?"%H:%M":"%I",&local);
    if(!use_24h){size_t used=strlen(value);snprintf(value+used,size-used," %s",BT(local.tm_hour<12?BT_AM:BT_PM));}
    if(!use_24h && value[0]=='0')memmove(value,value+1,strlen(value));
}
static void degrees(char *value,size_t size,float celsius,bool fahrenheit)
{
    if(!isfinite(celsius)){snprintf(value,size,"--");return;}
    float shown=roundf(buddy_weather_temperature(celsius,fahrenheit));if(shown==0)shown=0;
    snprintf(value,size,"%.0f\xc2\xb0",shown);
}

static void refresh_forecast(void)
{
    const buddy_weather_t *w=&weather_ui.weather;bool hourly=weather_ui.view==VIEW_HOURS;
    unsigned indices[BUDDY_WEATHER_HOURS],count=0,step=columns();time_t now=time(NULL);
    int64_t today=((int64_t)now+w->region_utc_offset)/86400;
    if(w->available){
        if(hourly){for(unsigned i=0;i<w->hourly_count && count<24;i++)if(w->hours[i].timestamp>(int64_t)now)indices[count++]=i;}
        else {for(unsigned i=0;i<w->daily_count;i++)if((w->days[i].timestamp+w->region_utc_offset)/86400>=today)indices[count++]=i;}
    }
    unsigned *offset=&weather_ui.offset[weather_ui.view];
    if(*offset>=count)*offset=count?((count-1)/step)*step:0;
    buddy_settings_t settings=buddy_settings_get();char value[96],low[24];
    for(unsigned i=0;i<CARDS;i++){
        bool show=i<step && *offset+i<count;visible(weather_ui.cards[i],show);if(!show)continue;
        unsigned index=indices[*offset+i];float probability;
        if(hourly){
            const buddy_weather_hour_t *h=&w->hours[index];
            hour_label(value,sizeof(value),h->timestamp,settings.use_24h);set_text(weather_ui.times[i],value);
            degrees(value,sizeof(value),h->temperature_c,settings.weather_fahrenheit);
            probability=h->precipitation_probability;set_icon(weather_ui.icons[i],h->code,h->is_day);
        }else{
            const buddy_weather_day_t *d=&w->days[index];time_t stamp=(time_t)(d->timestamp+w->region_utc_offset);struct tm local;gmtime_r(&stamp,&local);
            int64_t day=(d->timestamp+w->region_utc_offset)/86400;
            if(day==today)snprintf(value,sizeof(value),BT(BT_TODAY));else if(day==today+1)snprintf(value,sizeof(value),BT(BT_TOMORROW));else snprintf(value,sizeof(value),BT(BT_FORECAST_DATE_FORMAT),buddy_weekday(local.tm_wday),local.tm_mday);
            set_text(weather_ui.times[i],value);
            degrees(value,sizeof(value),d->high_c,settings.weather_fahrenheit);degrees(low,sizeof(low),d->low_c,settings.weather_fahrenheit);
            size_t used=strlen(value);snprintf(value+used,sizeof(value)-used," / %s",low);
            probability=d->precipitation_probability;set_icon(weather_ui.icons[i],d->code,true);
        }
        set_text(weather_ui.temperatures[i],value);
        if(isfinite(probability))snprintf(value,sizeof(value),"%.0f%%",probability);else snprintf(value,sizeof(value),"--");
        set_text(weather_ui.probabilities[i],value);
        lv_bar_set_value(weather_ui.rain[i],isfinite(probability)?(int)fminf(fmaxf(probability,0),100):0,LV_ANIM_OFF);
    }
    visible(weather_ui.empty,count==0);
    set_text(weather_ui.empty,w->failed || w->available ? BT(BT_FORECAST_UNAVAILABLE_RETRYING_SOON):BT(BT_FORECAST_IS_LOADING));
    bool paged=count>step;visible(weather_ui.previous,paged);visible(weather_ui.next,paged);
    enable(weather_ui.previous,*offset>0);enable(weather_ui.next,*offset+step<count);
    if(!count)value[0]=0;
    else if(hourly)snprintf(value,sizeof(value),BT(BT_U_U_OF_U_HOURS),*offset+1,*offset+step<count?*offset+step:count,count);
    else snprintf(value,sizeof(value),BT(BT_HIGH_LOW_UNITS_PRECIPITATION_CHANCE),buddy_weather_unit(settings.weather_fahrenheit));
    set_text(weather_ui.page_range,value);
}

static void refresh(void)
{
    const buddy_weather_t *weather=&weather_ui.weather;
    buddy_settings_t settings=buddy_settings_get();
    set_text(weather_ui.city,buddy_weather_city());char value[96];
    if(weather->available){
        float shown=roundf(buddy_weather_temperature(weather->temperature_c,settings.weather_fahrenheit));if(shown==0)shown=0;
        if(isfinite(shown))snprintf(value,sizeof(value),"%.0f",shown);else snprintf(value,sizeof(value),"--");
        set_text(weather_ui.temperature,value);set_text(weather_ui.unit,buddy_weather_unit(settings.weather_fahrenheit));
        set_text(weather_ui.description,buddy_weather_description(weather->code,weather->is_day));
        snprintf(value,sizeof(value),weather->stale?BT(BT_SAVED_LU_MIN_AGO):BT(BT_UPDATED_LU_MIN_AGO),(unsigned long)weather->age_s/60);set_text(weather_ui.note,value);
        weather_ui.state=BUDDY_WEATHER_ANIMATION(weather->code,weather->is_day);
        /* Today's range comes from the daily forecast entry for the region's current date. */
        int64_t today=((int64_t)time(NULL)+weather->region_utc_offset)/86400;value[0]=0;
        for(unsigned i=0;i<weather->daily_count;i++)if((weather->days[i].timestamp+weather->region_utc_offset)/86400==today){
            char high[24],low[24];degrees(high,sizeof(high),weather->days[i].high_c,settings.weather_fahrenheit);degrees(low,sizeof(low),weather->days[i].low_c,settings.weather_fahrenheit);
            snprintf(value,sizeof(value),BT(BT_HIGH_S_LOW_S),high,low);break;}
        set_text(weather_ui.range,value);
    }else{
        weather_ui.state=weather->failed?BUDDY_MASCOT_ERROR:BUDDY_MASCOT_WORKING;
        set_text(weather_ui.temperature,"--");set_text(weather_ui.unit,"");set_text(weather_ui.range,"");
        set_text(weather_ui.description,weather->failed?BT(BT_NO_WEATHER):BT(BT_LOADING_WEATHER));
        set_text(weather_ui.note,weather->failed?BT(BT_WILL_RETRY_OVER_WI_FI):BT(BT_WAITING_FOR_WI_FI));
    }
    /* The unit sits just after the number, top-aligned like a superscript. */
    lv_obj_update_layout(weather_ui.temperature);
    lv_obj_set_pos(weather_ui.unit,lv_obj_get_x(weather_ui.temperature)+lv_obj_get_width(weather_ui.temperature)+6,lv_obj_get_y(weather_ui.temperature)+4);
    float values[]={weather->feels_like_c,weather->humidity_percent,weather->wind_kmh,weather->precipitation_mm};
    const char *formats[]={NULL,"%.0f%%",BT(BT_0F_KM_H),BT(BT_1F_MM)};
    for(unsigned i=0;i<4;i++){if(weather->available&&isfinite(values[i])){if(i==0)buddy_weather_format_temperature(value,sizeof(value),values[i],settings.weather_fahrenheit);else snprintf(value,sizeof(value),formats[i],values[i]);}else snprintf(value,sizeof(value),"--");set_text(weather_ui.values[i],value);}
    if(weather_ui.view!=VIEW_NOW)refresh_forecast();
}
void buddy_weather_page_update(const buddy_weather_t *weather)
{
    if(!weather_ui.page)return;
    weather_ui.weather=*weather;refresh();
}
void buddy_weather_page_tick(void)
{
    if(!weather_ui.page)return;
    unsigned frame=(lv_tick_get()/BUDDY_MASCOT_FRAME_MS)%BUDDY_MASCOT_FRAME_COUNT;
    const lv_img_dsc_t *source=&buddy_mascot_frames[weather_ui.state][frame];
    if(!weather_ui.pixels){lv_img_set_src(weather_ui.crab,source);return;}
    memcpy(weather_ui.pixels,source->data,64);
    for(unsigned y=0;y<48;y++){
        uint8_t *row=weather_ui.pixels+64+y*SCALE*(SIZE/2);
        for(unsigned x=0;x<48;x++){
            unsigned pair=source->data[64+y*24+x/2],pixel=x%2?pair&15:pair>>4;
            for(unsigned j=0;j<SCALE/2;++j)row[x*(SCALE/2)+j]=(pixel<<4)|pixel;
        }
        for(unsigned s=1;s<SCALE;s++)memcpy(row+s*(SIZE/2),row,SIZE/2);
    }
    lv_img_cache_invalidate_src(&weather_ui.frame);lv_img_set_src(weather_ui.crab,&weather_ui.frame);lv_obj_invalidate(weather_ui.crab);
}
