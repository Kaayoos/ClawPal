/* Host-test stand-in for the ESP-IDF error type used by firmware headers. */
#pragma once
typedef int esp_err_t;
#define ESP_OK 0
#define ESP_FAIL -1
#define ESP_ERR_NO_MEM 0x101
#define ESP_ERR_NVS_NOT_FOUND 0x1102
