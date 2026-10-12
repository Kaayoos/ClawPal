#include "buddy_language.h"
#include "buddy_weather_parse.h"

#include <math.h>
#include <string.h>
#include "cJSON.h"

static bool number_in(const cJSON *value, double low, double high)
{
    return cJSON_IsNumber(value) && isfinite(value->valuedouble) &&
           value->valuedouble >= low && value->valuedouble <= high;
}

static float optional_reading(const cJSON *values, const cJSON *units, const char *key, const char *unit, double low, double high)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(values,key), *u=cJSON_GetObjectItemCaseSensitive(units,key);
    return number_in(v,low,high) && cJSON_IsString(u) && strcmp(u->valuestring,unit)==0 ? (float)v->valuedouble : NAN;
}

static bool bounded_nesting(const char *json, size_t size)
{
    unsigned depth = 0;
    bool quoted = false, escaped = false;
    for (size_t i = 0; i + 1 < size; ++i) {
        char ch = json[i];
        if (quoted) {
            if (escaped) escaped = false;
            else if (ch == '\\') escaped = true;
            else if (ch == '"') quoted = false;
        } else if (ch == '"') quoted = true;
        else if (ch == '{' || ch == '[') {
            if (++depth > 8) return false;
        } else if (ch == '}' || ch == ']') {
            if (depth == 0) return false;
            --depth;
        }
    }
    return !quoted && depth == 0;
}

const char *buddy_weather_description(int code, bool is_day)
{
    switch (code) {
    case 0: return is_day ? BT(BT_SUNNY) : BT(BT_CLEAR);
    case 1: return BT(BT_MOSTLY_CLEAR);
    case 2: return BT(BT_PARTLY_CLOUDY);
    case 3: return BT(BT_CLOUDY);
    case 45: case 48: return BT(BT_FOG);
    case 51: case 53: case 55: return BT(BT_DRIZZLE);
    case 56: case 57: return BT(BT_FREEZING_DRIZZLE);
    case 61: case 63: case 65: return BT(BT_RAIN);
    case 66: case 67: return BT(BT_FREEZING_RAIN);
    case 71: case 73: case 75: case 77: return BT(BT_SNOW);
    case 80: case 81: case 82: return BT(BT_SHOWERS);
    case 85: case 86: return BT(BT_SNOW_SHOWERS);
    case 95: case 97: return BT(BT_THUNDERSTORM);
    case 96: case 99: return BT(BT_THUNDER_HAIL);
    default: return NULL;
    }
}

static bool unit_is(const cJSON *units, const char *key, const char *expected)
{
    const cJSON *unit=cJSON_GetObjectItemCaseSensitive(units,key);
    return cJSON_IsString(unit) && strcmp(unit->valuestring,expected)==0;
}
static const cJSON *array(const cJSON *object,const char *key,int count)
{
    const cJSON *a=cJSON_GetObjectItemCaseSensitive(object,key);
    return cJSON_IsArray(a) && cJSON_GetArraySize(a)==count ? a : NULL;
}
static bool valid_code(const cJSON *code)
{
    return number_in(code,0,99) && code->valuedouble==code->valueint && buddy_weather_description(code->valueint,true)!=NULL;
}
/* A malformed forecast section cannot replace valid current conditions with zeroes.
 * Missing/null optional probabilities remain unknown; malformed core arrays are omitted. */
static void parse_hours(const cJSON *root,time_t now,buddy_weather_t *out)
{
    const cJSON *hourly=cJSON_GetObjectItemCaseSensitive(root,"hourly"), *units=cJSON_GetObjectItemCaseSensitive(root,"hourly_units");
    const cJSON *times=cJSON_GetObjectItemCaseSensitive(hourly,"time");int count=cJSON_GetArraySize(times);
    if(!cJSON_IsArray(times) || count<1 || count>168 || !unit_is(units,"time","unixtime") || !unit_is(units,"temperature_2m","\xc2\xb0" "C"))return;
    const cJSON *temps=array(hourly,"temperature_2m",count), *codes=array(hourly,"weather_code",count), *day=array(hourly,"is_day",count);
    const cJSON *prob=array(hourly,"precipitation_probability",count);
    if(!temps || !codes || !day)return;
    bool probability_unit=unit_is(units,"precipitation_probability","%");int64_t previous=0;
    for(int i=0;i<count && out->hourly_count<BUDDY_WEATHER_HOURS;i++){
        const cJSON *t=cJSON_GetArrayItem(times,i), *temp=cJSON_GetArrayItem(temps,i), *code=cJSON_GetArrayItem(codes,i), *d=cJSON_GetArrayItem(day,i), *p=cJSON_GetArrayItem(prob,i);
        if(!number_in(t,(double)now-86400,(double)now+8*86400) || t->valuedouble!=(double)(int64_t)t->valuedouble || (int64_t)t->valuedouble<=previous){out->hourly_count=0;return;}
        previous=(int64_t)t->valuedouble;
        if(previous<=now || previous>(int64_t)now+26*3600)continue;
        if(!number_in(temp,-100,70) || !valid_code(code) || !number_in(d,0,1) || d->valuedouble!=d->valueint)continue;
        out->hours[out->hourly_count++]=(buddy_weather_hour_t){.timestamp=previous,.temperature_c=temp->valuedouble,.code=code->valueint,.is_day=d->valueint==1,
            .precipitation_probability=probability_unit&&number_in(p,0,100)?(float)p->valuedouble:NAN};
    }
}
static void parse_days(const cJSON *root,time_t now,buddy_weather_t *out)
{
    const cJSON *daily=cJSON_GetObjectItemCaseSensitive(root,"daily"), *units=cJSON_GetObjectItemCaseSensitive(root,"daily_units");
    const cJSON *times=cJSON_GetObjectItemCaseSensitive(daily,"time"), *offset=cJSON_GetObjectItemCaseSensitive(root,"utc_offset_seconds");int count=cJSON_GetArraySize(times);
    if(!number_in(offset,-43200,50400) || offset->valuedouble!=offset->valueint || !cJSON_IsArray(times) || count<1 || count>16 ||
       !unit_is(units,"time","unixtime") || !unit_is(units,"temperature_2m_max","\xc2\xb0" "C") || !unit_is(units,"temperature_2m_min","\xc2\xb0" "C"))return;
    const cJSON *high=array(daily,"temperature_2m_max",count), *low=array(daily,"temperature_2m_min",count), *codes=array(daily,"weather_code",count), *prob=array(daily,"precipitation_probability_max",count);
    if(!high || !low || !codes)return;
    out->region_utc_offset=offset->valueint;
    bool probability_unit=unit_is(units,"precipitation_probability_max","%");int64_t previous=0;
    for(int i=0;i<count && out->daily_count<BUDDY_WEATHER_DAYS;i++){
        const cJSON *t=cJSON_GetArrayItem(times,i), *h=cJSON_GetArrayItem(high,i), *l=cJSON_GetArrayItem(low,i), *c=cJSON_GetArrayItem(codes,i), *p=cJSON_GetArrayItem(prob,i);
        if(!number_in(t,(double)now-86400,(double)now+7*86400) || t->valuedouble!=(double)(int64_t)t->valuedouble || (int64_t)t->valuedouble<=previous){out->daily_count=0;return;}
        previous=(int64_t)t->valuedouble;
        if(!number_in(h,-100,70) || !number_in(l,-100,70) || l->valuedouble>h->valuedouble || !valid_code(c))continue;
        out->days[out->daily_count++]=(buddy_weather_day_t){.timestamp=previous,.high_c=h->valuedouble,.low_c=l->valuedouble,.code=c->valueint,
            .precipitation_probability=probability_unit&&number_in(p,0,100)?(float)p->valuedouble:NAN};
    }
}

bool buddy_weather_parse(const char *json, size_t size, time_t now,
                         buddy_weather_t *out)
{
    if (!json || !out || size == 0 || size > BUDDY_WEATHER_RESPONSE_CAPACITY || now < 1700000000 ||
        json[size - 1] != '\0' || memchr(json, '\0', size - 1) != NULL ||
        !bounded_nesting(json, size)) return false;
    /* Require a single complete JSON document; reject truncated/trailing data. */
    cJSON *root = cJSON_ParseWithLengthOpts(json, size, NULL, true);
    if (!root) return false;
    const cJSON *current = cJSON_GetObjectItemCaseSensitive(root, "current");
    const cJSON *units = cJSON_GetObjectItemCaseSensitive(root, "current_units");
    const cJSON *temperature = cJSON_GetObjectItemCaseSensitive(current, "temperature_2m");
    const cJSON *code = cJSON_GetObjectItemCaseSensitive(current, "weather_code");
    const cJSON *day = cJSON_GetObjectItemCaseSensitive(current, "is_day");
    const cJSON *stamp = cJSON_GetObjectItemCaseSensitive(current, "time");
    const cJSON *temperature_unit = cJSON_GetObjectItemCaseSensitive(units, "temperature_2m");
    const cJSON *time_unit = cJSON_GetObjectItemCaseSensitive(units, "time");
    bool valid = cJSON_IsObject(root) && cJSON_IsObject(current) && cJSON_IsObject(units) &&
        !cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(root, "error")) &&
        number_in(temperature, -100, 70) && number_in(code, 0, 99) &&
        code->valuedouble == code->valueint &&
        buddy_weather_description(code->valueint, true) != NULL &&
        number_in(day, 0, 1) && day->valuedouble == day->valueint &&
        number_in(stamp, (double)now - 21600 + 1, (double)now + 300) &&
        cJSON_IsString(temperature_unit) && strcmp(temperature_unit->valuestring, "\xc2\xb0" "C") == 0 &&
        cJSON_IsString(time_unit) && strcmp(time_unit->valuestring, "unixtime") == 0;
    if (valid) {
        *out = (buddy_weather_t){
            .available = true,
            .temperature_c = (float)temperature->valuedouble,
            .feels_like_c = optional_reading(current,units,"apparent_temperature","\xc2\xb0" "C",-120,90),
            .humidity_percent = optional_reading(current,units,"relative_humidity_2m","%",0,100),
            .wind_kmh = optional_reading(current,units,"wind_speed_10m","km/h",0,500),
            .precipitation_mm = optional_reading(current,units,"precipitation","mm",0,1000),
            .code = code->valueint,
            .is_day = day->valueint == 1,
            .age_s = stamp->valuedouble < (double)now ?
                     (uint32_t)((double)now - stamp->valuedouble) : 0,
        };
    }
    if(valid){parse_hours(root,now,out);parse_days(root,now,out);}
    cJSON_Delete(root);
    return valid;
}
