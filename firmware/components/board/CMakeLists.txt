if(CLAWPAL_PROFILE EQUAL 0)
 set(board_sources "board.c" "lvgl_port.c")
elseif(CLAWPAL_BOARD STREQUAL "m5stack-cores3")
 set(board_sources "board_cores3.c")
else()
 set(board_sources "board_rgb.c" "lvgl_port.c")
endif()
idf_component_register(SRCS ${board_sources} INCLUDE_DIRS "include" PRIV_INCLUDE_DIRS "."
 REQUIRES lvgl__lvgl esp_lcd esp_timer driver espressif__esp_lcd_touch_gt911 espressif__esp_lcd_touch_ft5x06 espressif__esp_lcd_ili9341)