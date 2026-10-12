#include "buddy_language.h"
#include "lvgl.h"
#include "esp_heap_caps.h"
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static atomic_uint language;
typedef struct label_binding { lv_obj_t *label;buddy_text_key_t key;struct label_binding *next; } label_binding_t;
static label_binding_t *bindings;
const char *buddy_text(buddy_text_key_t key)
{
    return (unsigned)key<BT_COUNT?buddy_language_texts[atomic_load(&language)][key]:"";
}
bool buddy_language_supported(const char *code)
{
    if(!code)return false;
    for(unsigned i=0;i<buddy_language_count;i++)if(strcmp(code,buddy_language_codes[i])==0)return true;
    return false;
}
const char *buddy_language_code(void){return buddy_language_codes[atomic_load(&language)];}
int buddy_text_key(const char *text)
{
    // Pointer identity ensures a song title or SSID resembling a label is never translated.
    for(unsigned i=0;i<buddy_language_count;i++)for(unsigned k=0;k<BT_COUNT;k++)if(text==buddy_language_texts[i][k])return (int)k;
    return -1;
}
const char *buddy_retranslate(const char *text){int key=buddy_text_key(text);return key<0?text:BT(key);}
const char *buddy_retranslate_copy(const char *text)
{
    for(unsigned i=0;i<buddy_language_count;i++)for(unsigned k=0;k<BT_COUNT;k++)if(strcmp(text,buddy_language_texts[i][k])==0)return BT(k);
    return text;
}
static void label_deleted(lv_event_t *event)
{
    label_binding_t **link=&bindings;
    while(*link){label_binding_t *item=*link;if(item->label==lv_event_get_target(event)){*link=item->next;free(item);return;}link=&item->next;}
}
void buddy_label_set_text(lv_obj_t *label,const char *text)
{
    int key=buddy_text_key(text);label_binding_t **link=&bindings;
    while(*link&&(*link)->label!=label)link=&(*link)->next;
    if(key>=0){
        if(!*link){*link=heap_caps_calloc(1,sizeof(label_binding_t),MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);if(*link){(*link)->label=label;lv_obj_add_event_cb(label,label_deleted,LV_EVENT_DELETE,NULL);}}
        if(*link)(*link)->key=(buddy_text_key_t)key;
    }else if(*link){label_binding_t *old=*link;*link=old->next;lv_obj_remove_event_cb(label,label_deleted);free(old);}
    lv_label_set_text(label,text?text:"");
}
void buddy_language_set(const char *code)
{
    unsigned next=0;
    for(unsigned i=0;i<buddy_language_count;i++)if(code&&strcmp(code,buddy_language_codes[i])==0){next=i;break;}
    if(next==atomic_load(&language))return;
    // Called by the UI task while holding the LVGL lock, never by the network worker.
    atomic_store(&language,next);
    for(label_binding_t *item=bindings;item;item=item->next)lv_label_set_text(item->label,BT(item->key));
}
const char *buddy_weekday(int day)
{
    const buddy_text_key_t keys[]={BT_WEEKDAY_0,BT_WEEKDAY_1,BT_WEEKDAY_2,BT_WEEKDAY_3,BT_WEEKDAY_4,BT_WEEKDAY_5,BT_WEEKDAY_6};
    return BT(keys[day>=0&&day<7?day:0]);
}
void buddy_date(char *out,size_t size,const struct tm *local,bool use_24h)
{
    const buddy_text_key_t months[]={BT_MONTH_0,BT_MONTH_1,BT_MONTH_2,BT_MONTH_3,BT_MONTH_4,BT_MONTH_5,BT_MONTH_6,BT_MONTH_7,BT_MONTH_8,BT_MONTH_9,BT_MONTH_10,BT_MONTH_11};
    snprintf(out,size,BT(BT_DATE_FORMAT),buddy_weekday(local->tm_wday),local->tm_mday,BT(months[local->tm_mon]));
    if(!use_24h){size_t used=strlen(out);if(used<size)snprintf(out+used,size-used," / %s",BT(local->tm_hour<12?BT_AM:BT_PM));}
}
