"""Build the IANA -> POSIX time zone table the companion embeds. Python 3.10+.

The display's C library only understands POSIX TZ strings, while the location
search returns IANA names such as "America/Sao_Paulo". Every TZif v2+ file ends
with a footer holding the POSIX rule for times after its last transition; this
script copies that footer for each zone in the installed `tzdata` package.

Rules newlib cannot parse (RFC 8536 extensions: transition hours outside 0-24,
used by e.g. Asia/Jerusalem) are left out, so the companion falls back to the
manual time zone list instead of silently showing a wrong clock. So are zones
whose footer disagrees with the database over the next few years: a footer only
describes time after the file's last listed transition, and some zones (e.g.
Africa/Casablanca) list explicit transitions decades ahead.

Usage: python -m pip install tzdata && python scripts/generate_timezones.py
       python scripts/generate_timezones.py --check   (verifies the committed table)
"""
from datetime import datetime, timedelta, timezone
from pathlib import Path
import argparse, calendar, importlib.metadata, importlib.resources, json, re, sys

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'companion' / 'Assets' / 'timezones.json'
NAME = r'(?:[A-Za-z]{3,}|<[A-Za-z0-9+-]{3,}>)'
OFFSET = r'[+-]?\d{1,2}(?::\d{2}(?::\d{2})?)?'
# newlib reads rule times as unsigned hh[:mm[:ss]] and does not accept hours past 24.
TIME = r'(?:[01]?\d|2[0-4])(?::\d{2}(?::\d{2})?)?'
DATE = r'(?:M(?:1[0-2]|[1-9])\.[1-5]\.[0-6]|J\d{1,3}|\d{1,3})'
RULE = rf'{DATE}(?:/{TIME})?'
POSIX = re.compile(rf'{NAME}{OFFSET}(?:{NAME}(?:{OFFSET})?(?:,{RULE},{RULE})?)?')


def footer(data: bytes) -> str | None:
    if data[:4] != b'TZif' or data[4:5] < b'2':
        return None
    lines = data.rstrip(b'\n').rsplit(b'\n', 1)
    return lines[1].decode('ascii') if len(lines) == 2 else None


def seconds(text: str) -> int:
    sign = -1 if text.startswith('-') else 1
    parts = [int(p) for p in text.lstrip('+-').split(':')] + [0, 0]
    return sign * (parts[0] * 3600 + parts[1] * 60 + parts[2])


def rule_day(year: int, date: str) -> int:
    """Day of the year (0-based) on which a POSIX transition rule falls."""
    if date.startswith('M'):
        month, week, weekday = map(int, date[1:].split('.'))
        first = (calendar.weekday(year, month, 1) + 1) % 7  # POSIX: 0 is Sunday
        day = 1 + (weekday - first) % 7 + (week - 1) * 7
        while day > calendar.monthrange(year, month)[1]:
            day -= 7
        return datetime(year, month, day).timetuple().tm_yday - 1
    if date.startswith('J'):
        day = int(date[1:]) - 1
        return day + 1 if calendar.isleap(year) and day >= 59 else day
    return int(date)


def posix_offset(rule: str, moment: datetime) -> int:
    """UTC offset in seconds that a POSIX TZ string gives for an aware UTC moment."""
    parts = re.fullmatch(rf'({NAME})({OFFSET})(?:({NAME})({OFFSET})?(?:,({RULE}),({RULE}))?)?', rule)
    standard = -seconds(parts[2])
    if not parts[3]:
        return standard
    daylight = -seconds(parts[4]) if parts[4] else standard + 3600
    def instant(year, text, offset):
        date, _, at = text.partition('/')
        local = datetime(year, 1, 1, tzinfo=timezone.utc) + timedelta(days=rule_day(year, date), seconds=seconds(at or '2'))
        return local - timedelta(seconds=offset)
    year = moment.year
    start, end = instant(year, parts[5], standard), instant(year, parts[6], daylight)
    inside = start <= moment < end if start < end else not end <= moment < start
    return daylight if inside else standard


def matches_database(name: str, rule: str) -> bool:
    from zoneinfo import ZoneInfo
    zone = ZoneInfo(name)
    moment = datetime.now(timezone.utc).replace(minute=17, second=0, microsecond=0)
    for _ in range(3 * 365 * 4):  # every six hours for three years
        if moment.astimezone(zone).utcoffset() != timedelta(seconds=posix_offset(rule, moment)):
            return False
        moment += timedelta(hours=6)
    return True


def build() -> dict:
    root = importlib.resources.files('tzdata') / 'zoneinfo'
    zones, skipped = {}, []
    def walk(folder, prefix=''):
        for entry in folder.iterdir():
            name = prefix + entry.name
            if entry.is_dir():
                walk(entry, name + '/')
            elif re.fullmatch(r'[A-Z][A-Za-z0-9_+-]*(?:/[A-Za-z0-9_+-]+)*', name) and name not in ('Factory',):
                rule = footer(entry.read_bytes())
                if rule and POSIX.fullmatch(rule) and len(rule) < 128 and matches_database(name, rule):
                    zones[name] = rule
                elif rule:
                    skipped.append(name)
    walk(root)
    return {
        'source': 'IANA tz database via the Python tzdata package (public domain)',
        'tzdata': importlib.metadata.version('tzdata'),
        'unsupported': sorted(skipped),
        'zones': dict(sorted(zones.items())),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true', help='validate the committed table without tzdata')
    args = parser.parse_args()
    if args.check:
        table = json.loads(OUTPUT.read_text(encoding='utf-8'))
        bad = [name for name, rule in table['zones'].items() if not POSIX.fullmatch(rule) or len(rule) >= 128]
        if bad or len(table['zones']) < 300:
            sys.exit(f'{OUTPUT}: invalid or incomplete time zone table: {bad[:5]}')
        print(f"{len(table['zones'])} time zones validated (tzdata {table['tzdata']})")
        return
    table = build()
    OUTPUT.write_text(json.dumps(table, indent=1, ensure_ascii=True) + '\n', encoding='utf-8', newline='\n')
    print(f"{len(table['zones'])} time zones written to {OUTPUT.relative_to(ROOT)}; "
          f"{len(table['unsupported'])} need manual selection: {', '.join(table['unsupported'])}")


if __name__ == '__main__':
    main()
