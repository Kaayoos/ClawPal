#pragma once
#include "lvgl.h"
#include "buddy_model.h"

/* The Audio app: one fader per channel the PC reports (its output, each app playing sound and
 * its microphone), each with a mute button. Every call needs the LVGL lock. Moves are shown at
 * once and sent through the same command path as Media; telemetry then confirms them. */
typedef bool (*buddy_audio_send_t)(const char *action, uint32_t value);

/* App icons arrive from the PC as BUDDY_SHORTCUT_ICON_SIZE squares, like Shortcuts' own. */
lv_obj_t *buddy_audio_page_create(lv_obj_t *parent);
void buddy_audio_page_set_sender(buddy_audio_send_t send);
void buddy_audio_page_update(const buddy_stats_t *stats);
void buddy_audio_page_set_connected(bool connected);
/* Takes ownership of a BUDDY_SHORTCUT_ICON_BYTES buffer from heap_caps_malloc, or frees it
 * and returns false when there is no room for it. */
bool buddy_audio_page_set_icon(unsigned id, uint8_t *pixels);
/* A short line for Home and the Apps menu: the PC's volume, or why there is none. */
const char *buddy_audio_page_status(void);
