#include "buddy_lyrics_parse.h"
#include "buddy_telemetry_parse.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "cJSON.h"

/* Arrays and objects nest three deep at most: the body, its lines, one line. */
static bool shallow(const char *body, size_t length)
{
    unsigned depth = 0;
    bool in_string = false, escaped = false;
    for (size_t i = 0; i < length; ++i) {
        char c = body[i];
        if (c == '\0') return false;
        if (in_string) {
            if (escaped) {
                /* cJSON strings end at NUL; an escaped one must not hide the rest of a line. */
                if (c == 'u' && i + 4 < length && memcmp(body + i + 1, "0000", 4) == 0) return false;
                escaped = false;
            } else if (c == '\\') escaped = true;
            else if (c == '"') in_string = false;
        } else if (c == '"') in_string = true;
        else if (c == '{' || c == '[') { if (++depth > 3) return false; }
        else if (c == '}' || c == ']') { if (!depth) return false; --depth; }
    }
    return !in_string && depth == 0;
}

bool buddy_lyrics_parse(const char *body, size_t length, buddy_lyrics_t *lyrics)
{
    if (!body || !lyrics || length == 0 || length > BUDDY_LYRICS_BODY_MAX || !shallow(body, length)) return false;
    cJSON *root = cJSON_ParseWithLengthOpts(body, length + 1, NULL, true);
    if (!root) return false;
    memset(lyrics, 0, offsetof(buddy_lyrics_t, lines));
    const cJSON *id = cJSON_GetObjectItemCaseSensitive(root, "id");
    const cJSON *kind = cJSON_GetObjectItemCaseSensitive(root, "kind");
    const cJSON *lines = cJSON_GetObjectItemCaseSensitive(root, "lines");
    bool valid = cJSON_IsObject(root) && cJSON_IsNumber(id) && isfinite(id->valuedouble) && id->valuedouble >= 1 &&
                 id->valuedouble <= INT_MAX && floor(id->valuedouble) == id->valuedouble &&
                 cJSON_IsString(kind) && (strcmp(kind->valuestring, "synced") == 0 || strcmp(kind->valuestring, "plain") == 0) &&
                 cJSON_IsArray(lines) && cJSON_GetArraySize(lines) >= 1 && cJSON_GetArraySize(lines) <= BUDDY_LYRICS_MAX_LINES;
    if (valid) {
        lyrics->id = (uint32_t)id->valuedouble;
        lyrics->synced = strcmp(kind->valuestring, "synced") == 0;
    }
    uint32_t previous = 0;
    const cJSON *line, *each = valid ? lines : (const cJSON *)0;
    cJSON_ArrayForEach(line, each) {
        const cJSON *time = cJSON_GetArrayItem(line, 0), *words = cJSON_GetArrayItem(line, 1);
        if (!cJSON_IsArray(line) || cJSON_GetArraySize(line) != 2 || !cJSON_IsNumber(time) || !isfinite(time->valuedouble) ||
            time->valuedouble < 0 || time->valuedouble > BUDDY_LYRICS_MAX_MS || floor(time->valuedouble) != time->valuedouble ||
            (!cJSON_IsString(words) && !cJSON_IsNull(words))) { valid = false; break; }
        uint32_t at = (uint32_t)time->valuedouble;
        if (lyrics->synced ? at < previous : at != 0) { valid = false; break; }
        previous = at;
        const char *text = cJSON_IsString(words) ? words->valuestring : "";
        size_t size = strlen(text);
        if (size >= BUDDY_LYRICS_LINE_BYTES || (size && !buddy_display_text_valid(text, size)) ||
            lyrics->used + size + 1 > sizeof(lyrics->text)) { valid = false; break; }
        buddy_lyric_line_t *entry = &lyrics->lines[lyrics->count++];
        entry->time_ms = at;
        entry->offset = lyrics->used;
        entry->missing = cJSON_IsNull(words);
        memcpy(lyrics->text + lyrics->used, text, size + 1);
        lyrics->used += (uint32_t)size + 1;
    }
    cJSON_Delete(root);
    if (!valid) lyrics->count = 0;
    return valid;
}
