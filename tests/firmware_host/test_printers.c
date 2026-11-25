/* Host checks for the Printer app: the telemetry it reads and when its alert covers the screen.
 * Included by test_parsers.c after test_telemetry.c, which supplies telemetry_full. */
#include "buddy_printer_watch.h"

static bool settings(const char *city, const char *timezone, int schema, int apps, buddy_settings_t *out);

/* telemetry_full with a printer object added after auto_open_apps. */
static bool printer_frame(const char *object, buddy_stats_t *out)
{
    static char frame[sizeof(telemetry_full) + 2048];
    const char *open = "\"auto_open_apps\":false", *at = strstr(telemetry_full, open);
    snprintf(frame, sizeof(frame), "%.*s%s,\"printer\":%s%s", (int)(at - telemetry_full), telemetry_full, open, object, at + strlen(open));
    return telemetry(frame, out);
}

#define ONE(fields) "{\"alerts\":true,\"printers\":[{\"id\":1,\"name\":\"P1S\"," fields "}]}"

static buddy_printers_t one_printer(uint8_t id, buddy_print_state_t state, buddy_print_reason_t reason)
{
    buddy_printers_t p = { .present = true, .alerts = true, .count = 1 };
    p.items[0] = (buddy_printer_t){ .id = id, .state = (uint8_t)state, .reason = (uint8_t)reason };
    return p;
}

static buddy_print_alert_t feed(buddy_print_watch_t *w, buddy_print_state_t state, buddy_print_reason_t reason)
{
    buddy_printers_t p = one_printer(1, state, reason);
    unsigned index = 99;
    return buddy_print_watch_update(w, &p, &index);
}

static void printer_checks(void)
{
    buddy_stats_t s;
    check(telemetry(telemetry_full, &s) && !s.printers.present && !s.printers.count, "printers: an older companion has no Printer app");
    check(printer_frame("{\"alerts\":true,\"printers\":[]}", &s) && s.printers.present && s.printers.alerts && !s.printers.count,
          "printers: a PC with none set up sends an empty list");
    check(printer_frame(ONE("\"state\":\"printing\",\"progress\":42,\"remaining_s\":3720,\"layer\":120,\"layers\":300,"
                            "\"file\":\"Benchy \xc5\x82\xc3\xb3" "d\xc5\xba\",\"nozzle\":219.5,\"nozzle_target\":220,\"bed\":55,\"bed_target\":55"), &s) &&
          s.printers.count == 1 && s.printers.items[0].id == 1 && s.printers.items[0].state == BUDDY_PRINT_PRINTING &&
          s.printers.items[0].progress == 42 && s.printers.items[0].remaining_s == 3720 && s.printers.items[0].layer == 120 &&
          s.printers.items[0].layers == 300 && fabsf(s.printers.items[0].nozzle - 219.5f) < 0.01f && s.printers.items[0].bed_target == 55 &&
          !strcmp(s.printers.items[0].name, "P1S") && !strncmp(s.printers.items[0].file, "Benchy ", 7),
          "printers: a printing printer's progress, layers, time and temperatures reach the display");
    check(printer_frame(ONE("\"state\":\"idle\""), &s) && isnan(s.printers.items[0].nozzle) && isnan(s.printers.items[0].bed) &&
          s.printers.items[0].reason == BUDDY_PRINT_REASON_NONE, "printers: missing temperatures are unknown, not zero");
    check(printer_frame(ONE("\"state\":\"paused\",\"reason\":\"filament\",\"error\":\"0700_2000_0002_0001\""), &s) &&
          s.printers.items[0].state == BUDDY_PRINT_PAUSED && s.printers.items[0].reason == BUDDY_PRINT_REASON_FILAMENT &&
          !strcmp(s.printers.items[0].error, "0700_2000_0002_0001"), "printers: a pause names its reason and error code");
    const char *states[] = { "offline", "idle", "preparing", "printing", "paused", "finished", "failed" };
    bool all = true;
    for (unsigned i = 0; i < sizeof(states) / sizeof(states[0]); ++i) {
        char object[200];
        snprintf(object, sizeof(object), "{\"printers\":[{\"id\":9,\"name\":\"X1C\",\"state\":\"%s\"}]}", states[i]);
        all = all && printer_frame(object, &s) && s.printers.items[0].state == i && !s.printers.alerts;
    }
    check(all, "printers: every state parses; alerts are off unless the PC asks");
    check(!printer_frame(ONE("\"state\":\"melting\""), NULL) && !printer_frame(ONE("\"state\":\"paused\",\"reason\":\"cat\""), NULL) &&
          !printer_frame("{\"printers\":[{\"id\":1,\"name\":\"P1S\"}]}", NULL), "printers: unknown states and reasons are rejected, and a state is required");
    check(!printer_frame(ONE("\"state\":\"printing\",\"progress\":101"), NULL) && !printer_frame(ONE("\"state\":\"printing\",\"progress\":-1"), NULL) &&
          !printer_frame(ONE("\"state\":\"printing\",\"progress\":4.5"), NULL) && !printer_frame(ONE("\"state\":\"printing\",\"nozzle\":900"), NULL) &&
          !printer_frame(ONE("\"state\":\"printing\",\"remaining_s\":2592001"), NULL) && !printer_frame(ONE("\"state\":\"printing\",\"layer\":\"1\""), NULL),
          "printers: progress, temperatures, time and layers are bounded numbers");
    check(!printer_frame("{\"printers\":[{\"id\":0,\"name\":\"A\",\"state\":\"idle\"}]}", NULL) &&
          !printer_frame("{\"printers\":[{\"id\":256,\"name\":\"A\",\"state\":\"idle\"}]}", NULL) &&
          !printer_frame("{\"printers\":[{\"id\":2,\"name\":\"A\",\"state\":\"idle\"},{\"id\":2,\"name\":\"B\",\"state\":\"idle\"}]}", NULL) &&
          !printer_frame("{\"printers\":[{\"id\":2,\"name\":\"\",\"state\":\"idle\"}]}", NULL),
          "printers: ids are unique from 1 to 255 and every printer has a name");
    check(printer_frame("{\"printers\":[{\"id\":1,\"name\":\"A\",\"state\":\"idle\"},{\"id\":2,\"name\":\"B\",\"state\":\"idle\"},"
                        "{\"id\":3,\"name\":\"C\",\"state\":\"idle\"},{\"id\":4,\"name\":\"D\",\"state\":\"idle\"}]}", &s) && s.printers.count == 4 &&
          !printer_frame("{\"printers\":[{\"id\":1,\"name\":\"A\",\"state\":\"idle\"},{\"id\":2,\"name\":\"B\",\"state\":\"idle\"},"
                         "{\"id\":3,\"name\":\"C\",\"state\":\"idle\"},{\"id\":4,\"name\":\"D\",\"state\":\"idle\"},{\"id\":5,\"name\":\"E\",\"state\":\"idle\"}]}", NULL),
          "printers: at most four");
    check(!printer_frame(ONE("\"state\":\"idle\",\"file\":\"0123456789012345678901234567890123456789012345678901234567890123\""), NULL) &&
          printer_frame(ONE("\"state\":\"idle\",\"file\":\"012345678901234567890123456789012345678901234567890123456789012\""), NULL) &&
          !printer_frame(ONE("\"state\":\"idle\",\"file\":\"a\\u0007b\""), NULL), "printers: file names are bounded display text");
    check(!printer_frame("[]", NULL) && !printer_frame("{\"printers\":{}}", NULL) && !printer_frame("{\"alerts\":1,\"printers\":[]}", NULL),
          "printers: the object's shape is checked");

    /* When the alert covers the screen. */
    buddy_print_watch_t w = {0};
    check(feed(&w, BUDDY_PRINT_FINISHED, 0) == BUDDY_PRINT_ALERT_NONE, "alert: a printer already finished when the PC connects stays quiet");
    check(feed(&w, BUDDY_PRINT_PREPARING, 0) == BUDDY_PRINT_ALERT_NONE && feed(&w, BUDDY_PRINT_PRINTING, 0) == BUDDY_PRINT_ALERT_NONE &&
          feed(&w, BUDDY_PRINT_PRINTING, 0) == BUDDY_PRINT_ALERT_NONE, "alert: starting and printing stay quiet");
    check(feed(&w, BUDDY_PRINT_FINISHED, 0) == BUDDY_PRINT_ALERT_FINISHED, "alert: printing then finished alerts");
    check(feed(&w, BUDDY_PRINT_FINISHED, 0) == BUDDY_PRINT_ALERT_NONE, "alert: it alerts once per change");
    feed(&w, BUDDY_PRINT_PRINTING, 0);
    check(feed(&w, BUDDY_PRINT_PAUSED, BUDDY_PRINT_REASON_FILAMENT) == BUDDY_PRINT_ALERT_PAUSED, "alert: a filament runout pause alerts");
    check(feed(&w, BUDDY_PRINT_PRINTING, 0) == BUDDY_PRINT_ALERT_NONE && feed(&w, BUDDY_PRINT_PAUSED, BUDDY_PRINT_REASON_ERROR) == BUDDY_PRINT_ALERT_PAUSED,
          "alert: a pause for an error alerts");
    check(feed(&w, BUDDY_PRINT_FAILED, BUDDY_PRINT_REASON_ERROR) == BUDDY_PRINT_ALERT_FAILED, "alert: a paused print that fails alerts");
    feed(&w, BUDDY_PRINT_PRINTING, 0);
    check(feed(&w, BUDDY_PRINT_PAUSED, BUDDY_PRINT_REASON_USER) == BUDDY_PRINT_ALERT_NONE, "alert: a pause someone chose stays quiet");
    check(feed(&w, BUDDY_PRINT_FAILED, BUDDY_PRINT_REASON_USER) == BUDDY_PRINT_ALERT_NONE, "alert: a print someone cancelled stays quiet");
    feed(&w, BUDDY_PRINT_PRINTING, 0);
    check(feed(&w, BUDDY_PRINT_FAILED, 0) == BUDDY_PRINT_ALERT_FAILED, "alert: a failure without a reason alerts");
    feed(&w, BUDDY_PRINT_PRINTING, 0);
    check(feed(&w, BUDDY_PRINT_OFFLINE, 0) == BUDDY_PRINT_ALERT_NONE && feed(&w, BUDDY_PRINT_FINISHED, 0) == BUDDY_PRINT_ALERT_FINISHED,
          "alert: a print that ends while the printer is unreachable still alerts when it returns");
    feed(&w, BUDDY_PRINT_PRINTING, 0);
    check(feed(&w, BUDDY_PRINT_IDLE, 0) == BUDDY_PRINT_ALERT_NONE, "alert: a printer that goes straight to idle stays quiet");
    {
        buddy_print_watch_t quiet = {0};
        buddy_printers_t p = one_printer(1, BUDDY_PRINT_PRINTING, 0); p.alerts = false;
        buddy_print_watch_update(&quiet, &p, NULL);
        p.items[0].state = BUDDY_PRINT_FINISHED;
        check(buddy_print_watch_update(&quiet, &p, NULL) == BUDDY_PRINT_ALERT_NONE, "alert: the PC app can turn alerts off");
        buddy_printers_t gone = {0};
        buddy_print_watch_update(&quiet, &gone, NULL);
        p = one_printer(1, BUDDY_PRINT_FINISHED, 0);
        check(quiet.count == 0 && buddy_print_watch_update(&quiet, &p, NULL) == BUDDY_PRINT_ALERT_NONE,
              "alert: a printer removed on the PC is forgotten");
    }
    {
        buddy_print_watch_t two = {0};
        buddy_printers_t p = { .present = true, .alerts = true, .count = 2 };
        p.items[0] = (buddy_printer_t){ .id = 4, .state = BUDDY_PRINT_PRINTING };
        p.items[1] = (buddy_printer_t){ .id = 7, .state = BUDDY_PRINT_PRINTING };
        buddy_print_watch_update(&two, &p, NULL);
        /* The PC reorders: the finished printer moves down the list. */
        buddy_printers_t q = p;
        q.items[0] = (buddy_printer_t){ .id = 7, .state = BUDDY_PRINT_PRINTING };
        q.items[1] = (buddy_printer_t){ .id = 4, .state = BUDDY_PRINT_FINISHED };
        unsigned index = 9;
        check(buddy_print_watch_update(&two, &q, &index) == BUDDY_PRINT_ALERT_FINISHED && index == 1,
              "alert: printers are followed by id, not by place in the list");
    }
}
#undef ONE

static void printer_settings_checks(void)
{
    buddy_settings_t parsed;
    check(settings("Warsaw", "UTC0", 11, 2047, &parsed) && (parsed.enabled_apps & 1024), "schema 11 settings carry the Printer app");
    check(settings("Warsaw", "UTC0", 11, 1023, &parsed) && !(parsed.enabled_apps & 1024), "schema 11 settings can turn the Printer app off");
    check(settings("Warsaw", "UTC0", 10, 1023, &parsed) && (parsed.enabled_apps & 1024), "schema 10 settings saved by older firmware gain the Printer app");
    check(settings("Warsaw", "UTC0", 9, 511, &parsed) && (parsed.enabled_apps & 1024) && !(parsed.enabled_apps & 512),
          "schema 9 settings gain the Printer app and keep Audio off");
    check(!settings("Warsaw", "UTC0", 10, 2047, NULL), "schema 10 settings cannot name a twelfth app bit");
    check(!settings("Warsaw", "UTC0", 11, 4095, NULL), "apps beyond the Printer app are rejected");
    check(!settings("Warsaw", "UTC0", 13, 2047, NULL), "an unknown newer schema is rejected");
    cJSON *json = cJSON_Parse("{\"schema\":11,\"enabled_apps\":2047,\"home_order\":[10,0],\"use_24h\":true,\"weather_enabled\":true,"
        "\"city\":\"W\",\"latitude\":1,\"longitude\":2,\"timezone\":\"UTC0\",\"language\":\"en\",\"weather_fahrenheit\":false,"
        "\"dim_behavior\":1,\"dim_after_seconds\":60,\"dim_brightness\":40,\"theme\":\"crab\",\"accent\":\"champagne\",\"show_version\":false,"
        "\"night_enabled\":true,\"night_start\":1,\"night_end\":10,\"offline_app\":1}");
    check(json && buddy_settings_parse(json, &parsed) && parsed.home_order[0] == 10, "the Printer app can sit on Home");
    cJSON_ReplaceItemInObjectCaseSensitive(json, "schema", cJSON_CreateNumber(10));
    check(!buddy_settings_parse(json, &parsed), "schema 10 Home cannot name the Printer app");
    cJSON_ReplaceItemInObjectCaseSensitive(json, "schema", cJSON_CreateNumber(11));
    cJSON_ReplaceItemInObjectCaseSensitive(json, "offline_app", cJSON_CreateNumber(10));
    check(!buddy_settings_parse(json, &parsed), "the Printer app needs the PC, so it cannot be the offline app");
    cJSON_Delete(json);
}
