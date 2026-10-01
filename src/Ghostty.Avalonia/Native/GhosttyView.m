#import <AppKit/AppKit.h>
#import <QuartzCore/QuartzCore.h>
#import <IOSurface/IOSurface.h>
#import <Metal/Metal.h>
#include "ghostty.h"
#include "GhosttyView.h"

@interface GAVTerminalView : NSView <NSTextInputClient>
@property(nonatomic, assign) ghostty_surface_t surface;
@property(nonatomic, assign) BOOL overLink;
@property(nonatomic, strong) NSMutableAttributedString *marked;
@property(nonatomic, strong) NSMutableArray<NSString *> *keyText;
@property(nonatomic, copy) NSString *clipboardDirectory;
@property(nonatomic, assign) gav_view_event_cb callback;
@property(nonatomic, assign) gav_view_shortcut_cb shortcut;
@property(nonatomic, assign) void *context;
- (void)resizeSurface;
@end

static ghostty_app_t app;
static ghostty_input_mods_e modifiers(NSEventModifierFlags flags) {
    return ((flags & NSEventModifierFlagShift) ? GHOSTTY_MODS_SHIFT : 0) |
           ((flags & NSEventModifierFlagControl) ? GHOSTTY_MODS_CTRL : 0) |
           ((flags & NSEventModifierFlagOption) ? GHOSTTY_MODS_ALT : 0) |
           ((flags & NSEventModifierFlagCommand) ? GHOSTTY_MODS_SUPER : 0) |
           ((flags & NSEventModifierFlagCapsLock) ? GHOSTTY_MODS_CAPS : 0);
}
static void wakeup(void *data) {
    dispatch_async(dispatch_get_main_queue(), ^{ if (app) ghostty_app_tick(app); });
}
static bool action(ghostty_app_t instance, ghostty_target_s target, ghostty_action_s value) {
    if (value.tag == GHOSTTY_ACTION_MOUSE_OVER_LINK && target.tag == GHOSTTY_TARGET_SURFACE) {
        GAVTerminalView *view = (__bridge GAVTerminalView *)ghostty_surface_userdata(target.target.surface);
        view.overLink = value.action.mouse_over_link.len > 0;
        return true;
    }
    if (value.tag == GHOSTTY_ACTION_SHOW_CHILD_EXITED && target.tag == GHOSTTY_TARGET_SURFACE) {
        GAVTerminalView *view = (__bridge GAVTerminalView *)ghostty_surface_userdata(target.target.surface);
        if (view.callback) view.callback(view.context, GAV_VIEW_EXITED, (int32_t)value.action.child_exited.exit_code);
        // Ghostty still prints its own exit notice in the terminal.
        return false;
    }
    if (value.tag == GHOSTTY_ACTION_OPEN_URL) {
        NSString *text = [[NSString alloc] initWithBytes:value.action.open_url.url length:value.action.open_url.len encoding:NSUTF8StringEncoding];
        NSURL *url = [NSURL URLWithString:text];
        if (url) { [[NSWorkspace sharedWorkspace] openURL:url]; return true; }
    }
    return false;
}
static void readClipboard(void *data, ghostty_clipboard_e clipboard, void *state) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)data;
    NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
    NSString *text = [pasteboard stringForType:NSPasteboardTypeString] ?: @"";
    NSData *image = !view.clipboardDirectory ? nil : [pasteboard dataForType:NSPasteboardTypePNG] ?: [pasteboard dataForType:NSPasteboardTypeTIFF];
    if (image) {
        NSBitmapImageRep *bitmap = [NSBitmapImageRep imageRepWithData:image];
        NSData *png = [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        NSString *path = [view.clipboardDirectory stringByAppendingPathComponent:[NSUUID.UUID.UUIDString stringByAppendingPathExtension:@"png"]];
        NSError *error = nil;
        if (!png || ![NSFileManager.defaultManager createDirectoryAtPath:view.clipboardDirectory withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions: @0700} error:&error] ||
            ![png writeToFile:path options:NSDataWritingAtomic error:&error]) {
            NSAlert *alert = [NSAlert new];
            alert.messageText = @"Could not paste the clipboard image";
            alert.informativeText = error.localizedDescription ?: @"The clipboard image could not be decoded.";
            [alert runModal];
            ghostty_surface_complete_clipboard_request(view.surface, "", state, false);
            return;
        }
        text = [NSString stringWithFormat:@"'%@'", [path stringByReplacingOccurrencesOfString:@"'" withString:@"'\\''"]];
    }
    ghostty_surface_complete_clipboard_request(view.surface, text.UTF8String, state, false);
}
static void confirmClipboard(void *data, const char *text, void *state, ghostty_clipboard_request_e request) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)data;
    NSAlert *alert = [NSAlert new];
    alert.messageText = @"Allow terminal clipboard access?";
    alert.informativeText = @"The terminal requested clipboard data that requires confirmation.";
    [alert addButtonWithTitle:@"Allow"];
    [alert addButtonWithTitle:@"Cancel"];
    bool allowed = [alert runModal] == NSAlertFirstButtonReturn;
    // Completing even a cancelled read releases Ghostty's pending request.
    ghostty_surface_complete_clipboard_request(view.surface, allowed ? text : "", state, true);
}
static void writeClipboard(void *data, const char *text, ghostty_clipboard_e clipboard, bool confirm) {
    NSString *value = [NSString stringWithUTF8String:text];
    if (!value) return;
    if (confirm) {
        NSAlert *alert = [NSAlert new];
        alert.messageText = @"Allow terminal to replace the clipboard?";
        [alert addButtonWithTitle:@"Allow"]; [alert addButtonWithTitle:@"Cancel"];
        if ([alert runModal] != NSAlertFirstButtonReturn) return;
    }
    NSPasteboard *pasteboard = [NSPasteboard generalPasteboard];
    [pasteboard clearContents];
    [pasteboard setString:value forType:NSPasteboardTypeString];
}
static void closeSurface(void *data, bool alive) {
    // The tab owns the session; an exited shell stays visible until its tab closes.
}
static bool initialize(void) {
    if (app) return true;
    if (!MTLCreateSystemDefaultDevice()) return false;
    NSString *resources = [[[NSBundle mainBundle] executablePath].stringByDeletingLastPathComponent stringByAppendingPathComponent:@"ghostty"];
    NSString *bundled = [[NSBundle mainBundle].resourcePath stringByAppendingPathComponent:@"ghostty"];
    if ([[NSFileManager defaultManager] fileExistsAtPath:bundled]) resources = bundled;
    if ([[NSFileManager defaultManager] fileExistsAtPath:resources]) setenv("GHOSTTY_RESOURCES_DIR", resources.UTF8String, 1);
    char *args[] = {(char *)NSProcessInfo.processInfo.processName.UTF8String, NULL};
    if (ghostty_init(1, args) != GHOSTTY_SUCCESS) return false;
    ghostty_config_t config = ghostty_config_new();
    ghostty_config_finalize(config);
    ghostty_runtime_config_s runtime = {
        .wakeup_cb = wakeup, .action_cb = action,
        .read_clipboard_cb = readClipboard, .confirm_read_clipboard_cb = confirmClipboard,
        .write_clipboard_cb = writeClipboard, .close_surface_cb = closeSurface
    };
    app = ghostty_app_new(&runtime, config);
    ghostty_config_free(config);
    if (!app) return false;
    // Ghostty keys macos-option-as-alt off the keyboard layout; reload it now and whenever the selection changes,
    // as Ghostty's app does, so Option acts as Alt on U.S. layouts (Option+Backspace deletes a word).
    ghostty_app_keyboard_changed(app);
    [NSNotificationCenter.defaultCenter addObserverForName:NSTextInputContextKeyboardSelectionDidChangeNotification object:nil
        queue:NSOperationQueue.mainQueue usingBlock:^(NSNotification *note) { if (app) ghostty_app_keyboard_changed(app); }];
    return true;
}

@implementation GAVTerminalView
- (BOOL)isFlipped { return YES; }
- (BOOL)acceptsFirstResponder { return YES; }
- (BOOL)becomeFirstResponder {
    if (self.surface) { ghostty_app_set_focus(app, true); ghostty_surface_set_focus(self.surface, true); }
    return YES;
}
- (BOOL)resignFirstResponder {
    if (self.surface) ghostty_surface_set_focus(self.surface, false);
    return YES;
}
- (void)viewDidMoveToWindow {
    [super viewDidMoveToWindow];
    [self resizeSurface];
    if (self.surface) ghostty_surface_set_occlusion(self.surface, self.window != nil);
}
- (void)viewDidChangeBackingProperties { [super viewDidChangeBackingProperties]; [self resizeSurface]; }
- (void)setFrameSize:(NSSize)size { [super setFrameSize:size]; [self resizeSurface]; }
- (void)resizeSurface {
    if (!self.surface) return;
    CGFloat scale = self.window.backingScaleFactor ?: NSScreen.mainScreen.backingScaleFactor;
    ghostty_surface_set_content_scale(self.surface, scale, scale);
    ghostty_surface_set_size(self.surface, MAX(1, self.bounds.size.width * scale), MAX(1, self.bounds.size.height * scale));
    ghostty_surface_refresh(self.surface);
}
- (void)updateTrackingAreas {
    for (NSTrackingArea *area in self.trackingAreas) [self removeTrackingArea:area];
    [self addTrackingArea:[[NSTrackingArea alloc] initWithRect:NSZeroRect options:NSTrackingMouseMoved | NSTrackingActiveInKeyWindow | NSTrackingInVisibleRect owner:self userInfo:nil]];
    [super updateTrackingAreas];
}
// Consumed modifiers come from the translation event, as in Ghostty's own app: when Option acts as Alt it
// did not produce the text, so the encoder keeps Alt (for example ESC DEL or CSI 127;3u for Option+Backspace).
- (void)sendKey:(NSEvent *)event translation:(NSEvent *)translation action:(ghostty_input_action_e)kind text:(NSString *)text composing:(BOOL)composing {
    if (!self.surface) return;
    if (text.length && ([text characterAtIndex:0] < 0x20 || ([text characterAtIndex:0] >= 0xF700 && [text characterAtIndex:0] <= 0xF8FF))) text = nil;
    NSString *unshifted = [event charactersByApplyingModifiers:0];
    ghostty_input_key_s key = { .action = kind, .mods = modifiers(event.modifierFlags),
        .consumed_mods = modifiers(translation.modifierFlags & ~(NSEventModifierFlagControl | NSEventModifierFlagCommand)),
        .keycode = event.keyCode, .text = text.UTF8String,
        .unshifted_codepoint = unshifted.length ? [unshifted characterAtIndex:0] : 0, .composing = composing };
    ghostty_surface_key(self.surface, key);
}
// The owner sees Command key equivalents first, so its own shortcuts work while the terminal has focus.
- (BOOL)forwardShortcut:(NSEvent *)event {
    if (!(event.modifierFlags & NSEventModifierFlagCommand) || !self.shortcut) return NO;
    NSString *key = event.charactersIgnoringModifiers.lowercaseString ?: @"";
    return self.shortcut(self.context, key.UTF8String, modifiers(event.modifierFlags), event.isARepeat);
}
// Ghostty decides which modifiers take part in text translation (macos-option-as-alt and the keyboard layout).
- (NSEvent *)translationEvent:(NSEvent *)event {
    ghostty_input_mods_e mods = ghostty_surface_key_translation_mods(self.surface, modifiers(event.modifierFlags));
    NSEventModifierFlags flags = event.modifierFlags;
    // Keep the event's hidden bits, which matter for dead keys; only toggle the four translatable modifiers.
    const struct { NSEventModifierFlags flag; ghostty_input_mods_e mod; } pairs[] = {
        { NSEventModifierFlagShift, GHOSTTY_MODS_SHIFT }, { NSEventModifierFlagControl, GHOSTTY_MODS_CTRL },
        { NSEventModifierFlagOption, GHOSTTY_MODS_ALT }, { NSEventModifierFlagCommand, GHOSTTY_MODS_SUPER } };
    for (size_t index = 0; index < sizeof pairs / sizeof *pairs; index++)
        flags = (mods & pairs[index].mod) ? (flags | pairs[index].flag) : (flags & ~pairs[index].flag);
    // Reuse the original event when nothing changes; AppKit input methods such as Korean rely on its identity.
    if (flags == event.modifierFlags) return event;
    return [NSEvent keyEventWithType:event.type location:event.locationInWindow modifierFlags:flags timestamp:event.timestamp
        windowNumber:event.windowNumber context:nil characters:[event charactersByApplyingModifiers:flags] ?: @""
        charactersIgnoringModifiers:event.charactersIgnoringModifiers ?: @"" isARepeat:event.isARepeat keyCode:event.keyCode] ?: event;
}
- (void)keyDown:(NSEvent *)event {
    if ([self forwardShortcut:event]) return;
    if (!self.surface) return;
    NSEvent *translation = [self translationEvent:event];
    BOOL wasMarked = self.hasMarkedText;
    self.keyText = [NSMutableArray new];
    [self interpretKeyEvents:@[translation]];
    ghostty_surface_preedit(self.surface, self.marked.string.UTF8String, [self.marked.string lengthOfBytesUsingEncoding:NSUTF8StringEncoding]);
    ghostty_input_action_e kind = event.isARepeat ? GHOSTTY_ACTION_REPEAT : GHOSTTY_ACTION_PRESS;
    if (self.keyText.count) for (NSString *text in self.keyText) [self sendKey:event translation:translation action:kind text:text composing:NO];
    else [self sendKey:event translation:translation action:kind text:(self.hasMarkedText ? nil : translation.characters) composing:self.hasMarkedText || wasMarked];
    self.keyText = nil;
}
- (void)keyUp:(NSEvent *)event { [self sendKey:event translation:event action:GHOSTTY_ACTION_RELEASE text:nil composing:NO]; }
- (BOOL)performKeyEquivalent:(NSEvent *)event {
    if (self.window.firstResponder != self) return NO;
    if ([self forwardShortcut:event]) return YES;
    ghostty_input_key_s key = { .action = GHOSTTY_ACTION_PRESS, .mods = modifiers(event.modifierFlags), .keycode = event.keyCode, .text = event.characters.UTF8String };
    if (!ghostty_surface_key_is_binding(self.surface, key)) return NO;
    [self keyDown:event]; [self keyUp:event]; return YES;
}
- (void)insertText:(id)value replacementRange:(NSRange)range {
    NSString *text = [value isKindOfClass:NSAttributedString.class] ? [value string] : value;
    [self unmarkText];
    if (self.keyText) [self.keyText addObject:[text copy]];
    else ghostty_surface_text(self.surface, text.UTF8String, [text lengthOfBytesUsingEncoding:NSUTF8StringEncoding]);
}
- (void)setMarkedText:(id)value selectedRange:(NSRange)selection replacementRange:(NSRange)range {
    self.marked = [value isKindOfClass:NSAttributedString.class] ? [value mutableCopy] : [[NSMutableAttributedString alloc] initWithString:value];
}
- (void)unmarkText { self.marked = [[NSMutableAttributedString alloc] initWithString:@""]; }
- (BOOL)hasMarkedText { return self.marked.length > 0; }
- (NSRange)markedRange { return self.hasMarkedText ? NSMakeRange(0, self.marked.length) : NSMakeRange(NSNotFound, 0); }
- (NSRange)selectedRange { return NSMakeRange(NSNotFound, 0); }
- (NSArray<NSAttributedStringKey> *)validAttributesForMarkedText { return @[]; }
- (NSAttributedString *)attributedSubstringForProposedRange:(NSRange)range actualRange:(NSRangePointer)actual { return nil; }
- (NSUInteger)characterIndexForPoint:(NSPoint)point { return 0; }
- (NSRect)firstRectForCharacterRange:(NSRange)range actualRange:(NSRangePointer)actual {
    double x, y, width, height;
    ghostty_surface_ime_point(self.surface, &x, &y, &width, &height);
    return [self.window convertRectToScreen:[self convertRect:NSMakeRect(x, y, width, height) toView:nil]];
}
- (void)doCommandBySelector:(SEL)selector { }
- (void)mouseMoved:(NSEvent *)event {
    NSPoint point = [self convertPoint:event.locationInWindow fromView:nil];
    ghostty_surface_mouse_pos(self.surface, point.x, point.y, modifiers(event.modifierFlags));
}
- (void)mouseDragged:(NSEvent *)event { [self mouseMoved:event]; }
- (void)rightMouseDragged:(NSEvent *)event { [self mouseMoved:event]; }
- (void)otherMouseDragged:(NSEvent *)event { [self mouseMoved:event]; }
- (void)mouseDown:(NSEvent *)event {
    [self.window makeFirstResponder:self]; [self mouseMoved:event];
    ghostty_surface_mouse_button(self.surface, GHOSTTY_MOUSE_PRESS, GHOSTTY_MOUSE_LEFT, modifiers(event.modifierFlags));
}
- (void)mouseUp:(NSEvent *)event { ghostty_surface_mouse_button(self.surface, GHOSTTY_MOUSE_RELEASE, GHOSTTY_MOUSE_LEFT, modifiers(event.modifierFlags)); }
- (void)rightMouseDown:(NSEvent *)event { [self mouseMoved:event]; ghostty_surface_mouse_button(self.surface, GHOSTTY_MOUSE_PRESS, GHOSTTY_MOUSE_RIGHT, modifiers(event.modifierFlags)); }
- (void)rightMouseUp:(NSEvent *)event { ghostty_surface_mouse_button(self.surface, GHOSTTY_MOUSE_RELEASE, GHOSTTY_MOUSE_RIGHT, modifiers(event.modifierFlags)); }
- (void)otherMouseDown:(NSEvent *)event { [self mouseMoved:event]; ghostty_surface_mouse_button(self.surface, GHOSTTY_MOUSE_PRESS, GHOSTTY_MOUSE_MIDDLE, modifiers(event.modifierFlags)); }
- (void)otherMouseUp:(NSEvent *)event { ghostty_surface_mouse_button(self.surface, GHOSTTY_MOUSE_RELEASE, GHOSTTY_MOUSE_MIDDLE, modifiers(event.modifierFlags)); }
- (void)scrollWheel:(NSEvent *)event {
    double factor = event.hasPreciseScrollingDeltas ? 1 : 10;
    ghostty_surface_mouse_scroll(self.surface, event.scrollingDeltaX * factor, event.scrollingDeltaY * factor, event.hasPreciseScrollingDeltas ? 1 : 0);
}
@end

void *gav_view_create(const char *directory, const char *clipboard_directory, const char *command,
                      const char *environment_name, const char *environment_value,
                      gav_view_event_cb event, gav_view_shortcut_cb shortcut, void *context) {
    NSCAssert(NSThread.isMainThread, @"Terminal views require the main thread");
    if (!initialize()) return NULL;
    GAVTerminalView *view = [[GAVTerminalView alloc] initWithFrame:NSMakeRect(0, 0, 640, 360)];
    view.clipboardDirectory = clipboard_directory ? [NSString stringWithUTF8String:clipboard_directory] : nil;
    view.callback = event;
    view.shortcut = shortcut;
    view.context = context;
    [view unmarkText];
    ghostty_surface_config_s config = ghostty_surface_config_new();
    config.platform_tag = GHOSTTY_PLATFORM_MACOS;
    config.platform.macos.nsview = (__bridge void *)view;
    config.userdata = (__bridge void *)view;
    config.scale_factor = NSScreen.mainScreen.backingScaleFactor;
    config.working_directory = directory;
    config.command = command;
    ghostty_env_var_s variable = { .key = environment_name, .value = environment_value };
    if (environment_name && environment_value) { config.env_vars = &variable; config.env_var_count = 1; }
    view.surface = ghostty_surface_new(app, &config);
    if (!view.surface) return NULL;
    // Ghostty 1.2.3 presents Metal-rendered textures through its IOSurfaceLayer.
    if (![NSStringFromClass(view.layer.class) isEqualToString:@"IOSurfaceLayer"]) {
        ghostty_surface_free(view.surface); return NULL;
    }
    [view resizeSurface];
    NSLog(@"GHOSTTY_AVALONIA renderer=Metal presentation=IOSurfaceLayer device=%@", MTLCreateSystemDefaultDevice().name);
    return (__bridge_retained void *)view;
}
void gav_view_destroy(void *pointer) {
    GAVTerminalView *view = (__bridge_transfer GAVTerminalView *)pointer;
    view.callback = NULL;
    view.shortcut = NULL;
    [view removeFromSuperview];
    if (view.surface) { ghostty_surface_free(view.surface); view.surface = NULL; }
}
bool gav_view_busy(void *pointer) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    return view.surface && !ghostty_surface_process_exited(view.surface) && ghostty_surface_needs_confirm_quit(view.surface);
}
void gav_view_focus(void *pointer) { GAVTerminalView *view = (__bridge GAVTerminalView *)pointer; [view.window makeFirstResponder:view]; }
void gav_view_set_colors(void *pointer, const uint32_t *colors, double minimum_contrast) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    NSMutableString *text = [NSMutableString stringWithFormat:@"background = #%06x\nforeground = #%06x\n", colors[0] & 0xffffff, colors[1] & 0xffffff];
    NSArray<NSString *> *optional = @[@"cursor-color", @"selection-background", @"selection-foreground"];
    for (NSUInteger i = 0; i < optional.count; i++)
        if (colors[i + 2] >> 24) [text appendFormat:@"%@ = #%06x\n", optional[i], colors[i + 2] & 0xffffff];
    for (NSUInteger i = 0; i < 16; i++) [text appendFormat:@"palette = %lu=#%06x\n", (unsigned long)i, colors[i + 5] & 0xffffff];
    [text appendFormat:@"minimum-contrast = %@\n", @(minimum_contrast).stringValue];
    NSString *path = [NSTemporaryDirectory() stringByAppendingPathComponent:[@"ghostty-theme-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSError *error = nil;
    if (![text writeToFile:path atomically:YES encoding:NSUTF8StringEncoding error:&error]) {
        NSLog(@"Could not write Ghostty theme: %@", error);
        return;
    }
    ghostty_config_t config = ghostty_config_new();
    ghostty_config_load_file(config, path.fileSystemRepresentation);
    ghostty_config_finalize(config);
    ghostty_surface_update_config(view.surface, config);
    ghostty_config_free(config);
    [NSFileManager.defaultManager removeItemAtPath:path error:nil];
}
void gav_view_input(void *pointer, const char *text) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    const char *start = text;
    for (const char *cursor = text; ; cursor++) {
        if (*cursor != '\r' && *cursor != '\n' && *cursor != 0) continue;
        if (cursor > start) ghostty_surface_text(view.surface, start, cursor - start);
        if (!*cursor) break;
        ghostty_input_key_s key = { .action = GHOSTTY_ACTION_PRESS, .keycode = 36, .text = "\r" };
        ghostty_surface_key(view.surface, key);
        key.action = GHOSTTY_ACTION_RELEASE;
        ghostty_surface_key(view.surface, key);
        start = cursor + 1;
    }
}
bool gav_view_rendered(void *pointer) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    if (![NSStringFromClass(view.layer.class) isEqualToString:@"IOSurfaceLayer"] || !view.layer.contents) return false;
    CFTypeRef contents = (__bridge CFTypeRef)view.layer.contents;
    if (CFGetTypeID(contents) != IOSurfaceGetTypeID()) return false;
    IOSurfaceRef surface = (IOSurfaceRef)contents;
    if (IOSurfaceLock(surface, kIOSurfaceLockReadOnly, NULL) != kIOReturnSuccess) return false;
    const uint32_t *pixels = IOSurfaceGetBaseAddress(surface);
    size_t count = IOSurfaceGetAllocSize(surface) / sizeof(uint32_t);
    bool varied = false;
    for (size_t i = 1; pixels && i < count; i++) if (pixels[i] != pixels[0]) { varied = true; break; }
    IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, NULL);
    return varied;
}
size_t gav_view_read(void *pointer, char *buffer, size_t capacity) {
    if (!capacity) return 0;
    buffer[0] = 0;
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_selection_s selection = {
        .top_left = { .tag = GHOSTTY_POINT_SCREEN, .coord = GHOSTTY_POINT_COORD_TOP_LEFT },
        .bottom_right = { .tag = GHOSTTY_POINT_SCREEN, .coord = GHOSTTY_POINT_COORD_BOTTOM_RIGHT }
    };
    ghostty_text_s text;
    if (!ghostty_surface_read_text(view.surface, selection, &text)) return 0;
    size_t count = MIN(text.text_len, capacity - 1);
    memcpy(buffer, text.text, count); buffer[count] = 0;
    ghostty_surface_free_text(view.surface, &text);
    return count;
}

void gav_texture_resize(void *pointer, double width, double height, double scale) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    [view setFrameSize:NSMakeSize(width, height)];
    view.layer.contentsScale = scale;
    ghostty_surface_set_content_scale(view.surface, scale, scale);
    ghostty_surface_set_size(view.surface, MAX(1, width * scale), MAX(1, height * scale));
    ghostty_surface_refresh(view.surface);
}
void gav_texture_focus(void *pointer, bool focused) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_app_set_focus(app, true);
    ghostty_surface_set_focus(view.surface, focused);
}
void gav_texture_visible(void *pointer, bool visible) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_surface_set_occlusion(view.surface, visible);
    if (visible) ghostty_surface_refresh(view.surface);
}
bool gav_texture_key(void *pointer, int32_t action, uint32_t keycode, int32_t mods,
                     int32_t consumed, const char *text, uint32_t unshifted) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_input_key_s key = { .action = action, .keycode = keycode, .mods = mods,
        .consumed_mods = consumed, .text = text, .unshifted_codepoint = unshifted };
    return ghostty_surface_key(view.surface, key);
}
void gav_texture_text(void *pointer, const char *text) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_surface_preedit(view.surface, NULL, 0);
    // Committed IME text is input; ghostty_surface_text uses bracketed paste.
    ghostty_input_key_s key = { .action = GHOSTTY_ACTION_PRESS, .keycode = UINT32_MAX, .text = text };
    ghostty_surface_key(view.surface, key);
}
void gav_texture_preedit(void *pointer, const char *text) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_surface_preedit(view.surface, text, text ? strlen(text) : 0);
}
void gav_texture_ime_point(void *pointer, double *x, double *y, double *width, double *height) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_surface_ime_point(view.surface, x, y, width, height);
}
bool gav_texture_over_link(void *pointer) {
    return ((__bridge GAVTerminalView *)pointer).overLink;
}
void gav_texture_mouse(void *pointer, double x, double y, int32_t mods, int32_t action, int32_t button) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_surface_mouse_pos(view.surface, x, y, mods);
    if (action >= 0) ghostty_surface_mouse_button(view.surface, action, button, mods);
}
void gav_texture_scroll(void *pointer, double x, double y) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_surface_mouse_scroll(view.surface, x * 10, y * 10, 0);
}
void gav_texture_action(void *pointer, const char *action) {
    GAVTerminalView *view = (__bridge GAVTerminalView *)pointer;
    ghostty_surface_binding_action(view.surface, action, strlen(action));
}
