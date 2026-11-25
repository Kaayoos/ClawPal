#include "buddy_printer_watch.h"

static buddy_print_alert_t alert_for(uint8_t before, const buddy_printer_t *now)
{
    if (!buddy_print_active(before) || now->state == before) return BUDDY_PRINT_ALERT_NONE;
    if (now->state == BUDDY_PRINT_FINISHED) return BUDDY_PRINT_ALERT_FINISHED;
    /* Someone who cancels or pauses at the printer already knows. */
    if (now->reason == BUDDY_PRINT_REASON_USER) return BUDDY_PRINT_ALERT_NONE;
    if (now->state == BUDDY_PRINT_FAILED) return BUDDY_PRINT_ALERT_FAILED;
    if (now->state == BUDDY_PRINT_PAUSED) return BUDDY_PRINT_ALERT_PAUSED;
    return BUDDY_PRINT_ALERT_NONE;
}

buddy_print_alert_t buddy_print_watch_update(buddy_print_watch_t *watch, const buddy_printers_t *printers, unsigned *index)
{
    buddy_print_watch_t next = {0};
    buddy_print_alert_t alert = BUDDY_PRINT_ALERT_NONE;
    unsigned count = printers && printers->present ? printers->count : 0;
    if (count > BUDDY_PRINTERS) count = BUDDY_PRINTERS;
    for (unsigned i = 0; i < count; ++i) {
        const buddy_printer_t *printer = &printers->items[i];
        int before = -1;
        for (unsigned k = 0; k < watch->count; ++k)
            if (watch->ids[k] == printer->id) before = watch->states[k];
        uint8_t remembered = printer->state == BUDDY_PRINT_OFFLINE && before >= 0 ? (uint8_t)before : printer->state;
        next.ids[next.count] = printer->id;
        next.states[next.count++] = remembered;
        if (before < 0 || printer->state == BUDDY_PRINT_OFFLINE || alert != BUDDY_PRINT_ALERT_NONE) continue;
        buddy_print_alert_t found = alert_for((uint8_t)before, printer);
        if (found != BUDDY_PRINT_ALERT_NONE) {
            alert = found;
            if (index) *index = i;
        }
    }
    /* A PC app without printers (or an older one) leaves nothing to remember. */
    *watch = next;
    if (printers && printers->present && !printers->alerts) return BUDDY_PRINT_ALERT_NONE;
    return alert;
}
