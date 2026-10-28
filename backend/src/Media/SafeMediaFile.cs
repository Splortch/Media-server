using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaServer.Media;

// The returned stream owns the validated handle: never reopen the pathname to stream bytes.
internal static class SafeMediaFile
{
    public static FileStream Open(string path)
    {
        if (OperatingSystem.IsLinux())
            return OpenUnix(path, false);
        if (OperatingSystem.IsWindows())
            return OpenWindows(path);
        if (
            OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64
        )
            return OpenUnix(path, true);
        // Fail closed until the platform has an audited handle-based implementation.
        throw new IOException("Secure media handles are unsupported on this platform.");
    }

    private static FileStream OpenUnix(string path, bool mac)
    {
        // Darwin constants differ from Linux. Both walk from an opened root and
        // forbid following links at every component; never reopen the pathname.
        // Apple ABI: xnu/bsd/sys/fcntl.h and stat.h (64-bit inode layout).
        var directory = mac ? 0x100000 : 0x10000;
        var noFollow = mac ? 0x100 : 0x20000;
        var closeOnExec = mac ? 0x1000000 : 0x80000;
        var nonBlock = mac ? 0x4 : 0x800;
        using var root = Handle(
            OpenAt(mac ? -2 : -100, "/", directory | noFollow | closeOnExec, 0)
        );
        SafeFileHandle parent = root;
        try
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = Handle(
                    OpenAt(
                        parent.DangerousGetHandle().ToInt32(),
                        parts[i],
                        directory | noFollow | closeOnExec,
                        0
                    )
                );
                if (parent != root)
                    parent.Dispose();
                parent = next;
            }
            var file = Handle(
                OpenAt(
                    parent.DangerousGetHandle().ToInt32(),
                    parts[^1],
                    noFollow | closeOnExec | nonBlock,
                    0
                )
            );
            try
            {
                // Query the opened inode, not the path. Nonblocking open prevents
                // a FIFO from hanging validation; devices/directories are rejected.
                if (!IsRegularFile(file, mac))
                    throw new IOException("Media must be a regular file.");
                return new FileStream(file, FileAccess.Read, 65536, false);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        finally
        {
            if (parent != root)
                parent.Dispose();
        }
    }

    private static bool IsRegularFile(SafeFileHandle file, bool mac)
    {
        var fd = file.DangerousGetHandle().ToInt32();
        if (!mac)
            return Statx(fd, "", 0x1000, 1, out var linux) == 0 && (linux.Mode & 0xf000) == 0x8000;
        // x86_64 exports the inode64 spelling; arm64 only has the modern ABI.
        DarwinStat stat;
        var result =
            RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? FStatDarwinInode64(fd, out stat)
                : FStatDarwin(fd, out stat);
        return result == 0 && (stat.Mode & 0xf000) == 0x8000;
    }

    private static FileStream OpenWindows(string path)
    {
        using var opened = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (opened.IsInvalid)
            throw Failure();
        if (
            !GetAttributes(opened, 9, out var attributes, 8)
            || (attributes.Value & (0x400 | 0x10)) != 0
            || GetFileType(opened) != 1
        )
            throw new IOException("Media must be a regular file without reparse points.");
        var buffer = new StringBuilder(32768);
        var length = GetFinalPath(opened, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw Failure();
        var actual = buffer.ToString();
        actual =
            actual.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + actual[8..]
            : actual.StartsWith(@"\\?\", StringComparison.Ordinal) ? actual[4..]
            : actual;
        if (!string.Equals(path, actual, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Media path changed while opening.");
        // Duplicate ownership via FileStream is not safe with a using local: transfer the handle.
        var owned = new SafeFileHandle(opened.DangerousGetHandle(), true);
        opened.SetHandleAsInvalid();
        try
        {
            return new FileStream(owned, FileAccess.Read, 65536, false);
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    private static SafeFileHandle Handle(int fd) =>
        fd < 0 ? throw Failure() : new SafeFileHandle((IntPtr)fd, true);

    private static IOException Failure() =>
        new("Cannot open media securely.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Stat
    {
        [FieldOffset(28)]
        public ushort Mode;
    }

    // struct stat with 64-bit inodes: dev_t is 32-bit and mode_t is 16-bit.
    // Both supported Darwin architectures use this 144-byte layout.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(4)]
        public ushort Mode;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatDarwinInode64(int fd, out DarwinStat stat);

    [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatDarwin(int fd, out DarwinStat stat);

    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes
    {
        public uint Value;
        public uint Tag;
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int parent, string name, int flags, int mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int fd, string path, int flags, uint mask, out Stat stat);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafeFileHandle CreateFile(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint creation,
        uint flags,
        IntPtr template
    );

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern uint GetFinalPath(
        SafeFileHandle file,
        StringBuilder path,
        uint size,
        uint flags
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(
        SafeFileHandle file,
        int infoClass,
        out Attributes attributes,
        uint size
    );

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle file);
}
