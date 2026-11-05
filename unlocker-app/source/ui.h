#ifndef UI_H
#define UI_H

/* 由 main.c 提供 */
int api_count(void);
int api_get(int id, char *name, int cap, int *unlocked);
int api_unlock(int id);
void api_refresh(void);
int api_order_len(void);
int api_order_get(int i, int *id, int *delay_sec);
int api_user_id(void);

/* 进入界面主循环，用户退出时返回 */
void ui_run(void);

#endif
