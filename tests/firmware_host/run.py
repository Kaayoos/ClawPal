"""Build and run the firmware parser host tests.

Usage: python tests/firmware_host/run.py [--cjson DIR] [--fuzz N]
DIR holds cJSON.c/cJSON.h; it defaults to ESP-IDF's copy under $IDF_PATH.
N is the number of mutated telemetry messages to try (default 200000).
Uses $CC, gcc, cc or clang, falling back to `python -m ziglang cc`.
"""
import argparse, importlib.util, os, pathlib, shutil, subprocess, sys, tempfile

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent.parent
MAIN = REPO / "firmware" / "main"


def compiler():
    if os.environ.get("CC"):
        return os.environ["CC"].split()
    for name in ("gcc", "cc", "clang"):
        if shutil.which(name):
            return [name]
    if importlib.util.find_spec("ziglang"):
        return [sys.executable, "-m", "ziglang", "cc"]
    sys.exit("No C compiler found; install gcc or run: python -m pip install --require-hashes -r tests/simulator/requirements.txt")


def main():
    parser = argparse.ArgumentParser()
    idf = os.environ.get("IDF_PATH")
    parser.add_argument("--cjson", type=pathlib.Path, default=pathlib.Path(idf) / "components/json/cJSON" if idf else None)
    parser.add_argument("--fuzz", type=int, default=200000, help="mutated telemetry messages to try")
    args = parser.parse_args()
    if not args.cjson or not (args.cjson / "cJSON.c").exists():
        sys.exit("cJSON source not found; pass --cjson or set IDF_PATH")
    sources = [HERE / "test_parsers.c", MAIN / "settings/buddy_settings.c", MAIN / "ui/buddy_theme_data.c",
               MAIN / "weather/buddy_weather_parse.c",
               MAIN / "network/buddy_telemetry_parse.c", MAIN / "network/buddy_auth.c", args.cjson / "cJSON.c"]
    includes = [HERE / "shim", args.cjson, MAIN / "settings", MAIN / "weather", MAIN / "language", MAIN / "ui",
                MAIN / "network", MAIN / "include"]
    with tempfile.TemporaryDirectory() as out:
        exe = pathlib.Path(out) / "firmware_parsers"
        command = [*compiler(), "-std=gnu17", "-O1", "-Wall", "-fsanitize=undefined", "-fno-sanitize-recover=all",
                   "-include", str(HERE / "shim/host_string.h"),
                   *[f"-I{p}" for p in includes], *map(str, sources), "-lm", "-o", str(exe)]
        if subprocess.run(command).returncode:
            # Some toolchains lack the sanitizer runtime; the checks still matter without it.
            command = [c for c in command if not c.startswith(("-fsanitize", "-fno-sanitize"))]
            if subprocess.run(command).returncode:
                sys.exit("Firmware parser tests did not compile")
        sys.exit(subprocess.run([str(exe), str(args.fuzz)]).returncode)


if __name__ == "__main__":
    main()
