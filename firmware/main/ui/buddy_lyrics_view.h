#pragma once
#include "lvgl.h"
#include "buddy_model.h"

/* Synced lyrics inside the Media app. The view takes the place of the cover and the track's
 * details while it is open. Lines are timed on this display's own clock from the PC's reference
 * points (telemetry music.lyrics), so Wi-Fi delay never reaches them. They sit on a short reel,
 * the line being sung bright and the others dim, and the reel slides up as each new line
 * comes. Every call needs the LVGL lock. */

/* Builds the hidden view on the Media page. */
void buddy_lyrics_view_create(lv_obj_t *page);
/* Places it in page coordinates, with the type for its lines; heading shows the song above them. */
void buddy_lyrics_view_place(int x, int y, int width, int height, const lv_font_t *font, bool heading);
/* Takes ownership of a document from heap_caps_malloc, freeing the previous one. */
void buddy_lyrics_view_set_document(buddy_lyrics_t *lyrics);
/* The latest telemetry; title and artist label the open view. */
void buddy_lyrics_view_update(const buddy_lyrics_sync_t *sync, const char *title, const char *artist);
void buddy_lyrics_view_set_open(bool open);
bool buddy_lyrics_view_is_open(void);
/* True while the document for the current song has arrived and can be shown. */
bool buddy_lyrics_view_ready(void);
