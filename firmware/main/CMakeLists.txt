idf_component_register(
    SRCS "language/buddy_language_font_14.c" "language/buddy_language_font_16.c" "language/buddy_language_font_20.c" "language/buddy_language_font_24.c" "language/buddy_language_font_32.c" "language/buddy_language_font_48.c" "main.c" "language/buddy_language.c" "language/buddy_language_data.c" "update/buddy_update.c" "settings/buddy_settings.c" "ui/buddy_ui.c" "ui/buddy_ui_motion.c" "network/buddy_network.c" "network/buddy_telemetry_parse.c" "network/buddy_auth.c" "assets/buddy_mascot.c" "assets/buddy_clock_font.c"
         "assets/buddy_music_font_20.c" "assets/buddy_music_font_32.c"
         "assets/buddy_numeral_font_96.c" "assets/buddy_numeral_font_48.c" "assets/buddy_numeral_font_32.c" "assets/buddy_numeral_font_24.c" "assets/buddy_numeral_font_128.c"
         "weather/buddy_weather.c" "weather/buddy_weather_parse.c"
         "ui/buddy_home.c" "ui/buddy_theme.c" "ui/buddy_theme_data.c" "ui/buddy_weather_page.c" "ui/buddy_weather_icon.c" "ui/buddy_app_icons.c" "assets/buddy_home_logos.c"
         "../tests/display_power_policy.c" "../tests/weather_animation_policy.c" "../tests/weather_units.cpp"
    INCLUDE_DIRS "language" "include" "ui" "network" "assets" "weather" "settings" "update"
    REQUIRES board esp_wifi esp_netif esp_event esp_http_server esp_http_client nvs_flash json mbedtls lwip esp_timer esp_hw_support
    PRIV_REQUIRES esp_app_format app_update bootloader_support espcoredump esp_partition
)
