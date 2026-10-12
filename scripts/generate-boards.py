"""Generate build identities from boards/catalog.json. Use --check in CI."""
from pathlib import Path
import argparse,json
R=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--check',action='store_true');args=p.parse_args()
catalog=json.loads((R/'boards/catalog.json').read_text(encoding='utf-8'));boards=catalog['Boards']
assert catalog['Schema']==1 and len({b['Id'] for b in boards})==len(boards)
assert [b['Profile'] for b in boards]==list(range(len(boards))), 'Profile numbers are stable and contiguous'
outputs={};base=(R/'firmware/sdkconfig.defaults').read_text(encoding='utf-8')
for b in boards:
 assert b['Chip']=='esp32s3' and b['FlashBytes'] in (8388608,16777216) and b['Psram'] in ('quad','octal')
 assert b['MaxBytes']==(0x300000 if b['FlashBytes']==8388608 else 0x3f0000)
 lines=['#pragma once',f'#define CLAWPAL_BOARD_ID "{b["Id"]}"',f'#define CLAWPAL_BOARD_NAME "{b["Name"]}"',f'#define CLAWPAL_EXPERIMENTAL {int(b["Experimental"])}',f'#define BOARD_DISPLAY_WIDTH {b["Width"]}',f'#define BOARD_DISPLAY_HEIGHT {b["Height"]}',f'#define CLAWPAL_OTA_LAYOUT "{b["Layout"]}"',f'#define CLAWPAL_OTA_MAX_BYTES {hex(b["MaxBytes"])}',f'#define CLAWPAL_FLASH_BYTES {b["FlashBytes"]}',f'#define CLAWPAL_DRIVER_{b["Driver"].replace("-","_").upper()} 1']
 offsets=(0xf000,0x20000,0x320000) if b['FlashBytes']==8388608 else (0x800000,0x810000,0xc00000)
 for key,n in zip(('DATA','0','1'),offsets):lines.append(f'#define CLAWPAL_OTA_{key}_OFFSET {hex(n)}')
 outputs['firmware/boards/'+b['Id']+'.h']='\n'.join(lines)+'\n'
 s=base
 if b['FlashBytes']==8388608:s=s.replace('FLASHSIZE_16MB','FLASHSIZE_8MB').replace('"partitions.csv"','"partitions-8mb.csv"')
 if b['Psram']=='quad':s=s.replace('SPIRAM_MODE_OCT','SPIRAM_MODE_QUAD').replace('CONFIG_EXAMPLE_LVGL_PORT_AVOID_TEAR_ENABLE=y','CONFIG_EXAMPLE_LVGL_PORT_AVOID_TEAR_ENABLE=n')
 if b['Driver']=='guition-square':s=s.replace('CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG=y','CONFIG_ESP_CONSOLE_UART_DEFAULT=y')
 outputs['firmware/boards/'+b['Id']+'.defaults']=s
outputs['firmware/components/board/include/board_profile.h']='#pragma once\n#ifndef CLAWPAL_PROFILE\n#define CLAWPAL_PROFILE 0\n#endif\n'+''.join(('#if' if i==0 else '#elif')+f' CLAWPAL_PROFILE == {i}\n#include "../../../boards/{b["Id"]}.h"\n' for i,b in enumerate(boards))+'#else\n#error Unknown ClawPal board profile\n#endif\n'
outputs['firmware/boards/index.cmake']='set(_clawpal_boards '+ ' '.join(b['Id'] for b in boards)+')\n'
for path,text in outputs.items():
 out=R/path
 if args.check:
  if not out.exists() or out.read_text(encoding='utf-8')!=text:raise SystemExit('Regenerate board definitions: '+path)
 else:out.parent.mkdir(parents=True,exist_ok=True);out.write_text(text,encoding='utf-8',newline='\n')
print('Validated' if args.check else 'Generated',len(boards),'board profiles')
