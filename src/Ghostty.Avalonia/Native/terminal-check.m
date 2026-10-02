#import <AppKit/AppKit.h>
#include "GhosttyView.h"
#include <signal.h>

static void require(bool condition, const char *message) {
    if (!condition) { fprintf(stderr, "FAIL %s\n", message); exit(1); }
}
static void pump(void) { [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]]; }
static NSString *readText(void *view) {
    char buffer[65536]; gav_view_read(view, buffer, sizeof(buffer));
    return [NSString stringWithUTF8String:buffer];
}
static void waitText(void *view, NSString *needle) {
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:20];
    while (![readText(view) containsString:needle] && deadline.timeIntervalSinceNow > 0) pump();
    if (![readText(view) containsString:needle]) fprintf(stderr, "Terminal contents: %s\n", readText(view).UTF8String);
    require([readText(view) containsString:needle], needle.UTF8String);
}
static void checkAgentNewline(NSWindow *window, void *pointer, bool enabled, const char *mode, int bytes, NSString *expected, int stage) {
    gav_view_set_agent_newline(pointer, enabled);
    NSString *command = [NSString stringWithFormat:@"saved=$(stty -g); stty raw -echo; printf '\\033[=0u\\033[>4;0m%s'; printf '\\n%%s_%%d\\n' KEY_READY %d; dd bs=1 count=%d 2>/dev/null | od -An -tx1; printf '\\033[=0u\\033[>4;0m'; stty \"$saved\"; printf '\\n%%s_%%d\\n' KEY_DONE %d\r", mode, stage, bytes, stage];
    gav_view_input(pointer, command.UTF8String);
    waitText(pointer, [NSString stringWithFormat:@"KEY_READY_%d", stage]);
    NSEvent *event = [NSEvent keyEventWithType:NSEventTypeKeyDown location:NSZeroPoint modifierFlags:NSEventModifierFlagShift timestamp:0
        windowNumber:window.windowNumber context:nil characters:@"\r" charactersIgnoringModifiers:@"\r" isARepeat:NO keyCode:36];
    [NSApp sendEvent:event];
    waitText(pointer, [NSString stringWithFormat:@"KEY_DONE_%d", stage]);
    NSString *screen = readText(pointer);
    NSRange begin = [screen rangeOfString:[NSString stringWithFormat:@"KEY_READY_%d", stage] options:NSBackwardsSearch];
    NSRange end = [screen rangeOfString:[NSString stringWithFormat:@"KEY_DONE_%d", stage] options:NSBackwardsSearch];
    NSString *result = [screen substringWithRange:NSMakeRange(NSMaxRange(begin), end.location - NSMaxRange(begin))];
    NSString *normalized = [[result stringByReplacingOccurrencesOfString:@"\\s+" withString:@" " options:NSRegularExpressionSearch range:NSMakeRange(0, result.length)] stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    if (![normalized containsString:expected]) fprintf(stderr, "Agent newline stage %d terminal contents: %s\n", stage, screen.UTF8String);
    require([normalized isEqualToString:expected], expected.UTF8String);
}
int main(int argc, char **argv) {
    @autoreleasepool {
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
        NSWindow *window = [[NSWindow alloc] initWithContentRect:NSMakeRect(100, 100, 800, 500)
            styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
        window.title = @"Ghostty.Avalonia Metal verification";
        void *pointer = gav_view_create(argv[1], [[NSString stringWithUTF8String:argv[1]] stringByAppendingPathComponent:@".bench/clipboard"].UTF8String, NULL, NULL, NULL, NULL, NULL, NULL);
        require(pointer != NULL, "embedded surface creation");
        NSView *view = (__bridge NSView *)pointer;
        window.contentView = view;
        [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];
        gav_view_focus(pointer);
        gav_view_input(pointer, "export SHARPRAIL_TERMINAL_CHECK=retained; printf '\\n%s:%s:%s\\n' SHARPRAIL_SHELL $$ \"$PWD\"\r");
        waitText(pointer, @"SHARPRAIL_SHELL:");
        require([readText(pointer) containsString:[NSString stringWithUTF8String:argv[1]]], "shell working directory");
        gav_view_input(pointer, "printf '\\033[32mMETAL_%s\\033[0m\\n' OUTPUT\r");
        waitText(pointer, @"METAL_OUTPUT");
        NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:10];
        while (!gav_view_rendered(pointer) && deadline.timeIntervalSinceNow > 0) pump();
        require(gav_view_rendered(pointer), "Metal IOSurface contains a presented frame");
        [view removeFromSuperview]; window.contentView = [NSView new];
        for (int i = 0; i < 5; i++) pump();
        window.contentView = view; [window setContentSize:NSMakeSize(640, 320)];
        gav_view_focus(pointer);
        gav_view_input(pointer, "printf '\\n%s:%s\\n' SESSION \"$SHARPRAIL_TERMINAL_CHECK\"\r");
        waitText(pointer, @"SESSION:retained");
        // Exercise AppKit's key path, not just direct text injection.
        NSString *command = @"printf 'KEY_%s' OK\r";
        for (NSUInteger i = 0; i < command.length; i++) {
            NSString *text = [command substringWithRange:NSMakeRange(i, 1)];
            NSEvent *event = [NSEvent keyEventWithType:NSEventTypeKeyDown location:NSZeroPoint modifierFlags:0 timestamp:0
                windowNumber:window.windowNumber context:nil characters:text charactersIgnoringModifiers:text isARepeat:NO keyCode:[text isEqualToString:@"\r"] ? 36 : 0];
            [view keyDown:event];
        }
        waitText(pointer, @"KEY_OK");
        checkAgentNewline(window, pointer, true, "", 2, @"1b 0d", 1);
        checkAgentNewline(window, pointer, false, "", 10, @"1b 5b 32 37 3b 32 3b 31 33 7e", 2);
        checkAgentNewline(window, pointer, true, "\\033[>1u", 7, @"1b 5b 31 33 3b 32 75", 3);
        checkAgentNewline(window, pointer, true, "\\033[>4;2m", 10, @"1b 5b 32 37 3b 32 3b 31 33 7e", 4);
        gav_view_set_agent_newline(pointer, false);
        require(gav_view_rendered(pointer), "resized terminal still renders");
        printf("PASS embedded libghostty shell, cwd, ANSI, AppKit input, agent newline/default/kitty/modifyOtherKeys bytes, Metal IOSurface presentation, resize and retained session\n");
        gav_view_destroy(pointer);
        [window orderOut:nil];
    }
    return 0;
}
