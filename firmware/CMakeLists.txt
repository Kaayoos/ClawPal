cmake_minimum_required(VERSION 3.16)

set(CLAWPAL_BOARD "waveshare-esp32-s3-touch-lcd-4.3b" CACHE STRING "Exact ClawPal board ID")
include("${CMAKE_CURRENT_LIST_DIR}/boards/index.cmake")
list(FIND _clawpal_boards "${CLAWPAL_BOARD}" CLAWPAL_PROFILE)
if(CLAWPAL_PROFILE LESS 0)
 message(FATAL_ERROR "Unknown ClawPal board: ${CLAWPAL_BOARD}")
endif()
add_compile_definitions(CLAWPAL_PROFILE=${CLAWPAL_PROFILE})
set(SDKCONFIG_DEFAULTS "${CMAKE_CURRENT_LIST_DIR}/boards/${CLAWPAL_BOARD}.defaults")

set(PROJECT_VER "0.8.1")
set(EXTRA_COMPONENT_DIRS
    "${CMAKE_CURRENT_LIST_DIR}/../vendor/components"
)
set(COMPONENTS main)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(clawpal)

idf_component_get_property(lvgl_lib lvgl__lvgl COMPONENT_LIB)
target_compile_options(${lvgl_lib} PRIVATE -Wno-format)
