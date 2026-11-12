#ifndef UI_H
#define UI_H

#include <stdint.h>

/* 由 main.c 提供 */
int api_count(void);
int api_get(int id, char *name, int cap, int *unlocked);
int api_unlock(int id);
void api_refresh(void);
int api_order_len(void);
int api_order_get(int i, int *id, int *delay_sec);
int api_user_id(void);

/* 多奖杯集（合集类游戏）：npbind 里挂了几个 NPCOMMID 就有几个可选项 */
int api_game_count(void);
int api_game_cur(void);
int api_game_info(int idx, char *commid, int cap, uint32_t *label);
int api_switch_game(int idx);       /* 重新注册 context + 重数奖杯，0 = 成功 */

/* 进入界面主循环，用户退出时返回 */
void ui_run(void);

#endif
