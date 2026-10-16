#include <CoreGraphics/CoreGraphics.h>
#include <libproc.h>
#include <sys/resource.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <signal.h>

static unsigned long long stamp(void) {
    return clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
}

static int window(pid_t pid) {
    CFArrayRef list = CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly, kCGNullWindowID);
    if (!list) return 0;
    int found = 0;
    for (CFIndex i = 0; i < CFArrayGetCount(list); i++) {
        CFDictionaryRef row = CFArrayGetValueAtIndex(list, i);
        CFNumberRef owner = CFDictionaryGetValue(row, kCGWindowOwnerPID);
        CFNumberRef layer = CFDictionaryGetValue(row, kCGWindowLayer);
        CFDictionaryRef bounds = CFDictionaryGetValue(row, kCGWindowBounds);
        int p = 0, l = -1;
        CGRect r = CGRectZero;
        if (!owner || !layer || !bounds) continue;
        CFNumberGetValue(owner, kCFNumberIntType, &p);
        CFNumberGetValue(layer, kCFNumberIntType, &l);
        if (CGRectMakeWithDictionaryRepresentation(bounds, &r) && p == pid && l == 0 && r.size.width >= 300 && r.size.height >= 200) { found = 1; break; }
    }
    CFRelease(list);
    return found;
}

int main(int argc, char **argv) {
    if (argc < 3) return 2;
    if (!strcmp(argv[1], "group")) {
        if (setpgid(0, 0) != 0) return 3;
        execvp(argv[2], argv + 2);
        return 127;
    }
    if (!strcmp(argv[1], "window")) {
        pid_t pid = atoi(argv[2]);
        unsigned long long deadline = stamp() + 60000000000ULL;
        while (stamp() < deadline && kill(pid, 0) == 0) {
            if (window(pid)) { printf("%llu\n", stamp()); return 0; }
            usleep(10000);
        }
        return 1;
    }
    if (strcmp(argv[1], "memory")) return 2;
    unsigned long long started = stamp();
    int capacity = proc_listallpids(NULL, 0) + 16384;
    pid_t *pids = calloc(capacity, sizeof(pid_t));
    int count = proc_listallpids(pids, capacity * sizeof(pid_t));
    if (count < 0 || count >= capacity) { free(pids); return 1; }
    struct proc_bsdinfo *info = calloc(capacity, sizeof(*info));
    int *included = calloc(capacity, sizeof(int));
    int *unclassified = calloc(capacity, sizeof(int));
    pid_t *queue = calloc(capacity, sizeof(pid_t));
    int queued = 0, enumeration_error = 0;
    for (int j = 2; j < argc; j++) {
        pid_t pid = atoi(argv[j]);
        int duplicate = 0;
        for (int k = 0; k < queued; k++) if (queue[k] == pid) duplicate = 1;
        if (!duplicate) queue[queued++] = pid;
    }
    for (int cursor = 0; cursor < queued; cursor++) {
        pid_t *children = calloc(capacity, sizeof(pid_t));
        errno = 0;
        int found = proc_listchildpids(queue[cursor], children, capacity * sizeof(pid_t));
        if (found < 0 || found >= capacity || (found == 0 && errno != 0)) enumeration_error = errno ? errno : EOVERFLOW;
        else for (int j = 0; j < found; j++) {
            int duplicate = 0;
            for (int k = 0; k < queued; k++) if (queue[k] == children[j]) duplicate = 1;
            if (!duplicate) {
                if (queued >= capacity) { enumeration_error = EOVERFLOW; break; }
                queue[queued++] = children[j];
            }
        }
        free(children);
    }
    for (int j = 0; j < queued; j++) {
        int index = -1;
        for (int i = 0; i < count; i++) if (pids[i] == queue[j]) { index = i; break; }
        if (index < 0) {
            if (count >= capacity) { enumeration_error = EOVERFLOW; continue; }
            index = count++;
            pids[index] = queue[j];
        }
        included[index] = 1;
    }
    for (int i = 0; i < count; i++)
        if (proc_pidinfo(pids[i], PROC_PIDTBSDINFO, 0, &info[i], sizeof(info[i])) != sizeof(info[i])) unclassified[i] = 1;
    free(queue);
    printf("{\"timestamp\":%llu,\"frequency\":1000000000,\"processes\":[", started);
    int first = 1;
    for (int i = 0; i < count; i++) if (included[i]) {
        struct rusage_info_v4 usage;
        memset(&usage, 0, sizeof(usage));
        struct proc_bsdinfo checked;
        memset(&checked, 0, sizeof(checked));
        int valid = proc_pidinfo(pids[i], PROC_PIDTBSDINFO, 0, &checked, sizeof(checked)) == sizeof(checked)
            && checked.pbi_start_tvsec == info[i].pbi_start_tvsec && checked.pbi_start_tvusec == info[i].pbi_start_tvusec;
        int result = proc_pid_rusage(pids[i], RUSAGE_INFO_V4, (rusage_info_t *)&usage);
        int error = result ? errno : 0;
        struct proc_bsdinfo after;
        memset(&after, 0, sizeof(after));
        if (!valid || proc_pidinfo(pids[i], PROC_PIDTBSDINFO, 0, &after, sizeof(after)) != sizeof(after)
            || after.pbi_start_tvsec != checked.pbi_start_tvsec || after.pbi_start_tvusec != checked.pbi_start_tvusec) error = ESRCH;
        printf("%s{\"pid\":%d,\"parentPid\":%u,\"physicalFootprintBytes\":%llu,\"residentBytes\":%llu,\"error\":%d}", first ? "" : ",", pids[i], info[i].pbi_ppid, usage.ri_phys_footprint, usage.ri_resident_size, error);
        first = 0;
    }
    printf("],\"unclassifiedPids\":[");
    first = 1;
    for (int i = 0; i < count; i++) if (unclassified[i]) { printf("%s%d", first ? "" : ",", pids[i]); first = 0; }
    printf("],\"startedTimestamp\":%llu,\"completedTimestamp\":%llu,\"enumerationError\":%d}\n", started, stamp(), enumeration_error);
    free(unclassified);
    free(included); free(info); free(pids);
    return 0;
}
