// Test-process-only dyld interposer. Never linked into the application.
#include <CoreServices/CoreServices.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define CAPACITY 4096
struct stream { ConstFSEventStreamRef handle; unsigned refs; int running; };
static struct stream streams[CAPACITY];
static pthread_mutex_t gate = PTHREAD_MUTEX_INITIALIZER;
static int64_t created, released, live, running, peak, failed_starts, untracked;

static struct stream *find(ConstFSEventStreamRef handle)
{
    for (int i = 0; i < CAPACITY; i++) if (streams[i].handle == handle) return &streams[i];
    return NULL;
}

static void admit(FSEventStreamRef handle)
{
    if (!handle) return;
    pthread_mutex_lock(&gate);
    struct stream *slot = find(NULL);
    if (!slot) { fprintf(stderr, "FSEVENTS_PROBE capacity exceeded\n"); abort(); }
    *slot = (struct stream){handle, 1, 0};
    created++; live++; if (live > peak) peak = live;
    pthread_mutex_unlock(&gate);
}

static FSEventStreamRef create(CFAllocatorRef allocator, FSEventStreamCallback callback,
    FSEventStreamContext *context, CFArrayRef paths, FSEventStreamEventId since, CFTimeInterval latency,
    FSEventStreamCreateFlags flags)
{
    FSEventStreamRef result = FSEventStreamCreate(allocator, callback, context, paths, since, latency, flags);
    admit(result);
    return result;
}

static FSEventStreamRef create_device(CFAllocatorRef allocator, FSEventStreamCallback callback,
    FSEventStreamContext *context, dev_t device, CFArrayRef paths, FSEventStreamEventId since,
    CFTimeInterval latency, FSEventStreamCreateFlags flags)
{
    FSEventStreamRef result = FSEventStreamCreateRelativeToDevice(allocator, callback, context, device, paths, since, latency, flags);
    admit(result);
    return result;
}

static Boolean start(FSEventStreamRef handle)
{
    Boolean success = FSEventStreamStart(handle);
    pthread_mutex_lock(&gate);
    struct stream *slot = find(handle);
    if (!slot) untracked++;
    else if (success && !slot->running) { slot->running = 1; running++; }
    if (!success) failed_starts++;
    pthread_mutex_unlock(&gate);
    return success;
}

static void stopped(ConstFSEventStreamRef handle)
{
    struct stream *slot = find(handle);
    if (!slot) untracked++;
    else if (slot->running) { slot->running = 0; running--; }
}

static void stop(FSEventStreamRef handle)
{
    FSEventStreamStop(handle);
    pthread_mutex_lock(&gate); stopped(handle); pthread_mutex_unlock(&gate);
}

static void invalidate(FSEventStreamRef handle)
{
    FSEventStreamInvalidate(handle);
    pthread_mutex_lock(&gate); stopped(handle); pthread_mutex_unlock(&gate);
}

static void retain(FSEventStreamRef handle)
{
    FSEventStreamRetain(handle);
    pthread_mutex_lock(&gate);
    struct stream *slot = find(handle);
    if (slot) slot->refs++; else untracked++;
    pthread_mutex_unlock(&gate);
}

static void release(FSEventStreamRef handle)
{
    pthread_mutex_lock(&gate);
    struct stream *slot = find(handle);
    if (!slot) untracked++;
    else if (--slot->refs == 0)
    {
        if (slot->running) running--;
        *slot = (struct stream){0}; released++; live--;
    }
    pthread_mutex_unlock(&gate);
    FSEventStreamRelease(handle);
}

__attribute__((visibility("default"))) int SharpRailFSEventsSnapshot(int64_t *values, int count)
{
    if (count != 7) return 0;
    pthread_mutex_lock(&gate);
    int64_t snapshot[] = {created, released, live, running, peak, failed_starts, untracked};
    for (int i = 0; i < count; i++) values[i] = snapshot[i];
    pthread_mutex_unlock(&gate);
    return 1;
}

#define INTERPOSE(replacement, original) \
    __attribute__((used)) static struct { const void *replacement; const void *original; } \
    interpose_##original __attribute__((section("__DATA,__interpose"))) = {(const void *)&replacement, (const void *)&original}
INTERPOSE(create, FSEventStreamCreate);
INTERPOSE(create_device, FSEventStreamCreateRelativeToDevice);
INTERPOSE(start, FSEventStreamStart);
INTERPOSE(stop, FSEventStreamStop);
INTERPOSE(invalidate, FSEventStreamInvalidate);
INTERPOSE(retain, FSEventStreamRetain);
INTERPOSE(release, FSEventStreamRelease);
