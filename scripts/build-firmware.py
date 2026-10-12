"""Build one exact board, or the complete release matrix, inside ESP-IDF 5.5.2."""
from pathlib import Path
import argparse,json,os,subprocess,sys
root=Path(__file__).resolve().parents[1]
catalog=json.loads((root/'boards/catalog.json').read_text(encoding='utf-8'))
p=argparse.ArgumentParser();g=p.add_mutually_exclusive_group();g.add_argument('--board',default=catalog['Default']);g.add_argument('--all',action='store_true');args=p.parse_args()
ids=[b['Id'] for b in catalog['Boards']];selected=ids if args.all else [args.board]
if any(b not in ids for b in selected):p.error('Unknown exact board ID')
idf=Path(os.environ['IDF_PATH'])/'tools/idf.py';os.environ['IDF_COMPONENT_MANAGER']='0'
subprocess.run([sys.executable,str(root/'scripts/generate-boards.py'),'--check'],check=True)
subprocess.run([sys.executable,str(root/'scripts/generate_languages.py'),'--check'],check=True)
for board in selected:
 build=root/'firmware'/('build-'+board)
 subprocess.run([sys.executable,str(idf),'-C',str(root/'firmware'),'-B',str(build),'-DCLAWPAL_BOARD='+board,'-DSDKCONFIG='+str(build/'sdkconfig'),'build'],check=True)
 subprocess.run([sys.executable,str(root/'scripts/package-firmware.py'),'--board',board,'--build',str(build)],check=True)
subprocess.run([sys.executable,str(root/'scripts/collect-sdk-licenses.py')],check=True)
