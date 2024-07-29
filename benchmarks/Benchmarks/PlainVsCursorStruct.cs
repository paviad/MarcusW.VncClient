using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace Benchmarks;

public class PlainVsCursorStruct
{
    private const int Pixels = 1920 * 1080;
    private readonly byte[] _buffer = new byte[Pixels * 4];

    [Benchmark]
    public unsafe void Plain()
    {
        fixed (byte* ptr = &_buffer[0])
        {
            for (var i = 0; i < _buffer.Length; i += 4)
                SetColor(i, ptr + i);
        }
    }

    [Benchmark]
    public unsafe void WithCursor()
    {
        fixed (byte* ptr = &_buffer[0])
        {
            var cursor = new BufferCursor(ptr);
            for (var i = 0; i < Pixels; i++)
                cursor.SetNextPixel(i);
        }
    }

    [Benchmark]
    public unsafe void WithCursorNotInlined()
    {
        fixed (byte* ptr = &_buffer[0])
        {
            var cursor = new BufferCursorNotInlined(ptr);
            for (var i = 0; i < Pixels; i++)
                cursor.SetNextPixel(i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void SetColor(int i, byte* position)
    {
        // Some random operations
        *position++ = (byte)(i & 0xff);
        *position++ = (byte)((i >> 8) & 0xff);
        *position++ = (byte)((i >> 16) & 0xff);
        *position = (byte)((i >> 24) & 0xff);
    }

    private readonly unsafe struct BufferCursor(byte* ptr)
    {
        private readonly byte* _ptr = ptr;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void SetNextPixel(int i)
        {
            MoveNext();

            // Some random operations
            *_ptr = (byte)(i & 0xff);
            *(_ptr + 1) = (byte)((i >> 8) & 0xff);
            *(_ptr + 2) = (byte)((i >> 16) & 0xff);
            *(_ptr + 3) = (byte)((i >> 24) & 0xff);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void MoveNext()
        {
            *_ptr += 4;
        }
    }

    private readonly unsafe struct BufferCursorNotInlined(byte* ptr)
    {
        private readonly byte* _ptr = ptr;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public readonly void SetNextPixel(int i)
        {
            MoveNext();

            // Some random operations
            *_ptr = (byte)(i & 0xff);
            *(_ptr + 1) = (byte)((i >> 8) & 0xff);
            *(_ptr + 2) = (byte)((i >> 16) & 0xff);
            *(_ptr + 3) = (byte)((i >> 24) & 0xff);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public readonly void MoveNext()
        {
            *_ptr += 4;
        }
    }
}
