#include "buddy_theme.h"
#include "buddy_language.h"
#include "buddy_clock_font.h"
#include "buddy_language_fonts.h"
#include "buddy_numeral_fonts.h"

/* Crab until settings say otherwise, so anything drawn early keeps today's look. */
buddy_theme_t buddy_theme = BUDDY_THEME_CRAB_VALUES;

void buddy_theme_select(unsigned theme, unsigned accent)
{
    buddy_theme = buddy_themes[theme < BUDDY_THEME_COUNT ? theme : BUDDY_THEME_CRAB];
    if (buddy_theme.accents && accent < BUDDY_ACCENT_COUNT) buddy_theme.accent = buddy_theme_accents[accent];
    buddy_language_set_studio(theme == BUDDY_THEME_STUDIO);
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
