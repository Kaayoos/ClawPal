#include "buddy_telemetry_parse.h"

#include <limits.h>
#include <math.h>
#include <string.h>

#include "cJSON.h"

static bool valid_display_utf8(const char *value, size_t length)
{
    for (size_t i = 0; i < length;) {
        unsigned char first = (unsigned char)value[i++];
        uint32_t point;
        unsigned remaining;
        uint32_t minimum;
        if (first < 0x80) {
            point = first;
            remaining = 0;
            minimum = 0;
        } else if (first >= 0xC2 && first <= 0xDF) {
            point = first & 0x1F;
            remaining = 1;
            minimum = 0x80;
        } else if (first >= 0xE0 && first <= 0xEF) {
            point = first & 0x0F;
            remaining = 2;
            minimum = 0x800;
        } else if (first >= 0xF0 && first <= 0xF4) {
            point = first & 0x07;
            remaining = 3;
            minimum = 0x10000;
        } else return false;
        if (i + remaining > length) return false;
        while (remaining--) {
            unsigned char next = (unsigned char)value[i++];
            if ((next & 0xC0) != 0x80) return false;
            point = (point << 6) | (next & 0x3F);
        }
        if (point < minimum || point > 0x10FFFF ||
            (point >= 0xD800 && point <= 0xDFFF) || point < 32 ||
            (point >= 0x7F && point <= 0x9F) || point == 0x2028 || point == 0x2029) return false;
    }
    return true;
}

static bool copy_json_string(const cJSON *object, const char *key, char *out, size_t size)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    if (!value || cJSON_IsNull(value)) {
        out[0] = '\0';
        return true;
    }
    if (!cJSON_IsString(value) || !value->valuestring) return false;
    size_t length = strlen(value->valuestring);
    if (length >= size || !valid_display_utf8(value->valuestring, length)) return false;
    strlcpy(out, value->valuestring, size);
    return true;
}

static bool get_metric(const cJSON *object, const char *key, double min, double max, double *out)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    if (!value || cJSON_IsNull(value)) {
        *out = NAN;
        return true;
    }
    if (!cJSON_IsNumber(value) || !isfinite(value->valuedouble) ||
        value->valuedouble < min || value->valuedouble > max) return false;
    *out = value->valuedouble;
    return true;
}

static bool get_float_metric(const cJSON *object, const char *key, double min, double max, float *out)
{
    double value;
    if (!get_metric(object, key, min, max, &value)) return false;
    *out = (float)value;
    return true;
}

static bool get_uint64(const cJSON *object, const char *key, uint64_t *out)
{
    double value;
    if (!get_metric(object, key, 0, 9007199254740991.0, &value) ||
        !isfinite(value) || floor(value) != value) return false;
    *out = (uint64_t)value;
    return true;
}

static bool get_optional_boolean(const cJSON *object, const char *key, bool *out)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    if (!value) {
        *out = false;
        return true;
    }
    if (!cJSON_IsBool(value)) return false;
    *out = cJSON_IsTrue(value);
    return true;
}

static bool get_optional_uint32(const cJSON *object, const char *key, uint32_t maximum, uint32_t *out)
{
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(object, key);
    if (!value) {
        *out = 0;
        return true;
    }
    if (!cJSON_IsNumber(value) || !isfinite(value->valuedouble) || value->valuedouble < 0 ||
        value->valuedouble > maximum || floor(value->valuedouble) != value->valuedouble) return false;
    *out = (uint32_t)value->valuedouble;
    return true;
}

static bool parse_music(const cJSON *root, buddy_music_t *music)
{
    memset(music, 0, sizeof(*music));
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(root, "music");
    if (!value || cJSON_IsNull(value)) return true;
    return cJSON_IsObject(value) &&
           get_optional_boolean(value, "running", &music->running) &&
           get_optional_boolean(value, "available", &music->available) &&
           get_optional_boolean(value, "playing", &music->playing) &&
           get_optional_boolean(value, "can_play_pause", &music->can_play_pause) &&
           get_optional_boolean(value, "can_next", &music->can_next) &&
           get_optional_boolean(value, "can_previous", &music->can_previous) &&
           copy_json_string(value, "title", music->title, sizeof(music->title)) &&
           copy_json_string(value, "artist", music->artist, sizeof(music->artist)) &&
           copy_json_string(value, "album", music->album, sizeof(music->album)) &&
           get_optional_uint32(value, "position_s", BUDDY_MUSIC_MAX_SECONDS, &music->position_s) &&
           get_optional_uint32(value, "duration_s", BUDDY_MUSIC_MAX_SECONDS, &music->duration_s) &&
           get_optional_uint32(value, "control_id", INT_MAX, &music->control_id) &&
           get_optional_boolean(value, "control_ok", &music->control_ok) &&
           copy_json_string(value, "control_error", music->control_error, sizeof(music->control_error)) &&
           get_optional_boolean(value, "can_shuffle", &music->can_shuffle) &&
           get_optional_boolean(value, "shuffle", &music->shuffle) &&
           get_optional_boolean(value, "can_repeat", &music->can_repeat) &&
           get_optional_uint32(value, "repeat", 2, &music->repeat) &&
           get_optional_boolean(value, "can_seek", &music->can_seek) &&
           get_optional_boolean(value, "can_volume", &music->can_volume) &&
           get_optional_uint32(value, "volume", 100, &music->volume) &&
           get_optional_boolean(value, "muted", &music->muted) &&
           get_optional_uint32(value, "art_color", 0xFFFFFF, &music->art_color);
}

static bool parse_codex_window(const cJSON *codex, const char *key, buddy_codex_window_t *window)
{
    memset(window, 0, sizeof(*window));
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(codex, key);
    if (!value || cJSON_IsNull(value)) return true;
    /* Present Codex objects have a complete typed schema. Availability, not
     * the numeric value, distinguishes an absent limit from zero remaining. */
    return cJSON_IsObject(value) &&
           cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(value, "available")) &&
           get_optional_boolean(value, "available", &window->available) &&
           cJSON_IsNumber(cJSON_GetObjectItemCaseSensitive(value, "remaining_percent")) &&
           get_float_metric(value, "remaining_percent", 0, 100, &window->remaining_percent) &&
           cJSON_IsNumber(cJSON_GetObjectItemCaseSensitive(value, "window_minutes")) &&
           get_optional_uint32(value, "window_minutes", 525600, &window->window_minutes) &&
           cJSON_IsString(cJSON_GetObjectItemCaseSensitive(value, "reset_text")) &&
           copy_json_string(value, "reset_text", window->reset_text, sizeof(window->reset_text)) &&
           get_optional_uint32(value,"resets_at",UINT32_MAX,&window->resets_at);
}

static bool parse_codex_task(const cJSON *value, buddy_codex_task_t *task)
{
    if (!(cJSON_IsObject(value) &&
          cJSON_IsString(cJSON_GetObjectItemCaseSensitive(value, "title")) &&
          copy_json_string(value, "title", task->title, sizeof(task->title)) &&
          cJSON_IsString(cJSON_GetObjectItemCaseSensitive(value, "project")) &&
          copy_json_string(value, "project", task->project, sizeof(task->project)) &&
          cJSON_IsString(cJSON_GetObjectItemCaseSensitive(value, "state")) &&
          copy_json_string(value, "state", task->state, sizeof(task->state)) &&
          cJSON_IsNumber(cJSON_GetObjectItemCaseSensitive(value, "age_s")) &&
          get_optional_uint32(value, "age_s", 31536000, &task->age_s))) return false;
    return strcmp(task->state, "working") == 0 || strcmp(task->state, "completed") == 0 ||
           strcmp(task->state, "interrupted") == 0 || strcmp(task->state, "recent") == 0 ||
           strcmp(task->state, "unknown") == 0 || strcmp(task->state, "waiting") == 0;
}

static bool parse_codex_activity(const cJSON *codex, buddy_codex_activity_t *activity)
{
    memset(activity, 0, sizeof(*activity));
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(codex, "activity");
    if (!value || cJSON_IsNull(value)) return true;
    if (!(cJSON_IsObject(value) &&
          cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(value, "available")) &&
          get_optional_boolean(value, "available", &activity->available) &&
          cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(value, "stale")) &&
          get_optional_boolean(value, "stale", &activity->stale) &&
          cJSON_IsString(cJSON_GetObjectItemCaseSensitive(value, "status")) &&
          copy_json_string(value, "status", activity->status, sizeof(activity->status)) &&
          cJSON_IsNumber(cJSON_GetObjectItemCaseSensitive(value, "age_s")) &&
          get_optional_uint32(value, "age_s", 31536000, &activity->age_s) &&
          cJSON_IsNumber(cJSON_GetObjectItemCaseSensitive(value, "working_count")) &&
          get_optional_uint32(value, "working_count", 1000, &activity->working_count) &&
          cJSON_IsNumber(cJSON_GetObjectItemCaseSensitive(value, "total_count")) &&
          get_optional_uint32(value, "total_count", 1000, &activity->total_count))) return false;
    if (strcmp(activity->status, "local") != 0 && strcmp(activity->status, "partial") != 0 &&
        strcmp(activity->status, "waiting") != 0 && strcmp(activity->status, "unavailable") != 0 &&
        strcmp(activity->status, "closed") != 0 && strcmp(activity->status, "error") != 0) return false;

    const cJSON *items = cJSON_GetObjectItemCaseSensitive(value, "items");
    if (!cJSON_IsArray(items)) return false;
    int count = cJSON_GetArraySize(items);
    if (count < 0 || count > sizeof(activity->items) / sizeof(activity->items[0])) return false;
    const cJSON *item;
    cJSON_ArrayForEach(item, items) {
        if (!parse_codex_task(item, &activity->items[activity->item_count])) return false;
        ++activity->item_count;
    }
    return true;
}

static bool parse_codex(const cJSON *root, const char *provider, buddy_codex_t *codex)
{
    memset(codex, 0, sizeof(*codex));
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(root, provider);
    if (!value || cJSON_IsNull(value)) return true;
    return cJSON_IsObject(value) &&
           cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(value, "running")) &&
           get_optional_boolean(value, "running", &codex->running) &&
           cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(value, "available")) &&
           get_optional_boolean(value, "available", &codex->available) &&
           cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(value, "stale")) &&
           get_optional_boolean(value, "stale", &codex->stale) &&
           cJSON_IsString(cJSON_GetObjectItemCaseSensitive(value, "status")) &&
           copy_json_string(value, "status", codex->status, sizeof(codex->status)) &&
           cJSON_IsString(cJSON_GetObjectItemCaseSensitive(value, "plan")) &&
           copy_json_string(value, "plan", codex->plan, sizeof(codex->plan)) &&
           cJSON_IsNumber(cJSON_GetObjectItemCaseSensitive(value, "age_s")) &&
           get_optional_uint32(value, "age_s", 31536000, &codex->age_s) &&
           parse_codex_window(value, "primary", &codex->primary) &&
           parse_codex_window(value, "secondary", &codex->secondary) &&
           parse_codex_activity(value, &codex->activity);
}

static bool bounded_json(const char *payload, size_t length)
{
    unsigned depth = 0;
    bool in_string = false;
    bool escaped = false;
    for (size_t i = 0; i < length; ++i) {
        unsigned char c = (unsigned char)payload[i];
        if (c == '\0') return false;
        if (in_string) {
            if (escaped) {
                /* cJSON strings are NUL-terminated; do not allow an escaped
                 * NUL to conceal the rest of a decoded field. */
                if (c == 'u' && i + 4 < length && memcmp(payload + i + 1, "0000", 4) == 0) return false;
                escaped = false;
            }
            else if (c == '\\') escaped = true;
            else if (c == '"') in_string = false;
        } else if (c == '"') {
            in_string = true;
        } else if (c == '{' || c == '[') {
            if (++depth > 8) return false;
        } else if (c == '}' || c == ']') {
            if (!depth) return false;
            --depth;
        }
    }
    return !in_string && depth == 0;
}

static bool parse_game(const cJSON *root, buddy_game_t *game)
{
    memset(game, 0, sizeof(*game));
    game->fps = NAN;
    const cJSON *value = cJSON_GetObjectItemCaseSensitive(root, "game");
    if (!value || cJSON_IsNull(value)) return true;
    return cJSON_IsObject(value) && get_optional_boolean(value, "active", &game->active) &&
        copy_json_string(value, "name", game->name, sizeof(game->name)) &&
        copy_json_string(value, "status", game->status, sizeof(game->status)) &&
        get_float_metric(value, "fps", 0, 2000, &game->fps) &&
        (!game->active || game->name[0]);
}

bool buddy_telemetry_parse(const char *payload, size_t length, buddy_stats_t *stats)
{
    if (!bounded_json(payload, length)) return false;
    cJSON *root = cJSON_ParseWithLengthOpts(payload, length + 1, NULL, true);
    if (!root) return false;
    const cJSON *type = cJSON_GetObjectItemCaseSensitive(root, "type");
    const cJSON *version = cJSON_GetObjectItemCaseSensitive(root, "v");
    const cJSON *cpu = cJSON_GetObjectItemCaseSensitive(root, "cpu");
    const cJSON *gpu = cJSON_GetObjectItemCaseSensitive(root, "gpu");
    const cJSON *memory = cJSON_GetObjectItemCaseSensitive(root, "memory");
    const cJSON *network = cJSON_GetObjectItemCaseSensitive(root, "network");
    const cJSON *auto_open = cJSON_GetObjectItemCaseSensitive(root, "auto_open_apps");
    memset(stats, 0, sizeof(*stats));
    /* Older companions omit this setting and keep their existing behavior. */
    stats->auto_open_apps = !auto_open || cJSON_IsTrue(auto_open);
    uint64_t timestamp;
    bool valid = cJSON_IsObject(root) && cJSON_IsString(type) &&
                 strcmp(type->valuestring, "pc.stats") == 0 &&
                 cJSON_IsNumber(version) && version->valuedouble == 1 &&
                 cJSON_IsObject(cpu) && cJSON_IsObject(gpu) &&
                 cJSON_IsObject(memory) && cJSON_IsObject(network) &&
                 (!auto_open || cJSON_IsBool(auto_open)) &&
                 copy_json_string(root, "hostname", stats->hostname, sizeof(stats->hostname)) &&
                 copy_json_string(cpu, "name", stats->cpu_name, sizeof(stats->cpu_name)) &&
                 copy_json_string(gpu, "name", stats->gpu_name, sizeof(stats->gpu_name)) &&
                 get_uint64(root, "seq", &stats->sequence) &&
                 get_uint64(root, "timestamp", &timestamp) &&
                 get_uint64(root, "uptime_s", &stats->uptime_s) &&
                 get_float_metric(cpu, "usage", 0, 100, &stats->cpu_usage) &&
                 get_float_metric(cpu, "temperature", -100, 200, &stats->cpu_temperature) &&
                 get_float_metric(gpu, "usage", 0, 100, &stats->gpu_usage) &&
                 get_float_metric(gpu, "temperature", -100, 200, &stats->gpu_temperature) &&
                 get_float_metric(gpu, "vram_used_mb", 0, 1e9, &stats->vram_used_mb) &&
                 get_float_metric(gpu, "vram_total_mb", 0, 1e9, &stats->vram_total_mb) &&
                 get_float_metric(memory, "usage", 0, 100, &stats->memory_usage) &&
                 get_float_metric(memory, "used_mb", 0, 1e9, &stats->memory_used_mb) &&
                 get_float_metric(memory, "total_mb", 0, 1e9, &stats->memory_total_mb) &&
                 get_metric(network, "rx_bps", 0, 1e15, &stats->network_rx_bps) &&
                 get_metric(network, "tx_bps", 0, 1e15, &stats->network_tx_bps) &&
                 parse_music(root, &stats->music) &&
                 parse_codex(root, "codex", &stats->codex) && parse_codex(root, "claude", &stats->claude) && parse_game(root, &stats->game);
    if (valid) stats->timestamp_ms = (int64_t)timestamp;
    cJSON_Delete(root);
    return valid;
}
