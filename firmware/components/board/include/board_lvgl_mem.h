#pragma once

/* LVGL's allocator (LV_MEM_CUSTOM_*), wired up in firmware/CMakeLists.txt.
 *
 * Every LVGL object, style and label is a small allocation, and plain malloc puts
 * anything under CONFIG_SPIRAM_MALLOC_ALWAYSINTERNAL in internal RAM first. A UI with
 * many pages can then drain internal RAM until Wi-Fi buffers and the weather TLS
 * handshake fail. LVGL may use internal RAM only while this reserve remains free;
 * after that it lives in PSRAM, which has megabytes to spare. */

#include <stddef.h>
#include "esp_heap_caps.h"
#include "esp_memory_utils.h"

#define BOARD_LVGL_INTERNAL_RESERVE (72 * 1024)

static inline void *board_lvgl_malloc(size_t size)
{
    void *block = NULL;
    if (heap_caps_get_free_size(MALLOC_CAP_INTERNAL) > BOARD_LVGL_INTERNAL_RESERVE + size)
        block = heap_caps_malloc(size, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    if (!block) block = heap_caps_malloc(size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    return block ? block : heap_caps_malloc(size, MALLOC_CAP_8BIT);
}

/* A block grows where it already is when it can, so a label edit never pulls PSRAM
 * text back into scarce internal RAM. */
static inline void *board_lvgl_realloc(void *block, size_t size)
{
    if (!block) return board_lvgl_malloc(size);
    if (size == 0) { heap_caps_free(block); return NULL; }
    void *moved = heap_caps_realloc(block, size, esp_ptr_external_ram(block) ?
        MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT : MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    return moved ? moved : heap_caps_realloc(block, size, MALLOC_CAP_8BIT);
}

static inline void board_lvgl_free(void *block)
{
    heap_caps_free(block);
}
