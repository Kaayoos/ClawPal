"""Copy the reviewed source-tree notices into the offline installer payload."""
from pathlib import Path
import shutil
root=Path(__file__).resolve().parents[1];out=root/'payload/licenses';out.mkdir(parents=True,exist_ok=True)
if not (out/'esp-idf/LICENSE').exists():raise SystemExit('Build firmware and run scripts/collect-sdk-licenses.py first')
shutil.copytree(root/'LICENSES',out,dirs_exist_ok=True)
for name in ('LICENSE','NOTICE','TRADEMARKS.md','AUTHORS.md','THIRD_PARTY_NOTICES.md'):
 shutil.copyfile(root/name,out/(name.replace('.md','.txt')))
for component in (root/'vendor/components').iterdir():
 for p in component.iterdir():
  if p.is_file() and p.name.upper().startswith(('LICENSE','LICENCE','NOTICE','COPYING')):
   dest=out/'vendor'/component.name/p.name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(p,dest)
shutil.copyfile(root/'payload/tools/PresentMon/SOURCE.txt',out/'PresentMon-SOURCE.txt')
print('Offline licenses and credits prepared')
