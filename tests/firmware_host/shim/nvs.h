/* Host-test stand-in: parser tests never reach persistent storage. */
#pragma once
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"
typedef uint32_t nvs_handle_t;
typedef enum { NVS_READONLY, NVS_READWRITE } nvs_open_mode_t;
static inline esp_err_t nvs_open(const char *name, nvs_open_mode_t mode, nvs_handle_t *out) { (void)name; (void)mode; (void)out; return ESP_ERR_NVS_NOT_FOUND; }
static inline void nvs_close(nvs_handle_t nvs) { (void)nvs; }
static inline esp_err_t nvs_get_str(nvs_handle_t nvs, const char *key, char *out, size_t *size) { (void)nvs; (void)key; (void)out; (void)size; return ESP_ERR_NVS_NOT_FOUND; }
static inline esp_err_t nvs_set_str(nvs_handle_t nvs, const char *key, const char *value) { (void)nvs; (void)key; (void)value; return ESP_FAIL; }
static inline esp_err_t nvs_commit(nvs_handle_t nvs) { (void)nvs; return ESP_FAIL; }
