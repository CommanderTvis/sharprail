#import <AppKit/AppKit.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#include <mach/mach_time.h>
#include <mach/mach.h>
#include <pthread.h>
#include <stdatomic.h>

static mach_timebase_info_data_t timebase;
static double seconds(uint64_t ticks) { return (double)ticks * timebase.numer / timebase.denom / 1e9; }
double bench_now(void) { if (!timebase.denom) mach_timebase_info(&timebase); return seconds(mach_absolute_time()); }
double bench_footprint(void) {
    task_vm_info_data_t info; mach_msg_type_number_t count = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t)&info, &count) != KERN_SUCCESS) return -1;
    return info.phys_footprint / 1048576.0;
}
static NSWindow *window;
static SCStream *stream;
static atomic_int state;
static atomic_int frames;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static int target;
static double requested, observed, received;

@interface Capture : NSObject<SCStreamOutput, SCStreamDelegate>
@end
@implementation Capture
- (void)stream:(SCStream *)sender didStopWithError:(NSError *)error {
    fprintf(stderr, "CAPTURE_ERROR %s\n", error.localizedDescription.UTF8String); atomic_store(&state, -1);
}
- (void)stream:(SCStream *)sender didOutputSampleBuffer:(CMSampleBufferRef)sample ofType:(SCStreamOutputType)type {
    if (type != SCStreamOutputTypeScreen || !CMSampleBufferIsValid(sample)) return;
    NSArray *list = (__bridge NSArray *)CMSampleBufferGetSampleAttachmentsArray(sample, false);
    NSDictionary *info = list.firstObject;
    if ([info[SCStreamFrameInfoStatus] integerValue] != SCFrameStatusComplete) return;
    CVPixelBufferRef pixels = CMSampleBufferGetImageBuffer(sample);
    if (!pixels) return;
    double display = seconds([info[SCStreamFrameInfoDisplayTime] unsignedLongLongValue]);
    CVPixelBufferLockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
    const uint8_t *pixel = (const uint8_t *)CVPixelBufferGetBaseAddress(pixels)
        + (CVPixelBufferGetHeight(pixels) * 3 / 4) * CVPixelBufferGetBytesPerRow(pixels)
        + (CVPixelBufferGetWidth(pixels) * 3 / 4) * 4;
    int color = pixel[2] > 200 && pixel[1] < 70 && pixel[0] < 70 ? 1
        : pixel[0] > 200 && pixel[1] < 70 && pixel[2] < 70 ? 2 : 0;
    CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
    atomic_fetch_add(&frames, 1);
    pthread_mutex_lock(&lock);
    if (target && color == target && observed == 0 && display >= requested) {
        observed = display; received = bench_now();
    }
    pthread_mutex_unlock(&lock);
}
@end
static Capture *capture;

int bench_window(const char *title) {
    for (NSWindow *candidate in NSApp.windows)
        if ([candidate.title isEqualToString:[NSString stringWithUTF8String:title]]) { window = candidate; break; }
    if (!window) return 0;
    window.level = NSFloatingWindowLevel;
    [NSApp activateIgnoringOtherApps:YES]; [window makeKeyAndOrderFront:nil];
    return (int)window.windowNumber;
}
int bench_active(void) { return NSApp.active && window.keyWindow; }
int bench_hz(void) { return (int)window.screen.maximumFramesPerSecond; }
void bench_click(void) {
    NSPoint p = NSMakePoint(40, window.contentView.bounds.size.height - 40);
    for (NSNumber *type in @[@(NSEventTypeLeftMouseDown), @(NSEventTypeLeftMouseUp)])
        [NSApp postEvent:[NSEvent mouseEventWithType:type.integerValue location:p modifierFlags:0
            timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:window.windowNumber context:nil
            eventNumber:0 clickCount:1 pressure:1] atStart:NO];
}
void bench_key(const char *text) {
    if (getenv("BENCH_TRACE")) fprintf(stderr, "RESPONDER %s\n", NSStringFromClass(window.firstResponder.class).UTF8String);
    NSString *characters = [NSString stringWithUTF8String:text];
    unsigned short code = text[0] == 'p' ? 35 : text[0] == 't' ? 17 : text[0] == 'r' ? 15
        : text[0] == 'b' ? 11 : text[0] == '0' ? 29 : text[0] == 'q' ? 12 : 0;
    for (NSNumber *type in @[@(NSEventTypeKeyDown), @(NSEventTypeKeyUp)])
        [NSApp sendEvent:[NSEvent keyEventWithType:type.integerValue location:NSZeroPoint modifierFlags:0
            timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:window.windowNumber context:nil
            characters:characters charactersIgnoringModifiers:characters isARepeat:NO keyCode:code]];
}
void bench_capture_start(void) {
    bench_now(); atomic_store(&state, 1);
    [SCShareableContent getShareableContentExcludingDesktopWindows:YES onScreenWindowsOnly:YES completionHandler:^(SCShareableContent *content, NSError *error) {
        SCWindow *own = nil;
        for (SCWindow *candidate in content.windows) if (candidate.windowID == window.windowNumber) { own = candidate; break; }
        if (!own || error) { fprintf(stderr, "CAPTURE_START_ERROR %s\n", error.localizedDescription.UTF8String ?: "own window missing"); atomic_store(&state, -1); return; }
        SCContentFilter *filter = [[SCContentFilter alloc] initWithDesktopIndependentWindow:own];
        SCStreamConfiguration *config = [SCStreamConfiguration new];
        config.width = 960; config.height = 540;
        config.minimumFrameInterval = CMTimeMake(1, 120);
        config.queueDepth = 3; config.showsCursor = NO;
        config.pixelFormat = kCVPixelFormatType_32BGRA;
        config.capturesAudio = NO; config.ignoreShadowsSingleWindow = YES;
        capture = [Capture new]; stream = [[SCStream alloc] initWithFilter:filter configuration:config delegate:capture];
        NSError *failure = nil;
        if (![stream addStreamOutput:capture type:SCStreamOutputTypeScreen sampleHandlerQueue:dispatch_queue_create("own-window-pixels", DISPATCH_QUEUE_SERIAL) error:&failure]) {
            fprintf(stderr, "CAPTURE_OUTPUT_ERROR %s\n", failure.localizedDescription.UTF8String); atomic_store(&state, -1); return;
        }
        [stream startCaptureWithCompletionHandler:^(NSError *failure) {
            if (failure) fprintf(stderr, "CAPTURE_START_ERROR %s\n", failure.localizedDescription.UTF8String);
            atomic_store(&state, failure ? -1 : 2);
        }];
    }];
}
int bench_capture_state(void) { return atomic_load(&state); }
int bench_capture_frames(void) { return atomic_load(&frames); }
void bench_capture_stop(void) {
    atomic_store(&state, 3);
    [stream stopCaptureWithCompletionHandler:^(NSError *error) { atomic_store(&state, error ? -1 : 4); }];
}
double bench_target(int color) {
    pthread_mutex_lock(&lock);
    target = color; observed = 0; received = 0; requested = bench_now();
    double result = requested; pthread_mutex_unlock(&lock); return result;
}
int bench_result(double *display, double *arrival) {
    pthread_mutex_lock(&lock);
    *display = observed; *arrival = received; int result = observed != 0;
    pthread_mutex_unlock(&lock); return result;
}
