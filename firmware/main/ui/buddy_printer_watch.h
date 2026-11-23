#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "buddy_model.h"

/* Decides when the Printer alert covers the screen. Only a change the display saw counts:
 * a print it watched running and then finishing, failing or pausing for filament or an
 * error. A printer that is already finished when the PC connects, a print someone cancelled
 * or paused on purpose, and a printer dropping off the network never alert. Going offline
 * does not forget what a printer was doing, so a print that ends during a Wi-Fi blip still
 * alerts when the printer comes back. Plain C so the build host can test it. */
typedef enum {
    BUDDY_PRINT_ALERT_NONE,
    BUDDY_PRINT_ALERT_FINISHED,
    BUDDY_PRINT_ALERT_FAILED,
    BUDDY_PRINT_ALERT_PAUSED,
} buddy_print_alert_t;

typedef struct {
    uint8_t count;
    uint8_t ids[BUDDY_PRINTERS];
    uint8_t states[BUDDY_PRINTERS]; /* the last state other than offline */
} buddy_print_watch_t;

/* Feeds one telemetry frame. Returns the alert to show, if any, and the index of its printer
 * in `printers` through `index`. With several at once the first printer in the PC's order wins. */
buddy_print_alert_t buddy_print_watch_update(buddy_print_watch_t *watch, const buddy_printers_t *printers, unsigned *index);

/* True while a print is under way: preparing, printing or paused. */
static inline bool buddy_print_active(uint8_t state)
{
    return state == BUDDY_PRINT_PREPARING || state == BUDDY_PRINT_PRINTING || state == BUDDY_PRINT_PAUSED;
}
