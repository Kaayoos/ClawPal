#pragma once
#include <stdbool.h>
#include <stdint.h>
#define BUDDY_DISPLAY_IDLE_MS 60000U
typedef enum { BUDDY_DISPLAY_AWAKE, BUDDY_DISPLAY_DIM, BUDDY_DISPLAY_OFF } buddy_display_mode_t;
/* End is exclusive. A range may cross midnight; an equal range is disabled. */
#define BUDDY_NIGHT_RANGE(valid, enabled, hour, start, end) \
    ((valid) && (enabled) && (start)!=(end) && ((start)<(end) ? \
        (hour)>=(start) && (hour)<(end) : (hour)>=(start) || (hour)<(end)))
#define BUDDY_POWER_MODE(pc, night, idle, behavior, delay) \
    (!(pc) && (night) && (idle)>=BUDDY_DISPLAY_IDLE_MS ? BUDDY_DISPLAY_OFF : \
     ((behavior)==2 || ((behavior)==1 && !(pc))) && (idle)>=(delay) ? BUDDY_DISPLAY_DIM : BUDDY_DISPLAY_AWAKE)
/* Legacy/default policy assertions remain useful. */
#define BUDDY_DISPLAY_NIGHT_HOURS(valid,hour) BUDDY_NIGHT_RANGE(valid,true,hour,1,10)
#define BUDDY_DISPLAY_MODE(pc,night,idle) BUDDY_POWER_MODE(pc,night,idle,1,BUDDY_DISPLAY_IDLE_MS)
