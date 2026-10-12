#include "../main/ui/buddy_weather_animation.h"
_Static_assert(BUDDY_WEATHER_ANIMATION(0,1)==BUDDY_MASCOT_SUNNY,"Sunny day uses the sunny sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(0,0)==BUDDY_MASCOT_NIGHT,"Cloudless night uses the supplied moon sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(1,0)==BUDDY_MASCOT_IDLE,"Mostly clear night idles");
_Static_assert(BUDDY_WEATHER_ANIMATION(2,0)==BUDDY_MASCOT_CLOUDY,"Night clouds use the cloudy sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(3,1)==BUDDY_MASCOT_CLOUDY,"Overcast day uses the cloudy sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(63,0)==BUDDY_MASCOT_RAINY,"Rain uses the rainy sheet at night too");
_Static_assert(BUDDY_WEATHER_ANIMATION(51,1)==BUDDY_MASCOT_RAINY,"Drizzle uses the rainy sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(95,1)==BUDDY_MASCOT_RAINY,"Storms use the rainy sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(45,1)==BUDDY_MASCOT_IDLE,"Fog has no matching sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(73,1)==BUDDY_MASCOT_IDLE,"Snow has no matching sheet");
_Static_assert(BUDDY_WEATHER_ANIMATION(85,0)==BUDDY_MASCOT_IDLE,"Snow showers idle too");
_Static_assert(BUDDY_WEATHER_ANIMATION(100,1)==BUDDY_MASCOT_IDLE,"Unknown weather idles");
