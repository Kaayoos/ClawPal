#include "buddy_weather_units.h"
#include "buddy_theme.h"
#include "buddy_language_fonts.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"
#include "buddy_weather_page.h"
#include "board.h"
#include "buddy_weather_animation.h"
#include "buddy_music_font.h"
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
#define FORECAST_ROWS 5
enum { VIEW_NOW, VIEW_HOURS, VIEW_DAYS };
static struct {
    lv_obj_t *page,*city,*temperature,*description,*note,*crab,*values[4],*metrics,*forecast,*seam;
    lv_obj_t *tabs[3],*tab_labels[3],*head_time,*head_temperature;
    lv_obj_t *rows[FORECAST_ROWS],*times[FORECAST_ROWS],*conditions[FORECAST_ROWS],*temperatures[FORECAST_ROWS],*probabilities[FORECAST_ROWS];
    lv_obj_t *previous,*next,*range,*empty;
    unsigned view,hour_offset;
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
static void set_text(lv_obj_t *label,const char *value)
{
    if(strcmp(lv_label_get_text(label),value))lv_label_set_text(label,value);
}
static void rule(lv_obj_t *parent,int x,int y,int width,int height)
{
    lv_obj_t *obj=box(parent,x,y,width,height);
    lv_obj_set_style_bg_color(obj,lv_color_hex(RULE),0);lv_obj_set_style_bg_opa(obj,LV_OPA_COVER,0);
}
static lv_obj_t *button(lv_obj_t *parent,const char *title,int x,int y,int width,lv_event_cb_t cb,uintptr_t id,lv_obj_t **text)
{
    lv_obj_t *obj=box(parent,x,y,width,44);lv_obj_add_flag(obj,LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_radius(obj,8,0);lv_obj_set_style_bg_color(obj,lv_color_hex(BUDDY_COLOR_SURFACE),0);
    lv_obj_set_style_bg_color(obj,lv_color_hex(BUDDY_COLOR_PRESSED),LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(obj,LV_OPA_COVER,LV_STATE_PRESSED);
    lv_obj_add_event_cb(obj,cb,LV_EVENT_CLICKED,(void *)id);
    lv_obj_t *caption=label(obj,title,0,12,width,&buddy_language_font_16,ACCENT);
    lv_obj_set_style_text_align(caption,LV_TEXT_ALIGN_CENTER,0);
    if(text)*text=caption;
    return obj;
}
static void enable(lv_obj_t *obj,bool enabled)
{
    if(enabled)lv_obj_clear_state(obj,LV_STATE_DISABLED);else lv_obj_add_state(obj,LV_STATE_DISABLED);
    lv_obj_set_style_opa(obj,enabled?LV_OPA_COVER:LV_OPA_40,0);
}

static void compact_place(lv_obj_t *o,int x,int y,int w,int h,const lv_font_t *font){lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,h);if(font)lv_obj_set_style_text_font(o,font,0);}
static void compact_weather(void){
    if(BOARD_DISPLAY_WIDTH>=800)return;
    int w=BOARD_DISPLAY_WIDTH;bool tiny=BOARD_DISPLAY_HEIGHT<400,now=weather_ui.view==VIEW_NOW;
    lv_obj_add_flag(weather_ui.page,LV_OBJ_FLAG_SCROLLABLE|LV_OBJ_FLAG_CLICKABLE);lv_obj_set_scroll_dir(weather_ui.page,LV_DIR_VER);lv_obj_set_style_bg_color(weather_ui.page,lv_color_hex(ACCENT),LV_PART_SCROLLBAR);lv_obj_set_style_width(weather_ui.page,3,LV_PART_SCROLLBAR);
    visible(weather_ui.seam,false);
    compact_place(weather_ui.city,12,8,w-24,30,&buddy_language_font_24);
    for(int i=0;i<3;++i){int width=(w-32)/3;compact_place(weather_ui.tabs[i],12+i*(width+4),46,width,44,NULL);compact_place(weather_ui.tab_labels[i],2,12,width-4,24,&buddy_language_font_16);}
    compact_place(weather_ui.temperature,12,105,w-SIZE-36,52,tiny?&buddy_language_font_32:&buddy_language_font_48);
    compact_place(weather_ui.description,12,tiny?158:164,w-24,28,&buddy_language_font_16);
    compact_place(weather_ui.note,12,tiny?189:201,w-24,24,&buddy_language_font_14);
    lv_obj_set_pos(weather_ui.crab,w-SIZE-12,tiny?80:80);
    int start=tiny?230:288;
    compact_place(weather_ui.metrics,12,start,w-24,170,NULL);
    lv_obj_t *rule_obj=lv_obj_get_child(weather_ui.metrics,0);compact_place(rule_obj,0,0,w-24,1,NULL);
    for(int i=0;i<4;++i){int x=(i%2)*(w-24)/2,y=(i/2)*82;lv_obj_t *name=lv_obj_get_child(weather_ui.metrics,1+i*2);compact_place(name,x,y+16,(w-32)/2,22,&buddy_language_font_14);compact_place(weather_ui.values[i],x,y+43,(w-32)/2,32,&buddy_language_font_24);}
    compact_place(weather_ui.forecast,12,start,w-24,440,NULL);
    // Two lines per forecast row retain conditions without squeezing columns.
    compact_place(weather_ui.head_time,0,0,90,22,&buddy_language_font_14);
    compact_place(lv_obj_get_child(weather_ui.forecast,1),0,0,0,0,NULL);
    compact_place(weather_ui.head_temperature,(w-24)/2,0,(w-24)/2,22,&buddy_language_font_14);
    compact_place(lv_obj_get_child(weather_ui.forecast,3),0,0,0,0,NULL);
    for(int i=0;i<FORECAST_ROWS;++i){lv_obj_t *row=weather_ui.rows[i];compact_place(row,0,26+i*66,w-24,64,NULL);
        compact_place(weather_ui.times[i],0,0,(w-24)/2,28,&buddy_language_font_20);compact_place(weather_ui.temperatures[i],(w-24)/2,0,(w-24)/2,28,&buddy_language_font_20);
        compact_place(weather_ui.conditions[i],0,32,w-100,26,&buddy_language_font_16);compact_place(weather_ui.probabilities[i],w-96,32,72,26,&buddy_language_font_16);compact_place(lv_obj_get_child(row,4),0,63,w-24,1,NULL);
    }
    compact_place(weather_ui.previous,0,369,108,44,NULL);compact_place(weather_ui.next,w-132,369,108,44,NULL);compact_place(weather_ui.range,0,416,w-24,24,&buddy_language_font_14);
    compact_place(weather_ui.empty,0,80,w-24,100,&buddy_language_font_20);
    (void)now;
}

static void layout(void)
{
    bool now=weather_ui.view==VIEW_NOW;
    visible(weather_ui.metrics,now);visible(weather_ui.forecast,!now);visible(weather_ui.seam,!now);
    lv_obj_set_pos(weather_ui.temperature,32,now?121:106);lv_obj_set_width(weather_ui.temperature,now?460:194);
    lv_obj_set_pos(weather_ui.description,now?34:32,now?190:356);lv_obj_set_width(weather_ui.description,now?480:196);
    lv_obj_set_style_text_font(weather_ui.description,now?&buddy_language_font_24:&buddy_language_font_20,0);
    lv_obj_set_pos(weather_ui.note,now?34:32,now?235:396);lv_obj_set_width(weather_ui.note,now?472:196);
    lv_obj_set_style_text_font(weather_ui.note,now?&buddy_language_font_16:&buddy_language_font_14,0);
    lv_obj_set_pos(weather_ui.crab,now?555:28,now?86:164);
    for(unsigned i=0;i<3;i++){
        lv_obj_set_style_bg_opa(weather_ui.tabs[i],i==weather_ui.view?LV_OPA_COVER:LV_OPA_TRANSP,0);
        lv_obj_set_style_text_color(weather_ui.tab_labels[i],lv_color_hex(i==weather_ui.view?INK:MUTED),0);
    }
    compact_weather();
}
static void tab_clicked(lv_event_t *event)
{
    unsigned view=(unsigned)(uintptr_t)lv_event_get_user_data(event);
    if(view==weather_ui.view)return;
    weather_ui.view=view;layout();refresh();
    buddy_ui_motion_enter(view==VIEW_NOW?weather_ui.metrics:weather_ui.forecast,6,180,0);
}
static void page_clicked(lv_event_t *event)
{
    if((uintptr_t)lv_event_get_user_data(event))weather_ui.hour_offset+=FORECAST_ROWS;
    else weather_ui.hour_offset=weather_ui.hour_offset>=FORECAST_ROWS?weather_ui.hour_offset-FORECAST_ROWS:0;
    refresh();buddy_ui_motion_enter(weather_ui.forecast,4,150,0);
}
lv_obj_t *buddy_weather_page_create(lv_obj_t *parent)
{
    lv_obj_t *page=weather_ui.page=box(parent,0,0,BOARD_DISPLAY_WIDTH,BOARD_DISPLAY_HEIGHT-44);
    lv_obj_set_style_bg_color(page,lv_color_hex(BUDDY_COLOR_BG),0);lv_obj_set_style_bg_opa(page,LV_OPA_COVER,0);
    weather_ui.city=label(page,buddy_weather_city(),32,28,380,&buddy_language_font_32,INK);
    const char *tabs[]={BT(BT_NOW),BT(BT_24_HOURS),BT(BT_5_DAYS)};
    for(unsigned i=0;i<3;i++)weather_ui.tabs[i]=button(page,tabs[i],428+i*116,26,108,tab_clicked,i,&weather_ui.tab_labels[i]);
    weather_ui.temperature=label(page,"--",32,121,460,&buddy_language_font_48,INK);
    weather_ui.description=label(page,BT(BT_FINDING_YOUR_FORECAST),34,190,480,&buddy_language_font_24,MUTED);
    weather_ui.note=label(page,BT(BT_WAITING_FOR_WI_FI),34,235,472,&buddy_language_font_16,MUTED);
    weather_ui.crab=lv_img_create(page);lv_img_set_antialias(weather_ui.crab,false);lv_obj_clear_flag(weather_ui.crab,LV_OBJ_FLAG_CLICKABLE);
    weather_ui.pixels=heap_caps_malloc(BYTES,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
    weather_ui.frame=(lv_img_dsc_t){.header={.cf=LV_IMG_CF_INDEXED_4BIT,.w=SIZE,.h=SIZE},.data_size=BYTES,.data=weather_ui.pixels};
    weather_ui.metrics=box(page,0,299,800,130);rule(weather_ui.metrics,32,0,736,1);
    const char *names[]={BT(BT_FEELS_LIKE),BT(BT_HUMIDITY),BT(BT_WIND),BT(BT_PRECIPITATION)};
    for(unsigned i=0;i<4;i++){
        label(weather_ui.metrics,names[i],32+i*188,27,172,&buddy_language_font_16,MUTED);
        weather_ui.values[i]=label(weather_ui.metrics,"--",32+i*188,62,172,&buddy_language_font_24,INK);
    }
    weather_ui.seam=box(page,234,104,1,315);lv_obj_set_style_bg_color(weather_ui.seam,lv_color_hex(RULE),0);lv_obj_set_style_bg_opa(weather_ui.seam,LV_OPA_COVER,0);
    weather_ui.forecast=box(page,252,96,516,344);
    weather_ui.head_time=label(weather_ui.forecast,BT(BT_LOCAL_TIME),0,3,112,&buddy_language_font_14,MUTED);
    label(weather_ui.forecast,BT(BT_CONDITIONS),122,3,132,&buddy_language_font_14,MUTED);
    weather_ui.head_temperature=label(weather_ui.forecast,BT(BT_TEMP),262,3,160,&buddy_language_font_14,MUTED);
    lv_obj_set_style_text_align(weather_ui.head_temperature,LV_TEXT_ALIGN_CENTER,0);
    lv_obj_t *rain=label(weather_ui.forecast,BT(BT_PRECIP),430,3,86,&buddy_language_font_14,MUTED);lv_obj_set_style_text_align(rain,LV_TEXT_ALIGN_RIGHT,0);
    for(unsigned i=0;i<FORECAST_ROWS;i++){
        lv_obj_t *row=weather_ui.rows[i]=box(weather_ui.forecast,0,31+i*50,516,50);
        weather_ui.times[i]=label(row,"",0,12,112,&buddy_language_font_20,INK);
        weather_ui.conditions[i]=label(row,"",122,15,132,&buddy_language_font_16,MUTED);
        weather_ui.temperatures[i]=label(row,"",262,12,160,&buddy_language_font_20,INK);
        weather_ui.probabilities[i]=label(row,"",444,12,72,&buddy_language_font_20,ACCENT);
        lv_obj_set_style_text_align(weather_ui.temperatures[i],LV_TEXT_ALIGN_CENTER,0);
        lv_obj_set_style_text_align(weather_ui.probabilities[i],LV_TEXT_ALIGN_RIGHT,0);rule(row,0,49,516,1);
    }
    weather_ui.previous=button(weather_ui.forecast,BT(BT_PREVIOUS),0,295,108,page_clicked,0,NULL);
    weather_ui.next=button(weather_ui.forecast,BT(BT_NEXT),408,295,108,page_clicked,1,NULL);
    weather_ui.range=label(weather_ui.forecast,"",112,308,292,&buddy_language_font_14,MUTED);lv_obj_set_style_text_align(weather_ui.range,LV_TEXT_ALIGN_CENTER,0);
    weather_ui.empty=label(weather_ui.forecast,BT(BT_FORECAST_IS_LOADING),16,135,484,&buddy_language_font_20,MUTED);lv_obj_set_style_text_align(weather_ui.empty,LV_TEXT_ALIGN_CENTER,0);
    weather_ui.view=VIEW_HOURS;weather_ui.state=BUDDY_MASCOT_WORKING;
    layout();refresh();buddy_weather_page_tick();return page;
}
static void refresh_forecast(void)
{
    const buddy_weather_t *w=&weather_ui.weather;bool hourly=weather_ui.view==VIEW_HOURS;
    unsigned indices[BUDDY_WEATHER_HOURS],count=0;time_t now=time(NULL);
    int64_t today=((int64_t)now+w->region_utc_offset)/86400;
    if(w->available){
        if(hourly){for(unsigned i=0;i<w->hourly_count && count<24;i++)if(w->hours[i].timestamp>(int64_t)now)indices[count++]=i;}
        else {for(unsigned i=0;i<w->daily_count;i++)if((w->days[i].timestamp+w->region_utc_offset)/86400>=today)indices[count++]=i;}
    }
    if(weather_ui.hour_offset>=count)weather_ui.hour_offset=count?((count-1)/FORECAST_ROWS)*FORECAST_ROWS:0;
    unsigned offset=hourly?weather_ui.hour_offset:0;
    set_text(weather_ui.head_time,hourly?BT(BT_LOCAL_TIME):BT(BT_DAY));set_text(weather_ui.head_temperature,hourly?BT(BT_TEMP):BT(BT_HIGH_LOW));
    buddy_settings_t settings=buddy_settings_get();char value[96];
    for(unsigned row=0;row<FORECAST_ROWS;row++){
        bool show=offset+row<count;visible(weather_ui.rows[row],show);if(!show)continue;
        unsigned index=indices[offset+row];float probability;int code;bool is_day=true;
        if(hourly){
            const buddy_weather_hour_t *h=&w->hours[index];time_t stamp=(time_t)h->timestamp;struct tm local;localtime_r(&stamp,&local);
            strftime(value,sizeof(value),settings.use_24h?"%H:%M":"%I:%M",&local);
            if(!settings.use_24h){size_t used=strlen(value);snprintf(value+used,sizeof(value)-used," %s",BT(local.tm_hour<12?BT_AM:BT_PM));}
            if(!settings.use_24h && value[0]=='0')memmove(value,value+1,strlen(value));
            set_text(weather_ui.times[row],value);buddy_weather_format_temperature(value,sizeof(value),h->temperature_c,settings.weather_fahrenheit);
            probability=h->precipitation_probability;code=h->code;is_day=h->is_day;
        }else{
            const buddy_weather_day_t *d=&w->days[index];time_t stamp=(time_t)(d->timestamp+w->region_utc_offset);struct tm local;gmtime_r(&stamp,&local);
            int64_t day=(d->timestamp+w->region_utc_offset)/86400;
            if(day==today)snprintf(value,sizeof(value),BT(BT_TODAY));else if(day==today+1)snprintf(value,sizeof(value),BT(BT_TOMORROW));else snprintf(value,sizeof(value),BT(BT_FORECAST_DATE_FORMAT),buddy_weekday(local.tm_wday),local.tm_mday);
            set_text(weather_ui.times[row],value);snprintf(value,sizeof(value),"%.0f\xc2\xb0 / %.0f\xc2\xb0",buddy_weather_temperature(d->high_c,settings.weather_fahrenheit),buddy_weather_temperature(d->low_c,settings.weather_fahrenheit));
            probability=d->precipitation_probability;code=d->code;
        }
        set_text(weather_ui.temperatures[row],value);set_text(weather_ui.conditions[row],buddy_weather_description(code,is_day));
        if(isfinite(probability))snprintf(value,sizeof(value),"%.0f%%",probability);else snprintf(value,sizeof(value),"--");
        set_text(weather_ui.probabilities[row],value);
    }
    visible(weather_ui.empty,count==0);
    set_text(weather_ui.empty,w->failed || w->available ? BT(BT_FORECAST_UNAVAILABLE_RETRYING_SOON):BT(BT_FORECAST_IS_LOADING));
    visible(weather_ui.previous,hourly&&count>0);visible(weather_ui.next,hourly&&count>0);
    enable(weather_ui.previous,offset>0);enable(weather_ui.next,offset+FORECAST_ROWS<count);
    if(!count)value[0]=0;
    else if(hourly)snprintf(value,sizeof(value),BT(BT_U_U_OF_U_HOURS),offset+1,offset+FORECAST_ROWS<count?offset+FORECAST_ROWS:count,count);
    else snprintf(value,sizeof(value),BT(BT_HIGH_LOW_UNITS_PRECIPITATION_CHANCE),buddy_weather_unit(settings.weather_fahrenheit));
    set_text(weather_ui.range,value);
    lv_obj_set_x(weather_ui.range,BOARD_DISPLAY_WIDTH<800?0:(hourly?112:0));lv_obj_set_width(weather_ui.range,BOARD_DISPLAY_WIDTH<800?BOARD_DISPLAY_WIDTH-24:(hourly?292:516));
}
static void refresh(void)
{
    const buddy_weather_t *weather=&weather_ui.weather;
    buddy_settings_t settings=buddy_settings_get();
    set_text(weather_ui.city,buddy_weather_city());char value[96];
    if(weather->available){
        buddy_weather_format_temperature(value,sizeof(value),weather->temperature_c,settings.weather_fahrenheit);set_text(weather_ui.temperature,value);
        set_text(weather_ui.description,buddy_weather_description(weather->code,weather->is_day));
        snprintf(value,sizeof(value),weather->stale?BT(BT_SAVED_LU_MIN_AGO):BT(BT_UPDATED_LU_MIN_AGO),(unsigned long)weather->age_s/60);set_text(weather_ui.note,value);
        weather_ui.state=BUDDY_WEATHER_ANIMATION(weather->code,weather->is_day);
    }else{
        weather_ui.state=weather->failed?BUDDY_MASCOT_ERROR:BUDDY_MASCOT_WORKING;
        set_text(weather_ui.temperature,"--");set_text(weather_ui.description,weather->failed?BT(BT_NO_WEATHER):BT(BT_LOADING_WEATHER));
        set_text(weather_ui.note,weather->failed?BT(BT_WILL_RETRY_OVER_WI_FI):BT(BT_WAITING_FOR_WI_FI));
    }
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
