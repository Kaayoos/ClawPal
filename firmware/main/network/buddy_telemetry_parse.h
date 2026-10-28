#pragma once

#include <stdbool.h>
#include <stddef.h>
#include "buddy_model.h"

/* Validates one companion `pc.stats` WebSocket message. `payload` holds `length`
 * bytes followed by a NUL. Kept apart from networking so the build host can
 * test and fuzz it (tests/firmware_host). */
bool buddy_telemetry_parse(const char *payload, size_t length, buddy_stats_t *stats);
