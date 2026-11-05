#ifndef _UNLOCK_ORDER_H_
#define _UNLOCK_ORDER_H_

#define UO_MAX 512

/* 顺序文件：/data/GoldHEN/trophy_unlocker/<TITLEID>.csv
 * 一行一个：奖杯ID,延迟毫秒    （延迟 = 解锁这个之后等多久再做下一个）
 * # 开头是注释。
 */

/* 读顺序文件，填充 ids/delays，返回条目数；没有文件或为空返回 0 */
int uo_load(const char *tid, int *ids, int *delays, int max);

/* 生成模板：1..count-1 顺序，0 号（白金）放最后。
 * 返回 0=已生成，1=文件已存在（不覆盖），-1=失败 */
int uo_write_template(const char *tid, int count, int default_delay_ms);

/* 从 /app0/sce_sys/param.sfo 读 TITLE_ID，成功返回 0 */
int uo_title_id(char *out, int len);

#endif
