---
title: "PatternSearcher"
description: "Reference for the main scanning class, its constructors, and every public search overload."
---

Source file: `Llama.Memory/PatternSearcher.cs`

Import path:

```csharp
using Llama.Memory;
```

## Overview

`PatternSearcher` is the concrete implementation of `ISearcher`. It scans a contiguous byte region stored as `Memory<byte>`, applies wildcard-aware pattern matching, and returns `IntPtr` results with the configured `ImageBase` applied.

## Constructors

```csharp
public PatternSearcher(byte[] assemblyData, IntPtr imageBase)
public PatternSearcher(Span<byte> assemblyData, IntPtr imageBase)
public PatternSearcher(ref ReadOnlySpan<byte> assemblyData, IntPtr imageBase)
public PatternSearcher(ReadOnlySpan<byte> assemblyData, IntPtr imageBase)
public PatternSearcher(Memory<byte> assemblyData, IntPtr imageBase)
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `assemblyData` | `byte[]`, `Span<byte>`, `ReadOnlySpan<byte>`, or `Memory<byte>` | — | The contiguous buffer to scan. Span-based overloads copy into a managed array; `Memory<byte>` preserves the supplied backing store. |
| `imageBase` | `IntPtr` | — | The origin added to raw offsets after any post-match commands run. Use `IntPtr.Zero` for raw buffer offsets. |

Public property:

```csharp
public IntPtr ImageBase { get; }
public readonly Memory<byte> Data;
```

`Data` exposes the underlying memory region. It is a field rather than a property, which is unusual for a public API but important if you are reading the source directly.

## Methods

### Search

```csharp
public IntPtr Search(string pattern)
```

Searches the full buffer for the first match.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `pattern` | `string` | — | A space-separated signature with optional post-match commands. |

Returns `IntPtr.Zero` if no match is found.

Example:

```csharp
var result = searcher.Search("E8 ? ? ? ? TraceCall");
```

### Search with range

```csharp
public IntPtr Search(string pattern, IntPtr start, int maxSearchLength)
```

Searches a sub-range inside the current buffer.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `pattern` | `string` | — | Pattern syntax identical to the single-parameter overload. |
| `start` | `IntPtr` | — | Zero-based buffer offset where scanning begins. This is not an already rebased process address. |
| `maxSearchLength` | `int` | — | Maximum number of bytes to inspect from `start`. |

Example:

```csharp
var secondWindow = searcher.Search(pattern, new IntPtr(0x1000), 0x4000);
```

### SearchMany for one pattern

```csharp
public IntPtr[] SearchMany(string pattern)
```

Returns every non-overlapping match of one pattern in ascending order.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `pattern` | `string` | — | Pattern to match across the whole buffer. |

Example:

```csharp
var matches = searcher.SearchMany("41 B8 ? ? ? ?");
```

### Search for many patterns

```csharp
public IntPtr[] Search(string[] patterns)
```

Returns the first hit for each pattern, index-aligned to the input array.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `patterns` | `string[]` | — | Pattern list. Reuse the same array instance if you want the internal compilation cache to be reused. |

Example:

```csharp
var hits = searcher.Search(new[]
{
    "48 8D 0D ? ? ? ? Add 3 TraceRelative",
    "E8 ? ? ? ? TraceCall"
});
```

### SearchMany for many patterns

```csharp
public IntPtr[][] SearchMany(string[] patterns)
```

Returns every match for every pattern, again index-aligned to the input.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `patterns` | `string[]` | — | Pattern list to scan. |

Example:

```csharp
var allHits = searcher.SearchMany(patterns);
```

### GetSlice

```csharp
public ReadOnlySpan<byte> GetSlice(int start, int length)
```

Returns a zero-allocation slice of the current buffer.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `start` | `int` | — | Zero-based offset into the buffer. |
| `length` | `int` | — | Number of bytes to include. |

Return type: `ReadOnlySpan<byte>`

Example:

```csharp
var bytes = searcher.GetSlice(0x200, 32);
```

## Common Usage Patterns

Combine the parser and scanner for section-relative results:

```csharp
var pe = PeHeaderParser.GetPeHeaders(path);
var text = pe.TextSection ?? throw new InvalidDataException();

var fileBytes = File.ReadAllBytes(path);
var textBytes = fileBytes.AsMemory(
    checked((int)text.PointerToRawData),
    checked((int)text.SizeOfRawData));

var searcher = new PatternSearcher(textBytes, new IntPtr(text.VirtualAddress));
var result = searcher.Search("48 8D 0D ? ? ? ? Add 3 TraceRelative");
```

Inspect a portion of the buffer after a hit:

```csharp
var hit = searcher.Search("48 8B ?? ?? 89");
if (hit != IntPtr.Zero)
{
    var localOffset = (int)(hit.ToInt64() - searcher.ImageBase.ToInt64());
    var context = searcher.GetSlice(localOffset, 16);
}
```

## Related APIs

- `/docs/api-reference/isearcher`
- `/docs/api-reference/pattern-helpers`
- `/docs/pattern-syntax`
