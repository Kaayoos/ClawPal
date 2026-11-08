#pragma once
#include "lvgl.h"
#include "buddy_model.h"
#include "buddy_shortcuts.h"

/* The Shortcuts app: a grid of tiles that each run something on the PC.
 * Every call needs the LVGL lock. The page sends taps through the same
 * one-at-a-time command path as Media and reads the reply from telemetry. */
typedef bool (*buddy_shortcut_send_t)(const char *action, uint32_t value);

lv_obj_t *buddy_shortcuts_page_create(lv_obj_t *parent);
void buddy_shortcuts_page_set_sender(buddy_shortcut_send_t send);
void buddy_shortcuts_page_set_deck(const buddy_shortcut_deck_t *deck);
/* Takes ownership of a BUDDY_SHORTCUT_ICON_BYTES buffer from heap_caps_malloc;
 * frees it and returns false when the deck has moved on or the tile has no app icon. */
bool buddy_shortcuts_page_set_icon(uint32_t rev, unsigned slot, uint8_t *pixels);
void buddy_shortcuts_page_update(const buddy_stats_t *stats);
void buddy_shortcuts_page_set_connected(bool connected);
/* Clears a reply the page was waiting for when another command takes the slot. */
bool buddy_shortcuts_page_pending(void);
void buddy_shortcuts_page_tick(void);
/* A short line for Home: what needs attention, or that the deck is ready. */
const char *buddy_shortcuts_page_status(void);
