/* Force-included into every simulator translation unit.
 * Pins time to the scripted scene clock and treats local time as UTC so
 * renders are deterministic. Firmware sources are compiled unchanged. */
#pragma once
#include <time.h>
#include <string.h>
#define IRAM_ATTR
time_t sim_time(time_t *out);
#define time(x) sim_time(x)
static inline struct tm *sim_gmtime_r(const time_t *t, struct tm *out)
{
    long long s = (long long)*t; long long days = s / 86400; long long rem = s % 86400;
    if (rem < 0) { rem += 86400; --days; }
    memset(out, 0, sizeof(*out));
    out->tm_hour = (int)(rem / 3600); out->tm_min = (int)(rem / 60 % 60); out->tm_sec = (int)(rem % 60);
    out->tm_wday = (int)((4 + days) % 7); if (out->tm_wday < 0) out->tm_wday += 7;
    long long z = days + 719468, era = (z >= 0 ? z : z - 146096) / 146097;
    unsigned doe = (unsigned)(z - era * 146097);
    unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    long long y = (long long)yoe + era * 400; unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    unsigned mp = (5 * doy + 2) / 153, d = doy - (153 * mp + 2) / 5 + 1, m = mp < 10 ? mp + 3 : mp - 9;
    out->tm_year = (int)(y + (m <= 2) - 1900); out->tm_mon = (int)m - 1; out->tm_mday = (int)d;
    return out;
}
#define gmtime_r(t, o) sim_gmtime_r(t, o)
#define localtime_r(t, o) sim_gmtime_r(t, o)
