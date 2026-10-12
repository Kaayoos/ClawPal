"""Package an exact board build; reject stale/wrong-board binaries and offsets."""
from pathlib import Path
import argparse,hashlib,json,shutil,struct
root=Path(__file__).resolve().parents[1]
catalog=json.loads((root/'boards/catalog.json').read_text(encoding='utf-8'))
parser=argparse.ArgumentParser();parser.add_argument('--board',default=catalog['Default']);parser.add_argument('--build',type=Path);args=parser.parse_args()
model=next((b for b in catalog['Boards'] if b['Id']==args.board),None)
if model is None:raise SystemExit('Unknown exact board ID')
build=args.build or root/'firmware'/('build-'+args.board)
metadata=json.loads((build/'project_description.json').read_text(encoding='utf-8'))
config=json.loads((build/'config/sdkconfig.json').read_text(encoding='utf-8'))
if config['ESPTOOLPY_FLASHSIZE']!=str(model['FlashBytes']//1048576)+'MB':raise SystemExit('Wrong build flash size')
image=(build/'clawpal.bin').read_bytes()
if len(image)>model['MaxBytes'] or (model['Id']+'\0').encode() not in image:raise SystemExit('Wrong board identity or oversized OTA image')
sub='firmware' if args.board==catalog['Default'] else 'firmware/'+args.board
target=root/'payload'/sub;target.mkdir(parents=True,exist_ok=True)
offsets={'bootloader.bin':'0x0','partition-table.bin':'0x8000','clawpal.bin':'0x10000' if model['FlashBytes']==16777216 else '0x20000','ota-data-initial.bin':'0x800000' if model['FlashBytes']==16777216 else '0xf000'}
files=[('bootloader/bootloader.bin','bootloader.bin'),('partition_table/partition-table.bin','partition-table.bin'),('clawpal.bin','clawpal.bin'),('ota_data_initial.bin','ota-data-initial.bin')]
flash=json.loads((build/'flasher_args.json').read_text(encoding='utf-8'))['flash_files']
expected={int(v,16) for v in offsets.values()}
if {int(v,16) for v in flash}!=expected:raise SystemExit('Build offsets differ from board installation profile')
parts=[]
for source,dest in files:
 data=(build/source).read_bytes();shutil.copyfile(build/source,target/dest)
 parts.append(dict(File=sub+'/'+dest,Offset=offsets[dest],Sha256=hashlib.sha256(data).hexdigest()))
tool_hash='71B8C5E129A44E26851BD8E59DC8D299A1EB07BBD445A8E0A49ED64A0444ED1A'
board={**model,'Version':metadata['project_version'],'ToolSha256':tool_hash,'Parts':parts}
out=root/'payload/boards'/f'{args.board}.json';out.parent.mkdir(parents=True,exist_ok=True);out.write_text(json.dumps(board,indent=2)+'\n',encoding='utf-8',newline='\n')
if args.board==catalog['Default']:(root/'payload/board.json').write_text(json.dumps(board,indent=2)+'\n',encoding='utf-8',newline='\n')
print('Packaged',args.board,board['Version'],len(image),'bytes')
