#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <termios.h>
#include <sys/ioctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <poll.h>
#include <mach/mach_time.h>

static mach_timebase_info_data_t timebase;
static double now(void) { return (double)mach_absolute_time() * timebase.numer / timebase.denom / 1e9; }
static void output(const char *data, size_t size) {
    while (size) { ssize_t count = write(1, data, size); if (count <= 0) exit(3); data += count; size -= count; }
}
static void text(const char *s) { output(s, strlen(s)); }
static void report(const char *root, const char *name, double elapsed, size_t bytes, int frames) {
    char temp[2048], path[2048];
    snprintf(path, sizeof path, "%s/%s.json", root, name);
    snprintf(temp, sizeof temp, "%s.tmp", path);
    struct winsize size; ioctl(1, TIOCGWINSZ, &size);
    FILE *file = fopen(temp, "w"); if (!file) exit(4);
    fprintf(file, "{\"seconds\":%.9f,\"bytes\":%zu,\"frames\":%d,\"pid\":%d,\"columns\":%u,\"rows\":%u}\n",
        elapsed, bytes, frames, getpid(), size.ws_col, size.ws_row);
    fclose(file); if (rename(temp, path)) exit(5);
}
static void drain(void) {
    text("\033[6n");
    char c; int state = 0;
    for (;;) {
        struct pollfd fd = { .fd=0, .events=POLLIN };
        if (poll(&fd, 1, 30000) <= 0 || read(0, &c, 1) != 1) exit(6);
        if (state == 0) { if (c == 27) state = 1; }
        else if (state == 1) state = c == '[' ? 2 : 0;
        else if (c == 'R') break;
        else if ((c < '0' || c > '9') && c != ';') state = 0;
    }
}
int main(int argc, char **argv) {
    if (argc != 2) return 2;
    mach_timebase_info(&timebase);
    struct termios mode; tcgetattr(0, &mode); cfmakeraw(&mode); tcsetattr(0, TCSANOW, &mode);
    char fifo[2048]; snprintf(fifo, sizeof fifo, "%s/commands", argv[1]);
    if (mkfifo(fifo, 0600)) return 7;
    int commands = open(fifo, O_RDWR); if (commands < 0) return 8;
    text("\033[?25l\033[0m\033[2J\033[HBENCH_READY");
    report(argv[1], "ready", 0, 0, 0);
    char command; int burst = 0;
    while (read(commands, &command, 1) == 1) {
        if (command == 'q') return 0;
        if (command == 'r' || command == 'b') {
            double written = now();
            text(command == 'r' ? "\033[48;2;255;0;0m\033[2J\033[H " : "\033[48;2;0;0;255m\033[2J\033[H ");
            report(argv[1], "tone", written, 0, command == 'r' ? 1 : 2); continue;
        }
        if (command == '0') { text("\033[0m\033[2J\033[H"); continue; }
        if (command == 'p') {
            struct winsize size; ioctl(1, TIOCGWINSZ, &size);
            double start = now(); size_t total = 0;
            for (int frame = 0; frame < 480; frame++) {
                char buffer[32768]; size_t n = 0;
                for (int row = 0; row < size.ws_row - 1; row++)
                    n += snprintf(buffer + n, sizeof buffer - n,
                        "\033[%d;1H\033[0;38;5;%dm%04d %02d  render=Ghostty  status=running  漢字 λ é 0123456789 abcdefghijklmnopqrstuvwxyz\033[K",
                        row + 1, 16 + (frame + row) % 216, frame, row);
                output(buffer, n); total += n;
                double target = start + (frame + 1) / 60.0;
                mach_wait_until((uint64_t)(target * 1e9 * timebase.denom / timebase.numer));
            }
            drain(); report(argv[1], "paced", now() - start, total, 480);
        }
        if (command == 't') {
            char chunk[65536]; size_t n = 0;
            for (int row = 0; row < 400; row++)
                n += snprintf(chunk + n, sizeof chunk - n,
                    "\033[38;5;%dm%06d ANSI scrolling workload abcdefghijklmnopqrstuvwxyz 0123456789 漢字 λ é\033[0m\r\n",
                    16 + row % 216, row);
            size_t total = ((64 * 1024 * 1024 + n - 1) / n) * n, left = total;
            double start = now(); text("\033[0m\033[2J\033[H");
            while (left) { size_t count = left < n ? left : n; output(chunk, count); left -= count; }
            drain(); char name[32]; snprintf(name, sizeof name, "burst-%d", ++burst);
            report(argv[1], name, now() - start, total, 0);
        }
    }
    return 0;
}
