#import <AppKit/AppKit.h>
#include <ghostty/vt.h>
#include <string.h>

// VT callbacks borrow their requests and must reply before returning. AppKit's
// synchronous clipboard API also keeps permission prompts on the caller's UI thread.
static void writeClipboard(GhosttyTerminal terminal, void *userdata, const GhosttyClipboardWrite *request) {
    (void)terminal; (void)userdata;
    GhosttyClipboardWriteReply reply = { .size = sizeof(reply), .result = GHOSTTY_CLIPBOARD_WRITE_RESULT_UNSUPPORTED };
    NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
    if (request->contents_len == 0) {
        [pasteboard clearContents];
        reply.result = GHOSTTY_CLIPBOARD_WRITE_RESULT_SUCCESS;
    } else {
        for (size_t i = 0; i < request->contents_len; i++) {
            GhosttyClipboardContent content = request->contents[i];
            if (content.mime.len != 10 || memcmp(content.mime.ptr, "text/plain", 10)) continue;
            NSString *text = [[NSString alloc] initWithBytes:content.data.ptr length:content.data.len encoding:NSUTF8StringEncoding];
            if (!text) { reply.result = GHOSTTY_CLIPBOARD_WRITE_RESULT_INVALID_DATA; break; }
            [pasteboard clearContents];
            reply.result = [pasteboard setString:text forType:NSPasteboardTypeString]
                ? GHOSTTY_CLIPBOARD_WRITE_RESULT_SUCCESS : GHOSTTY_CLIPBOARD_WRITE_RESULT_IO_ERROR;
            break;
        }
    }
    request->reply(request, &reply);
}

static void readClipboard(GhosttyTerminal terminal, void *userdata, const GhosttyClipboardRead *request) {
    (void)terminal; (void)userdata;
    GhosttyClipboardReadReply reply = { .size = sizeof(reply), .result = GHOSTTY_CLIPBOARD_READ_RESULT_DENIED };
    bool wantsText = false;
    for (size_t i = 0; i < request->mimes_len; i++)
        wantsText |= request->mimes[i].len == 10 && !memcmp(request->mimes[i].ptr, "text/plain", 10);
    if (!wantsText) {
        reply.result = GHOSTTY_CLIPBOARD_READ_RESULT_UNSUPPORTED;
        request->reply(request, &reply);
        return;
    }
    NSAlert *alert = [NSAlert new];
    alert.messageText = @"Allow terminal clipboard access?";
    alert.informativeText = @"The terminal requested clipboard data that requires confirmation.";
    [alert addButtonWithTitle:@"Allow"];
    [alert addButtonWithTitle:@"Cancel"];
    if ([alert runModal] != NSAlertFirstButtonReturn) { request->reply(request, &reply); return; }
    NSData *text = [([NSPasteboard.generalPasteboard stringForType:NSPasteboardTypeString] ?: @"") dataUsingEncoding:NSUTF8StringEncoding];
    GhosttyClipboardContent content = { .mime = { .ptr = (const uint8_t *)"text/plain", .len = 10 },
        .data = { .ptr = text.bytes, .len = text.length } };
    reply.result = GHOSTTY_CLIPBOARD_READ_RESULT_SUCCESS;
    reply.contents = &content;
    reply.contents_len = 1;
    request->reply(request, &reply);
}

void gav_vt_install_clipboard(GhosttyTerminal terminal) {
    ghostty_terminal_set(terminal, GHOSTTY_TERMINAL_OPT_CLIPBOARD_WRITE, (const void *)writeClipboard);
    ghostty_terminal_set(terminal, GHOSTTY_TERMINAL_OPT_CLIPBOARD_READ, (const void *)readClipboard);
}
