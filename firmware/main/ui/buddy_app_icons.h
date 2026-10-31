#pragma once
#include "buddy_ui.h"
#include "lvgl.h"
lv_obj_t *buddy_app_icon_create(lv_obj_t *parent, buddy_app_t app, int x, int y, int size);
/* Switches every Media icon between the generic play mark and Spotify's logo. */
void buddy_app_icons_set_spotify(bool spotify);
