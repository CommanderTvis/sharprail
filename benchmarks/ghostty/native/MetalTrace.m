#import <Metal/Metal.h>
#import <Foundation/Foundation.h>
#import <IOSurface/IOSurface.h>
#import <objc/runtime.h>
#include <execinfo.h>
#include <mach/mach.h>

@interface Entry : NSObject
@property(nonatomic, weak) id object;
@property(nonatomic, strong) NSDictionary *info;
@end
@implementation Entry
@end
static NSMutableArray<Entry *> *entries;
static id<MTLDevice> device;
static NSLock *lock;
static NSMutableArray *events;
static id<MTLCommandQueue> firstQueue;
static uint64_t footprint(void);
static void delta(id object, NSString *kind, uint64_t before) { uint64_t after=footprint(); [lock lock]; [events addObject:@{@"kind":kind,@"before":@(before),@"after":@(after),@"device":[NSString stringWithFormat:@"%p",[object device]],@"address":[NSString stringWithFormat:@"%p",object]}]; [lock unlock]; }
static void record(id resource, NSString *kind) {
    if (!resource) return;
    void *frames[32]; int count=backtrace(frames,32); char **symbols=backtrace_symbols(frames,count);
    NSMutableArray *stack=[NSMutableArray new];
    for(int i=2;i<count;i++) [stack addObject:@(symbols[i])];
    free(symbols);
    Entry *entry=[Entry new]; entry.object=resource; entry.info=@{@"kind":kind,@"stack":stack};
    [lock lock]; [entries addObject:entry]; [lock unlock];
}
typedef id (*TextureFn)(id,SEL,id) __attribute__((ns_returns_retained));
typedef id (*SurfaceFn)(id,SEL,id,IOSurfaceRef,NSUInteger) __attribute__((ns_returns_retained));
typedef id (*BufferFn)(id,SEL,NSUInteger,MTLResourceOptions) __attribute__((ns_returns_retained));
typedef id (*BytesFn)(id,SEL,const void*,NSUInteger,MTLResourceOptions) __attribute__((ns_returns_retained));
static TextureFn textureOriginal, heapOriginal;
static SurfaceFn surfaceOriginal;
static BufferFn bufferOriginal;
static BytesFn bytesOriginal;
static id textureHook(id self,SEL sel,id desc) __attribute__((ns_returns_retained));
static id textureHook(id self,SEL sel,id desc) { uint64_t before=footprint(); id result=textureOriginal(self,sel,desc); delta(result,@"texture",before); record(result,@"texture"); return result; }
static id heapHook(id self,SEL sel,id desc) __attribute__((ns_returns_retained));
static id heapHook(id self,SEL sel,id desc) { uint64_t before=footprint(); id result=heapOriginal(self,sel,desc); delta(result,@"heap",before); record(result,@"heap"); return result; }
static id surfaceHook(id self,SEL sel,id desc,IOSurfaceRef surface,NSUInteger plane) __attribute__((ns_returns_retained));
static id surfaceHook(id self,SEL sel,id desc,IOSurfaceRef surface,NSUInteger plane) { uint64_t before=footprint(); id result=surfaceOriginal(self,sel,desc,surface,plane); delta(result,@"surface-texture",before); record(result,@"surface-texture"); return result; }
static id bufferHook(id self,SEL sel,NSUInteger length,MTLResourceOptions options) __attribute__((ns_returns_retained));
static id bufferHook(id self,SEL sel,NSUInteger length,MTLResourceOptions options) { uint64_t before=footprint(); id result=bufferOriginal(self,sel,length,options); delta(result,@"buffer",before); record(result,@"buffer"); return result; }
static id bytesHook(id self,SEL sel,const void *bytes,NSUInteger length,MTLResourceOptions options) __attribute__((ns_returns_retained));
static id bytesHook(id self,SEL sel,const void *bytes,NSUInteger length,MTLResourceOptions options) { uint64_t before=footprint(); id result=bytesOriginal(self,sel,bytes,length,options); delta(result,@"bytes-buffer",before); record(result,@"bytes-buffer"); return result; }
typedef id (*QueueFn)(id,SEL) __attribute__((ns_returns_retained));
static QueueFn queueOriginal;
static id (*blitOriginal)(id,SEL);
static id blitHook(id self,SEL sel) {
 if(getenv("TRACE_SKIP_ALL_BLIT")) return nil;
 if(getenv("TRACE_SKIP_EXPORT_BLIT") && [[[self commandQueue] label] isEqual:@"export-copy"]) return nil;
 return blitOriginal(self,sel);
}
static void (*submitOriginal)(id,SEL,id __unsafe_unretained*,NSUInteger);
static void submitHook(id self,SEL sel,id __unsafe_unretained *buffers,NSUInteger count) {
 uint64_t before=footprint(); submitOriginal(self,sel,buffers,count); uint64_t after=footprint();
 if(after>before+1024*1024) { [lock lock]; [events addObject:@{@"kind":@"submit",@"label":[self label] ?: @"",@"before":@(before),@"after":@(after),@"address":[NSString stringWithFormat:@"%p",self],@"count":@(count)}]; [lock unlock]; }
}

static uint64_t footprint(void) { task_vm_info_data_t info; mach_msg_type_number_t count=TASK_VM_INFO_COUNT; task_info(mach_task_self(),TASK_VM_INFO,(task_info_t)&info,&count);return info.phys_footprint; }
static id queueHook(id self,SEL sel) __attribute__((ns_returns_retained));
static id queueHook(id self,SEL sel) {
    uint64_t before=footprint();
    void *frames[24]; int count=backtrace(frames,24);char **symbols=backtrace_symbols(frames,count);NSMutableArray *stack=[NSMutableArray new];BOOL exporter=NO;
    for(int i=1;i<count;i++){NSString *s=@(symbols[i]);[stack addObject:s];if([s containsString:@"gav_texture_enable"])exporter=YES;}free(symbols);
    id result;
    if(exporter && getenv("TRACE_SHARE_QUEUE") && firstQueue) result=firstQueue;
    else if(exporter && getenv("TRACE_SMALL_QUEUE")) result=[self newCommandQueueWithMaxCommandBufferCount:2];
    else result=queueOriginal(self,sel);
    if(!exporter && [stack[0] containsString:@"AvaloniaNative"])firstQueue=result;
    [result setLabel:exporter ? @"export-copy" : ([stack[0] containsString:@"Ghostty"] ? @"Ghostty" : @"Avalonia")];
    [lock lock];[events addObject:@{@"kind":@"newCommandQueue",@"exporter":@(exporter),@"before":@(before),@"after":@(footprint()),@"address":[NSString stringWithFormat:@"%p",result],@"stack":stack}];[lock unlock];
    record(result,@"queue"); return result;
}
static IMP replace(Class cls, SEL selector, IMP replacement) {
    Method method=class_getInstanceMethod(cls,selector); IMP original=method_getImplementation(method);
    class_replaceMethod(cls,selector,replacement,method_getTypeEncoding(method)); return original;
}
void trace_start(void) {
    @autoreleasepool {
        entries=[NSMutableArray new]; events=[NSMutableArray new]; lock=[NSLock new]; device=MTLCreateSystemDefaultDevice(); Class cls=object_getClass(device);
        blitOriginal=(id(*)(id,SEL))replace(NSClassFromString(@"AGXG16XFamilyCommandBuffer"),@selector(blitCommandEncoder),(IMP)blitHook);
        Class queueClass=NSClassFromString(@"IOGPUMetalCommandQueue");
        submitOriginal=(void(*)(id,SEL,id __unsafe_unretained*,NSUInteger))replace(queueClass,NSSelectorFromString(@"_submitCommandBuffers:count:"),(IMP)submitHook);
        queueOriginal=(QueueFn)replace(cls,@selector(newCommandQueue),(IMP)queueHook);
        textureOriginal=(TextureFn)replace(cls,@selector(newTextureWithDescriptor:),(IMP)textureHook);
        surfaceOriginal=(SurfaceFn)replace(cls,@selector(newTextureWithDescriptor:iosurface:plane:),(IMP)surfaceHook);
        bufferOriginal=(BufferFn)replace(cls,@selector(newBufferWithLength:options:),(IMP)bufferHook);
        bytesOriginal=(BytesFn)replace(cls,@selector(newBufferWithBytes:length:options:),(IMP)bytesHook);
        heapOriginal=(TextureFn)replace(cls,@selector(newHeapWithDescriptor:),(IMP)heapHook);
    }
}
void trace_save(const char *path) {
    @autoreleasepool {
        NSMutableArray *live=[NSMutableArray new]; NSMutableSet *seen=[NSMutableSet new];
        [lock lock];
        for(Entry *entry in entries) {
            id object=entry.object; if(!object)continue;
            NSString *address=[NSString stringWithFormat:@"%p",object]; if([seen containsObject:address])continue; [seen addObject:address];
            NSMutableDictionary *row=[entry.info mutableCopy]; row[@"address"]=address; row[@"class"]=NSStringFromClass([object class]);
            row[@"label"]=[object label] ?: @""; row[@"device"]=[NSString stringWithFormat:@"%p",[object device]];
            if([row[@"kind"] isEqual:@"queue"]) { row[@"size"]=@0; }
            else if([row[@"kind"] isEqual:@"heap"]) { row[@"size"]=@([object size]); row[@"usedSize"]=@([object usedSize]); }
            else {
                id<MTLResource> resource=object; row[@"size"]=@(resource.allocatedSize); row[@"storageMode"]=@(resource.storageMode);
                if([object respondsToSelector:@selector(width)]) {
                    id<MTLTexture> tex=object;
                    row[@"width"]=@(tex.width); row[@"height"]=@(tex.height); row[@"samples"]=@(tex.sampleCount); row[@"format"]=@(tex.pixelFormat);
                    if(tex.iosurface) { row[@"surfaceID"]=@(IOSurfaceGetID(tex.iosurface)); row[@"surfaceBytes"]=@(IOSurfaceGetAllocSize(tex.iosurface)); }
                } else if([object respondsToSelector:@selector(length)]) row[@"length"]=@([object length]);
            }
            [live addObject:row];
        }
        [lock unlock];
        NSDictionary *report=@{@"device":device.name,@"allocatedSize":@(device.currentAllocatedSize),@"createdCount":@(entries.count),@"live":live,@"events":events};
        NSData *data=[NSJSONSerialization dataWithJSONObject:report options:NSJSONWritingPrettyPrinted error:nil]; [data writeToFile:@(path) atomically:YES];
    }
}
