/* Verify the SDK ABI consumed by SafeMediaFile's managed Darwin interop. */
#define _DARWIN_C_SOURCE 1
#define _DARWIN_USE_64_BIT_INODE 1
#include <sys/stat.h>
#include <fcntl.h>
#include <stddef.h>
#include <dlfcn.h>
#include <stdio.h>
_Static_assert(sizeof(struct stat) == 144, "Darwin stat size changed");
_Static_assert(offsetof(struct stat, st_mode) == 4, "Darwin mode offset changed");
_Static_assert(sizeof(((struct stat *)0)->st_mode) == 2, "Darwin mode width changed");
_Static_assert(O_DIRECTORY == 0x100000 && O_NOFOLLOW == 0x100, "Darwin open flags changed");
_Static_assert(O_CLOEXEC == 0x1000000 && O_NONBLOCK == 4 && AT_FDCWD == -2, "Darwin flags changed");
_Static_assert(S_IFREG == 0x8000 && S_IFMT == 0xf000, "Darwin file type mask changed");
int main(void) {
#if defined(__x86_64__)
    const char *symbol = "fstat$INODE64";
#elif defined(__arm64__)
    const char *symbol = "fstat";
#else
#error Unsupported macOS architecture
#endif
    if (!dlsym(RTLD_DEFAULT, symbol)) return 1;
    puts("Darwin media ABI and fstat symbol verified against the hosted SDK.");
    return 0;
}
