// SPDX-FileCopyrightText: 2023-2025 Espressif Systems (Shanghai) CO LTD
// SPDX-License-Identifier: Apache-2.0
// Derived from Espressif ESP32_Display_Panel, Apache-2.0. See board/README.md.
#pragma once
#define CLAWPAL_CMD(delay, cmd, ...) {cmd, (uint8_t[])__VA_ARGS__, sizeof((uint8_t[])__VA_ARGS__), delay}
#define CLAWPAL_CMD0(delay, cmd) {cmd, NULL, 0, delay}
#define ESP_PANEL_BOARD_LCD_RGB_CLK_HZ          (26 * 1000 * 1000)
#define ESP_PANEL_BOARD_LCD_RGB_HPW             (10)
#define ESP_PANEL_BOARD_LCD_RGB_HBP             (10)
#define ESP_PANEL_BOARD_LCD_RGB_HFP             (20)
#define ESP_PANEL_BOARD_LCD_RGB_VPW             (10)
#define ESP_PANEL_BOARD_LCD_RGB_VBP             (10)
#define ESP_PANEL_BOARD_LCD_RGB_VFP             (10)
#define ESP_PANEL_BOARD_LCD_RGB_PCLK_ACTIVE_NEG (0)     
#define ESP_PANEL_BOARD_LCD_RGB_IO_HSYNC        (16)
#define ESP_PANEL_BOARD_LCD_RGB_IO_VSYNC        (17)
#define ESP_PANEL_BOARD_LCD_RGB_IO_DE           (18)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_PCLK         (21)
#define ESP_PANEL_BOARD_LCD_RGB_IO_DISP         (-1)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA0        (4)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA1        (5)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA2        (6)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA3        (7)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA4        (15)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA5        (8)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA6        (20)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA7        (3)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA8        (46)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA9        (9)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA10       (10)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA11       (11)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA12       (12)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA13       (13)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA14       (14)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA15       (0)    
#define ESP_PANEL_BOARD_TOUCH_I2C_IO_SCL            (45)
#define ESP_PANEL_BOARD_TOUCH_I2C_IO_SDA            (19)
#define CLAWPAL_LCD_INIT()                       \
    {                                                               \
        CLAWPAL_CMD(0, 0xFF, {0x77, 0x01, 0x00, 0x00, 0x10}), \
        CLAWPAL_CMD(0, 0xC0, {0x3B, 0x00}), \
        CLAWPAL_CMD(0, 0xC1, {0x0D, 0x02}), \
        CLAWPAL_CMD(0, 0xC2, {0x31, 0x05}), \
        CLAWPAL_CMD(0, 0xCD, {0x00}), \
        CLAWPAL_CMD(0, 0xB0, {0x00, 0x11, 0x18, 0x0E, 0x11, 0x06, 0x07, 0x08, 0x07, 0x22, 0x04, 0x12, 0x0F, 0xAA, 0x31, 0x18}), \
        CLAWPAL_CMD(0, 0xB1, {0x00, 0x11, 0x19, 0x0E, 0x12, 0x07, 0x08, 0x08, 0x08, 0x22, 0x04, 0x11, 0x11, 0xA9, 0x32, 0x18}), \
        CLAWPAL_CMD(0, 0xFF, {0x77, 0x01, 0x00, 0x00, 0x11}), \
        CLAWPAL_CMD(0, 0xB0, {0x60}), \
        CLAWPAL_CMD(0, 0xB1, {0x32}), \
        CLAWPAL_CMD(0, 0xB2, {0x07}), \
        CLAWPAL_CMD(0, 0xB3, {0x80}), \
        CLAWPAL_CMD(0, 0xB5, {0x49}), \
        CLAWPAL_CMD(0, 0xB7, {0x85}), \
        CLAWPAL_CMD(0, 0xB8, {0x21}), \
        CLAWPAL_CMD(0, 0xC1, {0x78}), \
        CLAWPAL_CMD(0, 0xC2, {0x78}), \
        CLAWPAL_CMD(0, 0xE0, {0x00, 0x1B, 0x02}), \
        CLAWPAL_CMD(0, 0xE1, {0x08, 0xA0, 0x00, 0x00, 0x07, 0xA0, 0x00, 0x00, 0x00, 0x44, 0x44}), \
        CLAWPAL_CMD(0, 0xE2, {0x11, 0x11, 0x44, 0x44, 0xED, 0xA0, 0x00, 0x00, 0xEC, 0xA0, 0x00, 0x00}), \
        CLAWPAL_CMD(0, 0xE3, {0x00, 0x00, 0x11, 0x11}), \
        CLAWPAL_CMD(0, 0xE4, {0x44, 0x44}), \
        CLAWPAL_CMD(0, 0xE5, {0x0A, 0xE9, 0xD8, 0xA0, 0x0C, 0xEB, 0xD8, 0xA0, 0x0E, 0xED, 0xD8, 0xA0, 0x10, 0xEF, 0xD8, 0xA0}), \
        CLAWPAL_CMD(0, 0xE6, {0x00, 0x00, 0x11, 0x11}), \
        CLAWPAL_CMD(0, 0xE7, {0x44, 0x44}), \
        CLAWPAL_CMD(0, 0xE8, {0x09, 0xE8, 0xD8, 0xA0, 0x0B, 0xEA, 0xD8, 0xA0, 0x0D, 0xEC, 0xD8, 0xA0, 0x0F, 0xEE, 0xD8, 0xA0}), \
        CLAWPAL_CMD(0, 0xEB, {0x02, 0x00, 0xE4, 0xE4, 0x88, 0x00, 0x40}), \
        CLAWPAL_CMD(0, 0xEC, {0x3C, 0x00}), \
        CLAWPAL_CMD(0, 0xED, {0xAB, 0x89, 0x76, 0x54, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x20, 0x45, 0x67, 0x98, 0xBA}), \
        CLAWPAL_CMD(0, 0xFF, {0x77, 0x01, 0x00, 0x00, 0x13}), \
        CLAWPAL_CMD(0, 0xE5, {0xE4}), \
        CLAWPAL_CMD(0, 0xFF, {0x77, 0x01, 0x00, 0x00, 0x00}), \
        CLAWPAL_CMD0(120, 0x11), \
    }
