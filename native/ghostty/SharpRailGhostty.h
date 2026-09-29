#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
void *sr_terminal_create(const char *directory, const char *clipboard_directory);
void sr_terminal_set_colors(void *view, const uint32_t *colors, double minimum_contrast);
void sr_terminal_destroy(void *view);
void sr_terminal_focus(void *view);
void sr_terminal_input(void *view, const char *text);
bool sr_terminal_rendered(void *view);
size_t sr_terminal_read(void *view, char *buffer, size_t capacity);
