---
title: "Pattern Helpers"
description: "Reference for the public helper types used by the pattern parser and low-allocation tokenization path."
---

Source files:

- `Llama.Memory/CharSpanSplitter.cs`
- `Llama.Memory/Utilities.cs`

Import path:

```csharp
using Llama.Memory;
```

## CharSpanSplitter

```csharp
public readonly ref struct CharSpanSplitter
{
    public CharSpanSplitter(ReadOnlySpan<char> input)
    public Enumerator GetEnumerator()
}
```

`CharSpanSplitter` is a low-allocation tokenizer over `ReadOnlySpan<char>`. `PatternSearcher` uses it to walk a pattern string one token at a time without splitting into a temporary string array.

### Members

| Member | Type | Description |
|--------|------|-------------|
| `CharSpanSplitter(ReadOnlySpan<char> input)` | constructor | Stores the input span. |
| `GetEnumerator()` | `Enumerator` | Returns a custom enumerator for `foreach`-style token iteration. |

The nested enumerator also exposes:

```csharp
public ReadOnlySpan<char> Current { get; }
public bool MoveNext()
```

Example:

```csharp
ReadOnlySpan<char> pattern = "48 8D 0D ? ? ? ?";
foreach (var token in pattern.Split())
{
    Console.WriteLine(token.ToString());
}
```

In normal application code you usually rely on `PatternSearcher` to use this helper for you.

## CharSpanExtensions

```csharp
public static class CharSpanExtensions
{
    public static CharSpanSplitter Split(this ReadOnlySpan<char> input)
    public static CharSpanSplitter Split(this Span<char> input)
}
```

These extension methods construct a `CharSpanSplitter` from a span.

## Utilities

```csharp
public static class Utilities
{
    public static byte GetMask(this ReadOnlySpan<char> tok)
    public static bool IsValidHex(this ReadOnlySpan<char> str)
    public static int HexValueOf(this char c)
    public static byte GetByte(this ReadOnlySpan<char> tok)
}
```

### Methods

| Method | Return type | Description |
|--------|-------------|-------------|
| `GetMask(this ReadOnlySpan<char> tok)` | `byte` | Converts a token like `48`, `??`, `4?`, or `?F` into a match mask. |
| `IsValidHex(this ReadOnlySpan<char> str)` | `bool` | Validates that every character belongs to the allowed hex-and-wildcard set. |
| `HexValueOf(this char c)` | `int` | Converts one hex digit to its numeric value. |
| `GetByte(this ReadOnlySpan<char> tok)` | `byte` | Converts a token to a byte value, treating wildcards as zeroed nibbles. |

Example:

```csharp
ReadOnlySpan<char> token = "4?";
byte mask = token.GetMask();   // 0xF0
byte value = token.GetByte();  // 0x40
```

## When You Would Use These Directly

Most consumers never need these helpers directly because `PatternSearcher` hides them. They become useful when you are building your own pattern preprocessor, validating signature input before scanning, or experimenting with alternate search implementations that still want the same token semantics as the built-in scanner.

## Related APIs

- `/docs/pattern-syntax`
- `/docs/api-reference/pattern-searcher`
