using System.Buffers;

namespace Llama.Memory;

/// <summary>
/// A MemoryManager that creates a safe Memory wrapper around a raw unmanaged pointer.
/// </summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
public sealed unsafe class UnmanagedMemoryManager<T> : MemoryManager<T> where T : unmanaged
{
    private readonly T* _pointer;
    private readonly int _length;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnmanagedMemoryManager{T}"/> class wrapping the specified unmanaged pointer and length.
    /// </summary>
    /// <param name="pointer">A pointer to the unmanaged memory block.</param>
    /// <param name="length">The number of elements of type <typeparamref name="T"/> in the unmanaged memory block.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is negative.</exception>
    public UnmanagedMemoryManager(T* pointer, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        _pointer = pointer;
        _length = length;
    }

    /// <inheritdoc />
    public override Span<T> GetSpan()
    {
        return new Span<T>(_pointer, _length);
    }

    /// <inheritdoc />
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if (elementIndex < 0 || elementIndex >= _length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        // The memory is already unmanaged/fixed, so we just return the pointer directly.
        return new MemoryHandle(_pointer + elementIndex);
    }

    /// <inheritdoc />
    public override void Unpin()
    {
        // Nothing to do. We don't control the GC pinning for unmanaged memory.
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // The memory lifetime is managed by the external caller (e.g., the MMF Accessor).
        // We do nothing here.
    }
}