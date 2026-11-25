#pragma once

#include <stdbool.h>
#include <stddef.h>
#include "buddy_model.h"

/* Validates one POST /lyrics body and fills `lyrics`:
 *   {"id":7,"kind":"synced","lines":[[12340,"First line"],[15000,null],...]}
 * `body` holds `length` bytes followed by a NUL. Synced times must not go backwards; a null
 * line is one the PC found the fonts can't draw. Kept apart from networking so the build host
 * can test it (tests/firmware_host). */
bool buddy_lyrics_parse(const char *body, size_t length, buddy_lyrics_t *lyrics);

/* Line `index`'s text, or "" past the end. */
static inline const char *buddy_lyrics_text(const buddy_lyrics_t *lyrics, unsigned index)
{
    return lyrics && index < lyrics->count ? lyrics->text + lyrics->lines[index].offset : "";
}
