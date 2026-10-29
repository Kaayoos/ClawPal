#pragma once
#include "esp_http_server.h"

#include "board_profile.h"
#include "buddy_model.h"

/* Called on the HTTP task as a Wi-Fi install advances; received and total are bytes. */
typedef void (*buddy_update_progress_t)(buddy_install_stage_t stage, uint32_t received, uint32_t total);
void buddy_update_set_progress_callback(buddy_update_progress_t callback);
esp_err_t buddy_update_info(httpd_req_t *request);
esp_err_t buddy_update_upload(httpd_req_t *request);
void buddy_update_boot_guard(void);
void buddy_update_boot_ready(void);
