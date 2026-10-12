idf_component_register(SRCS "esp_lcd_ili9341.c" INCLUDE_DIRS "include" REQUIRES esp_lcd)
target_compile_definitions(${COMPONENT_LIB} PRIVATE ESP_LCD_ILI9341_VER_MAJOR=2 ESP_LCD_ILI9341_VER_MINOR=1 ESP_LCD_ILI9341_VER_PATCH=0)
