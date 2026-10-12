"""Preserve license/notice files from the exact SDK used for firmware."""
from pathlib import Path
import os,shutil
root=Path(__file__).resolve().parents[1];sdk=Path(os.environ['IDF_PATH']).resolve()
out=root/'payload/licenses/esp-idf';out.mkdir(parents=True,exist_ok=True)
count=0
for path in sdk.rglob('*'):
 if not path.is_file() or any(p in ('.git','build','__pycache__') for p in path.parts):continue
 if not path.name.upper().startswith(('LICENSE','LICENCE','COPYING','NOTICE')):continue
 dest=out/path.relative_to(sdk);dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(path,dest);count+=1
(out/'SOURCE.txt').write_text('ESP-IDF v5.5.2 with submodules\nhttps://github.com/espressif/esp-idf/tree/v5.5.2\nSee the recursive SDK source archive published with this release.\n',encoding='utf-8')
if count<10:raise SystemExit('SDK notices missing; verify IDF_PATH and recursive checkout')
print('Collected',count,'SDK license/notice files')
