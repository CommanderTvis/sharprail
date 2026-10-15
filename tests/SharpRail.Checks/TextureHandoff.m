#import <AppKit/AppKit.h>
#import <Metal/Metal.h>
#include <dlfcn.h>

@interface CALayer (GAVHandoffCheck)
- (BOOL)gavPresentTexture:(id<MTLTexture>)texture;
- (void)gavWillRenderTexture:(id<MTLTexture>)texture;
- (void)gavTextureRenderFailed;
@end

@interface GAVHandoffView : NSObject
@property(nonatomic, strong) CALayer *layer;
@end
@implementation GAVHandoffView
@end

// Called off the dispatcher after libGhostty has loaded. Force the producer /
// compositor overlap without relying on a GPU being slow enough by chance.
const char *sr_texture_handoff(void *library) {
    void *(*enable)(void *) = dlsym(library, "gav_texture_enable");
    void *(*acquire)(void *, int32_t *, int32_t *, void **) = dlsym(library, "gav_texture_acquire");
    void (*returnLease)(void *) = dlsym(library, "gav_texture_return");
    void (*stop)(void *) = dlsym(library, "gav_texture_stop");
    void (*release)(void *) = dlsym(library, "gav_texture_release");
    if (!enable || !acquire || !returnLease || !stop || !release) return "Texture bridge exports unavailable";
    id<MTLDevice> device = MTLCreateSystemDefaultDevice();
    MTLTextureDescriptor *descriptor = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm width:8 height:8 mipmapped:NO];
    id<MTLTexture> first = [device newTextureWithDescriptor:descriptor];
    id<MTLTexture> second = [device newTextureWithDescriptor:descriptor];
    if (!first || !second) return "Metal handoff textures unavailable";
    for (int scenario = 0; scenario < 3; scenario++) {
        GAVHandoffView *view = [GAVHandoffView new];
        view.layer = [CALayer layer];
        void *frames = enable((__bridge void *)view);
        [view.layer gavPresentTexture:first];
        dispatch_semaphore_t started = dispatch_semaphore_create(0);
        dispatch_semaphore_t finished = dispatch_semaphore_create(0);
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
            dispatch_semaphore_signal(started);
            [view.layer gavWillRenderTexture:first];
            dispatch_semaphore_signal(finished);
        });
        dispatch_semaphore_wait(started, DISPATCH_TIME_FOREVER);
        BOOL reusedEarly = dispatch_semaphore_wait(finished, dispatch_time(DISPATCH_TIME_NOW, 100 * NSEC_PER_MSEC)) == 0;
        int32_t width = 0, height = 0;
        void *lease = NULL;
        void *texture = acquire(frames, &width, &height, &lease);
        const char *failure = texture != (__bridge void *)first || width != 8 || height != 8
            ? "Compositor lost the completed frame while its target was being reused" : NULL;
        if (!failure && reusedEarly) failure = "Producer reused the latest completed texture before replacement";
        if (scenario == 0) {
            [view.layer gavPresentTexture:second];
            if (!reusedEarly && dispatch_semaphore_wait(finished, dispatch_time(DISPATCH_TIME_NOW, 50 * NSEC_PER_MSEC)) == 0) {
                reusedEarly = YES;
                failure = "Producer overwrote a texture with an outstanding compositor lease";
            }
        } else if (scenario == 1) [view.layer gavTextureRenderFailed];
        else stop(frames);
        if (lease) returnLease(lease);
        if (!reusedEarly && dispatch_semaphore_wait(finished, dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC)) != 0)
            failure = "Texture producer did not wake after replacement, failure or stop";
        if (scenario != 2 && !failure) {
            if (scenario == 1) {
                [view.layer gavPresentTexture:second];
                [view.layer gavTextureRenderFailed];
            }
            lease = NULL;
            texture = acquire(frames, &width, &height, &lease);
            if (texture != (__bridge void *)second) failure = "Compositor did not acquire the replacement frame";
            if (lease) returnLease(lease);
        }
        stop(frames);
        release(frames);
        if (failure) return failure;
    }
    return NULL;
}
