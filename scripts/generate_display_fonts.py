"""Build the heavy display type of the cased looks (Crab v2 and Crastudio). Python 3.10+ with Pillow.

The PC app sets its headlines in a black weight. The display gets the same voice from Archivo,
an open variable font, rendered at one heavy weight through FreeType. Each size is matched to a
Medium twin already in the firmware: the capitals (or, for the numeral-only clocks, the digits)
are exactly as tall as the twin's, and the font copies the twin's line height and baseline and
falls back to it for anything it lacks. Swapping a label's font therefore never moves a layout.

Usage: python scripts/generate_display_fonts.py [--preview out.png]
"""
from pathlib import Path
import argparse, json, re
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'assets/fonts/Archivo-Variable.ttf'
MEDIUM = ROOT / 'assets/fonts/Montserrat-Medium.ttf'
LIGHT = ROOT / 'assets/fonts/Montserrat-Light.ttf'
# Archivo's axes are weight (100-900) and width (62-125).
WEIGHT, WIDTH = 850, 96
COVERAGE = json.loads((ROOT / 'firmware/main/language/font_coverage.json').read_text(encoding='utf-8'))
DIGITS = [0x2d] + list(range(0x30, 0x3b))
# Digits and the marks of times, temperatures and percentages, plus the letters of short headlines.
NUMBERS = sorted(set([0x20, 0x25, 0xb0] + list(range(0x2b, 0x3b)) + list(range(0x41, 0x5b)) + list(range(0x61, 0x7b))))
# size: (twin font, its header, its C source, the TTF it was made from, code points, what sets the scale)
SIZES = {
    20: ('buddy_language_font_20', 'buddy_language_fonts.h', 'firmware/main/language/buddy_language_font_20.c', MEDIUM, COVERAGE, 'H'),
    24: ('buddy_language_font_24', 'buddy_language_fonts.h', 'firmware/main/language/buddy_language_font_24.c', MEDIUM, COVERAGE, 'H'),
    32: ('buddy_language_font_32', 'buddy_language_fonts.h', 'firmware/main/language/buddy_language_font_32.c', MEDIUM, COVERAGE, 'H'),
    48: ('buddy_language_font_48', 'buddy_language_fonts.h', 'firmware/main/language/buddy_language_font_48.c', MEDIUM, NUMBERS, 'H'),
    96: ('buddy_clock_font', 'buddy_clock_font.h', 'firmware/main/assets/buddy_clock_font.c', MEDIUM, DIGITS, '0'),
    128: ('buddy_numeral_font_128', 'buddy_numeral_fonts.h', 'firmware/main/assets/buddy_numeral_font_128.c', LIGHT, DIGITS, '0'),
}
METRIC = re.compile(r'\.(line_height|base_line|underline_position|underline_thickness) = (-?\d+),')


def ink(font, text):
    """The rendered ink of text on a generous canvas: (image, left, top, right, bottom, origin)."""
    size = int(font.size * 3) + 8
    canvas = Image.new('L', (size, size), 0)
    origin = (font.size, int(font.size * 2))
    ImageDraw.Draw(canvas).text(origin, text, font=font, fill=255, anchor='ls')
    return canvas, canvas.getbbox(), origin


def height(font, text):
    _, box, _ = ink(font, text)
    return box[3] - box[1] if box else 0


def heavy(size):
    font = ImageFont.truetype(str(SOURCE), size)
    font.set_variation_by_axes([WEIGHT, WIDTH])
    return font


def matched(target, reference):
    """The Archivo size whose reference glyph is exactly as tall as the twin's."""
    low, high = 4.0, 400.0
    for _ in range(40):
        middle = (low + high) / 2
        if height(heavy(middle), reference) < target: low = middle
        else: high = middle
    size = high
    # FreeType snaps outlines to whole pixels; settle on a size that lands exactly on the target.
    for step in range(0, 40):
        for candidate in (size - step * 0.05, size + step * 0.05):
            if height(heavy(candidate), reference) == target: return candidate
    return size


def glyph(font, code):
    """4-bit pixels of one glyph with its box and advance, as lv_font_fmt_txt stores them."""
    canvas, box, (ox, oy) = ink(font, chr(code))
    advance = font.getlength(chr(code))
    if not box: return dict(width=0, height=0, x=0, y=0, advance=advance, nibbles=[])
    pixels = canvas.crop(box)
    nibbles = [(value * 15 + 127) // 255 for value in pixels.tobytes()]
    width, rows = pixels.size
    # Trim rows and columns that rounded to nothing.
    grid = [nibbles[r * width:(r + 1) * width] for r in range(rows)]
    while grid and not any(grid[0]): grid.pop(0); box = (box[0], box[1] + 1, box[2], box[3])
    while grid and not any(grid[-1]): grid.pop(); box = (box[0], box[1], box[2], box[3] - 1)
    while grid and not any(row[0] for row in grid): grid = [row[1:] for row in grid]; box = (box[0] + 1, box[1], box[2], box[3])
    while grid and not any(row[-1] for row in grid): grid = [row[:-1] for row in grid]; box = (box[0], box[1], box[2] - 1, box[3])
    if not grid: return dict(width=0, height=0, x=0, y=0, advance=advance, nibbles=[])
    return dict(width=len(grid[0]), height=len(grid), x=box[0] - ox, y=oy - box[3], advance=advance,
                nibbles=[v for row in grid for v in row])


def missing(font):
    """Code points the font draws as its empty-box placeholder."""
    notdef = ink(font, '￿')[0].tobytes()
    return lambda code: code != 0x20 and ink(font, chr(code))[0].tobytes() == notdef


def runs(codes):
    out, start = [], 0
    for i in range(1, len(codes) + 1):
        if i == len(codes) or codes[i] != codes[i - 1] + 1:
            out.append((codes[start], i - start, start + 1)); start = i
    return out


def emit(name, size, twin, header, metrics, glyphs, codes, scale):
    bitmap, descriptors, index = [], [], 0
    for code, g in zip(codes, glyphs):
        packed = []
        nibbles = g['nibbles'] + ([0] if len(g['nibbles']) % 2 else [])
        for i in range(0, len(nibbles), 2): packed.append(nibbles[i] << 4 | nibbles[i + 1])
        descriptors.append(f"    {{.bitmap_index = {index}, .adv_w = {round(g['advance'] * 16)}, .box_w = {g['width']}, .box_h = {g['height']}, "
                           f".ofs_x = {g['x']}, .ofs_y = {g['y']}}},")
        if packed:
            bitmap.append(f'    /* U+{code:04X} */')
            bitmap += ['    ' + ', '.join(f'0x{b:02x}' for b in packed[i:i + 12]) + ',' for i in range(0, len(packed), 12)]
        index += len(packed)
    cmaps = runs(codes)
    lines = [
        '/* Archivo: Copyright 2020 The Archivo Project Authors (https://github.com/Omnibus-Type/Archivo).',
        ' * Font data licensed under SIL OFL 1.1; see assets/fonts/OFL-Archivo.txt.',
        f' * Generated by scripts/generate_display_fonts.py: weight {WEIGHT}, width {WIDTH}, rendered at {scale:.2f} px to match {twin}. */',
        '#include "lvgl.h"', f'#include "{header}"', '',
        'static LV_ATTRIBUTE_LARGE_CONST const uint8_t glyph_bitmap[] = {', *bitmap, '};', '',
        'static const lv_font_fmt_txt_glyph_dsc_t glyph_dsc[] = {',
        '    {.bitmap_index = 0, .adv_w = 0, .box_w = 0, .box_h = 0, .ofs_x = 0, .ofs_y = 0} /* id = 0 reserved */,', *descriptors, '};', '',
        'static const lv_font_fmt_txt_cmap_t cmaps[] = {',
        *[f'    {{.range_start = {start}, .range_length = {length}, .glyph_id_start = {first}, .unicode_list = NULL, '
          f'.glyph_id_ofs_list = NULL, .list_length = 0, .type = LV_FONT_FMT_TXT_CMAP_FORMAT0_TINY}},' for start, length, first in cmaps],
        '};', '',
        '#if LVGL_VERSION_MAJOR == 8', 'static lv_font_fmt_txt_glyph_cache_t cache;', '#endif',
        'static const lv_font_fmt_txt_dsc_t font_dsc = {',
        '    .glyph_bitmap = glyph_bitmap, .glyph_dsc = glyph_dsc, .cmaps = cmaps, .kern_dsc = NULL, .kern_scale = 0,',
        f'    .cmap_num = {len(cmaps)}, .bpp = 4, .kern_classes = 0, .bitmap_format = 0,',
        '#if LVGL_VERSION_MAJOR == 8', '    .cache = &cache', '#endif', '};', '',
        f'/* Line metrics are {twin}\'s, so a label keeps its place when its font changes. */',
        f'const lv_font_t {name} = {{',
        '    .get_glyph_dsc = lv_font_get_glyph_dsc_fmt_txt, .get_glyph_bitmap = lv_font_get_bitmap_fmt_txt,',
        f"    .line_height = {metrics['line_height']}, .base_line = {metrics['base_line']}, .subpx = LV_FONT_SUBPX_NONE,",
        f"    .underline_position = {metrics['underline_position']}, .underline_thickness = {metrics['underline_thickness']},",
        f'    .dsc = &font_dsc, .fallback = &{twin}, .user_data = NULL,', '};', '']
    return '\n'.join(lines), index


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--preview', type=Path); args = parser.parse_args()
    names, previews = [], []
    for size, (twin, header, twin_source, ttf, codes, reference) in SIZES.items():
        name = f'buddy_display_font_{size}'; names.append(name)
        metrics = {k: int(v) for k, v in METRIC.findall((ROOT / twin_source).read_text(encoding='utf-8'))}
        target = height(ImageFont.truetype(str(ttf), size), reference)
        wanted = sorted(set(codes))
        # Every glyph has to fit the twin's line box, or LVGL clips it at the label's edge. The clocks'
        # tight boxes leave no room for Archivo's rounder overshoot, so those step down a pixel at a time.
        ascent, descent = metrics['line_height'] - metrics['base_line'], metrics['base_line']
        for _ in range(6):
            scale = matched(target, reference)
            font = heavy(scale)
            absent = missing(font)
            codes = [c for c in wanted if not absent(c)]
            glyphs = [glyph(font, c) for c in codes]
            over = [f"U+{c:04X}" for c, g in zip(codes, glyphs) if g['height'] and (g['y'] + g['height'] > ascent or -g['y'] > descent)]
            if not over or reference != '0': break
            target -= 1
        if any(g['advance'] * 16 >= 4096 or g['width'] > 255 or g['height'] > 255 for g in glyphs): raise ValueError(f'{name}: glyph too large')
        source, used = emit(name, size, twin, header, metrics, glyphs, codes, scale)
        (ROOT / 'firmware/main/assets' / f'{name}.c').write_text(source, encoding='utf-8', newline='\n')
        print(f'{name}: {len(codes)} glyphs at {scale:.2f} px, {used / 1024:.1f} KB bitmap'
              + (f'; reaching past the line box: {" ".join(over)}' if over else ''))
        previews.append((name, font, metrics))
    (ROOT / 'firmware/main/assets/buddy_display_fonts.h').write_text(
        '#pragma once\n#include "lvgl.h"\n\n/* Archivo heavy display type for looks with heavy_type; anything else falls back to the Medium twin. */\n' +
        '\n'.join(f'LV_FONT_DECLARE({name});' for name in names) + '\n', encoding='utf-8', newline='\n')
    if args.preview:
        sample = {20: 'Shortcuts  Your PC, a tap away  Zażółć gęślą', 24: 'PC Monitor  Warsaw  Claude Code', 32: 'Apps  62°C  72%  Installing update',
                  48: 'ClawPal  12°  45%', 96: '10:24', 128: '10:24'}
        canvas = Image.new('RGB', (1200, 640), (16, 16, 16)); draw = ImageDraw.Draw(canvas); y = 8
        for name, font, metrics in previews:
            size = int(name.rsplit('_', 1)[1])
            draw.text((8, y), sample[size], font=font, fill=(245, 241, 235)); y += metrics['line_height'] + 8
        canvas.save(args.preview)
        print(args.preview)


if __name__ == '__main__':
    main()
