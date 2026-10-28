/* Host checks and a mutation fuzzer for the display's telemetry parser.
 * Included by test_parsers.c, which supplies check(). */
#include <limits.h>
#include "buddy_telemetry_parse.h"

static const char telemetry_full[] =
    "{\"type\":\"pc.stats\",\"v\":1,\"seq\":42,\"timestamp\":1760000000000,\"hostname\":\"Desk PC\",\"uptime_s\":3600,"
    "\"auto_open_apps\":false,"
    "\"cpu\":{\"name\":\"Ryzen\",\"usage\":12.5,\"temperature\":null},"
    "\"gpu\":{\"name\":\"RTX\",\"usage\":50,\"temperature\":61,\"vram_used_mb\":2048,\"vram_total_mb\":8192},"
    "\"memory\":{\"usage\":40,\"used_mb\":12800,\"total_mb\":32000},"
    "\"network\":{\"rx_bps\":1000,\"tx_bps\":250},"
    "\"music\":{\"running\":true,\"available\":true,\"playing\":true,\"can_play_pause\":true,\"can_next\":true,"
    "\"can_previous\":false,\"title\":\"Za\xc5\xbc\xc3\xb3\xc5\x82\xc4\x87\",\"artist\":\"Artist\",\"album\":\"Album\","
    "\"position_s\":30,\"duration_s\":200,\"control_id\":7,\"control_ok\":true,\"control_error\":null},"
    "\"codex\":{\"running\":true,\"available\":true,\"stale\":false,\"status\":\"ok\",\"plan\":\"pro\",\"age_s\":5,"
    "\"primary\":{\"available\":true,\"remaining_percent\":0,\"window_minutes\":300,\"reset_text\":\"Mon 14:00\",\"resets_at\":1760001000},"
    "\"secondary\":null,"
    "\"activity\":{\"available\":true,\"stale\":false,\"status\":\"local\",\"age_s\":3,\"working_count\":1,\"total_count\":2,"
    "\"items\":[{\"title\":\"Fix bug\",\"project\":\"clawpal\",\"state\":\"working\",\"age_s\":3},"
    "{\"title\":\"Review\",\"project\":\"clawpal\",\"state\":\"waiting\",\"age_s\":9}]}},"
    "\"claude\":{\"running\":false,\"available\":false,\"stale\":true,\"status\":\"waiting\",\"plan\":\"\",\"age_s\":0},"
    "\"game\":{\"active\":true,\"name\":\"Game\",\"status\":\"ok\",\"fps\":143.9}}";

static const char telemetry_legacy[] =
    "{\"type\":\"pc.stats\",\"v\":1,\"seq\":1,\"timestamp\":1,\"hostname\":\"PC\",\"uptime_s\":0,"
    "\"cpu\":{},\"gpu\":{},\"memory\":{},\"network\":{}}";

static bool telemetry(const char *json, buddy_stats_t *out)
{
    buddy_stats_t ignored;
    return buddy_telemetry_parse(json, strlen(json), out ? out : &ignored);
}

/* Replaces the first `from` in the full message with `to`. */
static bool telemetry_with(const char *from, const char *to)
{
    static char edited[sizeof(telemetry_full) + 512];
    const char *at = strstr(telemetry_full, from);
    if (!at || strlen(telemetry_full) - strlen(from) + strlen(to) >= sizeof(edited)) {
        printf("Bad telemetry edit: %s\n", from);
        exit(2);
    }
    snprintf(edited, sizeof(edited), "%.*s%s%s", (int)(at - telemetry_full), telemetry_full, to, at + strlen(from));
    return telemetry(edited, NULL);
}

static bool bounded(const char *text, size_t size) { return memchr(text, '\0', size) != NULL; }

/* Properties every accepted message must have, whatever the input bytes were. */
static bool telemetry_invariants(const buddy_stats_t *s)
{
    const buddy_codex_t *providers[] = {&s->codex, &s->claude};
    bool ok = bounded(s->hostname, sizeof(s->hostname)) && bounded(s->cpu_name, sizeof(s->cpu_name)) &&
              bounded(s->gpu_name, sizeof(s->gpu_name)) && bounded(s->music.title, sizeof(s->music.title)) &&
              bounded(s->music.artist, sizeof(s->music.artist)) && bounded(s->music.album, sizeof(s->music.album)) &&
              bounded(s->music.source, sizeof(s->music.source)) && s->music.art_id <= INT_MAX &&
              bounded(s->game.name, sizeof(s->game.name)) && (!s->game.active || s->game.name[0]) &&
              (isnan(s->cpu_usage) || (s->cpu_usage >= 0 && s->cpu_usage <= 100)) &&
              (isnan(s->game.fps) || (s->game.fps >= 0 && s->game.fps <= 2000)) &&
              s->music.duration_s <= BUDDY_MUSIC_MAX_SECONDS && s->music.repeat <= 2 && s->music.volume <= 100 &&
              s->music.art_color <= 0xFFFFFF;
    for (size_t p = 0; p < 2; ++p) {
        const buddy_codex_activity_t *a = &providers[p]->activity;
        ok = ok && bounded(providers[p]->status, sizeof(providers[p]->status)) && a->item_count <= 3 &&
             (isnan(providers[p]->primary.remaining_percent) ||
              (providers[p]->primary.remaining_percent >= 0 && providers[p]->primary.remaining_percent <= 100));
        for (uint32_t i = 0; ok && i < a->item_count; ++i)
            ok = bounded(a->items[i].title, sizeof(a->items[i].title)) && bounded(a->items[i].state, sizeof(a->items[i].state));
    }
    return ok;
}

static void telemetry_checks(void)
{
    buddy_stats_t s;
    check(telemetry(telemetry_full, &s) && s.sequence == 42 && s.timestamp_ms == 1760000000000LL &&
          !strcmp(s.hostname, "Desk PC") && fabsf(s.cpu_usage - 12.5f) < 0.01f && isnan(s.cpu_temperature) &&
          !s.auto_open_apps && s.music.playing && !strcmp(s.music.title, "Za\xc5\xbc\xc3\xb3\xc5\x82\xc4\x87") &&
          s.codex.primary.available && s.codex.primary.remaining_percent == 0 && s.codex.activity.item_count == 2 &&
          !strcmp(s.codex.activity.items[1].state, "waiting") && !s.claude.running && s.game.active &&
          fabsf(s.game.fps - 143.9f) < 0.01f && telemetry_invariants(&s),
          "telemetry: complete message parses every section");
    check(telemetry(telemetry_legacy, &s) && s.auto_open_apps && !s.music.running && !s.codex.available &&
          isnan(s.game.fps) && isnan(s.gpu_usage), "telemetry: older companion message keeps defaults");

    const char *controls = "\"control_error\":null,\"can_shuffle\":true,\"shuffle\":true,\"can_repeat\":true,\"repeat\":2,"
                           "\"can_seek\":true,\"can_volume\":true,\"volume\":35,\"muted\":false,\"art_color\":1193046}";
    check(telemetry_with("\"control_error\":null}", controls), "telemetry: shuffle, repeat, seek, volume and cover colour parse");
    check(!telemetry_with("\"control_error\":null}", "\"control_error\":null,\"repeat\":3}") &&
          !telemetry_with("\"control_error\":null}", "\"control_error\":null,\"volume\":101}") &&
          !telemetry_with("\"control_error\":null}", "\"control_error\":null,\"art_color\":16777216}") &&
          !telemetry_with("\"control_error\":null}", "\"control_error\":null,\"shuffle\":1}"),
          "telemetry: out-of-range music controls are rejected");
    check(!telemetry_with("\"pc.stats\"", "\"pc.other\""), "telemetry: wrong message type is rejected");
    check(!telemetry_with("\"v\":1", "\"v\":2"), "telemetry: unknown version is rejected");
    check(!telemetry_with("\"cpu\":{", "\"cpu\":[{}],\"x\":{"), "telemetry: non-object section is rejected");
    check(!telemetry_with("\"usage\":12.5", "\"usage\":100.5"), "telemetry: out-of-range metric is rejected");
    check(!telemetry_with("\"usage\":12.5", "\"usage\":\"12\""), "telemetry: string metric is rejected");
    check(!telemetry_with("\"seq\":42", "\"seq\":4.2"), "telemetry: fractional sequence is rejected");
    check(!telemetry_with("\"seq\":42", "\"seq\":-1"), "telemetry: negative sequence is rejected");
    check(!telemetry_with("\"seq\":42", "\"seq\":9007199254740993"), "telemetry: sequence past 2^53 is rejected");
    check(!telemetry_with("\"auto_open_apps\":false", "\"auto_open_apps\":0"), "telemetry: non-boolean preference is rejected");
    check(telemetry(telemetry_full, &s) && !s.away && !s.music.source[0] && s.music.art_id == 0,
          "telemetry: away, player and cover default off");
    check(telemetry_with("\"auto_open_apps\":false", "\"auto_open_apps\":false,\"away\":true") &&
          !telemetry_with("\"auto_open_apps\":false", "\"auto_open_apps\":false,\"away\":1"),
          "telemetry: away must be a boolean");
    check(telemetry_with("\"control_error\":null}", "\"control_error\":null,\"source\":\"Apple Music\",\"art_id\":77}") &&
          !telemetry_with("\"control_error\":null}", "\"control_error\":null,\"art_id\":-1}") &&
          !telemetry_with("\"control_error\":null}", "\"control_error\":null,\"source\":\"0123456789012345678901234567890123\"}"),
          "telemetry: player name and cover id are bounded");

    char name[128];
    memset(name, 'a', 63); name[63] = 0;
    char field[160];
    snprintf(field, sizeof(field), "\"hostname\":\"%s\"", name);
    check(telemetry_with("\"hostname\":\"Desk PC\"", field), "telemetry: 63-byte hostname fits");
    snprintf(field, sizeof(field), "\"hostname\":\"%sa\"", name);
    check(!telemetry_with("\"hostname\":\"Desk PC\"", field), "telemetry: 64-byte hostname is rejected, not truncated");
    check(!telemetry_with("\"Desk PC\"", "\"Desk\\u0001PC\""), "telemetry: control characters are rejected");
    check(!telemetry_with("\"Desk PC\"", "\"Desk\\u0000PC\""), "telemetry: escaped NUL cannot hide text");
    check(!telemetry_with("\"Desk PC\"", "\"Desk\xc3PC\""), "telemetry: invalid UTF-8 is rejected");
    check(!telemetry_with("\"Desk PC\"", "\"Desk\xed\xa0\x80PC\""), "telemetry: UTF-8 surrogates are rejected");
    check(!telemetry_with("\"Desk PC\"", "\"Desk\xe2\x80\xa8PC\""), "telemetry: line separator is rejected");

    check(!telemetry_with("\"state\":\"working\"", "\"state\":\"hacking\""), "telemetry: unknown task state is rejected");
    check(!telemetry_with("\"status\":\"local\"", "\"status\":\"remote\""), "telemetry: unknown activity status is rejected");
    check(!telemetry_with("\"items\":[", "\"items\":[{\"title\":\"a\",\"project\":\"b\",\"state\":\"recent\",\"age_s\":1},"
                                         "{\"title\":\"a\",\"project\":\"b\",\"state\":\"recent\",\"age_s\":1},"),
          "telemetry: more than three tasks is rejected");
    check(!telemetry_with("\"remaining_percent\":0", "\"remaining_percent\":null"), "telemetry: limit window without a value is rejected");
    check(!telemetry_with("\"name\":\"Game\"", "\"name\":\"\""), "telemetry: active game needs a name");
    check(!telemetry_with("\"running\":false,\"available\":false", "\"running\":0,\"available\":false"),
          "telemetry: provider flags must be booleans");

    check(!telemetry("{\"type\":\"pc.stats\",\"v\":1} trailing", NULL), "telemetry: trailing data is rejected");
    check(!telemetry("{\"a\":[[[[[[[[1]]]]]]]]}", NULL), "telemetry: nesting deeper than eight is rejected before parsing");
    check(!telemetry("{\"a\":\"open", NULL), "telemetry: unterminated string is rejected");
    check(!telemetry("}{", NULL), "telemetry: unbalanced brackets are rejected");
    const char truncated[] = "{\"type\":\"pc.stats\"";
    check(!buddy_telemetry_parse(truncated, sizeof(truncated) - 1, &s), "telemetry: truncated message is rejected");
}

/* Deterministic mutation fuzzing: no crash, sanitizer report or broken invariant for any input.
 * Each case lives in its own exact-size allocation so a sanitizer sees any overread. */
static void telemetry_fuzz(unsigned iterations)
{
    uint32_t state = 0x9e3779b9u;
    #define NEXT() (state ^= state << 13, state ^= state >> 17, state ^= state << 5, state)
    static const char tokens[][8] = {"{", "}", "[", "]", "\"", "\\", ":", ",", "null", "true", "-1", "1e999", "\\u0000", "\xc3", "\xff"};
    const char *seeds[] = {telemetry_full, telemetry_legacy};
    size_t accepted = 0, broken = 0;
    for (unsigned n = 0; n < iterations; ++n) {
        char work[sizeof(telemetry_full) + 256];
        size_t length = strlen(seeds[n & 1]);
        memcpy(work, seeds[n & 1], length);
        for (unsigned edits = 1 + NEXT() % 4; edits--;) {
            size_t at = length ? NEXT() % length : 0;
            switch (NEXT() % 5) {
            case 0: work[at] ^= (char)(1u << (NEXT() % 8)); break;
            case 1: if (length) { memmove(work + at, work + at + 1, length - at - 1); --length; } break;
            case 2: length = at; break;
            case 3: {
                const char *token = tokens[NEXT() % (sizeof(tokens) / sizeof(tokens[0]))];
                size_t size = strlen(token);
                if (length + size < sizeof(work)) { memmove(work + at + size, work + at, length - at); memcpy(work + at, token, size); length += size; }
                break;
            }
            default: work[at] = (char)(NEXT() & 0xff); break;
            }
        }
        char *input = malloc(length + 1);
        if (!input) exit(2);
        memcpy(input, work, length);
        input[length] = '\0';
        buddy_stats_t s;
        if (buddy_telemetry_parse(input, length, &s)) {
            ++accepted;
            if (!telemetry_invariants(&s)) ++broken;
        }
        free(input);
    }
    #undef NEXT
    printf("Fuzzed %u telemetry messages, %zu accepted\n", iterations, accepted);
    check(broken == 0, "telemetry: every accepted fuzz case keeps the parser's invariants");
}
