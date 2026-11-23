#pragma once
#include "lvgl.h"
#include "buddy_model.h"

/* Synced lyrics inside the Media app. The view takes the place of the cover and the track's
 * details while it is open; the transport, timeline and volume stay where they are. Lines are
 * timed on this display's own clock from the PC's reference points (telemetry music.lyrics), so
 * Wi-Fi delay never reaches them. Every call needs the LVGL lock. */

/* Builds the hidden panel and credit on the Media page. */
void buddy_lyrics_view_create(lv_obj_t *page);
/* Places them for this screen: the panel's area and the credit's line, both in page coordinates. */
void buddy_lyrics_view_place(int x, int y, int width, int height, int credit_x, int credit_y, int credit_width);
/* Takes ownership of a document from heap_caps_malloc, freeing the previous one. */
void buddy_lyrics_view_set_document(buddy_lyrics_t *lyrics);
/* The latest telemetry; title and artist label the open view. */
void buddy_lyrics_view_update(const buddy_lyrics_sync_t *sync, const char *title, const char *artist);
void buddy_lyrics_view_set_open(bool open);
bool buddy_lyrics_view_is_open(void);
/* The credit gives way while the Media page shows a command's feedback in the same band. */
void buddy_lyrics_view_set_credit_hidden(bool hidden);
