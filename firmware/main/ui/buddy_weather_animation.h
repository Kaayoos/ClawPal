#pragma once
#include "buddy_mascot.h"

/* Only use a weather sheet when its scene matches. All other conditions idle. */
#define BUDDY_WEATHER_ANIMATION(code,day) \
    ((code)==0 && !(day) ? BUDDY_MASCOT_NIGHT : \
     ((code)==0 || (code)==1) && (day) ? BUDDY_MASCOT_SUNNY : \
     ((code)==2 || (code)==3) ? BUDDY_MASCOT_CLOUDY : \
     ((code)==51 || (code)==53 || (code)==55 || (code)==56 || (code)==57 || \
      (code)==61 || (code)==63 || (code)==65 || (code)==66 || (code)==67 || \
      (code)==80 || (code)==81 || (code)==82 || (code)==95 || (code)==96 || (code)==97 || (code)==99) ? \
        BUDDY_MASCOT_RAINY : BUDDY_MASCOT_IDLE)
