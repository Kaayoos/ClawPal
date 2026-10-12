#include "buddy_update.h"
#include <stdlib.h>
#include <stdio.h>
#include <sys/param.h>
#include <string.h>
#include "cJSON.h"
#include "esp_app_desc.h"
#include "esp_app_format.h"
#include "esp_ota_ops.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "mbedtls/sha256.h"

static bool boot_ready;
static bool reboot_pending;
static int64_t ready_since;

static bool ota_ready(void)
{
    const esp_partition_t *data=esp_partition_find_first(ESP_PARTITION_TYPE_DATA,ESP_PARTITION_SUBTYPE_DATA_OTA,NULL);
    const esp_partition_t *a=esp_partition_find_first(ESP_PARTITION_TYPE_APP,ESP_PARTITION_SUBTYPE_APP_OTA_0,NULL);
    const esp_partition_t *b=esp_partition_find_first(ESP_PARTITION_TYPE_APP,ESP_PARTITION_SUBTYPE_APP_OTA_1,NULL);
    return data && data->address==CLAWPAL_OTA_DATA_OFFSET && a && a->address==CLAWPAL_OTA_0_OFFSET && a->size>=CLAWPAL_OTA_MAX_BYTES && b && b->address==CLAWPAL_OTA_1_OFFSET && b->size>=CLAWPAL_OTA_MAX_BYTES;
}

static bool pending_validation(void)
{
    esp_ota_img_states_t state;
    return esp_ota_get_state_partition(esp_ota_get_running_partition(),&state)==ESP_OK && state==ESP_OTA_IMG_PENDING_VERIFY;
}

static void guard_task(void *unused)
{
    vTaskDelay(pdMS_TO_TICKS(45000));
    if(pending_validation()) esp_ota_mark_app_invalid_rollback_and_reboot();
    vTaskDelete(NULL);
}

void buddy_update_boot_guard(void)
{
    if(pending_validation() && xTaskCreate(guard_task,"ota_guard",3072,NULL,3,NULL)!=pdPASS)
        esp_ota_mark_app_invalid_rollback_and_reboot();
}

void buddy_update_boot_ready(void)
{
    if(boot_ready)return;
    if(!ready_since){ready_since=esp_timer_get_time();return;}
    // The main loop has kept LVGL responsive after board, NVS and networking init.
    if(esp_timer_get_time()-ready_since<10000000)return;
    if(!pending_validation() || esp_ota_mark_app_valid_cancel_rollback()==ESP_OK) boot_ready=true;
}

esp_err_t buddy_update_info(httpd_req_t *request)
{
    const esp_partition_t *running=esp_ota_get_running_partition();
    cJSON *json=cJSON_CreateObject();
    cJSON_AddStringToObject(json,"board",CLAWPAL_BOARD_ID);
    cJSON_AddStringToObject(json,"layout",CLAWPAL_OTA_LAYOUT);
    cJSON_AddBoolToObject(json,"experimental",CLAWPAL_EXPERIMENTAL);
    cJSON_AddNumberToObject(json,"width",BOARD_DISPLAY_WIDTH);
    cJSON_AddNumberToObject(json,"height",BOARD_DISPLAY_HEIGHT);
    cJSON_AddStringToObject(json,"version",esp_app_get_description()->version);
    cJSON_AddBoolToObject(json,"ota_ready",ota_ready());
    cJSON_AddBoolToObject(json,"confirmed",boot_ready && !pending_validation());
    cJSON_AddStringToObject(json,"partition",running?running->label:"");
    cJSON_AddNumberToObject(json,"max_bytes",CLAWPAL_OTA_MAX_BYTES);
    char *body=cJSON_PrintUnformatted(json);cJSON_Delete(json);
    if(!body)return ESP_ERR_NO_MEM;
    httpd_resp_set_type(request,"application/json");httpd_resp_set_hdr(request,"Cache-Control","no-store");
    esp_err_t result=httpd_resp_sendstr(request,body);free(body);return result;
}

static void restart_task(void *unused){vTaskDelay(pdMS_TO_TICKS(1200));esp_restart();}
static bool header_equals(httpd_req_t *request,const char *name,const char *expected)
{
    char value[96];return httpd_req_get_hdr_value_str(request,name,value,sizeof(value))==ESP_OK && strcmp(value,expected)==0;
}

esp_err_t buddy_update_upload(httpd_req_t *request)
{
    if(reboot_pending || pending_validation()){httpd_resp_set_status(request,"503 Service Unavailable");return httpd_resp_sendstr(request,"Display is restarting or confirming its boot");}
    if(!ota_ready())return httpd_resp_send_err(request,HTTPD_400_BAD_REQUEST,"One-time USB migration required");
    const esp_partition_t *target=esp_ota_get_next_update_partition(NULL);
    if(!target || target==esp_ota_get_running_partition() || request->content_len<sizeof(esp_image_header_t)+sizeof(esp_image_segment_header_t)+sizeof(esp_app_desc_t) ||
       request->content_len>target->size || request->content_len>CLAWPAL_OTA_MAX_BYTES ||
       !header_equals(request,"X-ClawPal-Board",CLAWPAL_BOARD_ID) || !header_equals(request,"X-ClawPal-Layout",CLAWPAL_OTA_LAYOUT))
        return httpd_resp_send_err(request,HTTPD_400_BAD_REQUEST,"Incorrect board, layout or image size");
    char expected[65];
    if(httpd_req_get_hdr_value_str(request,"X-ClawPal-SHA256",expected,sizeof(expected))!=ESP_OK || strlen(expected)!=64)
        return httpd_resp_send_err(request,HTTPD_400_BAD_REQUEST,"Expected SHA-256 required");
    for(int i=0;i<64;i++)if(!strchr("0123456789abcdef",expected[i]))return httpd_resp_send_err(request,HTTPD_400_BAD_REQUEST,"Invalid SHA-256");

    uint8_t *buffer=malloc(4096);if(!buffer)return httpd_resp_send_err(request,HTTPD_500_INTERNAL_SERVER_ERROR,"Insufficient update memory");
    size_t first=0,minimum=sizeof(esp_image_header_t)+sizeof(esp_image_segment_header_t)+sizeof(esp_app_desc_t);
    while(first<minimum){int n=httpd_req_recv(request,(char *)buffer+first,minimum-first);if(n<=0){free(buffer);return ESP_FAIL;}first+=n;}
    const esp_image_header_t *image=(const esp_image_header_t *)buffer;
    const esp_app_desc_t *app=(const esp_app_desc_t *)(buffer+sizeof(esp_image_header_t)+sizeof(esp_image_segment_header_t));
    if(image->magic!=ESP_IMAGE_HEADER_MAGIC || image->chip_id!=ESP_CHIP_ID_ESP32S3 || app->magic_word!=ESP_APP_DESC_MAGIC_WORD || strncmp(app->project_name,"clawpal",sizeof(app->project_name))!=0){
        free(buffer);return httpd_resp_send_err(request,HTTPD_400_BAD_REQUEST,"Not a ClawPal ESP32-S3 image");
    }
    esp_ota_handle_t handle=0;
    esp_err_t result=esp_ota_begin(target,request->content_len,&handle);
    if(result!=ESP_OK){free(buffer);return httpd_resp_send_err(request,HTTPD_500_INTERNAL_SERVER_ERROR,"Could not prepare inactive firmware slot");}
    mbedtls_sha256_context sha;mbedtls_sha256_init(&sha);mbedtls_sha256_starts(&sha,0);
    size_t received=0;int64_t start=esp_timer_get_time();size_t count=first;
    while(result==ESP_OK){
        mbedtls_sha256_update(&sha,buffer,count);result=esp_ota_write(handle,buffer,count);received+=count;
        if(result!=ESP_OK || received==(size_t)request->content_len)break;
        if(esp_timer_get_time()-start>180000000){result=ESP_ERR_TIMEOUT;break;}
        int n=httpd_req_recv(request,(char *)buffer,MIN(4096,request->content_len-received));
        if(n<=0){result=ESP_FAIL;break;}count=n;
    }
    uint8_t digest[32];mbedtls_sha256_finish(&sha,digest);mbedtls_sha256_free(&sha);free(buffer);
    char actual[65];for(int i=0;i<32;i++)snprintf(actual+i*2,3,"%02x",digest[i]);
    if(result!=ESP_OK || received!=(size_t)request->content_len || strcmp(actual,expected)!=0){
        esp_ota_abort(handle);return httpd_resp_send_err(request,HTTPD_400_BAD_REQUEST,"Incomplete or corrupted image; current firmware retained");
    }
    result=esp_ota_end(handle);
    if(result!=ESP_OK)return httpd_resp_send_err(request,HTTPD_400_BAD_REQUEST,"Firmware validation failed; current firmware retained");
    result=esp_ota_set_boot_partition(target);
    if(result!=ESP_OK)return httpd_resp_send_err(request,HTTPD_500_INTERNAL_SERVER_ERROR,"Could not activate new firmware");
    reboot_pending=true;httpd_resp_set_type(request,"application/json");
    result=httpd_resp_sendstr(request,"{\"accepted\":true,\"restarting\":true}");
    if(xTaskCreate(restart_task,"ota_restart",2048,NULL,2,NULL)!=pdPASS)esp_restart();
    return result;
}
