#include <errno.h>
#include <stdio.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc < 2) return 126;
    if (setsid() == -1) {
        perror("Codex process group");
        return 126;
    }
    execv(argv[1], argv + 1);
    int error = errno;
    perror("Codex executable");
    return error == ENOENT ? 127 : 126;
}
