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
            return OpenLinux(path);
        if (OperatingSystem.IsWindows())
            return OpenWindows(path);
        // Fail closed until the platform has an audited handle-based implementation.
        throw new IOException("Secure media handles are unsupported on this platform.");
    }

    private static FileStream OpenLinux(string path)
    {
        const int directory = 0x10000,
            noFollow = 0x20000,
            closeOnExec = 0x80000,
            nonBlock = 0x800;
        using var root = Handle(OpenAt(-100, "/", directory | noFollow | closeOnExec, 0));
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
                // AT_EMPTY_PATH queries the opened inode; FIFOs/devices/directories are not media.
                if (
                    Statx(file.DangerousGetHandle().ToInt32(), "", 0x1000, 1, out var stat) != 0
                    || (stat.Mode & 0xf000) != 0x8000
                )
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
