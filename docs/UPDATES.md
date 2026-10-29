# Releases

GitHub Actions builds, signs and publishes every release to
**https://github.com/Kaayoos/clawpal** from a pushed `vX.Y.Z` tag
(`.github/workflows/release.yml`). It is the only publisher; nothing is
uploaded from a PC.

## One command after committing your changes

With Python 3.12 and Git available:

```powershell
.\scripts\release.ps1 -Publish
```

This checks the clean main branch, the publication audit, the release notes and
any existing tag, then pushes main and the matching annotated tag. It reuses your
Git Credential Manager login. GitHub then:

1. builds all seven firmware profiles and checks the parsers;
2. waits for approval in the `release` environment, then signs the firmware in
   a separate job that is the only one to receive the signing key;
3. runs the display simulations and companion tests, builds the installer
   around the signed firmware and attests the installer and firmware;
4. packages the source bundle from the same ESP-IDF container, uploads the
   exact asset list to a draft, checks the uploads, and publishes the release.

Open the run under **Actions → Release ClawPal** and choose **Review
deployments → Approve** when it reaches the signing job. If a later step fails,
use **Re-run failed jobs**: it reuses the signed files, and uploads resume by
skipping files whose size and SHA-256 match. Existing files with different bytes
are never overwritten, so after **Re-run all jobs** (which signs new bytes)
delete the leftover draft first. The updater cannot see a draft.

For a local package to review or test, without publishing, run this from an
ESP-IDF 5.5.2 PowerShell environment with .NET SDK 8.0.425:

```powershell
.\scripts\release.ps1
```

The output is `dist/releases/vX.Y.Z/assets/`, with a release plan and notes
beside it. Its firmware is signed only if `CLAWPAL_SIGNING_KEY` points to the key.

## Signing keys

**Firmware (required for releases).** Displays only accept a Wi-Fi update signed
with the key that signed the firmware they run. Create the key once, offline,
and keep two backups somewhere safe: losing it means every display can only be
updated over USB.

```powershell
python -m espsecure generate_signing_key --version 2 --scheme rsa3072 clawpal-release-key.pem
python scripts/sign-firmware.py --init --key clawpal-release-key.pem
```

Commit the generated `firmware/release-signing-key.sha256` (the public digest,
not the key). Signing refuses a key whose digest differs, so a release cannot
switch keys by accident. Never commit the `.pem`.

Give GitHub the key once, under **Settings → Environments → New environment**:

1. Name it `release`.
2. Under **Required reviewers**, add yourself. Every release then waits for your
   approval before the key is used.
3. Under **Deployment branches and tags**, choose **Selected branches and tags**
   and add the tag rule `v*`, so no branch or pull request can use it.
4. Under **Environment secrets**, add `CLAWPAL_SIGNING_KEY` with the full contents
   of `clawpal-release-key.pem`, including the BEGIN and END lines.

Keep the key only there and in your offline backups. Anyone who controls your
GitHub account can approve a release, so protect it with a security key or an
authenticator app. A tag ruleset that limits who can create `v*` tags adds
another check. Pull requests, forks and branch builds never receive the key;
their firmware is unsigned and installs over USB only.

The first release with signed firmware also starts signed LAN requests, which
older companions cannot send. `package-release.ps1` therefore sets
`min_companion` to 0.10.0 for it and every later release, so the PC updates first.

**Installer (recommended).** Without a code-signing certificate Windows
SmartScreen warns on first launch. `build.ps1` signs `ClawPal-Setup.exe` before
it is hashed when one of these is set: `CLAWPAL_SIGN_THUMBPRINT` (a certificate
in your CurrentUser store, including EV hardware tokens) or `CLAWPAL_SIGN_DLIB`
with `CLAWPAL_SIGN_METADATA` (Azure Trusted Signing). It needs `signtool.exe`
from the Windows SDK. Pass `-RequireCodeSigning` to `build.ps1` to fail instead
of warning. The release workflow does not set these yet, so released installers
are unsigned until a certificate is added to it. Open-source projects can apply
for a free certificate through the SignPath Foundation.

**Provenance.** CI attests the installer and firmware it builds on `main` and on
release tags (`gh attestation verify <file> --repo Kaayoos/clawpal`).

## Preparing a new version

Update the firmware and companion version, versioned user agent/log strings,
and `docs/releases/X.Y.Z.md`. Review and commit the intended source changes.
The release command requires a clean main branch and runs the publication audit;
it never uses `git add .`, creates commits or force-pushes. The release workflow
also refuses a tag that does not match the companion version or is not on main.
Build-generated changes to the tracked default board profile are restored after
local packaging.

Already-published versions cannot receive replacement installers or firmware.
Use a new version for application changes. Tags are created only if absent and
must already point to the current commit if they exist.

The build environment can optionally be configured once in the ignored file
`.tools/release-env.ps1`. It may export ESP-IDF and assign `$Python` and `$Dotnet`
to local tool paths. Do not commit machine-specific paths or credentials.

## Repair the original 0.8.1 release

If dependency source archives were accidentally removed, this recovers their
exact original bytes using the release's existing SHA256SUMS, without a rebuild:

```powershell
.\scripts\release.ps1 -RepairCurrent -Publish
```

Omit `-Publish` to recover the files locally first. Repair only appends missing
source archives with matching checksums. It does not replace the installer,
firmware, tag, release text, or existing assets. Nothing needs to be installed
on the PC or display again. A missing SDK archive needs the original build
artifact; the repair command will not substitute an unrelated SDK package.

## What people download

**Users download only ClawPal-Setup.exe.** Firmware for every supported board,
the runtime, flashing tools, PresentMon and notices are already included.

There are eleven deliberately selected upload assets:

- `ClawPal-Setup.exe` and `ClawPal-Setup.exe.sha256`.
- `clawpal-release.json`.
- Seven `clawpal-*.bin` files, one for each board profile.
- `ClawPal-Source-and-Licenses.zip` containing ClawPal source, licenses, the
  matching SDK with submodules, tool/stub source archives and their checksums.

Keep these eleven files together. Do not manually prune release assets.
The separate firmware filenames and JSON are required by existing updaters;
source/license materials are combined so they no longer clutter the asset list.
Source archives are made from committed tracked files; local promo projects,
credentials, builds and device backups are excluded.

## Update format and behavior

The only official feed is `Kaayoos/clawpal/releases/latest`. Stable releases
use a matching `vX.Y.Z` tag and schema-2 manifest with all catalog profiles.
Each board entry contains its board ID, layout, version, minimum companion
version, firmware filename, byte count and SHA-256. Missing boards, mismatched
tags, chips, sizes or hashes are rejected. Existing single-board manifests
remain understood by the companion.

Checks propose updates; they do not install automatically. Since 0.10.0 the partition
table has a 256 KiB `coredump` partition at the end of the unused storage area. Wi-Fi
updates keep a display's existing table; it gains crash reports at its next USB install. PC updates retain
configuration. Wi-Fi firmware updates write the inactive application slot,
preserve settings and wait for boot confirmation. A bootloader or partition
change may still require USB. Keep power connected during an update.

Original 16 MB profiles use `ota-v1` with a 0x3f0000-byte minimum OTA slot;
the 8 MB profile uses `ota-8m-v1` with 0x300000-byte slots. The companion chooses
the exact paired board ID and offers a PC update first if `min_companion`
requires it. Ordinary updates must preserve those layout contracts.

Firmware 0.9.0 and later requires an on-screen **Allow** before Wi-Fi or
firmware changes. These releases use `min_companion: 0.9.0`, so older
companions update before their display.
