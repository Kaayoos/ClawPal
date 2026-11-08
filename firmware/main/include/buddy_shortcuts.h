#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "cJSON.h"
#include "buddy_model.h"

/* The Shortcuts deck as the display sees it: how each tile looks, never what it does.
 * The PC keeps the actions and answers a tap by slot and deck revision. */
#define BUDDY_SHORTCUT_LABEL_BYTES 48
/* App icons are LV_IMG_CF_TRUE_COLOR_ALPHA: RGB565 little-endian, then 8-bit alpha. */
#define BUDDY_SHORTCUT_ICON_SIZE 48
#define BUDDY_SHORTCUT_ICON_BYTES (BUDDY_SHORTCUT_ICON_SIZE * BUDDY_SHORTCUT_ICON_SIZE * 3)
#define BUDDY_SHORTCUT_BODY_MAX 4096

typedef struct {
    bool used;
    bool toggle;     /* lights up while telemetry's "on" bit for this slot is set */
    bool locked_ok;  /* still runs while the PC is locked */
    bool app_icon;   /* the PC sends this tile's own icon; icon is the fallback */
    bool repeat;     /* repeats while held, like a volume step */
    uint8_t icon;    /* buddy_shortcut_icon_id_t */
    uint8_t color;   /* index into buddy_shortcut_colors; 0 follows the look's accent */
    char label[BUDDY_SHORTCUT_LABEL_BYTES + 1];
    char label_on[BUDDY_SHORTCUT_LABEL_BYTES + 1];
} buddy_shortcut_tile_t;

typedef struct {
    uint32_t rev;
    uint8_t size; /* 6, 8, 12 or 16 slots */
    buddy_shortcut_tile_t tiles[BUDDY_SHORTCUT_SLOTS];
} buddy_shortcut_deck_t;

bool buddy_shortcuts_parse(const cJSON *json, buddy_shortcut_deck_t *out);
/* A tap names its slot and the deck it saw, so an edited deck never runs the wrong tile. */
static inline uint32_t buddy_shortcut_control_value(uint32_t rev, unsigned slot) { return (rev << 5) | (slot & 31U); }
