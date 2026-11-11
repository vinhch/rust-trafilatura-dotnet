using System.Runtime.InteropServices;

namespace RustTrafilatura.Tests;

/// <summary>
/// Reads the calling thread's stack size from the OS. .NET does not expose it, and the stack a thread
/// gets is what the native extraction has to fit in, so the stack tests need the real number.
/// </summary>
internal static unsafe partial class ThreadStack
{
    /// <summary>Whether <see cref="CurrentSizeBytes"/> reads this OS. Anywhere else the stack tests skip.</summary>
    internal static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>The calling thread's stack size in bytes.</summary>
    /// <exception cref="NotSupportedException">The OS is not one <see cref="IsSupported"/> reads.</exception>
    /// <exception cref="InvalidOperationException">The OS call that reads the stack failed.</exception>
    internal static long CurrentSizeBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            GetCurrentThreadStackLimits(out nuint low, out nuint high);
            return (long)(high - low);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxStackSize();
        }

        if (OperatingSystem.IsMacOS())
        {
            return (long)pthread_get_stacksize_np(pthread_self());
        }

        throw new NotSupportedException("the thread stack size is read on Windows, Linux and macOS only");
    }

    [LibraryImport("kernel32.dll")]
    private static partial void GetCurrentThreadStackLimits(out nuint low, out nuint high);

    [LibraryImport("libc")]
    private static partial nuint pthread_self();

    [LibraryImport("libc")]
    private static partial nuint pthread_get_stacksize_np(nuint thread);

    [LibraryImport("libc")]
    private static partial int pthread_getattr_np(nuint thread, byte* attr);

    [LibraryImport("libc")]
    private static partial int pthread_attr_getstack(byte* attr, nint* stackAddress, nuint* stackSize);

    [LibraryImport("libc")]
    private static partial int pthread_attr_destroy(byte* attr);

    /// <summary>glibc reports a thread's stack through a <c>pthread_attr_t</c>; the 128-byte buffer is at least its size on aarch64.</summary>
    private static long LinuxStackSize()
    {
        byte* attr = stackalloc byte[128];
        int code = pthread_getattr_np(pthread_self(), attr);
        if (code != 0)
        {
            throw new InvalidOperationException($"pthread_getattr_np failed with {code}");
        }

        try
        {
            nint stackAddress = 0;
            nuint stackSize = 0;
            code = pthread_attr_getstack(attr, &stackAddress, &stackSize);
            if (code != 0)
            {
                throw new InvalidOperationException($"pthread_attr_getstack failed with {code}");
            }

            return (long)stackSize;
        }
        finally
        {
            pthread_attr_destroy(attr);
        }
    }
}
