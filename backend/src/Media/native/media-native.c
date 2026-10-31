/* Fixed-signature bridge: Apple's arm64 openat mode is a variadic stack argument.
 * Keep managed code out of the platform-specific variadic calling convention. */
#include <fcntl.h>
#include <sys/stat.h>
__attribute__((visibility("default")))
int MediaServerOpenAt(int parent, const char *name, int flags, int mode) {
    return openat(parent, name, flags, (mode_t)mode);
}
