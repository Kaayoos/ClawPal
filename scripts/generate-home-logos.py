"""Convert the owner's logos to LVGL RGB565+alpha, preserving the PNG originals."""
from pathlib import Path
from PIL import Image, ImageOps

root = Path(__file__).resolve().parents[1]
out = root / "firmware/main/assets"
source = ['/* Derived from owner-supplied assets/logos PNGs. See scripts/generate-home-logos.py. */', '#include "buddy_home_logos.h"']
header = ['#pragma once', '#include "lvgl.h"']
for name, filename in [('spotify', 'SpotifyLogo.png'), ('codex', 'CodexLogo.png'), ('claude', 'ClaudeLogo2.png')]:
    image = Image.open(root / 'assets/logos' / filename).convert('RGBA')
    image = ImageOps.contain(image.crop(image.getbbox()), (56, 56), Image.Resampling.LANCZOS)
    canvas = Image.new('RGBA', (56, 56))
    canvas.paste(image, ((56-image.width)//2, (56-image.height)//2))
    data = bytearray()
    for r, g, b, a in canvas.getdata():
        rgb = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)
        data.extend((rgb & 255, rgb >> 8, a))
    source.append(f'static const uint8_t {name}_pixels[] = {{')
    source.extend(','.join(f'0x{v:02x}' for v in data[i:i+24])+',' for i in range(0, len(data), 24))
    source.extend(['};', f'const lv_img_dsc_t buddy_logo_{name} = {{ .header = {{.cf = LV_IMG_CF_TRUE_COLOR_ALPHA, .w = 56, .h = 56}}, .data_size = sizeof({name}_pixels), .data = {name}_pixels }};'])
    header.append(f'extern const lv_img_dsc_t buddy_logo_{name};')
(out / 'buddy_home_logos.c').write_text('\n'.join(source)+'\n', encoding='utf-8')
(out / 'buddy_home_logos.h').write_text('\n'.join(header)+'\n', encoding='utf-8')
print('Converted supplied integration logos to 56 x 56 RGB565 + alpha.')
