"""Audit the Git publication boundary without printing possible secret values."""
from pathlib import Path
import re,subprocess,sys
root=Path(__file__).resolve().parents[1]
raw=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=root)
files=sorted(set(p.decode('utf-8') for p in raw.split(b'\0') if p));fail=[]
# Deliberate publication boundary: local side projects must never pass merely
# because they contain no recognizable secret. Review additions to this list.
publish_dirs={'.github','assets','boards','companion','docs','firmware','languages','LICENSES','payload','scripts','tests','themes','vendor'}
publish_files={'.gitattributes','.gitignore','AUTHORS.md','CONTRIBUTING.md','LICENSE','NOTICE','README.md','THIRD_PARTY_NOTICES.md','TRADEMARKS.md','global.json'}

# Upstream LVGL bundles demo fonts whose licenses forbid redistribution. ClawPal
# never uses them; keep them out when the vendored copy is refreshed.
restricted={'vendor/components/lvgl__lvgl/src/extra/libs/freetype/arial.ttf','vendor/components/lvgl__lvgl/src/font/korean.ttf','vendor/components/lvgl__lvgl/scripts/built_in_font/SimSun.woff'}
secret=re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|sk-(?:proj-)?[A-Za-z0-9_-]{32,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)')
private=re.compile(rb'(?:[A-Z]:[\\/]+Users[\\/]+(?!Public(?:[\\/]|$)|<)[A-Za-z0-9_.-]+|/home/(?!runner/|user/)[A-Za-z0-9_.-]+/)',re.I)
for name in files:
 if ('/' in name and name.split('/',1)[0] not in publish_dirs) or ('/' not in name and name not in publish_files):
  fail.append((name,'outside the app publication boundary'));continue
 if name in restricted:fail.append((name,'font licensed against redistribution'));continue
 p=root/name
 if not p.is_file():continue
 if p.suffix.lower() in ('.exe','.dll','.pdb','.bin','.log','.rgb565','.pfx','.key') or p.stat().st_size>40*1024*1024:fail.append((name,'unexpected binary/build/private file'));continue
 if any(part in ('.secrets','.clawpal','.test-output','backups','.codex','.impeccable') for part in p.relative_to(root).parts):fail.append((name,'private directory'));continue
 data=p.read_bytes()
 if re.match(r'(?:validation|diagnostics)-.*\.(?:txt|json)$',p.name,re.I):fail.append((name,'private diagnostic report'))
 if secret.search(data):fail.append((name,'possible credential/private key'))
 if p.suffix.lower() not in ('.png','.ico','.ttf','.woff','.woff2') and not name.startswith('vendor/') and private.search(data):fail.append((name,'personal absolute path'))
for name,reason in fail:print(f'{name}: {reason}')
print(f'{len(files)} publication candidates; {len(fail)} findings')
sys.exit(bool(fail))
