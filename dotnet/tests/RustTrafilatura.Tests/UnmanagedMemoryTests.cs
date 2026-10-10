using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace RustTrafilatura.Tests;

/// <summary>
/// <see cref="UnmanagedMemory.Wrap"/>: a caller-owned buffer behind a raw pointer becomes a
/// <see cref="Memory{T}"/> the entry points accept. Every test here allocates and frees its own
/// buffer, because the wrapper frees nothing. The private manager is reached through
/// <see cref="MemoryMarshal.TryGetMemoryManager{T, TMemoryManager}"/>, which is what a consumer can do
/// too, so its boundary checks are asserted where the framework's own validation does not reach.
/// </summary>
public sealed unsafe class UnmanagedMemoryTests
{
    [Fact]
    public void Unmanaged_Buffer_Extracts_Like_A_Managed_Copy()
    {
        var page = Fixtures.Article;
        var buffer = Allocate(page, out var expected);

        try
        {
            var pointer = (byte*)buffer.ToPointer();
            var fromUnmanaged = Extractor.ExtractFromBytes(UnmanagedMemory.Wrap(pointer, page.Length), AllFields);
            var fromManaged = Extractor.ExtractFromBytes(expected.AsMemory(), AllFields);

            // The wrapper is a view, so the caller's own array is untouched by either call.
            Assert.Equal(expected, page);

            Assert.Equal("Research results", fromUnmanaged.Metadata!.Title);
            Assert.Equal(fromManaged.ContentText, fromUnmanaged.ContentText);
            Assert.Equal(fromManaged.Metadata!.Title, fromUnmanaged.Metadata!.Title);
            Assert.Equal(fromManaged.Metadata.Sitename, fromUnmanaged.Metadata.Sitename);
            Assert.Equal(fromManaged.Metadata.Language, fromUnmanaged.Metadata.Language);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The wrapper is a view, not a copy, so the address it pins is the caller's own address and the
    /// bytes it exposes are the caller's own bytes. Both are read back, because a wrapper that copied
    /// the buffer would extract the same page either way. The manager is installed for a non-empty
    /// length, which is the other side of the zero-length case below.
    /// </summary>
    [Fact]
    public void Wrap_Points_At_The_Caller_Buffer()
    {
        var page = Fixtures.Article;
        var buffer = Allocate(page, out var expected);
        var pointer = (byte*)buffer.ToPointer();

        try
        {
            var memory = UnmanagedMemory.Wrap(pointer, page.Length);

            Assert.Equal(page.Length, memory.Length);
            Assert.True(TryGetManager(memory, out var manager));
            Assert.Equal(page.Length, manager!.GetSpan().Length);
            Assert.Equal(Encoding.Latin1.GetString(expected), Encoding.Latin1.GetString(ReadPinned(memory)));

            // Writing the caller's buffer changes what the same view reads, which a copy could not do.
            Marshal.WriteByte(buffer, 0, (byte)'X');
            Assert.Equal((byte)'X', ReadPinned(memory)[0]);
            Marshal.WriteByte(buffer, 0, expected[0]);

            var slice = memory.Slice(7, 23);
            Assert.Equal(23, slice.Length);
            Assert.Equal(Encoding.Latin1.GetString(expected, 7, 23), Encoding.Latin1.GetString(ReadPinned(slice)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>A wrapped address is not a handle to release, so the caller's buffer survives it.</summary>
    [Fact]
    public void Wrap_Frees_Nothing()
    {
        var page = Fixtures.Article;
        var buffer = Allocate(page, out var expected);

        try
        {
            var memory = UnmanagedMemory.Wrap((byte*)buffer.ToPointer(), page.Length);
            var first = Extractor.ExtractFromBytes(memory, AllFields);

            // What a caller does with the returned memory once, it can do again: no code here owns or
            // releases the buffer, so a second call still reads the same bytes.
            var second = Extractor.ExtractFromBytes(memory, AllFields);
            Assert.Equal(first.ContentText, second.ContentText);

            // And the bytes are still the caller's after both calls, so the caller may free them.
            Assert.Equal(expected[0], Marshal.ReadByte(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Releasing the view releases nothing, including when the manager is disposed rather than the
    /// handle: the base class's disposal is a no-op, so disposing installs no state that would make
    /// the caller's bytes unreadable, and the same view still extracts the same page afterwards.
    /// </summary>
    [Fact]
    public void Disposing_The_Manager_Releases_Nothing()
    {
        var page = Fixtures.Article;
        var buffer = Allocate(page, out var expected);

        try
        {
            var memory = UnmanagedMemory.Wrap((byte*)buffer.ToPointer(), page.Length);
            var before = Extractor.ExtractFromBytes(memory, AllFields);

            Assert.True(TryGetManager(memory, out var manager));
            ((IDisposable)manager!).Dispose();

            // The view still reads the caller's bytes, through the span and through the entry point,
            // and the caller's pointer still reads them too.
            Assert.Equal(page.Length, memory.Length);
            Assert.Equal(Encoding.Latin1.GetString(expected), Encoding.Latin1.GetString(memory.Span));
            Assert.Equal(before.ContentText, Extractor.ExtractFromBytes(memory, AllFields).ContentText);
            Assert.Equal(expected[0], Marshal.ReadByte(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// A zero length is an empty view, which is what an empty array is, and it installs no manager:
    /// the shared <see cref="Memory{T}.Empty"/> is returned, so a pin of it has no address to report
    /// and reads no caller memory. A null pointer with a zero length is that same empty view rather
    /// than an argument error. Extraction then reaches the core's own too-short answer, exactly as an
    /// empty array does.
    /// </summary>
    [Fact]
    public void Zero_Length_Installs_No_Manager()
    {
        var page = Fixtures.Article;
        var buffer = Allocate(page, out _);

        try
        {
            var empty = UnmanagedMemory.Wrap((byte*)buffer.ToPointer(), 0);
            Assert.Equal(0, empty.Length);
            Assert.True(empty.IsEmpty);
            Assert.Equal(Memory<byte>.Empty, empty);
            Assert.False(TryGetManager(empty, out _));
            using var emptyHandle = empty.Pin();
            Assert.Equal(nint.Zero, (nint)emptyHandle.Pointer);

            var fromNull = UnmanagedMemory.Wrap(null, 0);
            Assert.Equal(Memory<byte>.Empty, fromNull);
            Assert.False(TryGetManager(fromNull, out _));

            var expected = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(Array.Empty<byte>()));
            var fromEmpty = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(empty));
            var fromNullPointer = Assert.Throws<TrafilaturaException>(() => Extractor.ExtractFromBytes(fromNull));

            Assert.Equal(TrafilaturaErrorKind.OutputTooShort, expected.Kind);
            Assert.Equal(expected.Kind, fromEmpty.Kind);
            Assert.Equal(expected.Kind, fromNullPointer.Kind);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Wrap_Rejects_Arguments_That_Cannot_Be_A_View()
    {
        // A null pointer with a length is memory nobody allocated, and it cannot be told apart from an
        // unusable one at the call, so it is refused rather than deferred to the read.
        var nullPointer = Assert.Throws<ArgumentNullException>(() => UnmanagedMemory.Wrap(null, 1));
        Assert.Equal("pointer", nullPointer.ParamName);

        // A negative length is not an empty view either.
        var negative = Assert.Throws<ArgumentOutOfRangeException>(() => UnmanagedMemory.Wrap(null, -1));
        Assert.Equal("length", negative.ParamName);

        // And a view is bounded by the length the caller declared: reaching past its end is the
        // wrapper's own failure, not a page the caller never offered.
        var page = Fixtures.Article;
        var buffer = Allocate(page, out _);
        try
        {
            var memory = UnmanagedMemory.Wrap((byte*)buffer.ToPointer(), 8);
            Assert.Equal(8, memory.Length);
            Assert.Equal(8, memory.Span.Length);

            // The end of the view is a position like any other, so the empty memory there is legal,
            // while a byte past it is not. A start and a length are bounded separately, so both are
            // asserted rather than one standing for the other.
            Assert.True(memory.Slice(8).IsEmpty);
            Assert.Throws<ArgumentOutOfRangeException>(() => memory.Slice(9));
            Assert.Throws<ArgumentOutOfRangeException>(() => memory.Slice(0, 9));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The pinning contract: the memory the wrapper returns pins at the caller's own address, not at a
    /// copy's, through a slice as well as whole, and a released handle releases nothing of the
    /// caller's. Unpinning is the manager's business and is called directly here, because a handle
    /// built without an <see cref="System.Buffers.IPinnable"/> never asks for it.
    /// </summary>
    [Fact]
    public void Wrap_Reports_The_Caller_Pointer_When_Pinned()
    {
        var page = Fixtures.Article;
        var buffer = Allocate(page, out var expected);
        var pointer = (byte*)buffer.ToPointer();

        try
        {
            var memory = UnmanagedMemory.Wrap(pointer, page.Length);
            Assert.True(TryGetManager(memory, out var manager));

            Assert.Equal((nint)pointer, (nint)PinnedPointer(memory));

            // A slice pins the caller's address plus the slice's own offset, so the arithmetic is not
            // a constant that happens to be right for offset zero.
            const int Offset = 7;
            var slice = memory.Slice(Offset, 23);
            Assert.Equal((nint)(pointer + Offset), (nint)PinnedPointer(slice));

            // Pinning again after the handle was disposed reports the same address: nothing was
            // released with the handle, because there was nothing to release.
            Assert.Equal((nint)pointer, (nint)PinnedPointer(memory));

            // Unpinning touches nothing: the address a later pin reports is unchanged and the
            // caller's bytes are still the ones the view reads.
            manager!.Unpin();
            Assert.Equal((nint)pointer, (nint)PinnedPointer(memory));
            Assert.Equal(Encoding.Latin1.GetString(expected), Encoding.Latin1.GetString(ReadPinned(memory)));

            // The address is the buffer's, so a write through the caller's pointer is what the view
            // reads back.
            Marshal.WriteByte(buffer, 0, (byte)'Y');
            Assert.Equal((byte)'Y', ReadPinned(memory)[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The manager's index contract reaches farther than <see cref="Memory{T}"/> does:
    /// <see cref="MemoryMarshal.TryGetMemoryManager{T, TMemoryManager}"/> hands the private manager
    /// out typed as the public base, so a caller can pin an index no slice would produce. The end of
    /// the declared buffer is a legal position, because pinning an empty memory sliced at the end
    /// really does arrive as the length, and everything past it is refused.
    /// </summary>
    [Fact]
    public void Manager_Pin_Allows_The_End_And_Refuses_Beyond_It()
    {
        var page = Fixtures.Article;
        const int Length = 8;
        var buffer = Allocate(page, out _);
        var pointer = (byte*)buffer.ToPointer();

        try
        {
            var memory = UnmanagedMemory.Wrap(pointer, Length);
            Assert.True(TryGetManager(memory, out var manager));

            using var atEnd = manager!.Pin(Length);
            Assert.Equal((nint)(pointer + Length), (nint)atEnd.Pointer);

            // The same position through the public surface, which is why the end is allowed.
            using var slicedAtEnd = memory.Slice(Length).Pin();
            Assert.Equal((nint)(pointer + Length), (nint)slicedAtEnd.Pointer);

            var beyond = Assert.Throws<ArgumentOutOfRangeException>(() => manager.Pin(Length + 1));
            Assert.Equal("elementIndex", beyond.ParamName);

            var negative = Assert.Throws<ArgumentOutOfRangeException>(() => manager.Pin(-1));
            Assert.Equal("elementIndex", negative.ParamName);

            // A memory reached through the manager is bounded the same way, so the two paths agree.
            var sliced = memory.Slice(1, 2);
            Assert.Throws<ArgumentOutOfRangeException>(() => sliced.Slice(0, Length).Pin());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Every response field, so the comparison covers the whole result.</summary>
    private static ExtractionOptions AllFields => new()
    {
        ResponseFields = ["contentText", "commentsText", "contentHtml", "commentsHtml", "metadata"],
    };

    /// <summary>A copy of <paramref name="bytes"/> in unmanaged memory, with the original kept for comparison.</summary>
    private static nint Allocate(byte[] bytes, out byte[] expected)
    {
        expected = (byte[])bytes.Clone();
        var buffer = Marshal.AllocHGlobal(expected.Length);
        Marshal.Copy(expected, 0, buffer, expected.Length);
        return buffer;
    }

    /// <summary>
    /// The manager behind a view, as any consumer can reach it. Whether one is installed at all is
    /// what the zero-length case asserts, so the out parameter is null on the false path.
    /// </summary>
    /// <remarks>
    /// The nullable annotation is switched off for this member because the API it forwards to carries
    /// none: its out parameter is declared non-nullable even though a memory backed by an array
    /// reports false with a null manager, which is the false path every caller here reads.
    /// </remarks>
#nullable disable
    private static bool TryGetManager(Memory<byte> memory, out MemoryManager<byte> manager) =>
        MemoryMarshal.TryGetMemoryManager<byte, MemoryManager<byte>>(memory, out manager);
#nullable restore

    /// <summary>
    /// The address a pin reports, read while the memory is valid and released with the handle. A pin
    /// that advanced by the wrong offset and one that reported a copy's address differ here.
    /// </summary>
    private static byte* PinnedPointer(Memory<byte> memory)
    {
        using var handle = memory.Pin();
        return (byte*)handle.Pointer;
    }

    /// <summary>
    /// The bytes a memory pins to, copied out through the handle's own pointer. Comparing the bytes
    /// rather than the address also covers the wrapper's length: a view that exposed bytes outside the
    /// declared buffer would read something the caller never offered.
    /// </summary>
    private static byte[] ReadPinned(Memory<byte> memory)
    {
        using var handle = memory.Pin();
        var copy = new byte[memory.Length];
        Marshal.Copy((nint)handle.Pointer, copy, 0, copy.Length);
        return copy;
    }
}
