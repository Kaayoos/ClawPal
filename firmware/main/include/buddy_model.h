#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef enum {
    BUDDY_WIFI_CONNECTING,
    BUDDY_WIFI_READY,
    BUDDY_PC_CONNECTED,
    BUDDY_PC_OFFLINE,
    BUDDY_PROVISIONING,
} buddy_connection_state_t;

/* Remote changes that need approval on the touch screen before they are accepted. */
typedef enum {
    BUDDY_CONFIRM_NONE,
    BUDDY_CONFIRM_FIRMWARE,
    BUDDY_CONFIRM_WIFI,
} buddy_confirm_action_t;

/* What a confirmation is about: the new firmware version or Wi-Fi network name, or empty. */
#define BUDDY_CONFIRM_DETAIL_SIZE 40

/* Progress of a Wi-Fi firmware install, shown full screen while it runs. */
typedef enum {
    BUDDY_INSTALL_NONE,
    BUDDY_INSTALL_RECEIVING,
    BUDDY_INSTALL_CHECKING,
    BUDDY_INSTALL_RESTARTING,
    BUDDY_INSTALL_FAILED,
} buddy_install_stage_t;

/* What the PC says about the current song's synced lyrics (telemetry music.lyrics). */
typedef enum {
    BUDDY_LYRICS_LOADING,      /* still looking, or waiting to measure this display's clock */
    BUDDY_LYRICS_SYNCED,       /* timed lines in the document named by id */
    BUDDY_LYRICS_PLAIN,        /* untimed lines in the document named by id */
    BUDDY_LYRICS_NONE,         /* LRCLIB has nothing for this song */
    BUDDY_LYRICS_INSTRUMENTAL,
    BUDDY_LYRICS_UNSUPPORTED,  /* written in letters this display's fonts don't have */
    BUDDY_LYRICS_UNAVAILABLE,  /* the PC couldn't reach LRCLIB just now */
} buddy_lyrics_status_t;

/* A reference point: the song was at position_ms when this display's clock (lv_tick_get(), the
 * same clock GET /clock reports) read at_ms. Between points the display counts on by itself
 * while playing. anchor rises with each new point and is 0 while there is none. */
typedef struct {
    bool present; /* false while lyrics are off in the PC app, or from an older PC app */
    bool playing;
    bool auto_open; /* open the Lyrics view by itself when a new song has lyrics */
    uint8_t status; /* buddy_lyrics_status_t */
    uint32_t id, anchor, position_ms, at_ms;
} buddy_lyrics_sync_t;

/* One song's lyrics from POST /lyrics. Lines point into text; a missing line holds letters the
 * fonts can't draw and shows as a music note. Plain lyrics have every time_ms at 0. */
#define BUDDY_LYRICS_MAX_LINES 400
#define BUDDY_LYRICS_LINE_BYTES 256
#define BUDDY_LYRICS_BODY_MAX 49152
#define BUDDY_LYRICS_MAX_MS 604800000U
typedef struct {
    uint32_t time_ms;
    uint32_t offset; /* into text, NUL-terminated */
    bool missing;
} buddy_lyric_line_t;
typedef struct {
    uint32_t id;
    bool synced;
    uint16_t count;
    uint32_t used;
    buddy_lyric_line_t lines[BUDDY_LYRICS_MAX_LINES];
    char text[BUDDY_LYRICS_BODY_MAX];
} buddy_lyrics_t;

typedef struct {
    bool running;
    bool available;
    bool playing;
    bool can_play_pause;
    bool can_next;
    bool can_previous;
    char title[192];
    char artist[128];
    char album[128];
    uint32_t position_s;
    uint32_t duration_s;
    uint32_t control_id;
    bool control_ok;
    char control_error[32];
    /* Optional controls; older companions omit them, which hides the control. */
    bool can_shuffle;
    bool shuffle;
    bool can_repeat;
    uint32_t repeat; /* 0 off, 1 current track, 2 whole list */
    bool can_seek;
    bool can_volume;
    uint32_t volume; /* Spotify's Windows mixer level, 0..100 */
    bool muted;
    uint32_t art_color; /* 0xRRGGBB from the album cover; 0 keeps the theme accent */
    /* Player name from Windows media controls; empty from older companions (Spotify). */
    char source[32];
    /* Cover image the companion uploaded to POST /art; 0 when there is none. */
    uint32_t art_id;
    buddy_lyrics_sync_t lyrics;
} buddy_music_t;

typedef struct {
    bool available;
    float remaining_percent;
    uint32_t window_minutes;
    char reset_text[48];
    uint32_t resets_at;
} buddy_codex_window_t;

typedef struct {
    char title[64];
    char project[32];
    char state[16];
    uint32_t age_s;
} buddy_codex_task_t;

typedef struct {
    bool available;
    bool stale;
    char status[24];
    uint32_t age_s;
    uint32_t working_count;
    uint32_t total_count;
    uint32_t item_count;
    buddy_codex_task_t items[3];
} buddy_codex_activity_t;

typedef struct {
    bool running;
    bool available;
    bool stale;
    char status[24];
    char plan[32];
    uint32_t age_s;
    buddy_codex_window_t primary;
    buddy_codex_window_t secondary;
    /* Recent local root-session events, not authoritative desktop live state. */
    buddy_codex_activity_t activity;
} buddy_codex_t;

/* Unavailable sensor values are NAN. Percentages are in the range 0..100. */
typedef struct {
    bool active;
    char name[96];
    float fps;
    char status[16];
} buddy_game_t;

/* The PC owns the Shortcuts deck; telemetry carries only its revision and which toggle tiles are lit. */
#define BUDDY_SHORTCUT_SLOTS 16
#define BUDDY_SHORTCUT_MAX_REV 0xFFFF
typedef struct {
    bool present;
    uint32_t rev;
    uint32_t on;
} buddy_shortcuts_state_t;

/* The Audio app's mixer: the PC's output, its microphone and each app playing sound. The PC
 * names every channel with an ID it keeps while that app runs; the display only echoes it. */
#define BUDDY_MIXER_CHANNELS 10
#define BUDDY_MIXER_MAX_ID 255
#define BUDDY_MIXER_NAME_BYTES 32
typedef enum {
    BUDDY_MIXER_OUTPUT = 0, /* the default speakers or headphones */
    BUDDY_MIXER_MIC = 1,    /* the default microphone */
    BUDDY_MIXER_SYSTEM = 2, /* Windows' own sounds */
    BUDDY_MIXER_APP = 3,
} buddy_mixer_kind_t;
typedef struct {
    uint8_t id, kind, color, volume; /* color: a Shortcuts palette index, 0 for the look's accent */
    bool muted;
    char name[BUDDY_MIXER_NAME_BYTES];
} buddy_mixer_channel_t;
typedef struct {
    bool present; /* false when an older companion has no mixer */
    uint8_t count;
    buddy_mixer_channel_t channels[BUDDY_MIXER_CHANNELS];
} buddy_mixer_t;
/* mixer_volume carries (id << 7) | percent and mixer_mute (id << 1) | muted. */
#define BUDDY_MIXER_VOLUME_VALUE(id, percent) (((uint32_t)(id) << 7) | (uint32_t)(percent))
#define BUDDY_MIXER_MUTE_VALUE(id, muted) (((uint32_t)(id) << 1) | ((muted) ? 1U : 0U))

typedef struct {
    char hostname[64];
    char cpu_name[96];
    char gpu_name[96];
    float cpu_usage;
    float cpu_temperature;
    float gpu_usage;
    float gpu_temperature;
    float memory_usage;
    float memory_used_mb;
    float memory_total_mb;
    float vram_used_mb;
    float vram_total_mb;
    double network_rx_bps;
    double network_tx_bps;
    uint64_t uptime_s;
    uint64_t sequence;
    int64_t timestamp_ms;
    /* Tray preference; parser defaults true only for older companions. */
    bool auto_open_apps;
    /* The PC is locked or about to sleep; telemetry may stop without it being offline. */
    bool away;
    /* All zeros when an older companion does not send music metadata. */
    buddy_music_t music;
    /* Availability is authoritative; zero remaining is valid when available. */
    buddy_codex_t codex;
    buddy_codex_t claude;
    buddy_game_t game;
    /* present is false when an older companion has no Shortcuts. */
    buddy_shortcuts_state_t shortcuts;
    buddy_mixer_t mixer;
} buddy_stats_t;
