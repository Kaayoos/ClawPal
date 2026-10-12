#include "buddy_weather_units.h"
static_assert(buddy_weather_temperature(0,true)==32,"freezing point");
static_assert(buddy_weather_temperature(100,true)==212,"boiling point");
static_assert(buddy_weather_temperature(-40,true)==-40,"negative crossover");
static_assert(buddy_weather_temperature(-10,true)==14,"negative Celsius conversion");
static_assert(buddy_weather_temperature(20,true)==68,"room temperature");
static_assert(buddy_weather_temperature(7.5f,false)==7.5f,"Celsius remains unchanged");
static_assert(buddy_weather_temperature(-12.5f,false)==-12.5f,"negative Celsius unchanged");
static_assert(buddy_weather_unit(true)[2]=='F' && buddy_weather_unit(false)[2]=='C',"units agree with values");
