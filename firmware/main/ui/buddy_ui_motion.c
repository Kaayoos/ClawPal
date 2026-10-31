#include "buddy_ui_motion.h"
#include "buddy_theme.h"

static void set_translate_y(void *object, int32_t value)
{
    lv_obj_set_style_translate_y(object, (lv_coord_t)value, LV_PART_MAIN | LV_STATE_DEFAULT);
}

void buddy_ui_motion_cancel(lv_obj_t *object)
{
    if (!object) return;
    lv_anim_del(object, set_translate_y);
    set_translate_y(object, 0);
}

void buddy_ui_motion_enter(lv_obj_t *object, int16_t offset_y,
                           uint16_t duration_ms, uint16_t delay_ms)
{
    if (!object) return;
    buddy_ui_motion_cancel(object);
    /* Calmer themes travel a shorter distance over the same time. */
    offset_y = (int16_t)(offset_y * buddy_theme.motion_pct / 100);
    if (!duration_ms || !offset_y) return;

    lv_anim_t animation;
    lv_anim_init(&animation);
    lv_anim_set_var(&animation, object);
    lv_anim_set_exec_cb(&animation, set_translate_y);
    lv_anim_set_values(&animation, offset_y, 0);
    lv_anim_set_time(&animation, duration_ms);
    lv_anim_set_delay(&animation, delay_ms);
    lv_anim_set_path_cb(&animation, lv_anim_path_ease_out);
    /* lv_anim_init defaults to one pass, no playback, and immediate application
     * of the start value. No completion callback or external context is needed. */
    if (!lv_anim_start(&animation)) set_translate_y(object, 0);
}
