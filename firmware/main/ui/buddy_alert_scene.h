#pragma once
#include "lvgl.h"
#include "buddy_model.h"

/* The pixel picture at the top left of a full-screen prompt, 48 x 48 cells on the crab's grid:
 * a monitor asking a question for an agent that needs you, the watched printer for a print
 * alert, a chip settling into its socket for a firmware approval and a router sending out
 * waves for a Wi-Fi change. Every call needs the LVGL lock. */
#define BUDDY_ALERT_SCENE_CELLS 48

typedef enum {
    BUDDY_ALERT_SCENE_ASK,
    BUDDY_ALERT_SCENE_PRINTER,
    BUDDY_ALERT_SCENE_FIRMWARE,
    BUDDY_ALERT_SCENE_WIFI,
} buddy_alert_scene_t;

/* `scale` is screen pixels per cell. */
lv_obj_t *buddy_alert_scene_create(lv_obj_t *parent, int scale);
/* Redraws it with cells of `scale` pixels, resizing the object to match. */
void buddy_alert_scene_set_scale(lv_obj_t *scene, int scale);
/* `ink` colours the question on the monitor (the agent's own colour); `printer` is copied. */
void buddy_alert_scene_set(lv_obj_t *scene, buddy_alert_scene_t kind, uint32_t ink, const buddy_printer_t *printer);
