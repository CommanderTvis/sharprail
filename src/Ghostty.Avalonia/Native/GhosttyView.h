#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
// Events delivered on the main thread to the view's owner.
enum { GAV_VIEW_EXITED = 1 };
typedef void (*gav_view_event_cb)(void *context, int32_t kind, int32_t value);
// Offered every Command key equivalent before Ghostty sees it; returning true consumes the key.
// key is the key's character ignoring modifiers other than Shift; mods use Ghostty's modifier bits.
typedef bool (*gav_view_shortcut_cb)(void *context, const char *key, int32_t mods, bool repeat);
// A NULL command runs the login shell; the optional variable is added to the child's environment.
// A non-NULL clipboard_directory pastes clipboard images as the shell-quoted path of a PNG saved there.
void *gav_view_create(const char *directory, const char *clipboard_directory, const char *command,
                      const char *environment_name, const char *environment_value,
                      gav_view_event_cb event, gav_view_shortcut_cb shortcut, void *context);
bool gav_view_busy(void *view);
void gav_view_set_colors(void *view, const uint32_t *colors, double minimum_contrast);
void gav_view_destroy(void *view);
void gav_view_focus(void *view);
void gav_view_input(void *view, const char *text);
bool gav_view_rendered(void *view);
size_t gav_view_read(void *view, char *buffer, size_t capacity);
// Texture mode keeps Ghostty's platform view unparented; Avalonia owns presentation and input.
void *gav_texture_enable(void *view);
typedef void (*gav_texture_callback)(void);
void gav_texture_notify(void *frames, gav_texture_callback callback);
void *gav_texture_acquire(void *frames, int32_t *width, int32_t *height, void **lease);
void gav_texture_return(void *lease);
void gav_texture_stop(void *frames);
int32_t gav_texture_retained_count(void *frames);
void gav_texture_release(void *surface);
void gav_texture_resize(void *view, double width, double height, double scale);
void gav_texture_focus(void *view, bool focused);
void gav_texture_visible(void *view, bool visible);
bool gav_texture_key(void *view, int32_t action, uint32_t keycode, int32_t mods,
                     int32_t consumed, const char *text, uint32_t unshifted);
void gav_texture_text(void *view, const char *text);
void gav_texture_preedit(void *view, const char *text);
void gav_texture_ime_point(void *view, double *x, double *y, double *width, double *height);
void gav_texture_mouse(void *view, double x, double y, int32_t mods, int32_t action, int32_t button);
void gav_texture_scroll(void *view, double x, double y);
void gav_texture_action(void *view, const char *action);
