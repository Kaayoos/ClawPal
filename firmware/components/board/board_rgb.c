/* ClawPal RGB adapter. Board timings and ST7701 sequences derived from
 * Espressif ESP32_Display_Panel (Apache-2.0), pinned in README.md. */
#include "board.h"
#include "driver/gpio.h"
#include "driver/i2c.h"
#include "esp_check.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_rgb.h"
#include "esp_lcd_touch_gt911.h"
#include "esp_rom_sys.h"
#include "freertos/task.h"
#include "lvgl_port.h"

#if CLAWPAL_DRIVER_WAVESHARE_SQUARE
#include "waveshare_square.h"
#define BL_GPIO 6
#define TOUCH_INT 16
#define SPI_CLK 2
#define SPI_DATA 1
#elif CLAWPAL_DRIVER_GUITION_SQUARE
#include "guition_square.h"
#define BL_GPIO 38
#define TOUCH_INT -1
#define SPI_CLK 48
#define SPI_DATA 47
#else
#define BL_GPIO -1
#define TOUCH_INT 4
#define ESP_PANEL_BOARD_TOUCH_I2C_IO_SDA 8
#define ESP_PANEL_BOARD_TOUCH_I2C_IO_SCL 9
#define ESP_PANEL_BOARD_LCD_RGB_CLK_HZ 16000000
#define ESP_PANEL_BOARD_LCD_RGB_HPW 4
#define ESP_PANEL_BOARD_LCD_RGB_HBP 8
#define ESP_PANEL_BOARD_LCD_RGB_HFP 8
#define ESP_PANEL_BOARD_LCD_RGB_VPW 4
#define ESP_PANEL_BOARD_LCD_RGB_VBP 8
#define ESP_PANEL_BOARD_LCD_RGB_VFP 8
#define ESP_PANEL_BOARD_LCD_RGB_PCLK_ACTIVE_NEG 1
#define ESP_PANEL_BOARD_LCD_RGB_IO_HSYNC 46
#define ESP_PANEL_BOARD_LCD_RGB_IO_VSYNC 3
#define ESP_PANEL_BOARD_LCD_RGB_IO_DE 5
#define ESP_PANEL_BOARD_LCD_RGB_IO_PCLK 7
#endif
static const char *TAG="board";
static bool initialized;
static uint8_t exp_outputs;
static esp_err_t byte_write(uint8_t addr,uint8_t value) {return i2c_master_write_to_device(I2C_NUM_0,addr,&value,1,pdMS_TO_TICKS(100));}
static esp_err_t reg_write(uint8_t addr,uint8_t reg,uint8_t value) {uint8_t bytes[]={reg,value};return i2c_master_write_to_device(I2C_NUM_0,addr,bytes,2,pdMS_TO_TICKS(100));}
static esp_err_t outputs(uint8_t value) {
#if CLAWPAL_DRIVER_WAVESHARE_SQUARE
    esp_err_t e=reg_write(0x20,1,value);
#else
    esp_err_t e=byte_write(0x38,value);
#endif
    if(e==ESP_OK){exp_outputs=value;}
    return e;
}
#if !CLAWPAL_DRIVER_WAVESHARE_RGB
typedef struct {uint8_t cmd;const uint8_t *data;size_t count;unsigned delay;} init_cmd_t;
static const init_cmd_t init_commands[]=CLAWPAL_LCD_INIT();
static esp_err_t chip_select(bool high) {
#if CLAWPAL_DRIVER_WAVESHARE_SQUARE
    return outputs(high?exp_outputs|4:exp_outputs&~4);
#else
    return gpio_set_level(39,high);
#endif
}
static void spi_word(unsigned word) {
    for(int bit=8;bit>=0;--bit){gpio_set_level(SPI_CLK,0);gpio_set_level(SPI_DATA,(word>>bit)&1);esp_rom_delay_us(1);gpio_set_level(SPI_CLK,1);esp_rom_delay_us(1);}
    gpio_set_level(SPI_CLK,0);
}
static esp_err_t command(uint8_t cmd,const uint8_t *data,size_t count) {
    ESP_RETURN_ON_ERROR(chip_select(false),TAG,"LCD select");spi_word(cmd);
    for(size_t i=0;i<count;++i)spi_word(0x100|data[i]);
    return chip_select(true);
}
static esp_err_t lcd_control_init(void) {
    gpio_config_t conf={.pin_bit_mask=(1ULL<<SPI_CLK)|(1ULL<<SPI_DATA),.mode=GPIO_MODE_OUTPUT};
#if CLAWPAL_DRIVER_GUITION_SQUARE
    conf.pin_bit_mask|=1ULL<<39;
#endif
    ESP_RETURN_ON_ERROR(gpio_config(&conf),TAG,"LCD control pins");
    ESP_RETURN_ON_ERROR(chip_select(true),TAG,"LCD deselect");
    ESP_RETURN_ON_ERROR(command(0x01,NULL,0),TAG,"LCD reset");vTaskDelay(pdMS_TO_TICKS(120));
    // RGB panel input is wired as RGB565; vendor ST7701 tables use RGB666
    // mode with the missing low RGB bits tied to the board's data wiring.
    const uint8_t colmod=0x66;
    ESP_RETURN_ON_ERROR(command(0x3a,&colmod,1),TAG,"LCD color mode");
    for(unsigned i=0;i<sizeof(init_commands)/sizeof(init_commands[0]);++i){
        const init_cmd_t *c=&init_commands[i];ESP_RETURN_ON_ERROR(command(c->cmd,c->data,c->count),TAG,"LCD initialization");
        if(c->delay)vTaskDelay(pdMS_TO_TICKS(c->delay));
    }
    ESP_RETURN_ON_ERROR(command(0x29,NULL,0),TAG,"LCD display on");return ESP_OK;
}
#endif
static esp_err_t bus_init(void) {
    i2c_config_t conf={.mode=I2C_MODE_MASTER,.sda_io_num=ESP_PANEL_BOARD_TOUCH_I2C_IO_SDA,.scl_io_num=ESP_PANEL_BOARD_TOUCH_I2C_IO_SCL,.sda_pullup_en=true,.scl_pullup_en=true,.master.clk_speed=400000};
    ESP_RETURN_ON_ERROR(i2c_param_config(I2C_NUM_0,&conf),TAG,"I2C config");
    ESP_RETURN_ON_ERROR(i2c_driver_install(I2C_NUM_0,I2C_MODE_MASTER,0,0,0),TAG,"I2C start");
#if CLAWPAL_DRIVER_WAVESHARE_SQUARE
    ESP_RETURN_ON_ERROR(reg_write(0x20,3,0xf9),TAG,"TCA9554 outputs");
    ESP_RETURN_ON_ERROR(outputs(0xff),TAG,"TCA9554 idle");
#elif CLAWPAL_DRIVER_WAVESHARE_RGB
    ESP_RETURN_ON_ERROR(byte_write(0x24,1),TAG,"CH422G outputs");
    // EXIO5 stays low on these models to retain USB (rather than CAN).
    ESP_RETURN_ON_ERROR(outputs(0x0e),TAG,"CH422G idle");
#endif
#if !CLAWPAL_DRIVER_GUITION_SQUARE
    gpio_config_t pin={.pin_bit_mask=1ULL<<TOUCH_INT,.mode=GPIO_MODE_OUTPUT};
    ESP_RETURN_ON_ERROR(gpio_config(&pin),TAG,"Touch address pin");gpio_set_level(TOUCH_INT,0);
    ESP_RETURN_ON_ERROR(outputs(exp_outputs&~2),TAG,"Touch reset low");vTaskDelay(pdMS_TO_TICKS(100));
    ESP_RETURN_ON_ERROR(outputs(exp_outputs|2),TAG,"Touch reset high");vTaskDelay(pdMS_TO_TICKS(200));
    gpio_reset_pin(TOUCH_INT);
#endif
#if BL_GPIO >= 0
    gpio_config_t bl={.pin_bit_mask=1ULL<<BL_GPIO,.mode=GPIO_MODE_OUTPUT};ESP_RETURN_ON_ERROR(gpio_config(&bl),TAG,"Backlight pin");gpio_set_level(BL_GPIO,0);
#endif
    return ESP_OK;
}
static bool IRAM_ATTR frame_done(esp_lcd_panel_handle_t panel,const esp_lcd_rgb_panel_event_data_t *event,void *context){return lvgl_port_notify_rgb_vsync();}
esp_err_t board_init(void) {
    ESP_RETURN_ON_FALSE(!initialized,ESP_ERR_INVALID_STATE,TAG,"Already initialized");
    ESP_LOGI(TAG,"%s - experimental hardware profile",CLAWPAL_BOARD_NAME);
    ESP_RETURN_ON_ERROR(bus_init(),TAG,"Board bus");
#if !CLAWPAL_DRIVER_WAVESHARE_RGB
    ESP_RETURN_ON_ERROR(lcd_control_init(),TAG,"ST7701 control");
#endif
    esp_lcd_panel_handle_t panel=NULL;
    esp_lcd_rgb_panel_config_t conf={.clk_src=LCD_CLK_SRC_DEFAULT,.timings={
        .pclk_hz=ESP_PANEL_BOARD_LCD_RGB_CLK_HZ,.h_res=BOARD_DISPLAY_WIDTH,.v_res=BOARD_DISPLAY_HEIGHT,
        .hsync_pulse_width=ESP_PANEL_BOARD_LCD_RGB_HPW,.hsync_back_porch=ESP_PANEL_BOARD_LCD_RGB_HBP,.hsync_front_porch=ESP_PANEL_BOARD_LCD_RGB_HFP,
        .vsync_pulse_width=ESP_PANEL_BOARD_LCD_RGB_VPW,.vsync_back_porch=ESP_PANEL_BOARD_LCD_RGB_VBP,.vsync_front_porch=ESP_PANEL_BOARD_LCD_RGB_VFP,
        .flags.pclk_active_neg=ESP_PANEL_BOARD_LCD_RGB_PCLK_ACTIVE_NEG},
        .data_width=16,.bits_per_pixel=16,.num_fbs=LVGL_PORT_LCD_RGB_BUFFER_NUMS,.bounce_buffer_size_px=BOARD_DISPLAY_WIDTH*10,.sram_trans_align=4,.psram_trans_align=64,
        .hsync_gpio_num=ESP_PANEL_BOARD_LCD_RGB_IO_HSYNC,.vsync_gpio_num=ESP_PANEL_BOARD_LCD_RGB_IO_VSYNC,.de_gpio_num=ESP_PANEL_BOARD_LCD_RGB_IO_DE,.pclk_gpio_num=ESP_PANEL_BOARD_LCD_RGB_IO_PCLK,.disp_gpio_num=-1,
#if CLAWPAL_DRIVER_WAVESHARE_RGB
        .data_gpio_nums={14,38,18,17,10,39,0,45,48,47,21,1,2,42,41,40},
#else
        .data_gpio_nums={ESP_PANEL_BOARD_LCD_RGB_IO_DATA0,ESP_PANEL_BOARD_LCD_RGB_IO_DATA1,ESP_PANEL_BOARD_LCD_RGB_IO_DATA2,ESP_PANEL_BOARD_LCD_RGB_IO_DATA3,ESP_PANEL_BOARD_LCD_RGB_IO_DATA4,ESP_PANEL_BOARD_LCD_RGB_IO_DATA5,ESP_PANEL_BOARD_LCD_RGB_IO_DATA6,ESP_PANEL_BOARD_LCD_RGB_IO_DATA7,ESP_PANEL_BOARD_LCD_RGB_IO_DATA8,ESP_PANEL_BOARD_LCD_RGB_IO_DATA9,ESP_PANEL_BOARD_LCD_RGB_IO_DATA10,ESP_PANEL_BOARD_LCD_RGB_IO_DATA11,ESP_PANEL_BOARD_LCD_RGB_IO_DATA12,ESP_PANEL_BOARD_LCD_RGB_IO_DATA13,ESP_PANEL_BOARD_LCD_RGB_IO_DATA14,ESP_PANEL_BOARD_LCD_RGB_IO_DATA15},
#endif
        .flags.fb_in_psram=true};
    ESP_RETURN_ON_ERROR(esp_lcd_new_rgb_panel(&conf,&panel),TAG,"RGB allocation");ESP_RETURN_ON_ERROR(esp_lcd_panel_init(panel),TAG,"RGB init");
    esp_lcd_panel_io_handle_t io=NULL;esp_lcd_panel_io_i2c_config_t ic=ESP_LCD_TOUCH_IO_I2C_GT911_CONFIG();
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_i2c((esp_lcd_i2c_bus_handle_t)I2C_NUM_0,&ic,&io),TAG,"Touch IO");
    esp_lcd_touch_handle_t touch=NULL;esp_lcd_touch_config_t tc={.x_max=BOARD_DISPLAY_WIDTH,.y_max=BOARD_DISPLAY_HEIGHT,.rst_gpio_num=-1,.int_gpio_num=-1};
    ESP_RETURN_ON_ERROR(esp_lcd_touch_new_i2c_gt911(io,&tc,&touch),TAG,"GT911");
    ESP_RETURN_ON_ERROR(lvgl_port_init(panel,touch),TAG,"LVGL");
    esp_lcd_rgb_panel_event_callbacks_t cb={.on_bounce_frame_finish=frame_done};
    ESP_RETURN_ON_ERROR(esp_lcd_rgb_panel_register_event_callbacks(panel,&cb,NULL),TAG,"Frame callback");
    initialized=true;return board_set_backlight(true);
}
bool board_lvgl_lock(int ms){return lvgl_port_lock(ms);}
void board_lvgl_unlock(void){lvgl_port_unlock();}
esp_err_t board_set_backlight(bool on){
    if(!initialized)return ESP_ERR_INVALID_STATE;
#if BL_GPIO >= 0
    return gpio_set_level(BL_GPIO,on);
#else
    return outputs(on?exp_outputs|4:exp_outputs&~4);
#endif
}
