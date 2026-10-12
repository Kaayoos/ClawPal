#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdio.h>
#include <math.h>
#ifdef __cplusplus
#define BUDDY_UNITS_INLINE static constexpr
#else
#define BUDDY_UNITS_INLINE static inline
#endif
BUDDY_UNITS_INLINE float buddy_weather_temperature(float celsius, bool fahrenheit)
{
    return fahrenheit ? celsius * 9.0f / 5.0f + 32.0f : celsius;
}
BUDDY_UNITS_INLINE const char *buddy_weather_unit(bool fahrenheit)
{
    return fahrenheit ? "°" "F" : "°" "C";
}
#undef BUDDY_UNITS_INLINE
static inline void buddy_weather_format_temperature(char *out, size_t size, float celsius, bool fahrenheit)
{
    if (!isfinite(celsius)) { snprintf(out,size,"--"); return; }
    float value=roundf(buddy_weather_temperature(celsius,fahrenheit));
    if (value==0) value=0; /* Avoid a misleading negative-zero reading. */
    snprintf(out,size,"%.0f%s",value,buddy_weather_unit(fahrenheit));
}
