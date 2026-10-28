"""Sign a ClawPal application image for Wi-Fi updates (ESP-IDF Secure Boot V2 RSA scheme).

Displays accept a Wi-Fi update only when it is signed by the same key as the
firmware they are running (CONFIG_SECURE_SIGNED_ON_UPDATE_NO_SECURE_BOOT). USB
installs are not affected. The key's public digest is committed in
firmware/release-signing-key.sha256 so a release can never switch keys by
accident: devices running the old key would then only update over USB.

Usage: python scripts/sign-firmware.py IMAGE [--key PEM]   (default: $CLAWPAL_SIGNING_KEY)
       python scripts/sign-firmware.py --init --key PEM     (first release: record the digest)
Needs esptool's espsecure, which ESP-IDF's Python environment provides.
"""
from pathlib import Path
import argparse, os, subprocess, sys, tempfile

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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('image', type=Path, nargs='?')
    parser.add_argument('--key', type=Path, default=os.environ.get('CLAWPAL_SIGNING_KEY'))
    parser.add_argument('--init', action='store_true', help='record the public digest of --key')
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
    if not args.image or not args.image.is_file():
        sys.exit('Image to sign not found.')
    # An incremental build leaves an unchanged, already signed image in place.
    if subprocess.run([sys.executable, '-m', 'espsecure', 'verify_signature', '--version', '2', '--keyfile',
                       str(args.key), str(args.image)], capture_output=True).returncode == 0:
        print(f'{args.image.name} is already signed with key {digest[:16]}')
        return
    signed = args.image.with_suffix('.signed.bin')
    espsecure('sign_data', '--version', '2', '--keyfile', args.key, '--output', signed, args.image)
    espsecure('verify_signature', '--version', '2', '--keyfile', args.key, signed)
    signed.replace(args.image)
    print(f'Signed {args.image.name} with key {digest[:16]}')


if __name__ == '__main__':
    main()
