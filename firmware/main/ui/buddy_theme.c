#include "buddy_theme.h"
#include "buddy_language.h"
#include "buddy_clock_font.h"
#include "buddy_language_fonts.h"
#include "buddy_numeral_fonts.h"
#include "buddy_display_fonts.h"
#include "board.h"

/* Crab until settings say otherwise, so anything drawn early keeps today's look. */
buddy_theme_t buddy_theme = BUDDY_THEME_CRAB_VALUES;

void buddy_theme_select(unsigned theme, unsigned accent)
{
    buddy_theme = buddy_themes[theme < BUDDY_THEME_COUNT ? theme : BUDDY_THEME_CRAB];
    if (buddy_theme.accents && accent < BUDDY_ACCENT_COUNT) buddy_theme.accent = buddy_theme_accents[accent];
    /* Looks without the crab use the wording that never mentions it. */
    buddy_language_set_studio(!buddy_theme.mascot);
}

void buddy_theme_numerals(lv_obj_t *label)
{
    if (!label || !buddy_theme.light_numerals) return;
    static const struct { const lv_font_t *medium, *light; } twins[] = {
        { &buddy_clock_font, &buddy_numeral_font_96 }, { &buddy_language_font_48, &buddy_numeral_font_48 },
        { &buddy_language_font_32, &buddy_numeral_font_32 }, { &buddy_language_font_24, &buddy_numeral_font_24 },
    };
    const lv_font_t *font = lv_obj_get_style_text_font(label, LV_PART_MAIN);
    for (unsigned i = 0; i < sizeof(twins) / sizeof(twins[0]); ++i)
        if (font == twins[i].medium) { lv_obj_set_style_text_font(label, twins[i].light, 0); return; }
}

int buddy_theme_frame(void)
{
    if (!buddy_theme.cased) return 0;
    /* Thin enough that every layout's own margin still clears it. */
    return BOARD_DISPLAY_WIDTH >= 800 ? 8 : BOARD_DISPLAY_HEIGHT >= 400 ? 5 : 3;
}

/* Swaps a font for its heavy twin; display sizes always, headings on request. */
static bool heavy_twin(lv_obj_t *obj, bool headings)
{
    static const struct { const lv_font_t *medium, *heavy; bool heading; } twins[] = {
        { &buddy_clock_font, &buddy_display_font_96, false }, { &buddy_numeral_font_128, &buddy_display_font_128, false },
        { &buddy_language_font_48, &buddy_display_font_48, false }, { &buddy_language_font_32, &buddy_display_font_32, false },
        { &buddy_language_font_24, &buddy_display_font_24, true }, { &buddy_language_font_20, &buddy_display_font_20, true },
    };
    const lv_font_t *font = lv_obj_get_style_text_font(obj, LV_PART_MAIN);
    for (unsigned i = 0; i < sizeof(twins) / sizeof(twins[0]); ++i)
        if (font == twins[i].medium && (headings || !twins[i].heading)) { lv_obj_set_style_text_font(obj, twins[i].heavy, 0); return true; }
    return false;
}

void buddy_theme_type(lv_obj_t *root)
{
    if (!root || !buddy_theme.heavy_type) return;
    if (lv_obj_check_type(root, &lv_label_class)) heavy_twin(root, false);
    for (uint32_t i = 0; i < lv_obj_get_child_cnt(root); ++i) buddy_theme_type(lv_obj_get_child(root, i));
}

void buddy_theme_heading(lv_obj_t *label)
{
    if (label && buddy_theme.heavy_type) heavy_twin(label, true);
}
