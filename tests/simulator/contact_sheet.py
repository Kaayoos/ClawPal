"""Turn simulator frames into PNGs and one review page. Python 3.10+, stdlib only.

Usage: python tests/simulator/contact_sheet.py [--baseline DIR]
Reads .sim-output/<profile>/frames/<theme>/*.ppm and writes PNGs beside them plus
.sim-output/contact-sheet.html (scenes down, profile x theme across). With --baseline,
frames of the crab theme must match DIR/<profile>/<scene>.ppm byte for byte.
"""
import argparse, html, pathlib, struct, sys, zlib

REPO = pathlib.Path(__file__).resolve().parent.parent.parent
OUT = REPO / ".sim-output"


def read_ppm(path):
    data = path.read_bytes()
    fields, pos = [], 0
    while len(fields) < 4:
        while data[pos:pos + 1].isspace(): pos += 1
        start = pos
        while not data[pos:pos + 1].isspace(): pos += 1
        fields.append(data[start:pos])
    if fields[0] != b"P6" or fields[3] != b"255": raise ValueError(f"{path}: expected binary 8-bit PPM")
    return int(fields[1]), int(fields[2]), data[pos + 1:]


def write_png(path, width, height, rgb):
    rows = b"".join(b"\0" + rgb[y * width * 3:(y + 1) * width * 3] for y in range(height))
    chunk = lambda kind, body: struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) +
                     chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b""))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=pathlib.Path)
    args = parser.parse_args()
    columns, scenes, mismatches = [], set(), []
    for profile in sorted(p for p in OUT.iterdir() if p.is_dir() and (p / "frames").is_dir()):
        for theme in sorted(t for t in (profile / "frames").iterdir() if t.is_dir()):
            frames = sorted(theme.glob("*.ppm"))
            if not frames: continue
            columns.append((profile.name, theme.name, theme))
            for frame in frames:
                scenes.add(frame.stem)
                write_png(frame.with_suffix(".png"), *read_ppm(frame))
                if args.baseline and theme.name == "crab":
                    expected = args.baseline / profile.name / frame.name
                    if expected.exists() and expected.read_bytes() != frame.read_bytes(): mismatches.append(f"{profile.name}/{frame.stem}")
    cells = lambda scene: "".join(
        f'<td><img loading="lazy" src="{(t / (scene + ".png")).relative_to(OUT).as_posix()}"></td>' if (t / (scene + ".png")).exists() else "<td></td>"
        for _, _, t in columns)
    page = ["<!doctype html><meta charset=utf-8><title>ClawPal frames</title>",
            "<style>body{background:#222;color:#ddd;font:14px sans-serif}td{vertical-align:top;padding:6px}img{max-width:400px;display:block}</style><table>",
            "<tr><th></th>" + "".join(f"<th>{html.escape(p)} · {html.escape(t)}</th>" for p, t, _ in columns) + "</tr>"]
    page += [f"<tr><th>{html.escape(s)}</th>{cells(s)}</tr>" for s in sorted(scenes)]
    (OUT / "contact-sheet.html").write_text("\n".join(page + ["</table>"]), encoding="utf-8")
    print(f"{len(columns)} columns, {len(scenes)} scenes: {OUT / 'contact-sheet.html'}")
    if args.baseline:
        if mismatches: sys.exit("Crab frames differ from the baseline: " + ", ".join(mismatches))
        print("Crab frames match the baseline")


if __name__ == "__main__":
    main()
