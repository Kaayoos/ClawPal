/* Compact compositions share the same objects, callbacks and state as the
 * wide layout. Content scrolls vertically; the Apps/Home footer stays fixed. */
#if BOARD_DISPLAY_WIDTH < 800
static void place(lv_obj_t *o,int x,int y,int w,int h){lv_obj_set_align(o,LV_ALIGN_TOP_LEFT);lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,h);}
static void small_text(lv_obj_t *o,int x,int y,int w,int h,const lv_font_t *font){place(o,x,y,w,h);lv_obj_set_style_text_font(o,font,0);}
static void scrollable(lv_obj_t *o){lv_obj_add_flag(o,LV_OBJ_FLAG_SCROLLABLE|LV_OBJ_FLAG_CLICKABLE);lv_obj_set_scroll_dir(o,LV_DIR_VER);lv_obj_set_scrollbar_mode(o,LV_SCROLLBAR_MODE_AUTO);lv_obj_set_style_bg_color(o,color(C_ACCENT),LV_PART_SCROLLBAR);lv_obj_set_style_width(o,3,LV_PART_SCROLLBAR);}
static void fit_labels(lv_obj_t *parent,int width){
    for(uint32_t i=0;i<lv_obj_get_child_cnt(parent);++i){lv_obj_t *o=lv_obj_get_child(parent,i);
        if(lv_obj_check_type(o,&lv_label_class)){int x=lv_obj_get_x(o);if(x>=width)x=12;lv_obj_set_x(o,x);lv_obj_set_width(o,LV_MAX(20,width-x-12));}
        else if(lv_obj_get_height(o)==1){visible(o,false);}
    }
}
static void compact_agent(agent_view_t *view){
    int w=BOARD_DISPLAY_WIDTH;bool tiny=BOARD_DISPLAY_HEIGHT<400;
    fit_labels(view->page,w);visible(view->logo,false);
    small_text(view->heading,12,8,w-120,28,&buddy_language_font_20);small_text(view->plan,12,36,w-120,22,&buddy_language_font_14);
    lv_obj_set_pos(view->mascot,w-106,-15);
    place(view->tab_track,12,64,w-24,44);
    for(int i=0;i<2;++i){place(view->tabs[i],4+i*((w-32)/2),4,(w-32)/2,36);lv_obj_set_width(view->tab_labels[i],(w-32)/2-8);lv_obj_align(view->tab_labels[i],LV_ALIGN_CENTER,0,0);}
    // Usage cards stack full width; the gauge and type scale down with the screen.
    int card=tiny?108:128,gauge=tiny?84:104,body=card*2+10;
    place(view->usage_body,12,120,w-24,body);place(view->tasks_body,12,120,w-24,tiny?3*80:3*88);
    for(int i=0;i<2;++i)agent_card_layout(view,i,w-24,card,gauge,tiny?&buddy_language_font_24:&buddy_language_font_32,
        &buddy_language_font_14,tiny?&buddy_language_font_16:&buddy_language_font_20,&buddy_language_font_14);
    view->card_top=0;view->card_step=card+10;view->card_single_top=0;
    agent_task_layout(view,w-24,tiny?72:80,tiny?80:88,tiny?&buddy_language_font_16:&buddy_language_font_20,&buddy_language_font_14,tiny);
    lv_obj_t *titles[]={view->empty_title,view->tasks_empty_title},*bodies[]={view->empty_body,view->tasks_empty_body};
    for(int i=0;i<2;++i){small_text(titles[i],0,8,w-24,60,&buddy_language_font_24);small_text(bodies[i],0,74,w-24,170,&buddy_language_font_16);}
    int below=120+(tiny?3*80:body)+8;
    small_text(view->status,12,below,w-24,22,&buddy_language_font_14);lv_obj_set_style_text_align(view->status,LV_TEXT_ALIGN_LEFT,0);
    small_text(view->age,12,below+24,w-24,40,&buddy_language_font_14);lv_obj_set_style_text_align(view->age,LV_TEXT_ALIGN_LEFT,0);
}
static void compact_layout(void){
    int w=BOARD_DISPLAY_WIDTH,h=BOARD_DISPLAY_HEIGHT,body=h-44;bool tiny=h<400;
    lv_obj_t *pages[]={ui.music_page,ui.timer_page,ui.codex.page,ui.claude.page,ui.chooser,ui.empty,ui.details};
    for(unsigned i=0;i<sizeof(pages)/sizeof(pages[0]);++i){place(pages[i],0,0,w,body);scrollable(pages[i]);fit_labels(pages[i],w);}
    place(ui.monitor_header,0,0,w,52);lv_obj_set_pos(ui.mascot,4,0);
    small_text(ui.monitor_title,60,7,w-72,26,&buddy_language_font_20);visible(ui.host,false);visible(ui.date,false);visible(ui.dot,false);
    small_text(ui.clock,w-112,10,100,24,&buddy_language_font_20);visible(ui.clock,false);
    if(tiny){visible(ui.mascot,false);small_text(ui.monitor_title,12,7,w-170,24,&buddy_language_font_16);}
    small_text(ui.status,tiny?12:60,32,tiny?w-170:w-72,18,&buddy_language_font_14);lv_obj_set_style_text_align(ui.status,LV_TEXT_ALIGN_LEFT,0);
    place(ui.music_link,w-150,0,146,48);small_text(ui.music_link_title,8,23,130,22,&buddy_language_font_14);
    small_text(lv_obj_get_child(ui.music_link,0),8,3,130,20,&buddy_language_font_14);
    for(int i=0;i<3;++i)visible(ui.music_link_bars[i],false);
    place(ui.overview,12,56,w-24,body-56);scrollable(ui.overview);
    // Monitor cards stack full width and scroll; memory tiles pair up where they fit.
    int W=w-24,ch=tiny?100:120,tile=tiny?84:96;
    channel_layout(&ui.cpu,0,0,W,ch,tiny?76:96,&buddy_language_font_24,&buddy_language_font_24);
    channel_layout(&ui.gpu,0,ch+10,W,ch,tiny?76:96,&buddy_language_font_24,&buddy_language_font_24);
    int ty=2*ch+20;
    if(tiny){for(int i=0;i<3;++i)tile_layout(i,0,ty+i*(tile+10),W,tile);ty+=3*(tile+10);}
    else{tile_layout(0,0,ty,(W-10)/2,tile);tile_layout(1,(W-10)/2+10,ty,(W-10)/2,tile);tile_layout(2,0,ty+tile+10,W,tile);ty+=2*(tile+10);}
    chart_layout(0,ty,W,tiny?90:110);
    // Details and provisioning messages remain readable by vertical scrolling.
    place(ui.details,12,56,w-24,body-56);
    for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.details);++i){lv_obj_t *o=lv_obj_get_child(ui.details,i);if(lv_obj_get_x(o)>=490){lv_obj_set_x(o,0);lv_obj_set_y(o,lv_obj_get_y(o)+290);}}
    fit_labels(ui.details,w-24);
    for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.details);++i){lv_obj_t *o=lv_obj_get_child(ui.details,i);if(lv_obj_check_type(o,&lv_label_class)){lv_obj_set_style_text_font(o,&buddy_language_font_16,0);}}
    place(ui.empty,12,56,w-24,body-56);small_text(ui.empty_title,0,12,w-24,70,&buddy_language_font_24);small_text(ui.empty_body,0,90,w-24,170,&buddy_language_font_16);small_text(ui.empty_network,0,275,w-24,54,&buddy_language_font_14);
    place(ui.clock_page,0,0,w,body);small_text(ui.clock_large,12,tiny?12:115,w-24,tiny?64:96,tiny?&buddy_language_font_48:&buddy_clock_font);small_text(ui.clock_date,12,tiny?78:215,w-24,28,&buddy_language_font_16);
    place(ui.weather_row,12,tiny?109:250,w-24,28);small_text(ui.weather_note,12,tiny?174:397,w-24,22,&buddy_language_font_14);lv_obj_set_pos(ui.clock_mascot,(w-96)/2,tiny?105:286);if(tiny){lv_obj_set_pos(ui.clock_mascot,w-96,96);lv_obj_set_width(ui.weather_row,w-116);}
    // Music transport stays in the first viewport, even on CoreS3; volume scrolls in below.
    fit_labels(ui.music_page,w);visible(ui.music_logo,false);small_text(ui.music_heading,12,8,w-150,28,&buddy_language_font_20);
    small_text(ui.music_status,w-142,12,130,22,&buddy_language_font_14);
    if(tiny){visible(ui.music_cover,false);place(ui.music_meta,12,40,w-24,52);ui.music_title_lines=1;
        lv_obj_set_style_text_font(ui.music_title,&buddy_language_font_20,0);lv_obj_set_style_text_font(ui.music_artist,&buddy_language_font_14,0);visible(ui.music_album,false);}
    else{music_cover_layout(12,48,148);place(ui.music_meta,174,48,w-186,150);
        lv_obj_set_style_text_font(ui.music_title,&buddy_language_font_24,0);lv_obj_set_style_text_font(ui.music_artist,&buddy_language_font_16,0);lv_obj_set_style_text_font(ui.music_album,&buddy_language_font_14,0);}
    int seek=tiny?96:214;place(ui.music_progress,12,seek,w-24,6);small_text(ui.music_elapsed,12,seek+12,100,18,&buddy_language_font_14);small_text(ui.music_duration,w-112,seek+12,100,18,&buddy_language_font_14);
    if(tiny)music_transport_layout(w/2,150,52,44,40,62,120,&buddy_language_font_20,&buddy_language_font_16,&buddy_language_font_16);
    else music_transport_layout(w/2,282,64,52,44,88,170,&buddy_language_font_24,&buddy_language_font_20,&buddy_language_font_16);
    small_text(ui.music_feedback,12,tiny?182:322,w-24,36,&buddy_language_font_14);
    music_volume_layout(12,tiny?236:372,w-24,&buddy_language_font_16);
    // Timer: ring beside the steppers on square boards; CoreS3 stacks everything and scrolls.
    fit_labels(ui.timer_page,w);small_text(ui.timer_heading,12,10,w-150,28,&buddy_language_font_20);small_text(ui.timer_status,w-142,12,130,26,&buddy_language_font_14);
    if(tiny){timer_ring_layout((w-140)/2,40,140,&buddy_language_font_24,false);timer_stepper_rows(12,196,w-24,44,52);
        timer_presets_layout(12,356,w-24,44);timer_buttons_layout(12,412,w-24,48);}
    else{timer_ring_layout(12,52,190,&buddy_language_font_32,false);timer_stepper_rows(216,56,w-228,48,58);
        timer_presets_layout(12,262,w-24,44);timer_buttons_layout(12,320,w-24,52);}
    compact_agent(&ui.codex);compact_agent(&ui.claude);
    // The app chooser's grid places itself; only its heading shrinks here.
    small_text(ui.chooser_title,12,tiny?6:10,w-24,tiny?28:34,tiny?&buddy_language_font_20:&buddy_language_font_24);
    place(ui.app_transition,(w-128)/2,body-104,128,104);
    place(ui.apps_button,0,body,100,44);place(ui.details_button,100,body,w-204,44);small_text(ui.details_text,0,13,w-204,24,&buddy_language_font_14);
    place(ui.nav_button,w-100,body,100,44);small_text(ui.nav_text,0,13,88,24,&buddy_language_font_16);
    place(ui.timer_link,100,body,w-200,44);small_text(ui.timer_link_title,4,12,w-208,24,&buddy_language_font_14);place(ui.timer_link_progress,4,40,w-208,2);
}
#else
static void compact_layout(void){}
#endif
