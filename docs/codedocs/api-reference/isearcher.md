---
title: "ISearcher"
description: "Reference for the scanner contract implemented by PatternSearcher."
---

Source file: `Llama.Memory/ISearcher.cs`

Import path:

```csharp
using Llama.Memory;
```

## Signature

```csharp
public interface ISearcher
{
    public IntPtr ImageBase { get; }
    public IntPtr Search(string pattern);
    public IntPtr Search(string pattern, IntPtr start, int maxSearchLength);
    public IntPtr[] SearchMany(string pattern);
    public IntPtr[] Search(string[] patterns);
    public IntPtr[][] SearchMany(string[] patterns);
    public ReadOnlySpan<byte> GetSlice(int start, int length);
}
```

## Members

### ImageBase

```csharp
public IntPtr ImageBase { get; }
```

The address origin applied to raw scan offsets before results are returned.

### Search

```csharp
public IntPtr Search(string pattern)
public IntPtr Search(string pattern, IntPtr start, int maxSearchLength)
```

These overloads return the first matching result for one pattern, either in the whole buffer or in a restricted range.

### SearchMany

```csharp
public IntPtr[] SearchMany(string pattern)
public IntPtr[][] SearchMany(string[] patterns)
```

These overloads return all matches. The array-of-arrays variant preserves index alignment with the input pattern list.

### Search for multiple patterns

```csharp
public IntPtr[] Search(string[] patterns)
```

Returns one result slot per input pattern, with `IntPtr.Zero` for missing hits.

### GetSlice

```csharp
public ReadOnlySpan<byte> GetSlice(int start, int length)
```

Exposes a read-only view of the underlying memory region without allocating a copy.

## Behavioral Notes

`ISearcher` is synchronous by design. The XML documentation in `ISearcher.cs` explicitly notes that the methods do not return `Task` and do not create background work. If you need asynchronous orchestration, wrap the calls in your own `Task.Run(...)` boundary.

The interface also defines the pattern-language contract in its XML comments, including the meaning of `Add`, `Sub`, `Read8`, `Read16`, `Read32`, `Read64`, `TraceRelative`, and `TraceCall`. If you are implementing your own searcher, those comments are the behavioral specification you need to preserve.

## Example

```csharp
using Llama.Memory;

ISearcher searcher = new PatternSearcher(File.ReadAllBytes("module.bin"), IntPtr.Zero);
var result = searcher.Search("E8 ? ? ? ? TraceCall");
```

## Related APIs

- `/docs/api-reference/pattern-searcher`
- `/docs/pattern-syntax`
