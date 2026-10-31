#pragma once
/* Roles come from themes/themes.json (scripts/generate_themes.py); the companion
 * reads the same file. RGB565 displays round channel values at draw time;
 * original mascot and logo assets stay untouched. The look is chosen once at boot. */
#include "buddy_theme_data.h"
#include "lvgl.h"

extern buddy_theme_t buddy_theme;
void buddy_theme_select(unsigned theme, unsigned accent);
/* Light-numeral themes swap a numeric label's Medium font for its Montserrat Light twin,
 * which shares the same line metrics; other themes and other fonts are left alone. */
void buddy_theme_numerals(lv_obj_t *label);
static inline bool buddy_theme_has_mascot(void) { return buddy_theme.mascot; }

#define BUDDY_COLOR_BG       (buddy_theme.bg)
#define BUDDY_COLOR_SURFACE  (buddy_theme.surface)
#define BUDDY_COLOR_PRESSED  (buddy_theme.pressed)
#define BUDDY_COLOR_TEXT     (buddy_theme.text)
#define BUDDY_COLOR_MUTED    (buddy_theme.muted)
#define BUDDY_COLOR_RULE     (buddy_theme.rule)
#define BUDDY_COLOR_ACCENT   (buddy_theme.accent)
#define BUDDY_COLOR_WARNING  (buddy_theme.warning)
#define BUDDY_COLOR_CLOCK_BG (buddy_theme.clock_bg)
#define BUDDY_COLOR_CLOCK_TEXT (buddy_theme.clock_text)
/* Weather artwork retains its own colors independently of action accents. */
#define BUDDY_COLOR_WEATHER_APP (buddy_theme.weather_app)
#define BUDDY_COLOR_WEATHER_MARK (buddy_theme.weather_mark)
