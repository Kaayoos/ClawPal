// SPDX-FileCopyrightText: 2023-2025 Espressif Systems (Shanghai) CO LTD
// SPDX-License-Identifier: Apache-2.0
// Derived from Espressif ESP32_Display_Panel, Apache-2.0. See board/README.md.
#pragma once
#define CLAWPAL_CMD(delay, cmd, ...) {cmd, (uint8_t[])__VA_ARGS__, sizeof((uint8_t[])__VA_ARGS__), delay}
#define CLAWPAL_CMD0(delay, cmd) {cmd, NULL, 0, delay}
#define ESP_PANEL_BOARD_LCD_RGB_CLK_HZ          (18 * 1000 * 1000)
#define ESP_PANEL_BOARD_LCD_RGB_HPW             (8)
#define ESP_PANEL_BOARD_LCD_RGB_HBP             (10)
#define ESP_PANEL_BOARD_LCD_RGB_HFP             (50)
#define ESP_PANEL_BOARD_LCD_RGB_VPW             (2)
#define ESP_PANEL_BOARD_LCD_RGB_VBP             (18)
#define ESP_PANEL_BOARD_LCD_RGB_VFP             (8)
#define ESP_PANEL_BOARD_LCD_RGB_PCLK_ACTIVE_NEG (0)     
#define ESP_PANEL_BOARD_LCD_RGB_IO_HSYNC        (38)
#define ESP_PANEL_BOARD_LCD_RGB_IO_VSYNC        (39)
#define ESP_PANEL_BOARD_LCD_RGB_IO_DE           (40)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_PCLK         (41)
#define ESP_PANEL_BOARD_LCD_RGB_IO_DISP         (-1)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA0        (5)     
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA1        (45)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA2        (48)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA3        (47)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA4        (21)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA5        (14)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA6        (13)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA7        (12)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA8        (11)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA9        (10)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA10       (9)     
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA11       (46)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA12       (3)     
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA13       (8)     
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA14       (18)    
#define ESP_PANEL_BOARD_LCD_RGB_IO_DATA15       (17)    
#define ESP_PANEL_BOARD_TOUCH_I2C_IO_SCL            (7)
#define ESP_PANEL_BOARD_TOUCH_I2C_IO_SDA            (15)
#define CLAWPAL_LCD_INIT()                       \
    {                                                               \
        {0xFF, (uint8_t []){0x77, 0x01, 0x00, 0x00, 0x13}, 5, 0},\
        {0xEF, (uint8_t []){0x08}, 1, 0},\
        {0xFF, (uint8_t []){0x77, 0x01, 0x00, 0x00, 0x10}, 5, 0},\
        {0xC0, (uint8_t []){0x3B, 0x00}, 2, 0},\
        {0xC1, (uint8_t []){0x10, 0x0C}, 2, 0},\
        {0xC2, (uint8_t []){0x07, 0x0A}, 2, 0},\
        {0xC7, (uint8_t []){0x00}, 1, 0},\
        {0xCC, (uint8_t []){0x10}, 1, 0},\
        {0xCD, (uint8_t []){0x08}, 1, 0},\
        {0xB0, (uint8_t []){0x05, 0x12, 0x98, 0x0E, 0x0F, 0x07, 0x07, 0x09, 0x09, 0x23, 0x05, 0x52, 0x0F, 0x67, 0x2C, 0x11}, 16, 0},\
        {0xB1, (uint8_t []){0x0B, 0x11, 0x97, 0x0C, 0x12, 0x06, 0x06, 0x08, 0x08, 0x22, 0x03, 0x51, 0x11, 0x66, 0x2B, 0x0F}, 16, 0},\
        {0xFF, (uint8_t []){0x77, 0x01, 0x00, 0x00, 0x11}, 5, 0},\
        {0xB0, (uint8_t []){0x5D}, 1, 0},\
        {0xB1, (uint8_t []){0x3E}, 1, 0},\
        {0xB2, (uint8_t []){0x81}, 1, 0},\
        {0xB3, (uint8_t []){0x80}, 1, 0},\
        {0xB5, (uint8_t []){0x4E}, 1, 0},\
        {0xB7, (uint8_t []){0x85}, 1, 0},\
        {0xB8, (uint8_t []){0x20}, 1, 0},\
        {0xC1, (uint8_t []){0x78}, 1, 0},\
        {0xC2, (uint8_t []){0x78}, 1, 0},\
        {0xD0, (uint8_t []){0x88}, 1, 0},\
        {0xE0, (uint8_t []){0x00, 0x00, 0x02}, 3, 0},\
        {0xE1, (uint8_t []){0x06, 0x30, 0x08, 0x30, 0x05, 0x30, 0x07, 0x30, 0x00, 0x33, 0x33}, 11, 0},\
        {0xE2, (uint8_t []){0x11, 0x11, 0x33, 0x33, 0xF4, 0x00, 0x00, 0x00, 0xF4, 0x00, 0x00, 0x00}, 12, 0},\
        {0xE3, (uint8_t []){0x00, 0x00, 0x11, 0x11}, 4, 0},\
        {0xE4, (uint8_t []){0x44, 0x44}, 2, 0},\
        {0xE5, (uint8_t []){0x0D, 0xF5, 0x30, 0xF0, 0x0F, 0xF7, 0x30, 0xF0, 0x09, 0xF1, 0x30, 0xF0, 0x0B, 0xF3, 0x30, 0xF0}, 16, 0},\
        {0xE6, (uint8_t []){0x00, 0x00, 0x11, 0x11}, 4, 0},\
        {0xE7, (uint8_t []){0x44, 0x44}, 2, 0},\
        {0xE8, (uint8_t []){0x0C, 0xF4, 0x30, 0xF0, 0x0E, 0xF6, 0x30, 0xF0, 0x08, 0xF0, 0x30, 0xF0, 0x0A, 0xF2, 0x30, 0xF0}, 16, 0},\
        {0xE9, (uint8_t []){0x36, 0x01}, 2, 0},\
        {0xEB, (uint8_t []){0x00, 0x01, 0xE4, 0xE4, 0x44, 0x88, 0x40}, 7, 0},\
        {0xED, (uint8_t []){0xFF, 0x10, 0xAF, 0x76, 0x54, 0x2B, 0xCF, 0xFF, 0xFF, 0xFC, 0xB2, 0x45, 0x67, 0xFA, 0x01, 0xFF}, 16, 0},\
        {0xEF, (uint8_t []){0x08, 0x08, 0x08, 0x45, 0x3F, 0x54}, 6, 0},\
        {0xFF, (uint8_t []){0x77, 0x01, 0x00, 0x00, 0x00}, 5, 0},\
        {0x11, (uint8_t []){0x00}, 0, 120},\
        {0x3A, (uint8_t []){0x66}, 1, 0},\
        {0x36, (uint8_t []){0x00}, 1, 0},\
        {0x35, (uint8_t []){0x00}, 1, 0},\
        {0x29, (uint8_t []){0x00}, 0, 0},\
    }
