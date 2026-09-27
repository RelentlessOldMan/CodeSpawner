using System.Runtime.InteropServices;

namespace CodeSpawner.Generation;

/// <summary>
/// Best-effort detection of whether the volume backing a path is solid-state (no seek penalty) or a
/// spinning disk. This drives the default header write concurrency: parallel multi-GB sequential writes
/// are a big win on SSD/NVMe but thrash an HDD's heads (measured 5x slower at full fan-out). Windows-only;
/// any failure (network share, permissions, odd device) returns null and the caller picks a safe middle.
/// </summary>
public static class DiskProbe
{
    /// <summary>true = solid-state, false = spinning/seek-penalty, null = unknown.</summary>
    public static bool? IsSolidState(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return null; // UNC / network share — not a local physical volume
            char drive = char.ToUpperInvariant(root[0]);
            if (drive < 'A' || drive > 'Z') return null;

            using var h = CreateFileW($@"\\.\{drive}:", 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
            };
            var desc = new DEVICE_SEEK_PENALTY_DESCRIPTOR();
            bool ok = DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY,
                ref query, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                ref desc, Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(),
                out _, IntPtr.Zero);
            if (!ok) return null;
            return !desc.IncursSeekPenalty;
        }
        catch { return null; }
    }

    private const int IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters; // one-byte flexible array head; unused for this query
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, int dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer, int nInBufferSize,
        ref DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);
}
