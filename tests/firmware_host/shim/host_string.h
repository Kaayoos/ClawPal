/* ESP-IDF provides strlcpy; some host C libraries, including Windows, do not.
 * Keep this compatibility layer confined to the parser test executable. */
#pragma once
#include <string.h>

static inline size_t clawpal_host_strlcpy(char *destination, const char *source, size_t capacity)
{
    size_t length = strlen(source);
    if (capacity) {
        size_t copied = length < capacity - 1 ? length : capacity - 1;
        memcpy(destination, source, copied);
        destination[copied] = '\0';
    }
    return length;
}

#define strlcpy clawpal_host_strlcpy
