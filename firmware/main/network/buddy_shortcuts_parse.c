#include "buddy_shortcuts.h"
#include "buddy_shortcut_data.h"
#include "buddy_telemetry_parse.h"
#include <string.h>

static bool optional_bool(const cJSON *object, const char *key, bool *out)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    if (!value) { *out = false; return true; }
    if (!cJSON_IsBool(value)) return false;
    *out = cJSON_IsTrue(value);
    return true;
}

static bool whole(const cJSON *object, const char *key, int low, int high, int *out)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    if (!cJSON_IsNumber(value) || value->valuedouble != value->valueint || value->valueint < low || value->valueint > high) return false;
    *out = value->valueint;
    return true;
}

static bool label(const cJSON *object, const char *key, char *out)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    out[0] = 0;
    if (!value) return true;
    if (!cJSON_IsString(value)) return false;
    size_t length = strlen(value->valuestring);
    if (length > BUDDY_SHORTCUT_LABEL_BYTES || (length && !buddy_display_text_valid(value->valuestring, length))) return false;
    memcpy(out, value->valuestring, length + 1);
    return true;
}

static bool named(const cJSON *object, const char *key, unsigned count, const char *(*name)(unsigned), uint8_t *out)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    if (!cJSON_IsString(value)) return false;
    for (unsigned i = 0; i < count; ++i)
        if (strcmp(value->valuestring, name(i)) == 0) { *out = (uint8_t)i; return true; }
    return false;
}

static const char *icon_name(unsigned i) { return buddy_shortcut_icons[i].id; }
static const char *color_name(unsigned i) { return buddy_shortcut_color_ids[i]; }

bool buddy_shortcuts_parse(const cJSON *json, buddy_shortcut_deck_t *out)
{
    buddy_shortcut_deck_t deck;
    memset(&deck, 0, sizeof(deck));
    int rev, size;
    const cJSON *tiles = cJSON_GetObjectItemCaseSensitive(json, "tiles");
    if (!cJSON_IsObject(json) || !whole(json, "rev", 1, BUDDY_SHORTCUT_MAX_REV, &rev) || !whole(json, "size", 6, BUDDY_SHORTCUT_SLOTS, &size) ||
        (size != 6 && size != 8 && size != 12 && size != 16) || !cJSON_IsArray(tiles) || cJSON_GetArraySize(tiles) > size) return false;
    deck.rev = (uint32_t)rev;
    deck.size = (uint8_t)size;
    const cJSON *item;
    cJSON_ArrayForEach(item, tiles) {
        int slot;
        if (!cJSON_IsObject(item) || !whole(item, "slot", 0, size - 1, &slot) || deck.tiles[slot].used) return false;
        buddy_shortcut_tile_t *tile = &deck.tiles[slot];
        if (!named(item, "icon", BUDDY_SHORTCUT_ICON_COUNT, icon_name, &tile->icon) ||
            !named(item, "color", BUDDY_SHORTCUT_COLOR_COUNT, color_name, &tile->color) ||
            !label(item, "label", tile->label) || !label(item, "label_on", tile->label_on) ||
            !optional_bool(item, "toggle", &tile->toggle) || !optional_bool(item, "locked_ok", &tile->locked_ok) ||
            !optional_bool(item, "app_icon", &tile->app_icon) || !optional_bool(item, "repeat", &tile->repeat)) return false;
        tile->used = true;
    }
    *out = deck;
    return true;
}
