#pragma once
#ifndef CLAWPAL_PROFILE
#define CLAWPAL_PROFILE 0
#endif
#if CLAWPAL_PROFILE == 0
#include "../../../boards/waveshare-esp32-s3-touch-lcd-4.3b.h"
#elif CLAWPAL_PROFILE == 1
#include "../../../boards/waveshare-esp32-s3-touch-lcd-5.h"
#elif CLAWPAL_PROFILE == 2
#include "../../../boards/waveshare-esp32-s3-touch-lcd-7-16mb.h"
#elif CLAWPAL_PROFILE == 3
#include "../../../boards/waveshare-esp32-s3-touch-lcd-7-8mb.h"
#elif CLAWPAL_PROFILE == 4
#include "../../../boards/waveshare-esp32-s3-touch-lcd-2.8c.h"
#elif CLAWPAL_PROFILE == 5
#include "../../../boards/guition-esp32-4848s040c-i.h"
#elif CLAWPAL_PROFILE == 6
#include "../../../boards/m5stack-cores3.h"
#else
#error Unknown ClawPal board profile
#endif
