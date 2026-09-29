#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
// Events delivered on the main thread to the terminal's owner.
enum { SR_TERMINAL_EXITED = 1, SR_TERMINAL_TOGGLE_BOTTOM_PANEL = 2 };
typedef void (*sr_terminal_event_cb)(void *context, int32_t kind, int32_t value);
// A NULL command runs the login shell; the optional variable is added to the child's environment.
void *sr_terminal_create(const char *directory, const char *clipboard_directory, const char *command,
                         const char *environment_name, const char *environment_value,
                         sr_terminal_event_cb callback, void *context);
bool sr_terminal_busy(void *view);
void sr_terminal_set_colors(void *view, uint32_t background, uint32_t foreground);
void sr_terminal_destroy(void *view);
void sr_terminal_focus(void *view);
void sr_terminal_input(void *view, const char *text);
bool sr_terminal_rendered(void *view);
size_t sr_terminal_read(void *view, char *buffer, size_t capacity);
