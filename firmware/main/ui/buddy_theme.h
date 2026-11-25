#pragma once
/* Roles come from themes/themes.json (scripts/generate_themes.py); the companion
 * reads the same file. RGB565 displays round channel values at draw time;
 * original mascot and logo assets stay untouched. The look is chosen once at boot. */
#include "buddy_theme_data.h"
#include "lvgl.h"

extern buddy_theme_t buddy_theme;
/* hide_frame leaves a cased look's case off: no frame and no rail, only its colours and shapes. */
void buddy_theme_select(unsigned theme, unsigned accent, bool hide_frame);
/* Light-numeral themes swap a numeric label's Medium font for its Montserrat Light twin,
 * which shares the same line metrics; other themes and other fonts are left alone. */
void buddy_theme_numerals(lv_obj_t *label);
static inline bool buddy_theme_has_mascot(void) { return buddy_theme.mascot; }

/* Square looks draw every control with square corners and flat ends; illustrations keep
 * their own shapes. Other looks keep the radius they ask for. */
static inline lv_coord_t buddy_theme_round(lv_coord_t radius) { return buddy_theme.square ? 0 : radius; }
/* Cased looks sit the screen inside the orange case of the PC app: a frame around every
 * view and a rail along the bottom that carries Apps and Home. 0 without a case or with
 * the case left off. */
int buddy_theme_frame(void);
/* Whether the case is drawn at all: a cased look that has not left it off. */
bool buddy_theme_case(void);
/* Heavy-type looks set display sizes (32 px and up, and the clocks) in Archivo's heavy
 * weight across a subtree; call it once a view is built. Other looks are left alone. */
void buddy_theme_type(lv_obj_t *root);
/* The same voice for a heading set below display size (20 or 24 px). */
void buddy_theme_heading(lv_obj_t *label);

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
/* The case and the ink of anything printed on it. */
#define BUDDY_COLOR_FRAME     (buddy_theme.frame)
#define BUDDY_COLOR_FRAME_INK (buddy_theme.frame_ink)
