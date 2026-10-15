#import <AppKit/AppKit.h>
#import <Metal/Metal.h>
#import <objc/runtime.h>
#include "GhosttyView.h"

@interface GAVTextureFrames : NSObject
@property(nonatomic, strong) NSCondition *condition;
@property(nonatomic, strong) NSCountedSet<id<MTLTexture>> *readers;
@property(nonatomic, strong) id<MTLTexture> latest;
@property(nonatomic, assign) gav_texture_callback callback;
@property(nonatomic, assign) BOOL stopped;
@property(nonatomic, assign) BOOL replacementFailed;
@end
@implementation GAVTextureFrames
@end

@interface GAVTextureLease : NSObject
@property(nonatomic, strong) GAVTextureFrames *frames;
@property(nonatomic, strong) id<MTLTexture> texture;
@end
@implementation GAVTextureLease
@end

static char framesKey;

@interface CALayer (GAVTextureOutput)
- (BOOL)gavPresentTexture:(id<MTLTexture>)texture;
- (void)gavWillRenderTexture:(id<MTLTexture>)texture;
- (void)gavTextureRenderFailed;
@end

@implementation CALayer (GAVTextureOutput)
- (BOOL)gavPresentTexture:(id<MTLTexture>)texture {
    GAVTextureFrames *frames = objc_getAssociatedObject(self, &framesKey);
    if (!frames) return NO;
    [frames.condition lock];
    if (!frames.stopped) {
        frames.latest = texture;
        frames.replacementFailed = NO;
        [frames.condition broadcast];
        if (frames.callback) frames.callback();
    }
    [frames.condition unlock];
    return YES;
}
- (void)gavWillRenderTexture:(id<MTLTexture>)texture {
    GAVTextureFrames *frames = objc_getAssociatedObject(self, &framesKey);
    if (!frames) return;
    [frames.condition lock];
    // The three-target swap chain may catch up to the last completed target while
    // later submissions are still on the GPU. Keep that frame available to Skia
    // until a replacement completes; clearing it would expose a blank draw on
    // pointer/wheel invalidations. Only Ghostty's encoding worker waits here.
    while (!frames.stopped && !frames.replacementFailed && frames.latest == texture) [frames.condition wait];
    if (frames.latest == texture) frames.latest = nil;
    // A reader returns its lease only after Skia's submitted GPU work completes.
    while ([frames.readers containsObject:texture]) [frames.condition wait];
    [frames.condition unlock];
}
- (void)gavTextureRenderFailed {
    GAVTextureFrames *frames = objc_getAssociatedObject(self, &framesKey);
    if (!frames) return;
    [frames.condition lock];
    // Failed submissions cannot supply the replacement a writer is waiting for.
    // Let that writer invalidate only its own target; a newer completed frame
    // must survive a late failure callback from an earlier submission.
    frames.replacementFailed = YES;
    [frames.condition broadcast];
    [frames.condition unlock];
}
@end

void *gav_texture_enable(void *pointer) {
    NSView *view = (__bridge NSView *)pointer;
    GAVTextureFrames *frames = [GAVTextureFrames new];
    frames.condition = [NSCondition new];
    frames.readers = [NSCountedSet new];
    objc_setAssociatedObject(view.layer, &framesKey, frames, OBJC_ASSOCIATION_RETAIN);
    return (__bridge_retained void *)frames;
}

void gav_texture_notify(void *pointer, gav_texture_callback callback) {
    GAVTextureFrames *frames = (__bridge GAVTextureFrames *)pointer;
    [frames.condition lock];
    if (!frames.stopped) frames.callback = callback;
    [frames.condition unlock];
}

void *gav_texture_acquire(void *pointer, int32_t *width, int32_t *height, void **lease) {
    GAVTextureFrames *frames = (__bridge GAVTextureFrames *)pointer;
    [frames.condition lock];
    id<MTLTexture> texture = frames.stopped ? nil : frames.latest;
    if (texture) {
        GAVTextureLease *reader = [GAVTextureLease new];
        reader.frames = frames;
        reader.texture = texture;
        [frames.readers addObject:texture];
        *width = (int32_t)texture.width;
        *height = (int32_t)texture.height;
        *lease = (__bridge_retained void *)reader;
    }
    [frames.condition unlock];
    return (__bridge void *)texture;
}

void gav_texture_return(void *pointer) {
    GAVTextureLease *lease = (__bridge_transfer GAVTextureLease *)pointer;
    GAVTextureFrames *frames = lease.frames;
    [frames.condition lock];
    [frames.readers removeObject:lease.texture];
    [frames.condition broadcast];
    [frames.condition unlock];
}

int32_t gav_texture_retained_count(void *pointer) {
    GAVTextureFrames *frames = (__bridge GAVTextureFrames *)pointer;
    [frames.condition lock];
    NSUInteger count = frames.readers.count;
    if (frames.latest && ![frames.readers containsObject:frames.latest]) count++;
    [frames.condition unlock];
    return (int32_t)count;
}

void gav_texture_stop(void *pointer) {
    GAVTextureFrames *frames = (__bridge GAVTextureFrames *)pointer;
    [frames.condition lock];
    frames.stopped = YES;
    frames.callback = NULL;
    frames.latest = nil;
    [frames.condition broadcast];
    [frames.condition unlock];
}

void gav_texture_release(void *handle) { if (handle) CFRelease(handle); }
