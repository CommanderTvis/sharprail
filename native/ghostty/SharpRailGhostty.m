#import <AppKit/AppKit.h>
#import <QuartzCore/QuartzCore.h>
#import <IOSurface/IOSurface.h>
#import <Metal/Metal.h>
#include "ghostty.h"
#include "SharpRailGhostty.h"
extern void sr_ghostty_config_colors(ghostty_config_t config, uint32_t background, uint32_t foreground);

@interface SRTerminalView : NSView <NSTextInputClient>
@property(nonatomic, assign) ghostty_surface_t surface;
@property(nonatomic, strong) NSMutableAttributedString *marked;
@property(nonatomic, strong) NSMutableArray<NSString *> *keyText;
@property(nonatomic, copy) NSString *clipboardDirectory;
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
    if (value.tag == GHOSTTY_ACTION_OPEN_URL) {
        NSString *text = [[NSString alloc] initWithBytes:value.action.open_url.url length:value.action.open_url.len encoding:NSUTF8StringEncoding];
        NSURL *url = [NSURL URLWithString:text];
        if (url) { [[NSWorkspace sharedWorkspace] openURL:url]; return true; }
    }
    return false;
}
static void readClipboard(void *data, ghostty_clipboard_e clipboard, void *state) {
    SRTerminalView *view = (__bridge SRTerminalView *)data;
    NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
    NSString *text = [pasteboard stringForType:NSPasteboardTypeString] ?: @"";
    NSData *image = [pasteboard dataForType:NSPasteboardTypePNG] ?: [pasteboard dataForType:NSPasteboardTypeTIFF];
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
    SRTerminalView *view = (__bridge SRTerminalView *)data;
    NSAlert *alert = [NSAlert new];
    alert.messageText = @"Allow terminal clipboard access?";
    alert.informativeText = @"The terminal requested clipboard data that requires confirmation.";
    [alert addButtonWithTitle:@"Allow"];
    [alert addButtonWithTitle:@"Cancel"];
    if ([alert runModal] == NSAlertFirstButtonReturn)
        ghostty_surface_complete_clipboard_request(view.surface, text, state, true);
}
static void writeClipboard(void *data, const char *text, ghostty_clipboard_e clipboard, bool confirm) {
    if (confirm) {
        NSAlert *alert = [NSAlert new];
        alert.messageText = @"Allow terminal to replace the clipboard?";
        [alert addButtonWithTitle:@"Allow"]; [alert addButtonWithTitle:@"Cancel"];
        if ([alert runModal] != NSAlertFirstButtonReturn) return;
    }
    NSPasteboard *pasteboard = [NSPasteboard generalPasteboard];
    [pasteboard clearContents];
    [pasteboard setString:[NSString stringWithUTF8String:text] forType:NSPasteboardTypeString];
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
    char *args[] = {"SharpRail", NULL};
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
    return app != NULL;
}

@implementation SRTerminalView
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
- (void)sendKey:(NSEvent *)event action:(ghostty_input_action_e)kind text:(NSString *)text composing:(BOOL)composing {
    if (!self.surface) return;
    if (text.length && ([text characterAtIndex:0] < 0x20 || ([text characterAtIndex:0] >= 0xF700 && [text characterAtIndex:0] <= 0xF8FF))) text = nil;
    NSString *unshifted = [event charactersByApplyingModifiers:0];
    ghostty_input_key_s key = { .action = kind, .mods = modifiers(event.modifierFlags),
        .consumed_mods = modifiers(event.modifierFlags & ~(NSEventModifierFlagControl | NSEventModifierFlagCommand)),
        .keycode = event.keyCode, .text = text.UTF8String,
        .unshifted_codepoint = unshifted.length ? [unshifted characterAtIndex:0] : 0, .composing = composing };
    ghostty_surface_key(self.surface, key);
}
- (void)keyDown:(NSEvent *)event {
    BOOL wasMarked = self.hasMarkedText;
    self.keyText = [NSMutableArray new];
    [self interpretKeyEvents:@[event]];
    ghostty_surface_preedit(self.surface, self.marked.string.UTF8String, [self.marked.string lengthOfBytesUsingEncoding:NSUTF8StringEncoding]);
    ghostty_input_action_e kind = event.isARepeat ? GHOSTTY_ACTION_REPEAT : GHOSTTY_ACTION_PRESS;
    if (self.keyText.count) for (NSString *text in self.keyText) [self sendKey:event action:kind text:text composing:NO];
    else [self sendKey:event action:kind text:(self.hasMarkedText ? nil : event.characters) composing:self.hasMarkedText || wasMarked];
    self.keyText = nil;
}
- (void)keyUp:(NSEvent *)event { [self sendKey:event action:GHOSTTY_ACTION_RELEASE text:nil composing:NO]; }
- (BOOL)performKeyEquivalent:(NSEvent *)event {
    if (self.window.firstResponder != self) return NO;
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

void *sr_terminal_create(const char *directory, const char *clipboard_directory) {
    NSCAssert(NSThread.isMainThread, @"Terminal views require the main thread");
    if (!initialize()) return NULL;
    SRTerminalView *view = [[SRTerminalView alloc] initWithFrame:NSMakeRect(0, 0, 640, 360)];
    view.clipboardDirectory = [NSString stringWithUTF8String:clipboard_directory];
    [view unmarkText];
    ghostty_surface_config_s config = ghostty_surface_config_new();
    config.platform_tag = GHOSTTY_PLATFORM_MACOS;
    config.platform.macos.nsview = (__bridge void *)view;
    config.userdata = (__bridge void *)view;
    config.scale_factor = NSScreen.mainScreen.backingScaleFactor;
    config.working_directory = directory;
    view.surface = ghostty_surface_new(app, &config);
    if (!view.surface) return NULL;
    // Ghostty 1.2.3 presents Metal-rendered textures through its IOSurfaceLayer.
    if (![NSStringFromClass(view.layer.class) isEqualToString:@"IOSurfaceLayer"]) {
        ghostty_surface_free(view.surface); return NULL;
    }
    [view resizeSurface];
    NSLog(@"SHARPRAIL_TERMINAL renderer=Metal presentation=IOSurfaceLayer device=%@", MTLCreateSystemDefaultDevice().name);
    return (__bridge_retained void *)view;
}
void sr_terminal_destroy(void *pointer) {
    SRTerminalView *view = (__bridge_transfer SRTerminalView *)pointer;
    [view removeFromSuperview];
    if (view.surface) { ghostty_surface_free(view.surface); view.surface = NULL; }
}
void sr_terminal_focus(void *pointer) { SRTerminalView *view = (__bridge SRTerminalView *)pointer; [view.window makeFirstResponder:view]; }
void sr_terminal_set_colors(void *pointer, uint32_t background, uint32_t foreground) {
    SRTerminalView *view = (__bridge SRTerminalView *)pointer;
    ghostty_config_t config = ghostty_config_new();
    sr_ghostty_config_colors(config, background, foreground);
    ghostty_config_finalize(config);
    ghostty_surface_update_config(view.surface, config);
    ghostty_config_free(config);
}
void sr_terminal_input(void *pointer, const char *text) {
    SRTerminalView *view = (__bridge SRTerminalView *)pointer;
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
bool sr_terminal_rendered(void *pointer) {
    SRTerminalView *view = (__bridge SRTerminalView *)pointer;
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
size_t sr_terminal_read(void *pointer, char *buffer, size_t capacity) {
    if (!capacity) return 0;
    buffer[0] = 0;
    SRTerminalView *view = (__bridge SRTerminalView *)pointer;
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
