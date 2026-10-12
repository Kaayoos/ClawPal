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
    /* All zeros when an older companion does not send music metadata. */
    buddy_music_t music;
    /* Availability is authoritative; zero remaining is valid when available. */
    buddy_codex_t codex;
    buddy_codex_t claude;
    buddy_game_t game;
} buddy_stats_t;
