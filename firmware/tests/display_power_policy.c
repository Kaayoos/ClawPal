#include "../main/ui/buddy_display_power.h"

_Static_assert(!BUDDY_DISPLAY_NIGHT_HOURS(true, 0), "00:59 stays outside night hours");
_Static_assert(BUDDY_DISPLAY_NIGHT_HOURS(true, 1), "01:00 begins night hours");
_Static_assert(BUDDY_DISPLAY_NIGHT_HOURS(true, 9), "09:59 is still night hours");
_Static_assert(!BUDDY_DISPLAY_NIGHT_HOURS(true, 10), "10:00 ends night hours");
_Static_assert(!BUDDY_DISPLAY_NIGHT_HOURS(false, 3), "Unknown time must not switch off the screen");
_Static_assert(BUDDY_DISPLAY_MODE(false, false, 59999) == BUDDY_DISPLAY_AWAKE, "Full minute of grace by day");
_Static_assert(BUDDY_DISPLAY_MODE(false, true, 59999) == BUDDY_DISPLAY_AWAKE, "Full minute of grace at night");
_Static_assert(BUDDY_DISPLAY_MODE(false, false, 60000) == BUDDY_DISPLAY_DIM, "Offline idle dims at one minute");
_Static_assert(BUDDY_DISPLAY_MODE(false, true, 60000) == BUDDY_DISPLAY_OFF, "Offline idle switches off at night");
_Static_assert(BUDDY_DISPLAY_MODE(true, true, UINT32_MAX) == BUDDY_DISPLAY_AWAKE, "PC connection overrides night sleep");
_Static_assert(BUDDY_DISPLAY_MODE(true, false, UINT32_MAX) == BUDDY_DISPLAY_AWAKE, "PC connection overrides dimming");
_Static_assert(BUDDY_DISPLAY_MODE(false, true, 0) == BUDDY_DISPLAY_AWAKE, "Touch reset wakes a disconnected screen at night");

_Static_assert(BUDDY_NIGHT_RANGE(true,true,23,22,7), "Night range starts before midnight");
_Static_assert(BUDDY_NIGHT_RANGE(true,true,0,22,7), "Night range continues after midnight");
_Static_assert(!BUDDY_NIGHT_RANGE(true,true,7,22,7), "Night range end is exclusive");
_Static_assert(!BUDDY_NIGHT_RANGE(true,true,12,22,7), "Daytime stays out of wrapped range");
_Static_assert(!BUDDY_NIGHT_RANGE(true,false,2,1,10), "Night-off switch disables the range");
_Static_assert(!BUDDY_NIGHT_RANGE(false,true,2,1,10), "Unsynchronized time never turns off the display");
_Static_assert(!BUDDY_NIGHT_RANGE(true,true,2,2,2), "Equal hours cannot mean accidental all-day darkness");
_Static_assert(BUDDY_POWER_MODE(true,false,120000,2,120000)==BUDDY_DISPLAY_DIM, "Always-idle mode dims with PC connected");
_Static_assert(BUDDY_POWER_MODE(true,true,120000,2,120000)==BUDDY_DISPLAY_DIM, "PC-connected night never fully powers off");
_Static_assert(BUDDY_POWER_MODE(false,false,3600000,0,60000)==BUDDY_DISPLAY_AWAKE, "Never dim mode stays bright by day");
_Static_assert(BUDDY_POWER_MODE(false,true,60000,0,60000)==BUDDY_DISPLAY_OFF, "Night sleep is independent of dimming toggle");
_Static_assert(BUDDY_POWER_MODE(false,false,119999,1,120000)==BUDDY_DISPLAY_AWAKE, "Custom delay is inclusive at the boundary only");
_Static_assert(BUDDY_POWER_MODE(false,false,120000,1,120000)==BUDDY_DISPLAY_DIM, "Custom delay dims at its boundary");
_Static_assert(BUDDY_POWER_MODE(false,true,0,2,15000)==BUDDY_DISPLAY_AWAKE, "Touch always wakes even with both policies enabled");
