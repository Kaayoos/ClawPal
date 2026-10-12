"""Build the ClawPal display simulator with zig cc (pip install ziglang).

Compiles the vendored LVGL 8.4, the unmodified firmware UI/asset/language
sources and sim_main.c into .sim-output/<profile>/clawpal_sim.exe.
"""
import concurrent.futures as futures
import os
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent.parent
PROFILE = int(sys.argv[1]) if len(sys.argv)>1 else 0
OUT = REPO / ".sim-output" / str(PROFILE)
OBJ = OUT / "obj"
LVGL = REPO / "vendor" / "components" / "lvgl__lvgl"
FW = REPO / "firmware" / "main"

INCLUDES = [HERE / "shim", LVGL, LVGL / "src", FW / "include", FW / "ui", FW / "assets",
            FW / "language", FW / "weather", FW / "settings", REPO / "firmware" / "components" / "board" / "include"]
FLAGS = ["-O2", "-std=gnu17", "-target", "x86_64-windows-gnu", "-w",
         f"-DCLAWPAL_PROFILE={PROFILE}", '-DLV_CONF_KCONFIG_EXTERNAL_INCLUDE="sdkconfig.h"', "-DLV_CONF_INCLUDE_SIMPLE",
         "-include", str(HERE / "shim" / "sim_force.h")] + [f"-I{p}" for p in INCLUDES]


def sources():
    files = sorted((LVGL / "src").rglob("*.c"))
    files += sorted((FW / "ui").glob("*.c"))
    files += sorted((FW / "assets").glob("*.c"))
    files += sorted((FW / "language").glob("*.c"))
    files.append(HERE / "sim_main.c")
    return files


def compile_one(src: pathlib.Path):
    rel = src.relative_to(REPO) if REPO in src.parents else src.name
    obj = OBJ / (str(rel).replace(os.sep, "_").replace("/", "_") + ".o")
    deps = [src, *FW.rglob("*.h"), *FW.rglob("*.inc"), * (REPO / "firmware" / "components" / "board" / "include").glob("*.h"), HERE / "shim" / "sim_force.h", HERE / "shim" / "sdkconfig.h"]
    if obj.exists() and obj.stat().st_mtime > max(d.stat().st_mtime for d in deps):
        return obj, None
    cmd = [sys.executable, "-m", "ziglang", "cc", *FLAGS, "-c", str(src), "-o", str(obj)]
    result = subprocess.run(cmd, capture_output=True, text=True)
    return obj, (result.stderr if result.returncode else None)


def main():
    OBJ.mkdir(parents=True, exist_ok=True)
    objs, errors = [], []
    with futures.ThreadPoolExecutor(max_workers=min(8,os.cpu_count() or 4)) as pool:
        for obj, error in pool.map(compile_one, sources()):
            objs.append(obj)
            if error:
                errors.append(error)
    if errors:
        print("\n".join(errors[:5]))
        sys.exit(1)
    exe = OUT / "clawpal_sim.exe"
    link = [sys.executable, "-m", "ziglang", "cc", "-target", "x86_64-windows-gnu", "-O2", "-o", str(exe), *map(str, objs)]
    result = subprocess.run(link, capture_output=True, text=True)
    if result.returncode:
        print(result.stderr)
        sys.exit(1)
    print(exe)


if __name__ == "__main__":
    main()
