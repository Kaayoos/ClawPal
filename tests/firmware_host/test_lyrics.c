/* Host checks for synced lyrics: telemetry's music.lyrics and the POST /lyrics body.
 * Included by test_parsers.c after test_telemetry.c, which supplies telemetry_full. */
#include "buddy_lyrics_parse.h"

/* The full telemetry message with `lyrics` added to its music object. */
static bool telemetry_lyrics(const char *lyrics, buddy_stats_t *out)
{
    static char edited[sizeof(telemetry_full) + 512];
    const char *from = "\"control_error\":null}";
    const char *at = strstr(telemetry_full, from);
    snprintf(edited, sizeof(edited), "%.*s\"control_error\":null,\"lyrics\":%s}%s",
             (int)(at - telemetry_full), telemetry_full, lyrics, at + strlen(from));
    return buddy_telemetry_parse(edited, strlen(edited), out);
}

static bool lyrics_body(const char *body, buddy_lyrics_t *out) { return buddy_lyrics_parse(body, strlen(body), out); }

static void lyrics_checks(void)
{
    static buddy_stats_t s;
    check(telemetry(telemetry_full, &s) && !s.music.lyrics.present, "lyrics: absent while the PC app has them off");
    check(telemetry_lyrics("{\"id\":7,\"state\":\"synced\",\"anchor\":3,\"pos_ms\":61500,\"at_ms\":4294967295,\"playing\":true}", &s) &&
          s.music.lyrics.present && s.music.lyrics.status == BUDDY_LYRICS_SYNCED && s.music.lyrics.id == 7 && s.music.lyrics.anchor == 3 &&
          s.music.lyrics.position_ms == 61500 && s.music.lyrics.at_ms == UINT32_MAX && s.music.lyrics.playing,
          "lyrics: a reference point parses, including a wrapped display clock");
    check(!s.music.lyrics.auto_open && telemetry_lyrics("{\"state\":\"none\",\"auto_open\":true}", &s) && s.music.lyrics.auto_open &&
          !telemetry_lyrics("{\"state\":\"none\",\"auto_open\":1}", &s),
          "lyrics: auto-open is off unless the PC app asks, and must be a boolean");
    const char *states[] = { "loading", "synced", "plain", "none", "instrumental", "unsupported", "unavailable" };
    bool all = true;
    for (unsigned i = 0; i < 7; ++i) {
        char json[96];
        snprintf(json, sizeof(json), "{\"state\":\"%s\"}", states[i]);
        all = all && telemetry_lyrics(json, &s) && s.music.lyrics.status == i && !s.music.lyrics.anchor;
    }
    check(all, "lyrics: every state has its own value; a missing anchor means no reference point yet");
    check(telemetry_lyrics("{\"state\":\"karaoke\"}", &s) && s.music.lyrics.status == BUDDY_LYRICS_UNAVAILABLE,
          "lyrics: a state from a newer PC app reads as unavailable instead of dropping the connection");
    check(!telemetry_lyrics("{\"id\":1}", &s) && !telemetry_lyrics("{\"state\":7}", &s) &&
          !telemetry_lyrics("{\"state\":\"synced\",\"at_ms\":4294967296}", &s) &&
          !telemetry_lyrics("{\"state\":\"synced\",\"pos_ms\":604800001}", &s) &&
          !telemetry_lyrics("{\"state\":\"synced\",\"anchor\":-1}", &s) &&
          !telemetry_lyrics("{\"state\":\"synced\",\"playing\":1}", &s) && !telemetry_lyrics("[]", &s),
          "lyrics: a missing state, out-of-range times and wrong types are rejected");

    static buddy_lyrics_t l;
    check(lyrics_body("{\"id\":501,\"kind\":\"synced\",\"lines\":[[0,\"\"],[12000,\"Za\xc5\xbc\xc3\xb3\xc5\x82\xc4\x87\"],[12000,null],[30500,\"Last\"]]}", &l) &&
          l.id == 501 && l.synced && l.count == 4 && l.lines[1].time_ms == 12000 && !strcmp(buddy_lyrics_text(&l, 1), "Za\xc5\xbc\xc3\xb3\xc5\x82\xc4\x87") &&
          l.lines[2].missing && !buddy_lyrics_text(&l, 2)[0] && !l.lines[0].missing && !strcmp(buddy_lyrics_text(&l, 3), "Last") &&
          !buddy_lyrics_text(&l, 9)[0], "lyrics body: timed lines, a pause, an unreadable line and Polish letters");
    check(lyrics_body("{\"id\":9,\"kind\":\"plain\",\"lines\":[[0,\"One\"],[0,\"\"],[0,\"Two\"]]}", &l) && !l.synced && l.count == 3,
          "lyrics body: plain lines");
    check(!lyrics_body("{\"id\":9,\"kind\":\"plain\",\"lines\":[[5,\"Timed\"]]}", &l) && l.count == 0, "lyrics body: plain lines carry no time");
    check(!lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[2000,\"b\"],[1000,\"a\"]]}", &l), "lyrics body: time never runs backwards");
    check(!lyrics_body("{\"id\":0,\"kind\":\"synced\",\"lines\":[[0,\"a\"]]}", &l) &&
          !lyrics_body("{\"id\":1.5,\"kind\":\"synced\",\"lines\":[[0,\"a\"]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"words\",\"lines\":[[0,\"a\"]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,\"a\",1]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[-1,\"a\"]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[604800001,\"a\"]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,7]]}", &l),
          "lyrics body: bad IDs, kinds, empty or malformed lines are rejected");
    check(!lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,\"a\\nb\"]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,\"a\\u0000b\"]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,\"\xff\"]]}", &l),
          "lyrics body: control characters, hidden NULs and broken UTF-8 are rejected");
    check(!lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,[\"a\"]]]}", &l) &&
          !lyrics_body("{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,\"a\"]]} trailing", &l) && !buddy_lyrics_parse("{}", 0, &l),
          "lyrics body: deeper nesting, trailing data and empty bodies are rejected");

    /* Limits: the longest allowed line, too many lines, and a line one byte too long. */
    static char body[BUDDY_LYRICS_BODY_MAX + 64];
    char line[BUDDY_LYRICS_LINE_BYTES + 1];
    memset(line, 'a', BUDDY_LYRICS_LINE_BYTES - 1); line[BUDDY_LYRICS_LINE_BYTES - 1] = 0;
    snprintf(body, sizeof(body), "{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,\"%s\"]]}", line);
    check(lyrics_body(body, &l) && strlen(buddy_lyrics_text(&l, 0)) == BUDDY_LYRICS_LINE_BYTES - 1, "lyrics body: a line of 255 bytes fits");
    memset(line, 'a', BUDDY_LYRICS_LINE_BYTES); line[BUDDY_LYRICS_LINE_BYTES] = 0;
    snprintf(body, sizeof(body), "{\"id\":1,\"kind\":\"synced\",\"lines\":[[0,\"%s\"]]}", line);
    check(!lyrics_body(body, &l), "lyrics body: a longer line is rejected");
    size_t used = (size_t)snprintf(body, sizeof(body), "{\"id\":1,\"kind\":\"synced\",\"lines\":[");
    for (unsigned i = 0; i <= BUDDY_LYRICS_MAX_LINES; ++i) used += (size_t)snprintf(body + used, sizeof(body) - used, "%s[%u,\"x\"]", i ? "," : "", i);
    snprintf(body + used, sizeof(body) - used, "]}");
    check(!lyrics_body(body, &l), "lyrics body: more than 400 lines are rejected");
}
