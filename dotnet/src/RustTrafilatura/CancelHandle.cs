using System.Runtime.InteropServices;
using System.Threading;

namespace RustTrafilatura;

/// <summary>
/// One native cancel token, the handle `rt_extract` reads and `rt_cancel` sets. A call creates one only
/// when its token can be cancelled, and releases it when the call returns.
/// </summary>
/// <remarks>
/// A `SafeHandle` rather than a raw pointer: a cancel callback that runs late meets a disposed object,
/// which refuses the call, and never a freed pointer. The caller disposes the token's registration
/// before this handle, so a callback that is running is waited for.
/// </remarks>
internal sealed class CancelHandle : SafeHandle
{
    private static long _liveCount;
    private static long _createdCount;

    /// <summary>
    /// The handle a call passes when its token cannot be cancelled. It is invalid, so the native side
    /// reads a null, and it is never disposed: a disposed instance would refuse every later call.
    /// </summary>
    internal static readonly CancelHandle None = new();

    /// <summary>Valid handles that are not released yet. Read by tests to show that no handle leaks.</summary>
    internal static long LiveCount => Interlocked.Read(ref _liveCount);

    /// <summary>Valid handles created since the process started. It only rises.</summary>
    internal static long CreatedCount => Interlocked.Read(ref _createdCount);

    /// <summary>Test seam, null in production: runs inside <see cref="Dispose(bool)"/>, after the handle is released.</summary>
    internal static Action? AfterRelease { get; set; }

    /// <summary>
    /// Public although the type is internal: the marshaller that builds a returned handle keeps only a
    /// public parameterless constructor through Native AOT trimming.
    /// </summary>
    public CancelHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Creates a native token and counts it, or returns <see cref="None"/> when the native side returns null.</summary>
    internal static CancelHandle Create()
    {
        var created = Interop.RtCancelNew();
        if (created.IsInvalid)
        {
            return None;
        }

        Interlocked.Increment(ref _createdCount);
        Interlocked.Increment(ref _liveCount);
        return created;
    }

    /// <summary>Releases the native token and lowers the live count; an invalid instance owns nothing to release.</summary>
    protected override bool ReleaseHandle()
    {
        if (IsInvalid)
        {
            return true;
        }

        Interop.RtCancelFree(handle);
        Interlocked.Decrement(ref _liveCount);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        AfterRelease?.Invoke();
    }
}
