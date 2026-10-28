"""Sign a ClawPal application image for Wi-Fi updates (ESP-IDF Secure Boot V2 RSA scheme).

Displays accept a Wi-Fi update only when it is signed by the same key as the
firmware they are running (CONFIG_SECURE_SIGNED_ON_UPDATE_NO_SECURE_BOOT). USB
installs are not affected. The key's public digest is committed in
firmware/release-signing-key.sha256 so a release can never switch keys by
accident: devices running the old key would then only update over USB.

Usage: python scripts/sign-firmware.py IMAGE [--key PEM]   (default: $CLAWPAL_SIGNING_KEY)
       python scripts/sign-firmware.py --payload [--key PEM] (every packaged board, updating its hashes)
       python scripts/sign-firmware.py --init --key PEM     (first release: record the digest)
Needs esptool's espsecure, which ESP-IDF's Python environment provides.
"""
from pathlib import Path
import argparse, hashlib, json, os, subprocess, sys, tempfile

ROOT = Path(__file__).resolve().parents[1]
DIGEST = ROOT / 'firmware' / 'release-signing-key.sha256'


def espsecure(*arguments):
    result = subprocess.run([sys.executable, '-m', 'espsecure', *map(str, arguments)], capture_output=True, text=True)
    if result.returncode:
        sys.exit(f'espsecure {arguments[0]} failed:\n{result.stdout}{result.stderr}')
    return result.stdout


def key_digest(key: Path) -> str:
    with tempfile.TemporaryDirectory() as folder:
        out = Path(folder) / 'digest.bin'
        espsecure('digest_sbv2_public_key', '--keyfile', key, '--output', out)
        return out.read_bytes().hex()


def sign(image: Path, key: Path, digest: str):
    # An incremental build leaves an unchanged, already signed image in place.
    if subprocess.run([sys.executable, '-m', 'espsecure', 'verify_signature', '--version', '2', '--keyfile',
                       str(key), str(image)], capture_output=True).returncode == 0:
        print(f'{image.name} is already signed with key {digest[:16]}')
        return
    signed = image.with_suffix('.signed.bin')
    espsecure('sign_data', '--version', '2', '--keyfile', key, '--output', signed, image)
    espsecure('verify_signature', '--version', '2', '--keyfile', key, signed)
    signed.replace(image)
    print(f'Signed {image.name} with key {digest[:16]}')


def sign_payload(key: Path, digest: str):
    # CI signs the packaged payload in its own job, so the build job never sees the key.
    # Signing changes the image bytes; the board profiles carry their new SHA-256.
    catalog = json.loads((ROOT / 'boards/catalog.json').read_text(encoding='utf-8'))
    for model in catalog['Boards']:
        profile_file = ROOT / 'payload/boards' / f"{model['Id']}.json"
        profile = json.loads(profile_file.read_text(encoding='utf-8'))
        parts = [p for p in profile['Parts'] if p['File'].endswith('/clawpal.bin')]
        if len(parts) != 1:
            sys.exit(f"{profile_file.name} must list exactly one application image.")
        image = ROOT / 'payload' / parts[0]['File']
        if hashlib.sha256(image.read_bytes()).hexdigest() != parts[0]['Sha256']:
            sys.exit(f"{parts[0]['File']} does not match {profile_file.name}; package the build again.")
        sign(image, key, digest)
        data = image.read_bytes()
        if len(data) > model['MaxBytes']:
            sys.exit(f"Signed {parts[0]['File']} exceeds its OTA slot.")
        parts[0]['Sha256'] = hashlib.sha256(data).hexdigest()
        text = json.dumps(profile, indent=2) + '\n'
        profile_file.write_text(text, encoding='utf-8', newline='\n')
        if model['Id'] == catalog['Default']:
            (ROOT / 'payload/board.json').write_text(text, encoding='utf-8', newline='\n')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('image', type=Path, nargs='?')
    parser.add_argument('--key', type=Path, default=os.environ.get('CLAWPAL_SIGNING_KEY'))
    parser.add_argument('--init', action='store_true', help='record the public digest of --key')
    parser.add_argument('--payload', action='store_true', help='sign every packaged board image in payload/')
    args = parser.parse_args()
    if not args.key or not args.key.is_file():
        sys.exit('Set CLAWPAL_SIGNING_KEY to the release signing key (see docs/UPDATES.md).')
    digest = key_digest(args.key)
    if args.init:
        if DIGEST.exists() and DIGEST.read_text(encoding='ascii').strip() != digest:
            sys.exit(f'{DIGEST.relative_to(ROOT)} already names a different key; replacing it strands devices on USB updates.')
        DIGEST.write_text(digest + '\n', encoding='ascii', newline='\n')
        print(f'Recorded {DIGEST.relative_to(ROOT)}; commit it.')
        return
    if not DIGEST.exists():
        sys.exit(f'{DIGEST.relative_to(ROOT)} is missing; run: python scripts/sign-firmware.py --init --key <key.pem>')
    if DIGEST.read_text(encoding='ascii').strip() != digest:
        sys.exit('This is not the ClawPal release signing key: its public digest differs from '
                 f'{DIGEST.relative_to(ROOT)}. Devices would refuse the update.')
    if args.payload:
        sign_payload(args.key, digest)
        return
    if not args.image or not args.image.is_file():
        sys.exit('Image to sign not found.')
    sign(args.image, args.key, digest)


if __name__ == '__main__':
    main()
