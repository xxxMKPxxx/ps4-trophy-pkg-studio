#ifndef LOG_H
#define LOG_H

#include <stdint.h>

void log_open(void);
void log_str(const char *s);
void log_int(const char *label, int v);
void log_hex(const char *label, uint32_t v);
void log_line(const char *fmt, ...);

#endif
