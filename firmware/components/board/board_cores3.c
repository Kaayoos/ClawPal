/* CoreS3 adapter based on Espressif esp-bsp (Apache-2.0).
 * See README.md for the pinned upstream revision and hardware status. */
#include "board.h"
#include "driver/i2c.h"
#include "driver/spi_master.h"
#include "esp_check.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_ili9341.h"
#include "ili9342e_init_cmds.h"
#include "esp_lcd_touch_ft5x06.h"
#include "esp_timer.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "lvgl.h"
static const char *TAG="cores3";
static SemaphoreHandle_t mutex;
static bool initialized;
static esp_lcd_touch_handle_t touch;
static lv_disp_drv_t display_driver;
static esp_err_t reg_read(uint8_t addr,uint8_t reg,uint8_t *value){return i2c_master_write_read_device(I2C_NUM_0,addr,&reg,1,value,1,pdMS_TO_TICKS(100));}
static esp_err_t reg_write(uint8_t addr,uint8_t reg,uint8_t value){uint8_t b[]={reg,value};return i2c_master_write_to_device(I2C_NUM_0,addr,b,2,pdMS_TO_TICKS(100));}
static esp_err_t reg_bits(uint8_t addr,uint8_t reg,uint8_t mask,bool on){uint8_t value;ESP_RETURN_ON_ERROR(reg_read(addr,reg,&value),TAG,"Read control register");return reg_write(addr,reg,on?value|mask:value&~mask);}
esp_err_t board_set_backlight(bool on){
    if(!initialized)return ESP_ERR_INVALID_STATE;
    if(on)ESP_RETURN_ON_ERROR(reg_write(0x34,0x99,28),TAG,"DLDO1 3.3V");
    return reg_bits(0x34,0x90,0x80,on);
}
static bool IRAM_ATTR transfer_done(esp_lcd_panel_io_handle_t io,esp_lcd_panel_io_event_data_t *ev,void *ctx){lv_disp_flush_ready(&display_driver);return false;}
static void flush(lv_disp_drv_t *drv,const lv_area_t *area,lv_color_t *pixels){
    size_t count=(area->x2-area->x1+1)*(area->y2-area->y1+1);
    for(size_t i=0;i<count;++i)pixels[i].full=__builtin_bswap16(pixels[i].full);
    esp_err_t e=esp_lcd_panel_draw_bitmap(drv->user_data,area->x1,area->y1,area->x2+1,area->y2+1,pixels);
    if(e!=ESP_OK){ESP_LOGE(TAG,"SPI transfer failed: %s",esp_err_to_name(e));lv_disp_flush_ready(drv);}
}
static void read_touch(lv_indev_drv_t *drv,lv_indev_data_t *data){
    uint16_t x=0,y=0;uint8_t n=0;
    bool down=esp_lcd_touch_read_data(touch)==ESP_OK&&esp_lcd_touch_get_coordinates(touch,&x,&y,NULL,&n,1)&&n;
    data->state=down?LV_INDEV_STATE_PRESSED:LV_INDEV_STATE_RELEASED;
    if(down){data->point.x=x;data->point.y=y;}
}
static void tick(void *ctx){lv_tick_inc(2);}
bool board_lvgl_lock(int ms){return mutex&&xSemaphoreTakeRecursive(mutex,ms<0?portMAX_DELAY:pdMS_TO_TICKS(ms))==pdTRUE;}
void board_lvgl_unlock(void){xSemaphoreGiveRecursive(mutex);}
static void task(void *ctx){while(1){board_lvgl_lock(-1);uint32_t wait=lv_timer_handler();board_lvgl_unlock();vTaskDelay(pdMS_TO_TICKS(wait<2?2:wait>20?20:wait));}}
esp_err_t board_init(void){
    ESP_RETURN_ON_FALSE(!initialized,ESP_ERR_INVALID_STATE,TAG,"Already initialized");
    i2c_config_t ic={.mode=I2C_MODE_MASTER,.sda_io_num=12,.scl_io_num=11,.sda_pullup_en=true,.scl_pullup_en=true,.master.clk_speed=400000};
    ESP_RETURN_ON_ERROR(i2c_param_config(I2C_NUM_0,&ic),TAG,"I2C config");ESP_RETURN_ON_ERROR(i2c_driver_install(I2C_NUM_0,I2C_MODE_MASTER,0,0,0),TAG,"I2C init");
    // AW9523B: switch only touch reset P0.0 and LCD reset P1.1 to GPIO outputs.
    ESP_RETURN_ON_ERROR(reg_bits(0x58,0x12,1,true),TAG,"Touch GPIO mode");ESP_RETURN_ON_ERROR(reg_bits(0x58,0x13,2,true),TAG,"LCD GPIO mode");
    ESP_RETURN_ON_ERROR(reg_bits(0x58,0x04,1,false),TAG,"Touch output");ESP_RETURN_ON_ERROR(reg_bits(0x58,0x05,2,false),TAG,"LCD output");
    ESP_RETURN_ON_ERROR(reg_bits(0x58,0x02,1,false),TAG,"Touch reset");ESP_RETURN_ON_ERROR(reg_bits(0x58,0x03,2,false),TAG,"LCD reset");vTaskDelay(pdMS_TO_TICKS(20));
    ESP_RETURN_ON_ERROR(reg_bits(0x58,0x02,1,true),TAG,"Touch release");ESP_RETURN_ON_ERROR(reg_bits(0x58,0x03,2,true),TAG,"LCD release");vTaskDelay(pdMS_TO_TICKS(300));
    uint8_t revision=0;reg_read(0x38,0xa6,&revision);
    spi_bus_config_t bus={.mosi_io_num=37,.miso_io_num=-1,.sclk_io_num=36,.quadwp_io_num=-1,.quadhd_io_num=-1,.max_transfer_sz=320*24*2};
    ESP_RETURN_ON_ERROR(spi_bus_initialize(SPI3_HOST,&bus,SPI_DMA_CH_AUTO),TAG,"SPI init");
    esp_lcd_panel_io_handle_t io=NULL;esp_lcd_panel_io_spi_config_t pc={.dc_gpio_num=35,.cs_gpio_num=3,.pclk_hz=40000000,.lcd_cmd_bits=8,.lcd_param_bits=8,.spi_mode=0,.trans_queue_depth=10,.on_color_trans_done=transfer_done};
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_spi((esp_lcd_spi_bus_handle_t)SPI3_HOST,&pc,&io),TAG,"Panel IO");
    const ili9341_vendor_config_t vendor={.init_cmds=ili9342e_init_cmds,.init_cmds_size=sizeof(ili9342e_init_cmds)/sizeof(ili9342e_init_cmds[0])};
    esp_lcd_panel_dev_config_t dev={.reset_gpio_num=-1,.rgb_ele_order=LCD_RGB_ELEMENT_ORDER_BGR,.bits_per_pixel=16,.vendor_config=revision==0x12?(void*)&vendor:NULL};
    esp_lcd_panel_handle_t panel=NULL;ESP_RETURN_ON_ERROR(esp_lcd_new_panel_ili9341(io,&dev,&panel),TAG,"Panel driver");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_reset(panel),TAG,"Panel reset");ESP_RETURN_ON_ERROR(esp_lcd_panel_init(panel),TAG,"Panel init");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_invert_color(panel,true),TAG,"Color invert");ESP_RETURN_ON_ERROR(esp_lcd_panel_swap_xy(panel,false),TAG,"Orientation");ESP_RETURN_ON_ERROR(esp_lcd_panel_mirror(panel,false,false),TAG,"Mirror");ESP_RETURN_ON_ERROR(esp_lcd_panel_disp_on_off(panel,true),TAG,"Display on");
    esp_lcd_panel_io_handle_t ti=NULL;const esp_lcd_panel_io_i2c_config_t tic=ESP_LCD_TOUCH_IO_I2C_FT5x06_CONFIG();
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_i2c((esp_lcd_i2c_bus_handle_t)I2C_NUM_0,&tic,&ti),TAG,"Touch IO");
    esp_lcd_touch_config_t tc={.x_max=320,.y_max=240,.rst_gpio_num=-1,.int_gpio_num=-1};
    ESP_RETURN_ON_ERROR(esp_lcd_touch_new_i2c_ft5x06(ti,&tc,&touch),TAG,"Touch driver");
    lv_init();mutex=xSemaphoreCreateRecursiveMutex();ESP_RETURN_ON_FALSE(mutex,ESP_ERR_NO_MEM,TAG,"LVGL mutex");
    static lv_disp_draw_buf_t draw;lv_color_t *pixels=heap_caps_malloc(320*24*sizeof(lv_color_t),MALLOC_CAP_DMA|MALLOC_CAP_INTERNAL);ESP_RETURN_ON_FALSE(pixels,ESP_ERR_NO_MEM,TAG,"DMA buffer");
    lv_disp_draw_buf_init(&draw,pixels,NULL,320*24);lv_disp_drv_init(&display_driver);display_driver.hor_res=320;display_driver.ver_res=240;display_driver.draw_buf=&draw;display_driver.flush_cb=flush;display_driver.user_data=panel;
    ESP_RETURN_ON_FALSE(lv_disp_drv_register(&display_driver),ESP_ERR_NO_MEM,TAG,"LVGL display");
    static lv_indev_drv_t input;lv_indev_drv_init(&input);input.type=LV_INDEV_TYPE_POINTER;input.read_cb=read_touch;lv_indev_drv_register(&input);
    esp_timer_handle_t timer;esp_timer_create_args_t args={.callback=tick,.name="lv_tick"};ESP_RETURN_ON_ERROR(esp_timer_create(&args,&timer),TAG,"Tick timer");ESP_RETURN_ON_ERROR(esp_timer_start_periodic(timer,2000),TAG,"Tick start");
    ESP_RETURN_ON_FALSE(xTaskCreatePinnedToCore(task,"lvgl",8192,NULL,4,NULL,1)==pdPASS,ESP_ERR_NO_MEM,TAG,"LVGL task");
    initialized=true;return board_set_backlight(true);
}
