"""Release packaging and publication. Python standard library only.

Locally it packages a release for review and pushes the release tag. GitHub
Actions (.github/workflows/release.yml) builds and signs that tag, then runs
`prepare --ci` and `publish --ci`, so there is exactly one publisher.
No credentials, build outputs or private files are added to Git. Published
application assets are never replaced. Interrupted draft uploads are resumable.
"""
from pathlib import Path
import argparse
import hashlib
import http.client
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REPO = 'Kaayoos/clawpal'
API = 'https://api.github.com/repos/' + REPO
SOURCE_NAME = 'ClawPal-Source-and-Licenses.zip'
SDK_NAME = 'esp-idf-v5.5.2-with-submodules.tar.gz'
SOURCES = {
    'esptool-v4.11.0.tar.gz': 'espressif/esptool/v4.11.0',
    'esptool-legacy-flasher-stub-v1.11.1.tar.gz': 'espressif/esptool-legacy-flasher-stub/v1.11.1',
    'esp-flasher-stub-v0.3.0.tar.gz': 'esp-rs/esp-flasher-stub/v0.3.0',
    'PresentMon-v2.6.0.tar.gz': 'GameTechDev/PresentMon/v2.6.0',
}
SOURCE_HASHES = {
    'esptool-v4.11.0.tar.gz': '9e82f518718ac66e1135e3cc8171b610cc81132275b349b0f681795be6b2b990',
    'PresentMon-v2.6.0.tar.gz': '384f4d8600ed64a7004b9aaefbc687db7526a0670f780eef65793340c56910d5',
    'esptool-legacy-flasher-stub-v1.11.1.tar.gz': 'f056a3f717178c6ec6f66a4560e32b23d180c67119dbb9d1da774845b49c2bfc',
    'esp-flasher-stub-v0.3.0.tar.gz': '3a933c6622a256b831f025eec664e06656df21ac9d139e37eb7e39595071a13f',
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def git(*args, cwd=ROOT, binary=False):
    result = subprocess.run(['git', *args], cwd=cwd, capture_output=True, check=True)
    return result.stdout if binary else result.stdout.decode('utf-8').strip()


def version():
    project = (ROOT / 'companion/ClawPal.Companion.csproj').read_text(encoding='utf-8')
    value = re.search(r'<Version>(\d+\.\d+\.\d+)</Version>', project)
    require(value, 'Companion version must be X.Y.Z.')
    return value.group(1)


def request(path, method='GET', data=None, token=None, optional=False):
    # Authenticated requests never follow redirects to another host.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None
    headers = {'User-Agent': 'ClawPal-local-release', 'Accept': 'application/vnd.github+json',
               'X-GitHub-Api-Version': '2022-11-28'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    body = None if data is None else json.dumps(data).encode()
    if body is not None:
        headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(API + path, data=body, headers=headers, method=method)
    try:
        with urllib.request.build_opener(NoRedirect).open(req, timeout=60) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if optional and error.code == 404:
            return None
        raise ValueError(f'GitHub {method} {path} returned HTTP {error.code}. Check account permissions or retry later.') from None


def auth():
    token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if token:
        return token
    # Reuse the Git login used for pushing this repository. Capture privately;
    # never log credential output, persist it or pass it on a command line.
    env = {**os.environ, 'GIT_TERMINAL_PROMPT': '0', 'GCM_INTERACTIVE': 'Never'}
    result = subprocess.run(['git', 'credential', 'fill'], cwd=ROOT, env=env,
                            input=f'protocol=https\nhost=github.com\npath={REPO}.git\n\n',
                            text=True, capture_output=True)
    fields = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
    if result.returncode == 0 and fields.get('password'):
        return fields['password']
    if shutil.which('gh'):
        result = subprocess.run(['gh', 'auth', 'token', '--hostname', 'github.com'], capture_output=True, text=True)
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    raise ValueError('Sign in to GitHub through Git Credential Manager or gh auth login, then retry. No token belongs in a file.')


def download(url, path, expected=None):
    path = Path(path)
    if path.is_file() and expected and sha(path) == expected:
        return
    parsed = urllib.parse.urlparse(url)
    require(parsed.scheme == 'https' and parsed.hostname in ('github.com', 'codeload.github.com'), 'Unexpected source download host.')
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(path.name + '.partial')
    req = urllib.request.Request(url, headers={'User-Agent': 'ClawPal-local-release'})
    with urllib.request.urlopen(req, timeout=120) as response, partial.open('wb') as output:
        shutil.copyfileobj(response, output)
    require(not expected or sha(partial) == expected, f'Checksum mismatch for {path.name}.')
    partial.replace(path)


def checksum_map(text):
    entries = {}
    for line in text.splitlines():
        if not line.strip():
            continue
        match = re.fullmatch(r'([0-9a-fA-F]{64})\s+\*?(?:dist/sources/)?([A-Za-z0-9_.-]+)', line)
        require(match is not None, 'Unrecognized source checksum entry.')
        digest, name = match.groups()
        require(name not in entries, 'Duplicate source checksum entry.')
        entries[name] = digest.lower()
    return entries


def source_archive(name, destination, expected=None):
    require(name in SOURCES, 'Unknown dependency source.')
    expected = expected or SOURCE_HASHES.get(name)
    require(expected, 'Missing pinned source checksum for ' + name)
    cached = ROOT / 'dist/sources' / name
    if cached.is_file() and sha(cached) == expected:
        destination.parent.mkdir(parents=True, exist_ok=True)
        if cached.resolve() != destination.resolve():
            shutil.copyfile(cached, destination)
    else:
        repo, tag = SOURCES[name].rsplit('/', 1)
        download(f'https://github.com/{repo}/archive/refs/tags/{tag}.tar.gz', destination, expected)
    with tarfile.open(destination, 'r:gz') as archive:
        require(any(member.isfile() for member in archive), 'Empty source archive: ' + name)


def preflight(ci=False):
    if ci:
        # Actions checks out the pushed tag, detached; it must name this version and be on main.
        require(os.environ.get('GITHUB_REF') == 'refs/tags/v' + version(), 'The pushed tag does not match the companion version.')
        require(subprocess.run(['git', 'merge-base', '--is-ancestor', 'HEAD', 'origin/main'], cwd=ROOT).returncode == 0,
                'Release tags must point to a commit on main.')
    else:
        require(git('branch', '--show-current') == 'main', 'Release from the main branch.')
    origin = git('remote', 'get-url', 'origin').lower().rstrip('/')
    require(origin in ('https://github.com/kaayoos/clawpal.git', 'https://github.com/kaayoos/clawpal',
                       'git@github.com:kaayoos/clawpal.git'), 'Origin must be Kaayoos/clawpal.')
    require(not git('status', '--porcelain'), 'Commit your reviewed source changes before preparing a release. Nothing is auto-staged.')
    subprocess.run([sys.executable, str(ROOT / 'scripts/check-publication.py')], cwd=ROOT, check=True)
    return version(), git('rev-parse', 'HEAD')


def validate_payload(folder, ver, catalog):
    folder = Path(folder)
    manifest = json.loads((folder / 'clawpal-release.json').read_text(encoding='utf-8-sig'))
    require(manifest['schema'] == 2 and manifest['version'] == ver, 'Wrong release manifest version.')
    models = {board['Id']: board for board in catalog['Boards']}
    rows = manifest['boards']
    require(len(rows) == len(models) and {row['board'] for row in rows} == set(models), 'Missing or duplicate board firmware.')
    names = ['ClawPal-Setup.exe', 'ClawPal-Setup.exe.sha256', 'clawpal-release.json']
    setup = folder / names[0]
    require(setup.read_bytes()[:2] == b'MZ', 'Invalid Windows installer.')
    sums = checksum_map((folder / names[1]).read_text(encoding='utf-8-sig'))
    require(sums == {setup.name: sha(setup)}, 'Installer checksum mismatch.')
    for row in rows:
        model = models[row['board']]
        item = row['firmware']
        require(row['schema'] == 1 and row['version'] == ver and row['layout'] == model['Layout'], 'Wrong board metadata.')
        require(item['file'] == model['AssetName'], 'Wrong firmware asset name.')
        image = folder / item['file']
        data = image.read_bytes()
        require(288 <= len(data) <= model['MaxBytes'] and len(data) == item['bytes'], 'Wrong firmware image size.')
        require(data[0] == 0xe9 and data[12:14] == b'\x09\x00', 'Wrong ESP32 chip image.')
        require((model['Id'] + '\0').encode() in data and (ver + '\0').encode() in data, 'Firmware identity/version mismatch.')
        require(sha(image) == item['sha256'], 'Firmware checksum mismatch.')
        names.append(image.name)
    return names


def sdk_archive(destination):
    sdk = Path(os.environ.get('IDF_PATH', ''))
    require((sdk / 'tools/idf.py').is_file(), 'Export the ESP-IDF 5.5.2 environment first.')
    require(git('rev-parse', 'HEAD', cwd=sdk) == git('rev-parse', 'v5.5.2^{commit}', cwd=sdk), 'SDK must be the exact v5.5.2 checkout.')
    # Windows cannot preserve POSIX executable bits in its checkout. Ignore only
    # that difference for this check, and restore modes from Git in the tar file.
    require(not git('-c', 'core.filemode=false', 'status', '--porcelain', '--untracked-files=no', cwd=sdk), 'SDK source has tracked modifications.')
    submodules = git('submodule', 'status', '--recursive', cwd=sdk)
    require(all(line[0] not in '-+U' for line in submodules.splitlines() if line), 'Initialize all matching SDK submodules first.')
    files = git('ls-files', '--stage', '--recurse-submodules', '-z', cwd=sdk, binary=True).split(b'\0')
    partial = destination.with_name(destination.name + '.partial')
    print('Archiving the exact SDK source and submodules...', flush=True)
    with tarfile.open(partial, 'w:gz') as archive:
        for raw in files:
            if raw:
                metadata, name = raw.decode('utf-8').split('\t', 1)
                mode, _, stage = metadata.split()
                require(stage == '0' and mode in ('100644', '100755', '120000'), 'Unresolved SDK source entry.')
                path = sdk / name
                require(path.is_file() or path.is_symlink(), 'Missing SDK source: ' + name)
                info = archive.gettarinfo(str(path), arcname='idf/' + name)
                info.uid = info.gid = info.mtime = 0
                info.uname = info.gname = ''
                if mode == '120000':
                    info.type = tarfile.SYMTYPE
                    info.size = 0
                    info.mode = 0o777
                    info.linkname = os.readlink(path) if path.is_symlink() else path.read_text(encoding='utf-8')
                    archive.addfile(info)
                else:
                    info.mode = 0o755 if mode == '100755' else 0o644
                    with path.open('rb') as stream:
                        archive.addfile(info, stream)
    partial.replace(destination)


def prepare(output=None, ci=False):
    ver, commit = preflight(ci)
    catalog = json.loads((ROOT / 'boards/catalog.json').read_text(encoding='utf-8'))
    dist = ROOT / 'dist'
    names = validate_payload(dist, ver, catalog)
    output = Path(output) if output is not None else dist / 'releases' / ('v' + ver)
    assets = output / 'assets'
    assets.mkdir(parents=True, exist_ok=True)
    notes = (ROOT / 'docs/releases' / (ver + '.md')).read_text(encoding='utf-8')
    sources = output / 'source-work'
    sources.mkdir(exist_ok=True)
    for name in SOURCES:
        source_archive(name, sources / name)
    sdk_archive(sources / SDK_NAME)
    git('archive', '--format=zip', '--output=' + str(sources / 'ClawPal-source.zip'), commit)
    shutil.copyfile(dist / 'ClawPal-licenses.zip', sources / 'ClawPal-licenses.zip')
    source_names = [*SOURCES, SDK_NAME, 'ClawPal-source.zip', 'ClawPal-licenses.zip']
    sums = ''.join(sha(sources / name) + '  ' + name + '\n' for name in source_names)
    with zipfile.ZipFile(assets / (SOURCE_NAME + '.partial'), 'w', compression=zipfile.ZIP_STORED) as bundle:
        for name in source_names:
            bundle.write(sources / name, name)
        bundle.writestr('SHA256SUMS', sums)
        bundle.writestr('README.txt', 'Source and license materials for ClawPal ' + ver + '\nCommit: ' + commit +
                        '\nExtract ClawPal-source.zip; see docs/BUILDING.md for rebuild instructions.\n'
                        'Dependency sources and their original licenses are retained alongside it.\n')
    (assets / (SOURCE_NAME + '.partial')).replace(assets / SOURCE_NAME)
    for name in names:
        shutil.copyfile(dist / name, assets / name)
    names.append(SOURCE_NAME)
    plan = {'version': ver, 'commit': commit, 'assets': {name: sha(assets / name) for name in names},
            'notes': '**Download ClawPal-Setup.exe to get started. Everything needed is included.**\n\n'
                     'The other assets support automatic firmware updates and source/license availability.\n\n' + notes}
    (output / 'release-plan.json').write_text(json.dumps(plan, indent=2) + '\n', encoding='utf-8')
    print(f'Ready locally: {assets}\n{len(names)} upload assets. No files have been pushed or published.')


def validate_bundle(file):
    required = set(SOURCES) | {SDK_NAME, 'ClawPal-source.zip', 'ClawPal-licenses.zip'}
    with zipfile.ZipFile(file) as bundle:
        require(len(bundle.namelist()) == len(required) + 2 and
                set(bundle.namelist()) == required | {'SHA256SUMS', 'README.txt'}, 'Incomplete source/license bundle.')
        sums = checksum_map(bundle.read('SHA256SUMS').decode('utf-8'))
        require(set(sums) == required, 'Incomplete source/license checksums.')
        for name, digest in sums.items():
            with bundle.open(name) as stream:
                require(hashlib.file_digest(stream, 'sha256').hexdigest() == digest, 'Source checksum mismatch: ' + name)


def ensure_asset(release_id, file, token):
    # Idempotent retries: inspect first, skip identical bytes, never clobber.
    remote = request(f'/releases/{release_id}/assets?per_page=100', token=token)
    matches = [a for a in remote if a['name'] == file.name]
    digest = 'sha256:' + sha(file)
    if matches:
        require(len(matches) == 1 and matches[0].get('digest') == digest and matches[0]['size'] == file.stat().st_size,
                'An existing release asset differs: ' + file.name + '. No replacement was attempted.')
        print('Already uploaded:', file.name, flush=True)
        return
    print('Uploading:', file.name, flush=True)
    connection = http.client.HTTPSConnection('uploads.github.com', timeout=600)
    try:
        path = f'/repos/{REPO}/releases/{release_id}/assets?name=' + urllib.parse.quote(file.name)
        with file.open('rb') as stream:
            connection.request('POST', path, body=stream, headers={
                'Authorization': 'Bearer ' + token, 'User-Agent': 'ClawPal-local-release',
                'Content-Type': 'application/octet-stream', 'Content-Length': str(file.stat().st_size),
                'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28'})
            response = connection.getresponse()
            data = response.read()
        require(response.status == 201, f'Upload of {file.name} returned HTTP {response.status}. Retry the same package.')
        asset = json.loads(data)
        require(asset.get('digest') == digest and asset['size'] == file.stat().st_size,
                'GitHub upload checksum mismatch: ' + file.name)
    finally:
        connection.close()


def push_tag():
    # Local half of a release: GitHub Actions builds, signs and publishes the pushed tag.
    ver, commit = preflight()
    require((ROOT / 'docs/releases' / (ver + '.md')).is_file(), f'Write docs/releases/{ver}.md before releasing.')
    tag = 'v' + ver
    existing = request('/releases/tags/' + tag, optional=True)
    require(not existing or existing['draft'], 'This version is already published. Bump the version for new binaries.')
    # Check a tag before pushing anything. Never rewrite public or local tags.
    local_tag = subprocess.run(['git', 'rev-parse', '--verify', f'refs/tags/{tag}^{{commit}}'], cwd=ROOT, capture_output=True, text=True)
    require(local_tag.returncode != 0 or local_tag.stdout.strip() == commit, 'Existing tag points to another commit.')
    remote_tag = git('ls-remote', '--tags', 'origin', f'refs/tags/{tag}', f'refs/tags/{tag}^{{}}')
    if remote_tag:
        refs = dict(line.split('\t', 1)[::-1] for line in remote_tag.splitlines())
        require(refs.get(f'refs/tags/{tag}^{{}}', refs.get(f'refs/tags/{tag}')) == commit, 'Remote tag points to another commit.')
    git('push', 'origin', 'HEAD:refs/heads/main')
    if local_tag.returncode != 0:
        git('tag', '-a', tag, '-m', 'ClawPal ' + ver)
    git('push', 'origin', 'refs/tags/' + tag)
    print(f'Pushed {tag}. GitHub Actions is building it; approve the "release" environment there to sign\n'
          f'and publish it: https://github.com/{REPO}/actions/workflows/release.yml')


def publish():
    # Runs in GitHub Actions on the pushed tag, after `prepare --ci` in the same job.
    ver, commit = preflight(ci=True)
    output = ROOT / 'dist/releases' / ('v' + ver)
    plan = json.loads((output / 'release-plan.json').read_text(encoding='utf-8'))
    require(plan['version'] == ver and plan['commit'] == commit, 'Prepared package does not match this commit. Rebuild first.')
    assets = output / 'assets'
    catalog = json.loads((ROOT / 'boards/catalog.json').read_text(encoding='utf-8'))
    expected = set(validate_payload(assets, ver, catalog)) | {SOURCE_NAME}
    require(set(plan['assets']) == expected, 'Unexpected or missing files in release plan.')
    for name, digest in plan['assets'].items():
        require(sha(assets / name) == digest, 'Prepared asset changed: ' + name)
    validate_bundle(assets / SOURCE_NAME)
    token = auth()
    tag = 'v' + ver
    release = request('/releases/tags/' + tag, token=token, optional=True)
    require(not release or release['draft'], 'This version is already published. Bump the version for new binaries.')
    if not release:
        release = request('/releases', method='POST', token=token, data={
            'tag_name': tag, 'target_commitish': commit, 'name': 'ClawPal ' + tag,
            'body': plan['notes'], 'draft': True, 'prerelease': False, 'generate_release_notes': False})
    for name in plan['assets']:
        ensure_asset(release['id'], assets / name, token)
    # The updater only sees a release after every upload and hash check succeeded.
    remote = request(f"/releases/{release['id']}/assets?per_page=100", token=token)
    require({a['name'] for a in remote} == expected, 'Draft contains unexpected assets; review it before publication.')
    release = request(f"/releases/{release['id']}", method='PATCH', token=token,
                      data={'draft': False, 'make_latest': 'true', 'body': plan['notes']})
    print('Published:', release['html_url'])


def repair(publish_now=False):
    ver = version()
    release = request('/releases/tags/v' + ver)
    assets = {a['name']: a for a in release['assets']}
    require('SHA256SUMS' in assets, 'This repair is for the original separate-source release format.')
    output = ROOT / 'dist/release-repair' / ('v' + ver)
    sums_file = output / 'SHA256SUMS'
    item = assets['SHA256SUMS']
    require(item.get('digest', '').startswith('sha256:'), 'GitHub checksum metadata is missing.')
    download(item['browser_download_url'], sums_file, item['digest'][7:])
    sums = checksum_map(sums_file.read_text(encoding='utf-8-sig'))
    require(set(sums) == set(SOURCES) | {SDK_NAME}, 'Unexpected original release source manifest.')
    missing = []
    for name, expected in sums.items():
        if name in assets:
            require(assets[name].get('digest') == 'sha256:' + expected, 'Published source differs from its checksum: ' + name)
            continue
        require(name in SOURCES, 'Missing SDK archive must be recovered from the original build artifact.')
        destination = output / name
        source_archive(name, destination, expected)
        missing.append(destination)
    if not missing:
        print('Source archives are complete; no repair needed.')
        return
    print('Recovered locally: ' + ', '.join(file.name for file in missing))
    if publish_now:
        token = auth()
        for file in missing:
            ensure_asset(release['id'], file, token)
        print('Release source archives restored. Installer, firmware, tag and settings are unchanged.')
    else:
        print('To restore them to GitHub: .\\scripts\\release.ps1 -RepairCurrent -Publish')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['preflight', 'prepare', 'tag', 'publish', 'repair'])
    parser.add_argument('--publish', action='store_true')
    parser.add_argument('--ci', action='store_true', help='run in GitHub Actions on the pushed release tag')
    args = parser.parse_args()
    if args.action == 'preflight':
        ver, _ = preflight()
        print('Ready to build ClawPal ' + ver + ' locally.')
    elif args.action == 'prepare':
        prepare(ci=args.ci)
    elif args.action == 'tag':
        push_tag()
    elif args.action == 'publish':
        require(args.ci, r'GitHub Actions publishes releases; run .\scripts\release.ps1 -Publish to push the tag.')
        publish()
    else:
        repair(args.publish)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError, KeyError) as error:
        # HTTP request errors above are sanitized; credentials never appear here.
        print('Release stopped: ' + str(error), file=sys.stderr)
        sys.exit(1)
