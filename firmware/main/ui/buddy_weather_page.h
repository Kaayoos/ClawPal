#pragma once
#include "lvgl.h"
#include "buddy_weather.h"
lv_obj_t *buddy_weather_page_create(lv_obj_t *parent);
void buddy_weather_page_update(const buddy_weather_t *weather);
void buddy_weather_page_tick(void);
