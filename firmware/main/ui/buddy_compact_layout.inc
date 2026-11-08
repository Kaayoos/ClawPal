/* Compact compositions share the same objects, callbacks and state as the
 * wide layout. Content scrolls vertically; the Apps/Home footer stays fixed.
 * The 320 x 240 screen has 196 pixels above the footer, so each of its views is
 * composed to that height and scrolls a whole view at a time. */
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
/* An invisible mark where a scrolled view begins. */
static lv_obj_t *view_start(lv_obj_t *page,int y){lv_obj_t *o=box(page,0,y,2,2);lv_obj_clear_flag(o,LV_OBJ_FLAG_CLICKABLE);return o;}
/* Scrolling settles on the given children only, one view per swipe. */
static void snap_views(lv_obj_t *page,lv_obj_t *const *stops,unsigned count){
    for(uint32_t i=0;i<lv_obj_get_child_cnt(page);++i)lv_obj_clear_flag(lv_obj_get_child(page,i),LV_OBJ_FLAG_SNAPPABLE);
    for(unsigned i=0;i<count;++i)lv_obj_add_flag(stops[i],LV_OBJ_FLAG_SNAPPABLE);
    lv_obj_set_scroll_snap_y(page,LV_SCROLL_SNAP_START);lv_obj_add_flag(page,LV_OBJ_FLAG_SCROLL_ONE);
}
/* Name and the Usage / Tasks switch share the top row; below it each usage card, or two task
 * rows, fill the screen. A swipe brings the second card with the account status under it. */
static void tiny_agent(agent_view_t *view){
    int w=BOARD_DISPLAY_WIDTH,W=w-24,tab=(138-8)/2,top=46,card=140,step=152;
    // Task rows: title, then project and age, then the state chip on its own line.
    int title_h=lv_font_get_line_height(&buddy_language_font_16),detail_h=lv_font_get_line_height(&buddy_language_font_14);
    int details_y=4+title_h,chip_y=details_y+detail_h+1,row=chip_y+detail_h+6+3,row_step=row+2;
    small_text(view->heading,12,9,w-170,26,&buddy_language_font_20);visible(view->plan,false);visible(view->mascot,false);
    place(view->tab_track,w-150,6,138,32);
    for(int i=0;i<2;++i){place(view->tabs[i],4+i*tab,3,tab,26);lv_obj_set_width(view->tab_labels[i],tab-6);
        lv_obj_set_style_text_font(view->tab_labels[i],&buddy_language_font_14,0);lv_obj_align(view->tab_labels[i],LV_ALIGN_CENTER,0,0);}
    place(view->usage_body,12,top,W,step+card);place(view->tasks_body,12,top,W,2*row_step+row);
    // The gauge moves to the card's edge so the countdown keeps its whole line.
    for(int i=0;i<2;++i){agent_card_layout(view,i,W,card,88,&buddy_language_font_24,&buddy_language_font_14,&buddy_language_font_16,&buddy_language_font_14);
        lv_obj_set_x(view->gauges[i],16);
        lv_obj_t *lines[]={view->window_names[i],view->countdowns[i],view->resets[i],view->paces[i]};
        for(int k=0;k<4;++k){lv_obj_set_x(lines[k],118);lv_obj_set_width(lines[k],W-118-12);}}
    view->card_top=0;view->card_step=step;view->card_single_top=0;
    agent_task_layout(view,W,row,row_step,&buddy_language_font_16,&buddy_language_font_14,true);view->chip_below=true;
    for(int i=0;i<3;++i){
        lv_obj_set_pos(view->task_titles[i],36,4);lv_obj_set_width(view->task_titles[i],W-52);
        lv_obj_set_pos(view->task_dots[i],16,4+(title_h-10)/2);
        lv_obj_set_pos(view->task_details[i],36,details_y);lv_obj_set_width(view->task_details[i],W-52);
        lv_obj_align(view->task_states[i],LV_ALIGN_TOP_LEFT,36,chip_y);
    }
    lv_obj_t *titles[]={view->empty_title,view->tasks_empty_title},*bodies[]={view->empty_body,view->tasks_empty_body};
    for(int i=0;i<2;++i){small_text(titles[i],0,8,W,30,&buddy_language_font_20);small_text(bodies[i],0,42,W,96,&buddy_language_font_14);}
    int below=top+step+card+8;
    lv_obj_t *notes[]={view->status,view->age};
    for(int i=0;i<2;++i){small_text(notes[i],12,below+i*20,W,20,&buddy_language_font_14);lv_obj_set_style_text_align(notes[i],LV_TEXT_ALIGN_LEFT,0);lv_label_set_long_mode(notes[i],LV_LABEL_LONG_DOT);}
    lv_obj_t *stops[]={view_start(view->page,0),view_start(view->page,top+step-8)};snap_views(view->page,stops,2);
}
static void compact_agent(agent_view_t *view){
    int w=BOARD_DISPLAY_WIDTH;
    fit_labels(view->page,w);visible(view->logo,false);
    if(BOARD_DISPLAY_HEIGHT<400){tiny_agent(view);return;}
    small_text(view->heading,12,8,w-120,28,&buddy_language_font_20);small_text(view->plan,12,36,w-120,22,&buddy_language_font_14);
    lv_obj_set_pos(view->mascot,w-106,-15);
    place(view->tab_track,12,64,w-24,44);
    for(int i=0;i<2;++i){place(view->tabs[i],4+i*((w-32)/2),4,(w-32)/2,36);lv_obj_set_width(view->tab_labels[i],(w-32)/2-8);lv_obj_align(view->tab_labels[i],LV_ALIGN_CENTER,0,0);}
    // Usage cards stack full width; the gauge and type scale down with the screen.
    int card=128,gauge=104,body=card*2+10;
    place(view->usage_body,12,120,w-24,body);place(view->tasks_body,12,120,w-24,3*88);
    for(int i=0;i<2;++i)agent_card_layout(view,i,w-24,card,gauge,&buddy_language_font_32,
        &buddy_language_font_14,&buddy_language_font_20,&buddy_language_font_14);
    view->card_top=0;view->card_step=card+10;view->card_single_top=0;
    agent_task_layout(view,w-24,80,88,&buddy_language_font_20,&buddy_language_font_14,false);
    lv_obj_t *titles[]={view->empty_title,view->tasks_empty_title},*bodies[]={view->empty_body,view->tasks_empty_body};
    for(int i=0;i<2;++i){small_text(titles[i],0,8,w-24,60,&buddy_language_font_24);small_text(bodies[i],0,74,w-24,170,&buddy_language_font_16);}
    int below=120+body+8;
    small_text(view->status,12,below,w-24,22,&buddy_language_font_14);lv_obj_set_style_text_align(view->status,LV_TEXT_ALIGN_LEFT,0);
    small_text(view->age,12,below+24,w-24,40,&buddy_language_font_14);lv_obj_set_style_text_align(view->age,LV_TEXT_ALIGN_LEFT,0);
}
/* PC Monitor: title and connection on one line, then one card per view: CPU, GPU, both memories,
 * network and the history chart. */
static void tiny_monitor(void){
    int w=BOARD_DISPLAY_WIDTH,body=BOARD_DISPLAY_HEIGHT-44,W=w-24,top=36,view=body-top,card=view-10;
    visible(ui.mascot,false);small_text(ui.monitor_title,12,8,w-160,22,&buddy_language_font_16);
    small_text(ui.status,w-148,10,136,20,&buddy_language_font_14);lv_obj_set_style_text_align(ui.status,LV_TEXT_ALIGN_RIGHT,0);
    place(ui.overview,12,top,W,view);scrollable(ui.overview);lv_obj_set_style_pad_bottom(ui.overview,10,0);
    channel_layout(&ui.cpu,0,0,W,card,100,&buddy_language_font_24,&buddy_language_font_24);
    channel_layout(&ui.gpu,0,view,W,card,100,&buddy_language_font_24,&buddy_language_font_24);
    int tile=(card-8)/2;
    tile_layout(0,0,2*view,W,tile);tile_layout(1,0,2*view+tile+8,W,tile);
    for(int i=0;i<2;++i){lv_obj_t *value=i?ui.vram:ui.memory,*bar=i?ui.vram_bar:ui.memory_bar;
        lv_obj_set_y(value,30);lv_obj_set_height(value,26);lv_obj_set_style_text_font(value,&buddy_language_font_20,0);lv_obj_set_y(bar,tile-12);}
    tile_layout(2,0,3*view,W,card);chart_layout(0,4*view,W,card);
    lv_obj_t *stops[]={ui.cpu.button,ui.gpu.button,ui.tiles[0],ui.tiles[2],ui.chart_card,ui.game_panel};
    snap_views(ui.overview,stops,sizeof(stops)/sizeof(stops[0]));
}
/* Clock: one centred column with large numerals. There is no room for the crab here. */
static void tiny_clock(void){
    int w=BOARD_DISPLAY_WIDTH,top=22;
    visible(ui.clock_mascot,false);
    // The numerals' glyphs rise a few pixels above their line box; the gaps allow for it.
    small_text(ui.clock_large,12,top,w-24,70,&buddy_clock_font);small_text(ui.clock_date,12,top+82,w-24,22,&buddy_language_font_16);
    place(ui.weather_row,12,top+110,w-24,28);small_text(ui.weather_note,12,top+140,w-24,20,&buddy_language_font_14);
}
/* Media fits one screen: source and state, the track, the timeline with its times beside it,
 * transport and volume. The sliders' touch bands are narrowed so they don't overlap the transport,
 * and the bottom band holds only command feedback, so a stray swipe there changes nothing. */
static void tiny_music(void){
    int w=BOARD_DISPLAY_WIDTH;
    visible(ui.music_cover,false);visible(ui.music_album,false);ui.music_title_lines=1;
    small_text(ui.music_heading,12,6,w-152,18,&buddy_language_font_14);small_text(ui.music_status,w-142,6,130,18,&buddy_language_font_14);
    place(ui.music_meta,12,26,w-24,52);
    lv_obj_set_style_text_font(ui.music_title,&buddy_language_font_20,0);lv_obj_set_style_text_font(ui.music_artist,&buddy_language_font_14,0);
    small_text(ui.music_elapsed,12,80,44,18,&buddy_language_font_14);lv_obj_set_style_text_align(ui.music_elapsed,LV_TEXT_ALIGN_LEFT,0);
    place(ui.music_progress,60,86,w-120,6);
    small_text(ui.music_duration,w-56,80,44,18,&buddy_language_font_14);lv_obj_set_style_text_align(ui.music_duration,LV_TEXT_ALIGN_RIGHT,0);
    music_transport_layout(w/2,124,40,32,30,58,112,&buddy_language_font_20,&buddy_language_font_16,&buddy_language_font_16);
    music_volume_layout(12,160,w-24,&buddy_language_font_16);
    lv_obj_set_size(ui.music_buttons[MUSIC_MUTE_BUTTON],36,36);lv_obj_set_pos(ui.music_buttons[MUSIC_MUTE_BUTTON],8,142);
    lv_obj_set_ext_click_area(ui.music_progress,8);lv_obj_set_ext_click_area(ui.music_volume,8);
    small_text(ui.music_feedback,12,177,w-24,18,&buddy_language_font_14);lv_obj_set_style_text_align(ui.music_feedback,LV_TEXT_ALIGN_CENTER,0);
}
/* Timer: the ring with Start and Reset beside it; a swipe brings the steppers and presets. */
static void tiny_timer(void){
    int w=BOARD_DISPLAY_WIDTH,body=BOARD_DISPLAY_HEIGHT-44,ring=136,col=ring+28,cw=w-col-12;
    small_text(ui.timer_heading,12,8,w-150,26,&buddy_language_font_20);small_text(ui.timer_status,w-142,11,130,22,&buddy_language_font_14);
    timer_ring_layout(14,46,ring,&buddy_language_font_24,false);
    for(unsigned i=0;i<2;++i){int y=i?122:68,h=i?40:46;place(ui.timer_buttons[i],col,y,cw,h);
        lv_obj_set_width(ui.timer_button_labels[i],cw-16);lv_obj_align(ui.timer_button_labels[i],LV_ALIGN_CENTER,0,0);}
    timer_stepper_rows(12,body+8,w-24,40,46);timer_presets_layout(12,body+150,w-24,36);
    lv_obj_t *stops[]={view_start(ui.timer_page,0),view_start(ui.timer_page,body),view_start(ui.timer_page,2*body-2)};
    snap_views(ui.timer_page,stops,2);
}
static void compact_layout(void){
    int w=BOARD_DISPLAY_WIDTH,h=BOARD_DISPLAY_HEIGHT,body=h-44;bool tiny=h<400;
    lv_obj_t *pages[]={ui.music_page,ui.timer_page,ui.codex.page,ui.claude.page,ui.chooser,ui.empty,ui.details};
    for(unsigned i=0;i<sizeof(pages)/sizeof(pages[0]);++i){place(pages[i],0,0,w,body);scrollable(pages[i]);fit_labels(pages[i],w);}
    place(ui.monitor_header,0,0,w,52);lv_obj_set_pos(ui.mascot,4,0);
    small_text(ui.monitor_title,60,7,w-72,26,&buddy_language_font_20);visible(ui.host,false);visible(ui.date,false);visible(ui.dot,false);
    small_text(ui.clock,w-112,10,100,24,&buddy_language_font_20);visible(ui.clock,false);
    small_text(ui.status,60,32,w-72,18,&buddy_language_font_14);lv_obj_set_style_text_align(ui.status,LV_TEXT_ALIGN_LEFT,0);
    place(ui.music_link,w-150,0,146,48);small_text(ui.music_link_title,8,23,130,22,&buddy_language_font_14);
    small_text(lv_obj_get_child(ui.music_link,0),8,3,130,20,&buddy_language_font_14);
    for(int i=0;i<3;++i)visible(ui.music_link_bars[i],false);
    int content=tiny?36:56;
    if(tiny)tiny_monitor();
    else{
        place(ui.overview,12,56,w-24,body-56);scrollable(ui.overview);
        // Monitor cards stack full width and scroll; memory tiles pair up.
        int W=w-24,ch=120,tile=96;
        channel_layout(&ui.cpu,0,0,W,ch,96,&buddy_language_font_24,&buddy_language_font_24);
        channel_layout(&ui.gpu,0,ch+10,W,ch,96,&buddy_language_font_24,&buddy_language_font_24);
        int ty=2*ch+20;
        tile_layout(0,0,ty,(W-10)/2,tile);tile_layout(1,(W-10)/2+10,ty,(W-10)/2,tile);tile_layout(2,0,ty+tile+10,W,tile);ty+=2*(tile+10);
        chart_layout(0,ty,W,110);
    }
    // Details and provisioning messages remain readable by vertical scrolling.
    place(ui.details,12,content,w-24,body-content);
    for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.details);++i){lv_obj_t *o=lv_obj_get_child(ui.details,i);if(lv_obj_get_x(o)>=490){lv_obj_set_x(o,0);lv_obj_set_y(o,lv_obj_get_y(o)+290);}}
    fit_labels(ui.details,w-24);
    for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.details);++i){lv_obj_t *o=lv_obj_get_child(ui.details,i);if(lv_obj_check_type(o,&lv_label_class)){lv_obj_set_style_text_font(o,&buddy_language_font_16,0);}}
    place(ui.empty,12,content,w-24,body-content);small_text(ui.empty_title,0,12,w-24,70,&buddy_language_font_24);small_text(ui.empty_body,0,90,w-24,170,&buddy_language_font_16);small_text(ui.empty_network,0,275,w-24,54,&buddy_language_font_14);
    place(ui.clock_page,0,0,w,body);
    if(tiny)tiny_clock();
    else{small_text(ui.clock_large,12,115,w-24,96,&buddy_clock_font);small_text(ui.clock_date,12,215,w-24,28,&buddy_language_font_16);
        place(ui.weather_row,12,250,w-24,28);small_text(ui.weather_note,12,397,w-24,22,&buddy_language_font_14);lv_obj_set_pos(ui.clock_mascot,(w-96)/2,286);}
    fit_labels(ui.music_page,w);visible(ui.music_logo,false);
    if(tiny)tiny_music();
    else{
        small_text(ui.music_heading,12,8,w-150,28,&buddy_language_font_20);small_text(ui.music_status,w-142,12,130,22,&buddy_language_font_14);
        music_cover_layout(12,48,148);place(ui.music_meta,174,48,w-186,150);
        lv_obj_set_style_text_font(ui.music_title,&buddy_language_font_24,0);lv_obj_set_style_text_font(ui.music_artist,&buddy_language_font_16,0);lv_obj_set_style_text_font(ui.music_album,&buddy_language_font_14,0);
        place(ui.music_progress,12,214,w-24,6);small_text(ui.music_elapsed,12,226,100,18,&buddy_language_font_14);small_text(ui.music_duration,w-112,226,100,18,&buddy_language_font_14);
        music_transport_layout(w/2,282,64,52,44,88,170,&buddy_language_font_24,&buddy_language_font_20,&buddy_language_font_16);
        small_text(ui.music_feedback,12,322,w-24,36,&buddy_language_font_14);
        music_volume_layout(12,372,w-24,&buddy_language_font_16);
    }
    fit_labels(ui.timer_page,w);
    if(tiny)tiny_timer();
    else{
        // Timer: ring beside the steppers.
        small_text(ui.timer_heading,12,10,w-150,28,&buddy_language_font_20);small_text(ui.timer_status,w-142,12,130,26,&buddy_language_font_14);
        timer_ring_layout(12,52,190,&buddy_language_font_32,false);timer_stepper_rows(216,56,w-228,48,58);
        timer_presets_layout(12,262,w-24,44);timer_buttons_layout(12,320,w-24,52);
    }
    compact_agent(&ui.codex);compact_agent(&ui.claude);
    // The app chooser's grid places itself; the smallest screen fits it without a heading.
    small_text(ui.chooser_title,12,10,w-24,34,&buddy_language_font_24);if(tiny)visible(ui.chooser_title,false);
    place(ui.app_transition,(w-128)/2,body-104,128,104);
    place(ui.apps_button,0,body,100,44);place(ui.details_button,100,body,w-204,44);small_text(ui.details_text,0,13,w-204,24,&buddy_language_font_14);
    place(ui.nav_button,w-100,body,100,44);small_text(ui.nav_text,0,13,88,24,&buddy_language_font_16);
    // The running-timer shortcut is a slim pill between Apps and Home.
    if(tiny){place(ui.timer_link,100,body+6,w-200,32);small_text(ui.timer_link_title,8,5,w-216,20,&buddy_language_font_14);place(ui.timer_link_progress,8,27,w-216,2);}
    else{place(ui.timer_link,100,body,w-200,44);small_text(ui.timer_link_title,4,12,w-208,24,&buddy_language_font_14);place(ui.timer_link_progress,4,40,w-208,2);}
}
#else
static void compact_layout(void){}
#endif
