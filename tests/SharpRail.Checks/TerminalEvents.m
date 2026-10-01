#import <Carbon/Carbon.h>
#import <AppKit/AppKit.h>
#import <ApplicationServices/ApplicationServices.h>
#include <unistd.h>
#import <objc/runtime.h>
#import <IOSurface/IOSurface.h>
#import <QuartzCore/QuartzCore.h>

static NSPasteboard *testPasteboard;
void *sr_texture_content(const char *title) {
    NSString *name = [NSString stringWithUTF8String:title];
    for (NSWindow *window in NSApp.windows)
        if ([window.title isEqualToString:name]) return (__bridge void *)window.contentView;
    return NULL;
}
int32_t sr_texture_window_number(void *pointer) { return (int32_t)((__bridge NSView *)pointer).window.windowNumber; }
bool sr_texture_has_native_terminal(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    if ([NSStringFromClass(view.class) isEqualToString:@"GAVTerminalView"]) return true;
    for (NSView *child in view.subviews) if (sr_texture_has_native_terminal((__bridge void *)child)) return true;
    return false;
}
static NSPasteboard *isolatedPasteboard(id self, SEL selector) { (void)self; (void)selector; return testPasteboard; }

static IMP originalPasteboardGetter, originalAlertModal;
static bool allowClipboardRead;
static int clipboardPrompts;
static NSModalResponse clipboardAlert(id self, SEL selector) {
    (void)selector;
    NSAlert *alert = self;
    if (![alert.messageText isEqualToString:@"Allow terminal clipboard access?"])
        [NSException raise:@"Unexpected clipboard prompt" format:@"%@", alert.messageText];
    clipboardPrompts++;
    return allowClipboardRead ? NSAlertFirstButtonReturn : NSAlertSecondButtonReturn;
}
void sr_check_clipboard_begin(bool allow) {
    testPasteboard = [NSPasteboard pasteboardWithUniqueName];
    allowClipboardRead = allow;
    clipboardPrompts = 0;
    originalPasteboardGetter = method_setImplementation(class_getClassMethod(NSPasteboard.class, @selector(generalPasteboard)), (IMP)isolatedPasteboard);
    originalAlertModal = method_setImplementation(class_getInstanceMethod(NSAlert.class, @selector(runModal)), (IMP)clipboardAlert);
}
void sr_check_clipboard_end(void) {
    method_setImplementation(class_getClassMethod(NSPasteboard.class, @selector(generalPasteboard)), originalPasteboardGetter);
    method_setImplementation(class_getInstanceMethod(NSAlert.class, @selector(runModal)), originalAlertModal);
    [testPasteboard releaseGlobally]; testPasteboard = nil;
}
int sr_check_clipboard_prompts(void) { return clipboardPrompts; }
void sr_check_clipboard_allow(bool allow) { allowClipboardRead = allow; }
void sr_check_clipboard_set(const char *text) {
    [testPasteboard clearContents];
    [testPasteboard setString:[NSString stringWithUTF8String:text] forType:NSPasteboardTypeString];
}
size_t sr_check_clipboard_get(uint8_t *buffer, size_t capacity) {
    NSData *text = [([testPasteboard stringForType:NSPasteboardTypeString] ?: @"") dataUsingEncoding:NSUTF8StringEncoding];
    if (buffer) memcpy(buffer, text.bytes, MIN(capacity, text.length));
    return text.length;
}

void sr_check_paste(void *pointer, int format) {
    NSView *view = (__bridge NSView *)pointer;
    testPasteboard = [NSPasteboard pasteboardWithUniqueName];
    if (format == 0) [testPasteboard setString:@"TEXT_PASTE_OK" forType:NSPasteboardTypeString];
    else {
        NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:2 pixelsHigh:2
            bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
        memset(bitmap.bitmapData, 255, bitmap.bytesPerRow * bitmap.pixelsHigh);
        NSData *data = format == 1 ? [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}] : bitmap.TIFFRepresentation;
        [testPasteboard setData:data forType:format == 1 ? NSPasteboardTypePNG : NSPasteboardTypeTIFF];
    }
    Method getter = class_getClassMethod(NSPasteboard.class, @selector(generalPasteboard));
    IMP original = method_setImplementation(getter, (IMP)isolatedPasteboard);
    @try {
        [NSApp sendEvent:[NSEvent keyEventWithType:NSEventTypeKeyDown location:NSZeroPoint modifierFlags:NSEventModifierFlagCommand
            timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:view.window.windowNumber context:nil
            characters:@"v" charactersIgnoringModifiers:@"v" isARepeat:NO keyCode:9]];
    } @finally {
        method_setImplementation(getter, original);
        [testPasteboard releaseGlobally]; testPasteboard = nil;
    }
}

uint32_t sr_check_background(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    IOSurfaceRef surface = (__bridge IOSurfaceRef)view.layer.contents;
    if (!surface || CFGetTypeID(surface) != IOSurfaceGetTypeID()) return 0;
    if (IOSurfaceLock(surface, kIOSurfaceLockReadOnly, NULL) != kIOReturnSuccess) return 0;
    size_t x = IOSurfaceGetWidth(surface) - 2, y = IOSurfaceGetHeight(surface) - 2;
    const uint8_t *pixel = (const uint8_t *)IOSurfaceGetBaseAddress(surface) + y * IOSurfaceGetBytesPerRow(surface) + x * 4;
    uint32_t color = ((uint32_t)pixel[2] << 16) | ((uint32_t)pixel[1] << 8) | pixel[0];
    IOSurfaceUnlock(surface, kIOSurfaceLockReadOnly, NULL);
    return color;
}

static void interpretMutableText(id view, SEL selector, NSArray<NSEvent *> *events) {
    (void)selector;
    for (NSEvent *event in events) {
        NSMutableString *text = [event.characters mutableCopy];
        [(id<NSTextInputClient>)view insertText:text replacementRange:NSMakeRange(NSNotFound, 0)];
        // AppKit can reuse its mutable input buffer as soon as insertText returns.
        [text setString:@""];
    }
}

void sr_check_mutable_input(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    Class original = object_getClass(view);
    Class input = objc_allocateClassPair(original, "SRMutableInputCheck", 0);
    class_addMethod(input, @selector(interpretKeyEvents:), (IMP)interpretMutableText, "v@:@");
    objc_registerClassPair(input);
    object_setClass(view, input);
    NSString *command = @"printf 'MUTABLE_%s_OK\\n' INPUT";
    for (NSUInteger i = 0; i < command.length; i++) {
        NSString *text = [command substringWithRange:NSMakeRange(i, 1)];
        [NSApp sendEvent:[NSEvent keyEventWithType:NSEventTypeKeyDown location:NSZeroPoint modifierFlags:0
            timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:view.window.windowNumber context:nil
            characters:text charactersIgnoringModifiers:text isARepeat:NO keyCode:0]];
    }
    object_setClass(view, original);
}

void sr_check_activate(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    [NSApp activateIgnoringOtherApps:YES];
    [view.window makeKeyAndOrderFront:nil];
}

bool sr_check_ready(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    return NSApp.active && view.window.keyWindow && view.bounds.size.width > 0;
}

void sr_check_click(void *pointer, double x, double y) {
    NSView *view = (__bridge NSView *)pointer;
    NSWindow *window = view.window;
    // Checks use Avalonia's top-left coordinates, including unflipped content views.
    if (!view.isFlipped) y = view.bounds.size.height - y;
    NSPoint point = [view convertPoint:NSMakePoint(x, y) toView:nil];
    for (NSNumber *type in @[@(NSEventTypeLeftMouseDown), @(NSEventTypeLeftMouseUp)]) {
        [NSApp postEvent:[NSEvent mouseEventWithType:type.integerValue location:point modifierFlags:0
            timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:window.windowNumber context:nil
            eventNumber:0 clickCount:1 pressure:1] atStart:NO];
    }
}

bool sr_check_focused(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    return view.window.firstResponder == view;
}

void sr_check_key(void *pointer, const char *text, unsigned short keyCode, bool control) {
    NSView *view = (__bridge NSView *)pointer;
    NSString *characters = [NSString stringWithUTF8String:text];
    if (getenv("SHARPRAIL_CHECK_OS_INPUT")) {
        for (int down = 1; down >= 0; down--) {
            CGEventRef event = CGEventCreateKeyboardEvent(NULL, keyCode, down);
            UniChar buffer[16];
            NSUInteger length = MIN(characters.length, 16);
            [characters getCharacters:buffer range:NSMakeRange(0, length)];
            CGEventKeyboardSetUnicodeString(event, length, buffer);
            CGEventSetFlags(event, control ? kCGEventFlagMaskControl : 0);
            CGEventPostToPid(getpid(), event);
            CFRelease(event);
        }
        return;
    }
    for (NSNumber *type in @[@(NSEventTypeKeyDown), @(NSEventTypeKeyUp)]) {
        [NSApp sendEvent:[NSEvent keyEventWithType:type.integerValue location:NSZeroPoint
            modifierFlags:control ? NSEventModifierFlagControl : 0 timestamp:NSProcessInfo.processInfo.systemUptime
            windowNumber:view.window.windowNumber context:nil characters:characters
            charactersIgnoringModifiers:characters isARepeat:NO keyCode:keyCode]];
    }
}

// Selects a keyboard layout by input source id and returns the previous one (caller frees), so layout-dependent
// checks such as Option-as-Alt run on a known layout and restore the user's afterwards.
char *sr_check_select_layout(const char *identifier) {
    TISInputSourceRef current = TISCopyCurrentKeyboardLayoutInputSource();
    NSString *previous = [(__bridge NSString *)TISGetInputSourceProperty(current, kTISPropertyInputSourceID) copy];
    CFRelease(current);
    NSDictionary *filter = @{ (__bridge NSString *)kTISPropertyInputSourceID: [NSString stringWithUTF8String:identifier] };
    CFArrayRef sources = TISCreateInputSourceList((__bridge CFDictionaryRef)filter, true);
    if (sources && CFArrayGetCount(sources) > 0) TISSelectInputSource((TISInputSourceRef)CFArrayGetValueAtIndex(sources, 0));
    if (sources) CFRelease(sources);
    [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.2]];
    return strdup(previous.UTF8String);
}

// Sends Option+Backspace through the application, as a keyboard would while the view has focus.
void sr_check_option_backspace(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    if (getenv("SHARPRAIL_CHECK_OS_INPUT")) {
        for (int down = 1; down >= 0; down--) {
            CGEventRef event = CGEventCreateKeyboardEvent(NULL, 51, down);
            CGEventSetFlags(event, kCGEventFlagMaskAlternate);
            CGEventPostToPid(getpid(), event);
            CFRelease(event);
        }
        return;
    }
    for (NSNumber *type in @[@(NSEventTypeKeyDown), @(NSEventTypeKeyUp)]) {
        [NSApp sendEvent:[NSEvent keyEventWithType:type.integerValue location:NSZeroPoint modifierFlags:NSEventModifierFlagOption
            timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:view.window.windowNumber context:nil
            characters:@"\x7f" charactersIgnoringModifiers:@"\x7f" isARepeat:NO keyCode:51]];
    }
}

// Sends Command+Shift+J through the application, as a keyboard would while the view has focus.
void sr_check_toggle_bottom(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    for (NSNumber *type in @[@(NSEventTypeKeyDown), @(NSEventTypeKeyUp)]) {
        [NSApp sendEvent:[NSEvent keyEventWithType:type.integerValue location:NSZeroPoint
            modifierFlags:NSEventModifierFlagCommand | NSEventModifierFlagShift
            timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:view.window.windowNumber context:nil
            characters:@"J" charactersIgnoringModifiers:@"j" isARepeat:NO keyCode:38]];
    }
}
