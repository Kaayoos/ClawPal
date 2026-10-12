/*
 * Based on Waveshare's ESP32-S3-Touch-LCD-4.3B-BOX-Demo,
 * ESP-IDF/08_lvgl_Porting/main/waveshare_rgb_lcd_port.c.
 * Original SPDX-FileCopyrightText: 2022 Espressif Systems (Shanghai) CO LTD
 * Original SPDX-License-Identifier: CC0-1.0
 * Pin mapping, timings and GT911 reset sequence are preserved.
 */
#include "board.h"

#include "driver/gpio.h"
#include "driver/i2c.h"
#include "esp_check.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_rgb.h"
#include "esp_lcd_touch_gt911.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lvgl_port.h"

static const char *TAG = "board";
static bool initialized;

#define BOARD_I2C_PORT I2C_NUM_0
#define BOARD_I2C_SDA GPIO_NUM_8
#define BOARD_I2C_SCL GPIO_NUM_9
#define BOARD_TOUCH_INT GPIO_NUM_4
#define CH422G_MODE_ADDRESS 0x24
#define CH422G_OUTPUT_ADDRESS 0x38
#define CH422G_BACKLIGHT_BIT (1U << 2)

/* Vendor startup: LCD and touch reset deasserted; unused external IO stable. */
static uint8_t ch422g_outputs = 0x2E;

static esp_err_t expander_write(uint8_t address, uint8_t value)
{
    return i2c_master_write_to_device(BOARD_I2C_PORT, address, &value, 1,
                                      pdMS_TO_TICKS(1000));
}

static esp_err_t touch_bus_init(void)
{
    const i2c_config_t config = {
        .mode = I2C_MODE_MASTER,
        .sda_io_num = BOARD_I2C_SDA,
        .scl_io_num = BOARD_I2C_SCL,
        .sda_pullup_en = GPIO_PULLUP_ENABLE,
        .scl_pullup_en = GPIO_PULLUP_ENABLE,
        .master.clk_speed = 400000,
    };
    ESP_RETURN_ON_ERROR(i2c_param_config(BOARD_I2C_PORT, &config), TAG, "I2C config");
    ESP_RETURN_ON_ERROR(i2c_driver_install(BOARD_I2C_PORT, I2C_MODE_MASTER, 0, 0, 0),
                        TAG, "I2C driver");

    const gpio_config_t gpio = {
        .pin_bit_mask = 1ULL << BOARD_TOUCH_INT,
        .mode = GPIO_MODE_OUTPUT,
        .intr_type = GPIO_INTR_DISABLE,
    };
    ESP_RETURN_ON_ERROR(gpio_config(&gpio), TAG, "Touch reset GPIO");
    ESP_RETURN_ON_ERROR(expander_write(CH422G_MODE_ADDRESS, 0x01), TAG, "CH422G output mode");
    ESP_RETURN_ON_ERROR(expander_write(CH422G_OUTPUT_ADDRESS, 0x2C), TAG, "GT911 reset low");
    vTaskDelay(pdMS_TO_TICKS(100));
    ESP_RETURN_ON_ERROR(gpio_set_level(BOARD_TOUCH_INT, 0), TAG, "GT911 I2C address select");
    vTaskDelay(pdMS_TO_TICKS(100));
    ESP_RETURN_ON_ERROR(expander_write(CH422G_OUTPUT_ADDRESS, ch422g_outputs), TAG, "GT911 reset high");
    vTaskDelay(pdMS_TO_TICKS(200));
    return ESP_OK;
}

static bool IRAM_ATTR on_frame_finished(esp_lcd_panel_handle_t panel,
                                        const esp_lcd_rgb_panel_event_data_t *event,
                                        void *context)
{
    return lvgl_port_notify_rgb_vsync();
}

esp_err_t board_init(void)
{
    if (initialized) {
        return ESP_ERR_INVALID_STATE;
    }

    ESP_LOGI(TAG, "Waveshare ESP32-S3-Touch-LCD-4.3B: 800x480 RGB565, GT911");
    esp_lcd_panel_handle_t panel = NULL;
    const esp_lcd_rgb_panel_config_t panel_config = {
        .clk_src = LCD_CLK_SRC_DEFAULT,
        .timings = {
            .pclk_hz = 16000000,
            .h_res = BOARD_DISPLAY_WIDTH,
            .v_res = BOARD_DISPLAY_HEIGHT,
            .hsync_pulse_width = 4,
            .hsync_back_porch = 8,
            .hsync_front_porch = 8,
            .vsync_pulse_width = 4,
            .vsync_back_porch = 8,
            .vsync_front_porch = 8,
            .flags.pclk_active_neg = true,
        },
        .data_width = 16,
        .bits_per_pixel = 16,
        .num_fbs = LVGL_PORT_LCD_RGB_BUFFER_NUMS,
        .bounce_buffer_size_px = BOARD_DISPLAY_WIDTH * CONFIG_EXAMPLE_LCD_RGB_BOUNCE_BUFFER_HEIGHT,
        .sram_trans_align = 4,
        .psram_trans_align = 64,
        .hsync_gpio_num = GPIO_NUM_46,
        .vsync_gpio_num = GPIO_NUM_3,
        .de_gpio_num = GPIO_NUM_5,
        .pclk_gpio_num = GPIO_NUM_7,
        .disp_gpio_num = -1,
        .data_gpio_nums = {
            GPIO_NUM_14, GPIO_NUM_38, GPIO_NUM_18, GPIO_NUM_17,
            GPIO_NUM_10, GPIO_NUM_39, GPIO_NUM_0, GPIO_NUM_45,
            GPIO_NUM_48, GPIO_NUM_47, GPIO_NUM_21, GPIO_NUM_1,
            GPIO_NUM_2, GPIO_NUM_42, GPIO_NUM_41, GPIO_NUM_40,
        },
        .flags.fb_in_psram = true,
    };
    ESP_RETURN_ON_ERROR(esp_lcd_new_rgb_panel(&panel_config, &panel), TAG, "RGB panel allocation");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_init(panel), TAG, "RGB panel init");
    ESP_RETURN_ON_ERROR(touch_bus_init(), TAG, "Touch bus initialization");

    esp_lcd_panel_io_handle_t touch_io = NULL;
    const esp_lcd_panel_io_i2c_config_t io_config = ESP_LCD_TOUCH_IO_I2C_GT911_CONFIG();
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_i2c((esp_lcd_i2c_bus_handle_t)BOARD_I2C_PORT,
                                               &io_config, &touch_io), TAG, "Touch IO init");
    esp_lcd_touch_handle_t touch = NULL;
    const esp_lcd_touch_config_t touch_config = {
        .x_max = BOARD_DISPLAY_WIDTH,
        .y_max = BOARD_DISPLAY_HEIGHT,
        .rst_gpio_num = -1,
        .int_gpio_num = -1,
        .levels = { .reset = 0, .interrupt = 0 },
        .flags = { .swap_xy = 0, .mirror_x = 0, .mirror_y = 0 },
    };
    ESP_RETURN_ON_ERROR(esp_lcd_touch_new_i2c_gt911(touch_io, &touch_config, &touch), TAG, "GT911 init");
    ESP_RETURN_ON_ERROR(lvgl_port_init(panel, touch), TAG, "LVGL init");

    const esp_lcd_rgb_panel_event_callbacks_t callbacks = {
#if CONFIG_EXAMPLE_LCD_RGB_BOUNCE_BUFFER_HEIGHT > 0
        .on_bounce_frame_finish = on_frame_finished,
#else
        .on_vsync = on_frame_finished,
#endif
    };
    ESP_RETURN_ON_ERROR(esp_lcd_rgb_panel_register_event_callbacks(panel, &callbacks, NULL),
                        TAG, "RGB callbacks");
    initialized = true;
    ESP_LOGI(TAG, "Display, touch and LVGL ready");
    return ESP_OK;
}

bool board_lvgl_lock(int timeout_ms)
{
    return lvgl_port_lock(timeout_ms);
}

void board_lvgl_unlock(void)
{
    lvgl_port_unlock();
}

esp_err_t board_set_backlight(bool enabled)
{
    if (!initialized) {
        return ESP_ERR_INVALID_STATE;
    }
    /* Preserve every expander output other than the backlight bit. */
    uint8_t next = enabled ? (ch422g_outputs | CH422G_BACKLIGHT_BIT)
                           : (ch422g_outputs & ~CH422G_BACKLIGHT_BIT);
    esp_err_t error = expander_write(CH422G_OUTPUT_ADDRESS, next);
    if (error == ESP_OK) {
        ch422g_outputs = next;
    }
    return error;
}
