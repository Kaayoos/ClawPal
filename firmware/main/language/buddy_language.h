#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <time.h>
#include "buddy_language_data.h"

const char *buddy_text(buddy_text_key_t key);
#define BT(key) buddy_text(key)
bool buddy_language_supported(const char *code);
void buddy_language_set(const char *code);
/* The Studio theme's calmer wording, falling back to the base text per key. */
void buddy_language_set_studio(bool enabled);
const char *buddy_language_code(void);
const char *buddy_retranslate(const char *text);
const char *buddy_retranslate_copy(const char *text);
int buddy_text_key(const char *text);
const char *buddy_weekday(int day);
void buddy_date(char *out,size_t size,const struct tm *local,bool use_24h);

#ifdef BUDDY_LANGUAGE_LVGL
#include "lvgl.h"
void buddy_label_set_text(lv_obj_t *label,const char *text);
// Bind static labels while preserving LVGL's normal handling of dynamic/user text.
#define lv_label_set_text buddy_label_set_text
#endif
