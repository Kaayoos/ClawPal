/* Compact compositions share the same objects, callbacks and state as the
 * wide layout. Content scrolls vertically; the Apps/Home footer stays fixed. */
#if BOARD_DISPLAY_WIDTH < 800
static void place(lv_obj_t *o,int x,int y,int w,int h){lv_obj_set_align(o,LV_ALIGN_TOP_LEFT);lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,h);}
static void small_text(lv_obj_t *o,int x,int y,int w,int h,const lv_font_t *font){place(o,x,y,w,h);lv_obj_set_style_text_font(o,font,0);}
static void scrollable(lv_obj_t *o){lv_obj_add_flag(o,LV_OBJ_FLAG_SCROLLABLE|LV_OBJ_FLAG_CLICKABLE);lv_obj_set_scroll_dir(o,LV_DIR_VER);lv_obj_set_scrollbar_mode(o,LV_SCROLLBAR_MODE_AUTO);lv_obj_set_style_bg_color(o,color(C_ACCENT),LV_PART_SCROLLBAR);lv_obj_set_style_width(o,3,LV_PART_SCROLLBAR);}
static void fit_button(lv_obj_t *o,int x,int y,int w,int h){place(o,x,y,w,h);lv_obj_t *label=lv_obj_get_child(o,0);if(label&&lv_obj_check_type(label,&lv_label_class)){small_text(label,4,(h-20)/2,w-8,24,&buddy_language_font_16);lv_obj_set_style_text_align(label,LV_TEXT_ALIGN_CENTER,0);}}
static void fit_labels(lv_obj_t *parent,int width){
    for(uint32_t i=0;i<lv_obj_get_child_cnt(parent);++i){lv_obj_t *o=lv_obj_get_child(parent,i);
        if(lv_obj_check_type(o,&lv_label_class)){int x=lv_obj_get_x(o);if(x>=width)x=12;lv_obj_set_x(o,x);lv_obj_set_width(o,LV_MAX(20,width-x-12));}
        else if(lv_obj_get_height(o)==1){visible(o,false);}
    }
}
static void compact_agent(bool claude){
    int w=BOARD_DISPLAY_WIDTH;
    lv_obj_t *page=claude?ui.claude_page:ui.codex_page;
    lv_obj_t *usage=claude?ui.claude_usage_body:ui.codex_usage_body,*tasks=claude?ui.claude_tasks_body:ui.codex_tasks_body;
    fit_labels(page,w);small_text(lv_obj_get_child(page,0),12,10,w-116,28,&buddy_language_font_20);
    small_text(claude?ui.claude_plan:ui.codex_plan,12,36,w-116,22,&buddy_language_font_14);
    lv_obj_set_pos(claude?ui.claude_mascot:ui.codex_mascot,w-106,-15);
    fit_button(claude?ui.claude_tabs[0]:ui.codex_tabs[0],12,64,(w-32)/2,44);
    fit_button(claude?ui.claude_tabs[1]:ui.codex_tabs[1],20+(w-32)/2,64,(w-32)/2,44);
    place(usage,0,120,w,312);place(tasks,0,120,w,312);
    for(int i=0;i<2;++i){lv_obj_t *row=claude?ui.claude_windows[i]:ui.codex_windows[i];place(row,12,i*144,w-24,136);fit_labels(row,w-24);
        small_text(claude?ui.claude_window_names[i]:ui.codex_window_names[i],0,0,w-24,24,&buddy_language_font_16);
        small_text(claude?ui.claude_remaining[i]:ui.codex_remaining[i],0,28,130,40,&buddy_language_font_32);
        small_text(lv_obj_get_child(row,2),136,42,w-160,26,&buddy_language_font_14);
        place(claude?ui.claude_bars[i]:ui.codex_bars[i],0,77,w-24,6);
        small_text(claude?ui.claude_resets[i]:ui.codex_resets[i],0,94,w-24,40,&buddy_language_font_16);
    }
    small_text(claude?ui.claude_task_summary:ui.codex_task_summary,12,0,w-24,36,&buddy_language_font_14);
    for(int i=0;i<3;++i){lv_obj_t *row=claude?ui.claude_task_rows[i]:ui.codex_task_rows[i];place(row,12,40+i*87,w-24,85);fit_labels(row,w-24);
        small_text(claude?ui.claude_task_titles[i]:ui.codex_task_titles[i],0,0,w-24,24,&buddy_language_font_16);
        small_text(claude?ui.claude_task_projects[i]:ui.codex_task_projects[i],0,26,w-24,24,&buddy_language_font_14);
        small_text(claude?ui.claude_task_states[i]:ui.codex_task_states[i],w-152,53,126,22,&buddy_language_font_14);
        small_text(claude?ui.claude_task_ages[i]:ui.codex_task_ages[i],0,53,w-166,22,&buddy_language_font_14);
    }
    lv_obj_t *titles[]={claude?ui.claude_empty_title:ui.codex_empty_title,claude?ui.claude_tasks_empty_title:ui.codex_tasks_empty_title};
    lv_obj_t *bodies[]={claude?ui.claude_empty_body:ui.codex_empty_body,claude?ui.claude_tasks_empty_body:ui.codex_tasks_empty_body};
    for(int i=0;i<2;++i){small_text(titles[i],12,40,w-24,60,&buddy_language_font_24);small_text(bodies[i],12,110,w-24,190,&buddy_language_font_16);}
    small_text(claude?ui.claude_status:ui.codex_status,12,430,w-24,36,&buddy_language_font_14);
    small_text(claude?ui.claude_age:ui.codex_age,12,470,w-24,40,&buddy_language_font_14);
}
static void compact_layout(void){
    int w=BOARD_DISPLAY_WIDTH,h=BOARD_DISPLAY_HEIGHT,body=h-44;bool tiny=h<400;
    lv_obj_t *pages[]={ui.music_page,ui.timer_page,ui.codex_page,ui.claude_page,ui.chooser,ui.empty,ui.details};
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
    int cw=(w-36)/2;
    channel_t *channels[]={&ui.cpu,&ui.gpu};
    for(int i=0;i<2;++i){channel_t *c=channels[i];visible(lv_obj_get_child(c->button,2),false);visible(lv_obj_get_child(c->button,4),false);place(c->button,i*(cw+12),0,cw,134);fit_labels(c->button,cw);
        small_text(c->name,0,0,cw,22,&buddy_language_font_16);small_text(c->usage,0,25,cw,43,&buddy_language_font_32);
        small_text(c->temperature,0,71,cw,24,&buddy_language_font_16);small_text(c->model,0,98,cw,20,&buddy_language_font_14);place(c->bar,0,126,cw,5);place(c->selection,0,132,cw,2);}
    // Reflow the secondary readings into three full-width rows.
    for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.overview);++i){lv_obj_t *o=lv_obj_get_child(ui.overview,i);int x=lv_obj_get_x(o),y=lv_obj_get_y(o);
        if(o==ui.cpu.button||o==ui.gpu.button||o==ui.chart||o==ui.game_panel||o==ui.chart_title)continue;
        if(lv_obj_check_type(o,&lv_label_class)&&y>=151&&y<240){int group=x>=547?2:x>=278?1:0;int oldx=group==2?547:group==1?278:0;small_text(o,x-oldx,148+group*92+(y-151),w-24-(x-oldx),28,&buddy_language_font_16);}
        else if(!lv_obj_check_type(o,&lv_label_class))visible(o,false);
    }
    small_text(ui.chart_title,0,432,w-24,22,&buddy_language_font_14);place(ui.chart,0,462,w-24,64);
    place(ui.game_panel,0,424,w-24,115);small_text(ui.game_title,0,0,w-24,28,&buddy_language_font_16);small_text(ui.game_status,0,34,w-120,54,&buddy_language_font_14);small_text(ui.game_fps,w-116,35,92,42,&buddy_language_font_32);small_text(lv_obj_get_child(ui.game_panel,3),w-116,80,92,24,&buddy_language_font_14);
    // Details and provisioning messages remain readable by vertical scrolling.
    place(ui.details,12,56,w-24,body-56);
    for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.details);++i){lv_obj_t *o=lv_obj_get_child(ui.details,i);if(lv_obj_get_x(o)>=490){lv_obj_set_x(o,0);lv_obj_set_y(o,lv_obj_get_y(o)+290);}}
    fit_labels(ui.details,w-24);
    for(uint32_t i=0;i<lv_obj_get_child_cnt(ui.details);++i){lv_obj_t *o=lv_obj_get_child(ui.details,i);if(lv_obj_check_type(o,&lv_label_class)){lv_obj_set_style_text_font(o,&buddy_language_font_16,0);}}
    place(ui.empty,12,56,w-24,body-56);small_text(ui.empty_title,0,12,w-24,70,&buddy_language_font_24);small_text(ui.empty_body,0,90,w-24,170,&buddy_language_font_16);small_text(ui.empty_network,0,275,w-24,54,&buddy_language_font_14);
    place(ui.clock_page,0,0,w,body);small_text(ui.clock_large,12,tiny?12:115,w-24,tiny?64:96,tiny?&buddy_language_font_48:&buddy_clock_font);small_text(ui.clock_date,12,tiny?78:215,w-24,28,&buddy_language_font_16);
    place(ui.weather_row,12,tiny?109:250,w-24,28);small_text(ui.weather_note,12,tiny?174:397,w-24,22,&buddy_language_font_14);lv_obj_set_pos(ui.clock_mascot,(w-96)/2,tiny?105:286);if(tiny){lv_obj_set_pos(ui.clock_mascot,w-96,96);lv_obj_set_width(ui.weather_row,w-116);}
    // Music playback buttons are in the first viewport, even on CoreS3.
    fit_labels(ui.music_page,w);visible(ui.music_mascot,false);small_text(lv_obj_get_child(ui.music_page,1),12,8,w-112,28,&buddy_language_font_20);
    small_text(ui.music_status,12,tiny?105:44,w-24,22,&buddy_language_font_14);lv_obj_set_style_text_align(ui.music_status,LV_TEXT_ALIGN_LEFT,0);
    lv_obj_set_pos(ui.music_buddy,w-108,tiny?0:100);small_text(ui.music_title,12,tiny?39:100,w-130,tiny?28:80,tiny?&buddy_language_font_20:&buddy_language_font_32);
    small_text(ui.music_artist,12,tiny?73:190,w-130,28,&buddy_language_font_16);small_text(ui.music_album,12,228,w-24,28,&buddy_language_font_16);visible(ui.music_album,!tiny);
    place(ui.music_progress,12,tiny?129:290,w-24,4);small_text(ui.music_elapsed,12,tiny?134:300,90,18,&buddy_language_font_14);small_text(ui.music_duration,w-102,tiny?134:300,90,18,&buddy_language_font_14);
    small_text(ui.music_feedback,12,tiny?199:324,w-24,36,&buddy_language_font_14);
    for(int i=0;i<3;++i)fit_button(ui.music_buttons[i],12+i*((w-32)/3+4),tiny?152:370,(w-32)/3,44);
    // Timer editor keeps 44px targets. CoreS3 stacks the three unit controls.
    fit_labels(ui.timer_page,w);small_text(lv_obj_get_child(ui.timer_page,0),12,10,w-130,28,&buddy_language_font_20);small_text(ui.timer_status,w-132,12,120,26,&buddy_language_font_14);
    lv_obj_set_pos(ui.timer_mascot,w-108,26);small_text(ui.timer_time,12,65,w-130,54,&buddy_language_font_32);place(ui.timer_progress,12,123,w-24,5);
    for(int i=0;i<3;++i){int x=tiny?12:12+i*((w-32)/3+4),y=tiny?149+i*68:210,cw2=tiny?w-24:(w-32)/3;
        // The unit caption is immediately before its value in the construction order.
        lv_obj_t *caption=lv_obj_get_child(ui.timer_page,6+i*4);small_text(caption,x,tiny?y:y-30,tiny?90:cw2,24,&buddy_language_font_14);
        small_text(ui.timer_units[i],tiny?130:x+44,y+10,tiny?50:cw2-88,30,&buddy_language_font_20);
        fit_button(ui.timer_adjust[i*2],tiny?82:x,y,44,44);fit_button(ui.timer_adjust[i*2+1],tiny?w-60:x+cw2-44,y,44,44);
    }
    for(int i=0;i<2;++i)fit_button(ui.timer_buttons[i],12+i*((w-32)/2+8),tiny?359:330,(w-32)/2,48);
    compact_agent(false);compact_agent(true);
    // App chooser is a touch list, not a shrunken desktop grid.
    small_text(lv_obj_get_child(ui.chooser,0),12,12,w-24,32,&buddy_language_font_24);small_text(lv_obj_get_child(ui.chooser,1),12,48,w-24,38,&buddy_language_font_14);
    for(int i=0;i<BUDDY_APP_COUNT;++i){lv_obj_t *row=ui.app_rows[i];place(row,12,88+i*64,w-24,60);small_text(lv_obj_get_child(row,1),60,4,w-84,26,&buddy_language_font_20);small_text(ui.app_hint[i],60,33,w-84,20,&buddy_language_font_14);visible(ui.app_description[i],false);visible(ui.app_current[i],false);}
    place(ui.app_transition,(w-128)/2,body-104,128,104);
    place(ui.apps_button,0,body,100,44);place(ui.details_button,100,body,w-204,44);small_text(ui.details_text,0,13,w-204,24,&buddy_language_font_14);
    place(ui.nav_button,w-100,body,100,44);small_text(ui.nav_text,0,13,88,24,&buddy_language_font_16);
    place(ui.timer_link,100,body,w-200,44);small_text(ui.timer_link_title,4,12,w-208,24,&buddy_language_font_14);place(ui.timer_link_progress,4,40,w-208,2);
}
#else
static void compact_layout(void){}
#endif
