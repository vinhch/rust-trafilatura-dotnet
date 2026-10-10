using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MarshallingProbe;

// One [LibraryImport] declaration per case. The case is selected with
// -p:DefineConstants=CASE_..., so a bare build compiles no declaration at all and stays green.
internal static partial class Cases
{
#if CASE_POINTER
    [LibraryImport("rt", EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int PointerArgs(byte* header, int headerLength, byte* payload, int payloadLength);
#endif

#if CASE_ARRAY
    [LibraryImport("rt", EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ArrayArgs(byte[] header, byte[] payload);
#endif

#if CASE_SPAN
    [LibraryImport("rt", EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SpanArgs(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload);
#endif

#if CASE_MUTABLE_SPAN
    [LibraryImport("rt", EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MutableSpanArgs(Span<byte> header, Span<byte> payload);
#endif

#if CASE_MEMORY
    [LibraryImport("rt", EntryPoint = "rt_extract", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MemoryArgs(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload);
#endif
}

// Runtime checks that the documents label observed: the value a null array becomes, and the
// allocations the byte-surface rows quote. Not tied to any CASE_, so a bare build runs them.
internal static class Program
{
    private static void Main()
    {
        byte[]? nullArray = null;
        ReadOnlySpan<byte> nullSpan = nullArray;
        Console.WriteLine($"null byte[] to ReadOnlySpan<byte>: Length = {nullSpan.Length}");

        ReadOnlyMemory<byte> nullMemory = nullArray;
        Console.WriteLine(
            $"null byte[] to ReadOnlyMemory<byte>: Length = {nullMemory.Length}, IsEmpty = {nullMemory.IsEmpty}");

        IReadOnlyList<string> empty = [];
        Console.WriteLine(
            $"collection expression [] with an IReadOnlyList<string> target is Array.Empty<string>(): {ReferenceEquals(empty, Array.Empty<string>())}");

        Console.WriteLine($"new byte[1024]: {Measured(NewArray)} B");
        Console.WriteLine($"new List<string>(): {Measured(NewList)} B");
        Console.WriteLine($"byte[] to ReadOnlyMemory<byte> (implicit): {Measured(ImplicitMemory)} B");
        Console.WriteLine($"array.AsMemory(0, 1024): {Measured(AsMemory)} B");
        Console.WriteLine($"Span<byte>.ToArray() over the same 1024 bytes: {Measured(ToArray)} B");
    }

    // The source array lives outside every measured body: these rows measure the conversion or the
    // copy, not the buffer somebody else allocated.
    private static readonly byte[] FixedBuffer = new byte[1024];

    private static byte[] NewArray() => new byte[1024];

    private static List<string> NewList() => new List<string>();

    private static ReadOnlyMemory<byte> ImplicitMemory()
    {
        ReadOnlyMemory<byte> memory = FixedBuffer;
        return memory;
    }

    private static ReadOnlyMemory<byte> AsMemory() => FixedBuffer.AsMemory(0, FixedBuffer.Length);

    private static byte[] ToArray() => FixedBuffer.AsSpan().ToArray();

    private static long Measured<T>(Func<T> allocate)
    {
        T warmup = allocate();
        GC.KeepAlive(warmup);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        T value = allocate();
        long after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(value);
        return after - before;
    }
}
