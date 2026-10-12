#pragma once

#include <stddef.h>
#include <time.h>
#include "buddy_weather.h"

/* Validates the bounded conditions and optional forecasts, including units, timestamps and age. */
bool buddy_weather_parse(const char *json, size_t size, time_t now,
                         buddy_weather_t *out);
