using System.Buffers;

namespace Llama.Memory;

/// <summary>
/// A MemoryManager that creates a safe Memory wrapper around a raw unmanaged pointer.
/// </summary>
public sealed unsafe class UnmanagedMemoryManager<T> : MemoryManager<T> where T : unmanaged
{
    private readonly T* _pointer;
    private readonly int _length;

    public UnmanagedMemoryManager(T* pointer, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        _pointer = pointer;
        _length = length;
    }

    // This is called whenever your WitchHunt scanner calls _data.Span
    public override Span<T> GetSpan()
    {
        return new Span<T>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if (elementIndex < 0 || elementIndex >= _length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        // The memory is already unmanaged/fixed, so we just return the pointer directly.
        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
        // Nothing to do. We don't control the GC pinning for unmanaged memory.
    }

    protected override void Dispose(bool disposing)
    {
        // The memory lifetime is managed by the external caller (e.g., the MMF Accessor).
        // We do nothing here.
    }
}