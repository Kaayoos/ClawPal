/* ClawPal display interaction regression simulator.
 *
 * Runs the unmodified firmware UI sources (firmware/main/ui, assets, language)
 * against the vendored LVGL 8.4 with the firmware's own sdkconfig LVGL options
 * and RGB565 colour depth. Only hardware/network services are stubbed. Each
 * scene scripts telemetry, settings and touches on a deterministic clock and
 * records 60 fps frames as raw RGB565 for scripts/convert_frames.py.
 *
 * All data shown is anonymous demonstration data. */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "lvgl.h"
#include "board.h"
#include "esp_app_desc.h"
#include "esp_random.h"
#include "buddy_ui.h"
#include "buddy_settings.h"
#include "buddy_weather.h"
#define BUDDY_LANGUAGE_LVGL
#include "buddy_language.h"

#define W BOARD_DISPLAY_WIDTH
#define H BOARD_DISPLAY_HEIGHT

/* ---------- Stubbed platform services ---------- */
static uint32_t rng = 0x9E3779B9u;
uint32_t esp_random(void) { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }
static const esp_app_desc_t app_desc = { .version = "0.9.0" };
const esp_app_desc_t *esp_app_get_description(void) { return &app_desc; }
esp_err_t board_set_backlight(bool enabled) { (void)enabled; return ESP_OK; }

static buddy_settings_t settings;
buddy_settings_t buddy_settings_get(void) { return settings; }

/* Copied from firmware/main/weather/buddy_weather_parse.c (which needs cJSON). */
const char *buddy_weather_description(int code, bool is_day)
{
    switch (code) {
    case 0: return is_day ? BT(BT_SUNNY) : BT(BT_CLEAR);
    case 1: return BT(BT_MOSTLY_CLEAR);
    case 2: return BT(BT_PARTLY_CLOUDY);
    case 3: return BT(BT_CLOUDY);
    case 45: case 48: return BT(BT_FOG);
    case 51: case 53: case 55: return BT(BT_DRIZZLE);
    case 56: case 57: return BT(BT_FREEZING_DRIZZLE);
    case 61: case 63: case 65: return BT(BT_RAIN);
    case 66: case 67: return BT(BT_FREEZING_RAIN);
    case 71: case 73: case 75: case 77: return BT(BT_SNOW);
    case 80: case 81: case 82: return BT(BT_SHOWERS);
    case 85: case 86: return BT(BT_SNOW_SHOWERS);
    case 95: case 97: return BT(BT_THUNDERSTORM);
    case 96: case 99: return BT(BT_THUNDER_HAIL);
    default: return NULL;
    }
}
/* Same behaviour as buddy_weather.c for a named location. */
static char weather_city[64];
const char *buddy_weather_city(void)
{
    snprintf(weather_city, sizeof(weather_city), "%s", strcmp(settings.city, "Weather") == 0 ? BT(BT_WEATHER) : settings.city);
    return weather_city;
}

/* ---------- Deterministic clock ---------- */
/* Monday 12 October 2026, 10:24:00 display-local time (local time == UTC here). */
static const int64_t FILM_EPOCH = 1791800640;
static uint32_t now_ms;              /* simulated milliseconds since boot */
static int64_t clock_offset_s;       /* lets a scene move the wall clock */
time_t sim_time(time_t *out)
{
    time_t value = (time_t)(FILM_EPOCH + clock_offset_s + (int64_t)now_ms / 1000);
    if (out) *out = value;
    return value;
}

/* ---------- Display / input ---------- */
static uint16_t framebuffer[W * H];
static lv_color_t draw_buffer[W * H];
static lv_disp_drv_t disp_drv;
static lv_disp_draw_buf_t disp_buf;
static lv_disp_t *display;
static lv_indev_state_t touch_state = LV_INDEV_STATE_RELEASED;
static int touch_x, touch_y;

static void flush(lv_disp_drv_t *drv, const lv_area_t *area, lv_color_t *colors)
{
    int width = area->x2 - area->x1 + 1;
    if(area->x1<0||area->y1<0||area->x2>=W||area->y2>=H){fprintf(stderr,"Framebuffer bounds exceeded\n");exit(25);}
    for (int y = area->y1; y <= area->y2; ++y)
        memcpy(&framebuffer[y * W + area->x1], &colors[(y - area->y1) * width], (size_t)width * 2);
    lv_disp_flush_ready(drv);
}
static void touch_read(lv_indev_drv_t *drv, lv_indev_data_t *data)
{
    (void)drv;
    data->point.x = (lv_coord_t)touch_x;
    data->point.y = (lv_coord_t)touch_y;
    data->state = touch_state;
}

/* ---------- Recording ---------- */
static FILE *recording;
static FILE *events;
static int frame_index;
static double frame_clock;           /* fractional ms accumulator for 60 fps */
static char out_dir[512];

static void step(uint32_t ms)
{
    while (ms--) {
        lv_tick_inc(1);
        ++now_ms;
        lv_timer_handler();
    }
}
static void capture(void)
{
    lv_refr_now(display);
    fwrite(framebuffer, 2, W * H, recording);
    ++frame_index;
}
/* Advance time by ms with millisecond accuracy. While recording, capture
 * every 1/60 s boundary that is crossed. */
static double sim_clock;             /* exact ms since recording start */
static void advance_to(double target)
{
    uint32_t whole = (uint32_t)(llround(target) - llround(sim_clock));
    step(whole);
    sim_clock = target;
}
static void wait_ms(double ms)
{
    if (!recording) { step((uint32_t)llround(ms)); return; }
    double target = sim_clock + ms;
    for (;;) {
        double next = frame_clock + 1000.0 / 60.0;
        if (next > target + 1e-6) break;
        advance_to(next);
        frame_clock = next;
        capture();
    }
    advance_to(target);
}
static void update_clock(void);
static void record_start(void)
{
    /* Every scene's wall clock reads 10:24 when its recording begins. */
    clock_offset_s = -(int64_t)(now_ms / 1000) + 3;
    update_clock();
    char path[600];
    snprintf(path, sizeof(path), "%s/frames.rgb565", out_dir);
    recording = fopen(path, "wb");
    snprintf(path, sizeof(path), "%s/events.txt", out_dir);
    events = fopen(path, "w");
    frame_index = 0;
    frame_clock = 0;
    sim_clock = 0;
    capture();                        /* frame 0 == record start */
}
static void record_stop(void)
{
    if (recording) fclose(recording);
    if (events) { fprintf(events, "frames %d\n", frame_index); fclose(events); }
    recording = NULL; events = NULL;
}
static double rec_time(void) { return sim_clock / 1000.0; }

/* A touch: press, hold, release. Logged for the touch-ring overlay. */
static void tap(int x, int y, double hold_ms)
{
    touch_x = x; touch_y = y; touch_state = LV_INDEV_STATE_PRESSED;
    if (events) fprintf(events, "tap %.4f %d %d %.1f\n", rec_time(), x, y, hold_ms);
    wait_ms(hold_ms);
    touch_state = LV_INDEV_STATE_RELEASED;
    wait_ms(1);
}
static void mark(const char *name) { if (events) fprintf(events, "mark %.4f %s\n", rec_time(), name); }

/* ---------- Demonstration data ---------- */
static buddy_stats_t stats;
static buddy_weather_t weather;
static uint64_t sequence;

static void base_settings(const uint8_t *order, unsigned count)
{
    memset(&settings, 0, sizeof(settings));
    settings.revision = 1;
    settings.enabled_apps = 255;
    settings.home_count = (uint8_t)count;
    memcpy(settings.home_order, order, count);
    settings.use_24h = true;
    settings.weather_enabled = true;
    settings.weather_fahrenheit = false;
    settings.dim_behavior = 1; settings.dim_brightness = 40; settings.dim_after_seconds = 60;
    settings.night_enabled = true; settings.night_start = 1; settings.night_end = 10;
    settings.offline_app = 1;
    snprintf(settings.language, sizeof(settings.language), "en");
    snprintf(settings.city, sizeof(settings.city), "Warsaw");
    snprintf(settings.timezone, sizeof(settings.timezone), "UTC0");
    settings.latitude = 52.2297; settings.longitude = 21.0122;
}

static void base_weather(void)
{
    memset(&weather, 0, sizeof(weather));
    weather.available = true; weather.is_day = true;
    weather.code = 61; weather.temperature_c = 12.0f; weather.feels_like_c = 10.0f;
    weather.humidity_percent = 86; weather.wind_kmh = 14; weather.precipitation_mm = 0.6f;
    weather.age_s = 240; weather.region_utc_offset = 0;
    weather.hourly_count = 25;
    static const int codes[25] = { 61, 61, 80, 3, 2, 2, 1, 0, 0, 1, 2, 3, 3, 2, 1, 1, 0, 0, 2, 3, 3, 61, 61, 3, 2 };
    static const float temps[25] = { 12, 12, 13, 14, 15, 15, 16, 16, 15, 14, 13, 12, 11, 11, 10, 10, 9, 9, 9, 9, 9, 10, 10, 11, 12 };
    static const float rain[25] = { 80, 70, 55, 30, 15, 10, 5, 5, 5, 5, 10, 15, 20, 15, 10, 5, 5, 5, 10, 25, 40, 60, 55, 30, 15 };
    int64_t hour0 = (FILM_EPOCH / 3600) * 3600;     /* 10:00 boundary */
    for (int i = 0; i < 25; ++i) {
        weather.hours[i].timestamp = hour0 + (int64_t)i * 3600;
        weather.hours[i].code = codes[i]; weather.hours[i].temperature_c = temps[i];
        weather.hours[i].precipitation_probability = rain[i];
        int hour = (int)((weather.hours[i].timestamp % 86400) / 3600);
        weather.hours[i].is_day = hour >= 7 && hour < 18;
    }
    weather.daily_count = 5;
    static const int dcodes[5] = { 61, 2, 0, 3, 80 };
    static const float hi[5] = { 16, 17, 18, 14, 13 }, lo[5] = { 9, 8, 9, 10, 8 }, dp[5] = { 80, 20, 5, 35, 65 };
    int64_t day0 = (FILM_EPOCH / 86400) * 86400;
    for (int i = 0; i < 5; ++i) {
        weather.days[i].timestamp = day0 + (int64_t)i * 86400;
        weather.days[i].code = dcodes[i]; weather.days[i].high_c = hi[i]; weather.days[i].low_c = lo[i];
        weather.days[i].precipitation_probability = dp[i];
    }
}

static void base_stats(void)
{
    memset(&stats, 0, sizeof(stats));
    snprintf(stats.hostname, sizeof(stats.hostname), "studio-pc");
    snprintf(stats.cpu_name, sizeof(stats.cpu_name), "AMD Ryzen 7 7700");
    snprintf(stats.gpu_name, sizeof(stats.gpu_name), "AMD Radeon RX 9060 XT");
    stats.cpu_usage = 31; stats.cpu_temperature = 58; stats.gpu_usage = 42; stats.gpu_temperature = 61;
    stats.memory_usage = 37; stats.memory_used_mb = 11.8f * 1024; stats.memory_total_mb = 32 * 1024;
    stats.vram_used_mb = 5.2f * 1024; stats.vram_total_mb = 16 * 1024;
    stats.network_rx_bps = 2.4e6; stats.network_tx_bps = 186e3;
    stats.uptime_s = 3 * 3600 + 12 * 60;
    stats.auto_open_apps = false;
    stats.game.fps = NAN;
    /* Codex: reported account usage windows only. */
    buddy_codex_t *codex = &stats.codex;
    codex->running = true; codex->available = true; snprintf(codex->status, sizeof(codex->status), "live");
    codex->age_s = 20;
    codex->primary = (buddy_codex_window_t){ .available = true, .remaining_percent = 72.0f, .window_minutes = 300,
        .resets_at = (uint32_t)(FILM_EPOCH + 2 * 3600 + 41 * 60) };
    codex->secondary = (buddy_codex_window_t){ .available = true, .remaining_percent = 88.5f, .window_minutes = 10080,
        .resets_at = (uint32_t)(FILM_EPOCH + 3 * 86400 - 2 * 3600 - 24 * 60) };
    /* Claude Code: status-line quota windows plus recorded local sessions. */
    buddy_codex_t *claude = &stats.claude;
    claude->running = true; claude->available = true; snprintf(claude->status, sizeof(claude->status), "ready");
    claude->age_s = 12;
    claude->primary = (buddy_codex_window_t){ .available = true, .remaining_percent = 64.0f, .window_minutes = 300,
        .resets_at = (uint32_t)(FILM_EPOCH + 3 * 3600 + 6 * 60) };
    claude->secondary = (buddy_codex_window_t){ .available = true, .remaining_percent = 81.0f, .window_minutes = 10080,
        .resets_at = (uint32_t)(FILM_EPOCH + 4 * 86400 + 7 * 3600 - 24 * 60) };
    buddy_codex_activity_t *a = &claude->activity;
    a->available = true; a->stale = false; snprintf(a->status, sizeof(a->status), "local");
    a->working_count = 0; a->total_count = 3; a->item_count = 3;
    a->items[0] = (buddy_codex_task_t){ "Polish the weather screen", "clawpal", "completed", 95 };
    a->items[1] = (buddy_codex_task_t){ "Add timer tests", "clawpal", "waiting", 40 };
    a->items[2] = (buddy_codex_task_t){ "Tidy settings sync", "companion", "completed", 1260 };
    /* Spotify metadata: fictional track. */
    buddy_music_t *m = &stats.music;
    m->running = false;
}

static void music_playing(const char *title, const char *artist, const char *album, uint32_t position, uint32_t duration)
{
    buddy_music_t *m = &stats.music;
    m->running = m->available = m->playing = true;
    m->can_play_pause = m->can_next = m->can_previous = true;
    m->can_shuffle = m->can_repeat = m->can_seek = m->can_volume = true;
    m->shuffle = true; m->repeat = 2; m->volume = 64; m->art_color = 0x3FA9F5;
    snprintf(m->title, sizeof(m->title), "%s", title);
    snprintf(m->artist, sizeof(m->artist), "%s", artist);
    snprintf(m->album, sizeof(m->album), "%s", album);
    m->position_s = position; m->duration_s = duration;
    m->control_ok = true;
}

/* Telemetry varies gently each second, like the companion's one-second frames. */
static double wobble(double t, double a, double b, double c) { return sin(t * a) * b + sin(t * c + 1.3) * b * 0.6; }
static void send_stats(void)
{
    double t = now_ms / 1000.0;
    double burst = fmax(0, sin(t * 0.21)) * 26 + fmax(0, sin(t * 0.57 + 2)) * 12;
    stats.cpu_usage = (float)fmin(96, fmax(4, 22 + burst + wobble(t, 1.37, 5, 2.21)));
    stats.gpu_usage = (float)fmin(98, fmax(3, 44 + wobble(t, 0.23, 14, 0.91)));
    stats.cpu_temperature = (float)(56 + stats.cpu_usage * 0.12);
    stats.gpu_temperature = (float)(58 + stats.gpu_usage * 0.10);
    stats.memory_usage = (float)(37 + wobble(t, 0.05, 0.6, 0.11));
    stats.memory_used_mb = stats.memory_usage / 100.f * stats.memory_total_mb;
    stats.network_rx_bps = 2.4e6 + wobble(t, 0.9, 6e5, 1.7);
    stats.network_tx_bps = 186e3 + wobble(t, 0.7, 4e4, 1.3);
    stats.uptime_s = 3 * 3600 + 12 * 60 + (uint64_t)t;
    stats.sequence = ++sequence;
    stats.timestamp_ms = (int64_t)(FILM_EPOCH * 1000 + now_ms);
    buddy_ui_update_stats(&stats);
}
static void update_clock(void)
{
    time_t now = sim_time(NULL);
    struct tm local; localtime_r(&now, &local);
    char clock_text[16], date_text[96];
    strftime(clock_text, sizeof(clock_text), settings.use_24h ? "%H:%M" : "%I:%M", &local);
    if (!settings.use_24h && clock_text[0] == '0') memmove(clock_text, clock_text + 1, strlen(clock_text));
    buddy_date(date_text, sizeof(date_text), &local, settings.use_24h);
    buddy_ui_set_clock(clock_text, date_text, false);
}

static int music_command_pending;
static char music_last_action[16];
static uint32_t music_last_value;
static bool music_control(const char *action, uint32_t value)
{
    if (events) fprintf(events, "music %.4f %s %lu\n", rec_time(), action, (unsigned long)value);
    music_command_pending = strcmp(action, "next") == 0 ? 2 : strcmp(action, "previous") == 0 ? 3 : 1;
    snprintf(music_last_action, sizeof(music_last_action), "%s", action);
    music_last_value = value;
    return true;
}
/* The companion answers by advancing control_id, as Connection.cs does. */
static void music_reply(void)
{
    ++stats.music.control_id;
    stats.music.control_ok = true;
    music_command_pending = 0;
}

/* Feed telemetry and the clock like main.c while time passes. */
static double since_stats;
static void live(double ms)
{
    double left = ms;
    while (left > 0) {
        double chunk = fmin(left, 1000.0 - since_stats);
        wait_ms(chunk);
        since_stats += chunk; left -= chunk;
        if (since_stats >= 999.999) {
            since_stats = 0;
            if (stats.music.playing && stats.music.position_s < stats.music.duration_s) ++stats.music.position_s;
            send_stats();
            update_clock();
        }
    }
}

static void boot(const uint8_t *order, unsigned count)
{
    lv_init();
    lv_disp_draw_buf_init(&disp_buf, draw_buffer, NULL, W * H);
    lv_disp_drv_init(&disp_drv);
    disp_drv.hor_res = W; disp_drv.ver_res = H;
    disp_drv.flush_cb = flush; disp_drv.draw_buf = &disp_buf;
    display = lv_disp_drv_register(&disp_drv);
    /* 60 Hz sampling for the film; the UI's own timers and animations are unchanged. */
    lv_timer_set_period(display->refr_timer, 16);
    static lv_indev_drv_t indev;
    lv_indev_drv_init(&indev);
    indev.type = LV_INDEV_TYPE_POINTER; indev.read_cb = touch_read;
    lv_indev_t *pointer = lv_indev_drv_register(&indev);
    lv_timer_set_period(pointer->driver->read_timer, 8);

    base_settings(order, count);
    base_weather();
    base_stats();
    buddy_language_set("en");
    buddy_ui_init();
    buddy_ui_apply_settings(&settings);
    buddy_ui_set_music_control_callback(music_control);
    buddy_ui_set_weather(&weather);
    update_clock();
    buddy_ui_set_connection(BUDDY_WIFI_READY, "Home Wi-Fi", "192.168.1.40", "");
    step(1200);
    buddy_ui_set_connection(BUDDY_PC_CONNECTED, "Home Wi-Fi", "192.168.1.40", "");
    /* Fill the 60-second CPU history and let the boot splash finish. */
    live(66000);
    /* Pin the wall clock so every scene reads 10:24 when recording starts. */
    clock_offset_s = -(int64_t)(now_ms / 1000) + 3;
    update_clock();
}


static void save_frame(const char *name){
    lv_refr_now(display);char path[700];snprintf(path,sizeof(path),"%s/%s.ppm",out_dir,name);FILE *file=fopen(path,"wb");if(!file){perror(path);exit(1);}fprintf(file,"P6\n%d %d\n255\n",W,H);
    for(unsigned i=0;i<W*H;++i){uint16_t c=framebuffer[i];uint8_t rgb[]={((c>>11)&31)*255/31,((c>>5)&63)*255/63,(c&31)*255/31};fwrite(rgb,1,3,file);}fclose(file);
}
static lv_obj_t *find_label(lv_obj_t *root,const char *text){
    if(lv_obj_has_flag(root,LV_OBJ_FLAG_HIDDEN))return NULL;
    if(lv_obj_check_type(root,&lv_label_class)&&!strcmp(lv_label_get_text(root),text))return root;
    for(uint32_t i=0;i<lv_obj_get_child_cnt(root);++i){lv_obj_t *found=find_label(lv_obj_get_child(root,i),text);if(found)return found;}return NULL;
}
static void tap_label(const char *text){
    lv_obj_t *label=find_label(lv_scr_act(),text);if(!label){fprintf(stderr,"Missing control %s\n",text);exit(20);}
    lv_obj_t *button=lv_obj_get_parent(label);lv_area_t area;lv_obj_get_coords(button,&area);
    int x=(area.x1+area.x2)/2,y=(area.y1+area.y2)/2;
    if(x<0||x>=W||y<0||y>=H-44){fprintf(stderr,"Unreachable control %s at %d,%d\n",text,x,y);exit(21);}tap(x,y,90);live(180);
}
/* Full-screen overlays cover the app footer, so their controls may sit at the bottom edge. */
static void tap_overlay_label(const char *text){
    lv_obj_t *label=find_label(lv_scr_act(),text);if(!label){fprintf(stderr,"Missing control %s\n",text);exit(20);}
    lv_obj_t *button=lv_obj_get_parent(label);lv_area_t area;lv_obj_get_coords(button,&area);
    int x=(area.x1+area.x2)/2,y=(area.y1+area.y2)/2;
    if(x<0||x>=W||y<0||y>=H){fprintf(stderr,"Unreachable control %s at %d,%d\n",text,x,y);exit(21);}tap(x,y,90);live(180);
}
static lv_obj_t *find_slider(lv_obj_t *root,int *index){
    if(lv_obj_has_flag(root,LV_OBJ_FLAG_HIDDEN))return NULL;
    if(lv_obj_check_type(root,&lv_slider_class)&&(*index)--==0)return root;
    for(uint32_t i=0;i<lv_obj_get_child_cnt(root);++i){lv_obj_t *found=find_slider(lv_obj_get_child(root,i),index);if(found)return found;}return NULL;
}
static lv_obj_t *visible_slider(int index){return find_slider(lv_scr_act(),&index);}
/* Press on the track, slide across in small steps and lift, like a finger. */
static void drag_slider(lv_obj_t *slider,int from,int to){
    lv_area_t area;lv_obj_get_coords(slider,&area);int y=(area.y1+area.y2)/2,width=area.x2-area.x1;
    int x0=area.x1+width*from/100,x1=area.x1+width*to/100;
    if(y<0||y>=H-44){fprintf(stderr,"Unreachable slider at %d\n",y);exit(21);}
    touch_x=x0;touch_y=y;touch_state=LV_INDEV_STATE_PRESSED;if(events)fprintf(events,"tap %.4f %d %d 220.0\n",rec_time(),x0,y);wait_ms(40);
    for(int s=1;s<=10;++s){touch_x=x0+(x1-x0)*s/10;wait_ms(16);}
    touch_state=LV_INDEV_STATE_RELEASED;wait_ms(20);live(180);
}
static int confirm_answers;static buddy_confirm_action_t confirm_last;static bool confirm_approved;
static void confirm_result(buddy_confirm_action_t action,bool approved){++confirm_answers;confirm_last=action;confirm_approved=approved;}
static void scroll_page(void){
    touch_x=W/2;touch_y=H-62;touch_state=LV_INDEV_STATE_PRESSED;step(60);
    for(int y=H-62;y>30;y-=6){touch_y=y;step(12);}touch_state=LV_INDEV_STATE_RELEASED;step(600);
}
int main(int argc,char **argv){
    if(argc<2)return 2;snprintf(out_dir,sizeof(out_dir),"%s",argv[1]);
    static const uint8_t order[]={0,2,4,7,6};boot(order,5);
    music_playing("Golden Hour Drive","Neon Harbor","Coastline Sessions",71,204);send_stats();
    const char *names[]={"monitor","clock","music","timer","codex","home","weather","claude"};
    for(int app=0;app<BUDDY_APP_COUNT;++app){
        if(!buddy_ui_select_app((buddy_app_t)app))return 3;live(1400);if(buddy_ui_get_state().active_app!=(unsigned)app)return 4;
        save_frame(names[app]);
        if(W<800){for(int i=0;i<4;++i)scroll_page();char name[60];snprintf(name,sizeof(name),"%s-scrolled",names[app]);save_frame(name);}
        if(app==BUDDY_APP_TIMER){tap_label(BT(BT_START));if(!buddy_ui_get_state().timer_running)return 22;live(72000);save_frame("timer-running");
            lv_obj_t *pause=find_label(lv_scr_act(),BT(BT_PAUSE));if(pause)lv_obj_scroll_to_view_recursive(pause,LV_ANIM_OFF);tap_label(BT(BT_PAUSE));if(buddy_ui_get_state().timer_running)return 23;}
        if(app==BUDDY_APP_MUSIC){
            tap_label(LV_SYMBOL_NEXT);if(music_command_pending!=2)return 24;music_reply();live(1000);
            lv_obj_t *seek=visible_slider(0),*volume=visible_slider(1);if(!seek||!volume)return 25;
            drag_slider(seek,10,50);if(strcmp(music_last_action,"seek")||abs((int)music_last_value-(int)stats.music.duration_s/2)>8)return 26;
            stats.music.position_s=music_last_value;music_reply();live(1000);
            // A slider released while another command is in flight sends once Spotify answers.
            tap_label(LV_SYMBOL_SHUFFLE);if(strcmp(music_last_action,"shuffle"))return 27;
            drag_slider(volume,64,25);if(strcmp(music_last_action,"shuffle"))return 28;
            stats.music.shuffle=false;music_reply();live(1000);if(strcmp(music_last_action,"volume")||abs((int)music_last_value-25)>4)return 29;
            stats.music.volume=music_last_value;music_reply();live(1000);
            tap_label(LV_SYMBOL_LOOP);if(strcmp(music_last_action,"repeat"))return 30;stats.music.repeat=1;music_reply();live(1000);
            save_frame("music-controls");
            buddy_music_t current=stats.music;
            snprintf(stats.music.title,sizeof(stats.music.title),"Midnight Ferry to the Lighthouse Islands (Extended Harbour Mix)");
            send_stats();live(400);save_frame("music-long");stats.music=current;send_stats();
            stats.music.playing=false;stats.music.art_color=0;send_stats();live(400);save_frame("music-paused");
            buddy_music_t playing=stats.music;memset(&stats.music,0,sizeof(stats.music));send_stats();live(400);save_frame("music-idle");
            stats.music=playing;stats.music.playing=true;stats.music.art_color=0x3FA9F5;send_stats();
        }
        if(app==BUDDY_APP_WEATHER){
            // Forecast tabs page with arrows; every size reaches all three views.
            const char *views[]={BT(BT_24_HOURS),BT(BT_5_DAYS),BT(BT_NOW)},*frames[]={"weather-hours","weather-days",NULL};
            for(int v=0;v<3;++v){lv_obj_t *tab=find_label(lv_scr_act(),views[v]);if(!tab)return 35;lv_obj_scroll_to_view_recursive(tab,LV_ANIM_OFF);live(100);
                tap_label(views[v]);live(300);if(frames[v])save_frame(frames[v]);
                if(v==0){lv_obj_t *next=find_label(lv_scr_act(),LV_SYMBOL_RIGHT);if(!next)return 36;lv_obj_scroll_to_view_recursive(next,LV_ANIM_OFF);live(100);
                    tap_label(LV_SYMBOL_RIGHT);if(!find_label(lv_scr_act(),LV_SYMBOL_LEFT))return 37;save_frame("weather-hours-next");}}
        }
        if(app==BUDDY_APP_CLAUDE){
            // A nearly spent window turns amber and projects when it runs out.
            buddy_codex_t saved=stats.claude;stats.claude.primary.remaining_percent=12.0f;send_stats();live(400);save_frame("claude-low");
            stats.claude=saved;stats.claude.activity.items[0]=(buddy_codex_task_t){ "Polish the weather screen", "clawpal", "working", 12 };
            stats.claude.activity.working_count=1;send_stats();live(400);
            lv_obj_t *tab=find_label(lv_scr_act(),BT(BT_TASKS));if(!tab)return 31;lv_obj_scroll_to_view_recursive(tab,LV_ANIM_OFF);live(100);
            tap_label(BT(BT_TASKS));if(!find_label(lv_scr_act(),BT(BT_LOCAL_SNAPSHOT)))return 33;save_frame("claude-tasks");
            lv_obj_t *usage=find_label(lv_scr_act(),BT(BT_USAGE));if(!usage)return 32;lv_obj_scroll_to_view_recursive(usage,LV_ANIM_OFF);live(100);
            tap_label(BT(BT_USAGE));if(find_label(lv_scr_act(),BT(BT_LOCAL_SNAPSHOT)))return 34;
            stats.claude=saved;send_stats();live(200);
        }
    }
    buddy_ui_select_app(BUDDY_APP_HOME);live(600);tap(46,H-22,90);live(500);if(!buddy_ui_get_state().menu_open)return 5;save_frame("apps");
    if(W<800){for(int i=0;i<4;++i)scroll_page();save_frame("apps-scrolled");}
    tap(46,H-22,90);live(400);if(buddy_ui_get_state().menu_open)return 6;
    // Remote firmware and Wi-Fi changes wait for a tap on the display; silence counts as a denial.
    buddy_ui_set_confirm_callback(confirm_result);
    buddy_ui_request_confirmation(BUDDY_CONFIRM_FIRMWARE);live(300);buddy_ui_request_confirmation(BUDDY_CONFIRM_FIRMWARE);live(300);
    if(!find_label(lv_scr_act(),BT(BT_CONFIRM_FIRMWARE_TITLE))||confirm_answers)return 8;save_frame("confirm-firmware");
    tap_overlay_label(BT(BT_CONFIRM_ALLOW));if(confirm_answers!=1||confirm_last!=BUDDY_CONFIRM_FIRMWARE||!confirm_approved)return 9;
    if(find_label(lv_scr_act(),BT(BT_CONFIRM_FIRMWARE_TITLE)))return 10;
    buddy_ui_request_confirmation(BUDDY_CONFIRM_WIFI);live(300);save_frame("confirm-wifi");
    tap_overlay_label(BT(BT_CONFIRM_DENY));if(confirm_answers!=2||confirm_last!=BUDDY_CONFIRM_WIFI||confirm_approved)return 11;
    buddy_ui_request_confirmation(BUDDY_CONFIRM_WIFI);wait_ms(61000);
    if(confirm_answers!=3||confirm_approved||find_label(lv_scr_act(),BT(BT_CONFIRM_WIFI_TITLE)))return 12;
    // All size classes retain the same offline fallback and wake/dim policy.
    buddy_ui_set_connection(BUDDY_PC_OFFLINE,"Home Wi-Fi","192.168.1.40","");wait_ms(61000);update_clock();
    if(buddy_ui_get_state().active_app!=BUDDY_APP_CLOCK)return 7;
    printf("PASS %dx%d: all apps, footer navigation, remote-change confirmation, offline fallback\n",W,H);return 0;
}
