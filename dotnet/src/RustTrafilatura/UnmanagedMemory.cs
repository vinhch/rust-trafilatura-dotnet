using System.Buffers;
using System.Runtime.InteropServices;

namespace RustTrafilatura;

/// <summary>
/// Views a caller-owned unmanaged buffer as a <see cref="Memory{T}"/>, so bytes that live behind a
/// raw pointer can reach the entry points that take memory without being copied first.
/// </summary>
public static class UnmanagedMemory
{
    /// <summary>
    /// Wraps <paramref name="pointer"/> without copying it and without taking ownership of it. The
    /// returned <see cref="Memory{T}"/> is valid exactly as long as the buffer is: the caller keeps it
    /// alive and unmanaged, and nothing here frees it, not even when the memory is disposed. A
    /// pointer whose length is zero produces an empty view, which is the same input an empty array is.
    /// </summary>
    /// <param name="pointer">The first byte of the buffer, which the caller allocated and still owns.</param>
    /// <param name="length">How many bytes starting at <paramref name="pointer"/> may be read.</param>
    /// <remarks>
    /// The caller's allocator is unknowable: a buffer may come from <c>malloc</c>, from
    /// <c>NativeMemory.Alloc</c>, from a native library's own pool or from a slice of a larger
    /// allocation, and releasing it correctly differs in every one of those cases. So the wrapper
    /// holds no reference that could be freed and no callback that could be invoked, and the caller
    /// is the only one who can release the buffer.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pointer"/> is null and <paramref name="length"/> is not zero.</exception>
    public static unsafe Memory<byte> Wrap(byte* pointer, int length)
    {
        // The length is checked first because it is the one argument whose error a null pointer cannot
        // mask: a negative length is invalid for every pointer, while a null pointer is invalid only
        // for a length that says memory was allocated.
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (pointer is null && length != 0)
        {
            throw new ArgumentNullException(
                nameof(pointer),
                "A null pointer with a non-zero length reads memory that was never allocated.");
        }

        return length == 0 ? Memory<byte>.Empty : new UnmanagedMemoryManager(pointer, length).Memory;
    }

    /// <summary>
    /// The <see cref="MemoryManager{T}"/> behind <see cref="Wrap"/>: it reports the caller's own
    /// address, so pinning it neither copies nor moves the buffer. Disposal releases nothing, which is
    /// what makes "releasing the view releases nothing" true: this type holds no resource, so a
    /// disposed view still reads the caller's bytes.
    /// </summary>
    private sealed unsafe class UnmanagedMemoryManager(byte* pointer, int length) : MemoryManager<byte>
    {
        /// <summary>The caller's first byte, which the caller allocated and keeps alive.</summary>
        private readonly byte* _pointer = pointer;

        /// <summary>How many bytes of it may be read, which is also this manager's end offset.</summary>
        private readonly int _length = length;

        /// <summary>The whole view, which is what <see cref="Wrap"/> hands the caller.</summary>
        public override Span<byte> GetSpan() => new(_pointer, _length);

        /// <summary>
        /// The caller's own address, which a fixed buffer does not need pinned or moved. The index is
        /// checked because this method's contract reaches farther than <see cref="Memory{T}"/> does:
        /// <see cref="MemoryMarshal.TryGetMemoryManager{T, TMemoryManager}"/> hands this instance out
        /// typed as the public base class, and an unchecked index would then compute an address
        /// outside the caller's buffer instead of failing. The end of the buffer is a legal position,
        /// because pinning an empty memory sliced at the end arrives here as the length itself.
        /// </summary>
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
            if (elementIndex > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            // No IPinnable: the only object that could move these bytes is none, because they are not
            // on a managed heap, so the handle's disposal has nothing to unpin.
            return new MemoryHandle(_pointer + elementIndex);
        }

        /// <summary>
        /// Unpinning a fixed address is a no-op: nothing was pinned, because nothing was copied.
        /// </summary>
        public override void Unpin() { }

        /// <summary>
        /// Releases nothing. The base class declares this abstract, so the override is the shortest way
        /// to say that a disposed view is still the caller's buffer, and the tests assert exactly that
        /// rather than trusting the empty body.
        /// </summary>
        protected override void Dispose(bool disposing) { }
    }
}
