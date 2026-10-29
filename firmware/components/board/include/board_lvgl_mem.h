#pragma once

/* LVGL's allocator (LV_MEM_CUSTOM_*), wired up in firmware/CMakeLists.txt.
 *
 * Every LVGL object, style and label is a small allocation, and plain malloc puts
 * anything under CONFIG_SPIRAM_MALLOC_ALWAYSINTERNAL in internal RAM first. The UI
 * builds all its pages before Wi-Fi starts, so it took nearly all internal RAM and
 * left Wi-Fi buffers and the weather TLS connection without any. LVGL therefore lives
 * in PSRAM, which has megabytes to spare; internal RAM is only a fallback. */

#include <stddef.h>
#include "esp_heap_caps.h"

static inline void *board_lvgl_malloc(size_t size)
{
    void *block = heap_caps_malloc(size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    return block ? block : heap_caps_malloc(size, MALLOC_CAP_8BIT);
}

static inline void *board_lvgl_realloc(void *block, size_t size)
{
    if (!block) return board_lvgl_malloc(size);
    if (size == 0) { heap_caps_free(block); return NULL; }
    void *moved = heap_caps_realloc(block, size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    return moved ? moved : heap_caps_realloc(block, size, MALLOC_CAP_8BIT);
}

static inline void board_lvgl_free(void *block)
{
    heap_caps_free(block);
}
