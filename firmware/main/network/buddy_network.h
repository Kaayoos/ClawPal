#pragma once

#include "esp_err.h"
#include "buddy_model.h"

typedef struct {
    /* Callbacks run on network tasks. Copy strings/data before returning. */
    void (*on_state)(buddy_connection_state_t state, const char *ssid,
                     const char *ip, const char *detail);
    void (*on_stats)(const buddy_stats_t *stats);
} buddy_network_callbacks_t;

/* NVS must already be initialized. Namespace buddy: ssid, password, token,
 * setup_key. Secrets are never written to logs. */
esp_err_t buddy_network_start(const buddy_network_callbacks_t *callbacks);

/* Nonblocking request for play_pause, next or previous. False means offline,
 * unsupported, or busy. Acceptance means queued, not confirmed execution;
 * music.control_id/control_ok/control_error in telemetry confirm the result. */
bool buddy_network_music_control(const char *action);
