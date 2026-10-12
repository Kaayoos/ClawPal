#pragma once

#include <stdint.h>

#include "lvgl.h"

/* Call under the LVGL lock with a live object. The helper owns the object's
 * main/default translate_y style; its layout position and input flags stay
 * unchanged. A new entrance replaces the helper's previous animation. */
void buddy_ui_motion_enter(lv_obj_t *object, int16_t offset_y,
                           uint16_t duration_ms, uint16_t delay_ms);

/* Cancel only this helper's animation and restore translate_y to zero.
 * NULL is a no-op. Call before hiding an object to stop unnecessary motion. */
void buddy_ui_motion_cancel(lv_obj_t *object);
