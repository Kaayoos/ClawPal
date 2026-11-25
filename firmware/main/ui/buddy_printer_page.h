#pragma once
#include "lvgl.h"
#include "buddy_model.h"

/* The Printer app: one printer at a time with a progress ring, its file, layer, time left,
 * when it will be done and its nozzle and bed temperatures. Read-only: nothing on this page
 * sends anything to the PC or the printer. Every call needs the LVGL lock. */
lv_obj_t *buddy_printer_page_create(lv_obj_t *parent);
/* `connected` is false while the PC is offline or its telemetry is stale. */
void buddy_printer_page_update(const buddy_printers_t *printers, bool connected);
/* Shows the printer with this id, such as the one an alert named. */
void buddy_printer_page_show(uint8_t id);
/* The page's pixel printer, 48 x 48 cells of `scale` pixels from (x, y), in `printer`'s state;
 * NULL draws it offline. For a draw event of another object, such as the print alert. */
#define BUDDY_PRINTER_ART_CELLS 48
void buddy_printer_draw(lv_draw_ctx_t *ctx, int x, int y, int scale, const buddy_printer_t *printer);
/* A short line for Home and the Apps menu. */
const char *buddy_printer_page_status(void);
/* Name of a state as the page writes it, with the reason when it explains a pause or a stop. */
const char *buddy_printer_state_text(const buddy_printer_t *printer);
/* "Done at 14:32" for a print with time left and a set clock; empty otherwise. */
void buddy_printer_done_at(char *buffer, size_t size, const buddy_printer_t *printer);
