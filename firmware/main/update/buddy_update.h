#pragma once
#include "esp_http_server.h"

#include "board_profile.h"
esp_err_t buddy_update_info(httpd_req_t *request);
esp_err_t buddy_update_upload(httpd_req_t *request);
void buddy_update_boot_guard(void);
void buddy_update_boot_ready(void);
