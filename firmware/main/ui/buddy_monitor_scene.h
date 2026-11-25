#pragma once
#include "lvgl.h"
#include "buddy_model.h"

/* PC Monitor's pixel PC: a case with its side panel off, drawn on the crab's grid. Fans spin
 * faster with load and their rings warm with temperature, the RAM sticks fill with memory in
 * use, a strip on the graphics card fills with its memory, and the front light blinks with
 * network traffic. Offline, it stands grey and still. Every call needs the LVGL lock. */
#define BUDDY_MONITOR_SCENE_CELLS 36

lv_obj_t *buddy_monitor_scene_create(lv_obj_t *parent);
/* `scale` is screen pixels per cell; the object is BUDDY_MONITOR_SCENE_CELLS * scale square. */
void buddy_monitor_scene_place(int x, int y, int scale);
/* `stats` may be NULL before the first reading; `live` is false while the PC is offline. */
void buddy_monitor_scene_update(const buddy_stats_t *stats, bool live);
