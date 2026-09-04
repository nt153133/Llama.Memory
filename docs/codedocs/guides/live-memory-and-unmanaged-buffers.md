---
title: "Live Memory and Unmanaged Buffers"
description: "Wrap an existing native buffer as Memory<byte> and scan it without copying into a new managed array."
---

This guide covers the interop path for cases where your tool already has a live memory snapshot in unmanaged memory, such as a memory-mapped file, a native plugin buffer, or a process-memory capture API.

## Problem

You already own a raw pointer and length, and copying that data into a new `byte[]` just to use `PatternSearcher` would waste time and memory.

## Solution

Use `UnmanagedMemoryManager<byte>` to expose the unmanaged region as `Memory<byte>`, then pass the memory directly to `PatternSearcher`.

<Steps>
<Step>
### Acquire or prepare the unmanaged buffer

In a real application this buffer usually comes from a native API, mapped file, or injected-process memory region.

```csharp
byte* pointer = GetBufferPointer();
int length = GetBufferLength();
IntPtr moduleBase = GetModuleBaseAddress();
```
</Step>
<Step>
### Wrap the pointer as `Memory<byte>`

```csharp
using Llama.Memory;

using var manager = new UnmanagedMemoryManager<byte>(pointer, length);
Memory<byte> memory = manager.Memory;
```
</Step>
<Step>
### Scan the unmanaged memory

```csharp
var searcher = new PatternSearcher(memory, moduleBase);
var result = searcher.Search("E8 ? ? ? ? TraceCall");

Console.WriteLine(result == IntPtr.Zero
    ? "Call target not found"
    : $"Resolved call target: 0x{result.ToInt64():X}");
```
</Step>
</Steps>

## Complete Example

```csharp
using System.Runtime.InteropServices;
using Llama.Memory;

unsafe
{
    const int length = 64;
    nint native = Marshal.AllocHGlobal(length);

    try
    {
        var span = new Span<byte>((void*)native, length);
        span.Clear();
        span[10] = 0xE8;
        span[11] = 0x05;
        span[12] = 0x00;
        span[13] = 0x00;
        span[14] = 0x00;

        using var manager = new UnmanagedMemoryManager<byte>((byte*)native, length);
        var searcher = new PatternSearcher(manager.Memory, new IntPtr(0x140000000));

        var target = searcher.Search("E8 ? ? ? ? TraceCall");
        Console.WriteLine($"Target: 0x{target.ToInt64():X}");
    }
    finally
    {
        Marshal.FreeHGlobal(native);
    }
}
```

## Why This Pattern Works

`UnmanagedMemoryManager<T>` implements `MemoryManager<T>` and returns a `Span<T>` directly over the unmanaged pointer in `GetSpan()` (`Llama.Memory/UnmanagedMemoryManager.cs`, lines 21-25). `PatternSearcher` already has a `Memory<byte>` constructor, so the unsafe boundary stays confined to the setup code. Once wrapped, the scanner works exactly as it does for managed memory.

The important ownership rule is that `UnmanagedMemoryManager<T>` does not free the pointer for you. Its `Dispose(bool)` method is intentionally empty (`UnmanagedMemoryManager.cs`, lines 41-45). That means your code must guarantee that the native memory remains valid for the entire time the searcher or any derived spans are used.

## Variations

" "Mapped memory"]}>
<Tab value="Managed snapshot">
```csharp
var bytes = new byte[length];
// Fill from ReadProcessMemory, then scan normally.
var searcher = new PatternSearcher(bytes, moduleBase);
```
</Tab>
<Tab value="Pinned array">
```csharp
fixed (byte* ptr = bytes)
{
    using var manager = new UnmanagedMemoryManager<byte>(ptr, bytes.Length);
    var searcher = new PatternSearcher(manager.Memory, moduleBase);
}
```
</Tab>
<Tab value="Mapped memory">
```csharp
// Use your mapped-file accessor to obtain a stable pointer and length,
// then wrap it with UnmanagedMemoryManager<byte>.
```
</Tab>
</Tabs>

## Verification Tip

Start with a pattern whose expected location you already know. That lets you validate both the native-memory lifetime and the `imageBase` value before you trust more complicated command chains such as `TraceRelative`.
