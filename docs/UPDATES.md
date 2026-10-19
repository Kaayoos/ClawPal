# Local releases

Build on your Windows PC and publish the finished files directly to
**https://github.com/Kaayoos/clawpal**. GitHub Actions checks source changes;
it does not build or publish a second release when you push a tag.

## One command after committing your changes

From an ESP-IDF 5.5.2 PowerShell environment, with Python 3.12, .NET SDK 8.0.425
and Git available:

```powershell
.\scripts\release.ps1 -Publish
```

This builds all seven firmware profiles, runs the display simulations and
companion tests, builds the installer, and packages the complete release locally.
It then pushes the committed main branch and matching annotated tag, uploads
the exact asset list to a draft, checks the uploads, and publishes the release.
There is no manual file selection, checksum editing or GitHub Actions wait.
It reuses your Git Credential Manager login. GitHub CLI authentication or a
process-level GH_TOKEN/GITHUB_TOKEN also works; never save a token in this repo.

For a local package without publishing, omit `-Publish`:

```powershell
.\scripts\release.ps1
```

The reviewable output is `dist/releases/vX.Y.Z/assets/`. A release plan and notes
sit beside that folder. After reviewing it, or to resume an interrupted upload:

```powershell
.\scripts\release.ps1 -UploadOnly
```

Uploads resume by skipping files whose size and SHA-256 match. Existing files
with different bytes are never overwritten. A failed upload leaves a draft;
the updater cannot see it until all files are uploaded and verified.

## Preparing a new version

Update the firmware and companion version, versioned user agent/log strings,
and `docs/releases/X.Y.Z.md`. Review and commit the intended source changes.
The release command requires a clean main branch and runs the publication audit;
it never uses `git add .`, creates commits or force-pushes. Build-generated
changes to the tracked default board profile are restored after packaging.

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

Checks propose updates; they do not install automatically. PC updates retain
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
