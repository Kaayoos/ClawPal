#pragma once

#include <stdbool.h>
#include <stddef.h>
#include "buddy_model.h"

/* Longest track position or duration the display accepts, in seconds. */
#define BUDDY_MUSIC_MAX_SECONDS (7U * 24 * 60 * 60)

/* Validates one companion `pc.stats` WebSocket message. `payload` holds `length`
 * bytes followed by a NUL. Kept apart from networking so the build host can
 * test and fuzz it (tests/firmware_host). */
bool buddy_telemetry_parse(const char *payload, size_t length, buddy_stats_t *stats);
