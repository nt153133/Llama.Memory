---
title: "UnmanagedMemoryManager"
description: "Reference for the MemoryManager wrapper that turns a raw pointer into Memory<T>."
---

Source file: `Llama.Memory/UnmanagedMemoryManager.cs`

Import path:

```csharp
using Llama.Memory;
```

## Signature

```csharp
public sealed unsafe class UnmanagedMemoryManager<T> : MemoryManager<T> where T : unmanaged
```

## Constructor

```csharp
public UnmanagedMemoryManager(T* pointer, int length)
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `pointer` | `T*` | — | Raw unmanaged pointer to the first element. |
| `length` | `int` | — | Number of elements in the buffer. Must be non-negative. |

## Overridden Members

### GetSpan

```csharp
public override Span<T> GetSpan()
```

Returns a `Span<T>` directly over the unmanaged memory.

### Pin

```csharp
public override MemoryHandle Pin(int elementIndex = 0)
```

Returns a `MemoryHandle` starting at the requested element index.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `elementIndex` | `int` | `0` | Element offset to pin from. Must be within range. |

### Unpin

```csharp
public override void Unpin()
```

No-op because the class does not own GC pinning for unmanaged memory.

## Usage Example

```csharp
unsafe
{
    using var manager = new UnmanagedMemoryManager<byte>(pointer, length);
    var searcher = new PatternSearcher(manager.Memory, moduleBase);
    var result = searcher.Search("E8 ? ? ? ? TraceCall");
}
```

## Practical Notes

This type is intentionally small because it exists to bridge an unsafe ownership model into the `Memory<T>` ecosystem used by the rest of the library. `GetSpan()` simply projects the raw pointer and length into a `Span<T>`, and `Pin()` returns a `MemoryHandle` over the same unmanaged region. There is no hidden allocation, pinning table, or copy step.

That design is efficient, but it shifts responsibility to the caller. The pointer must stay valid, correctly aligned for your usage, and unchanged for as long as downstream code might read from `manager.Memory` or any span derived from it. If the native owner frees or repurposes the buffer too early, the scanner will read invalid memory.

## Example: scan a native snapshot then inspect a context window

```csharp
unsafe
{
    using var manager = new UnmanagedMemoryManager<byte>(pointer, length);
    var searcher = new PatternSearcher(manager.Memory, moduleBase);
    var hit = searcher.Search("48 8B ?? ?? 89");

    if (hit != IntPtr.Zero)
    {
        var localOffset = (int)(hit.ToInt64() - moduleBase.ToInt64());
        var context = searcher.GetSlice(localOffset, 16);
        Console.WriteLine(context[0]);
    }
}
```

## Ownership Rules

`UnmanagedMemoryManager<T>` does not free the underlying pointer when disposed. Your code must guarantee that the unmanaged region outlives any `Memory<T>` or `Span<T>` views created from it.

## Related APIs

- `/docs/guides/live-memory-and-unmanaged-buffers`
- `/docs/api-reference/pattern-searcher`
