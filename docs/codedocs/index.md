---
title: "Getting Started"
description: "Learn what Llama.Memory does, why it exists, and how to get a working scanner running in a few minutes."
---

`Llama.Memory` is a dependency-free .NET library for fast byte-pattern scanning, PE header parsing, and unmanaged-memory interop.

## The Problem

- Reverse-engineering and binary-analysis tools often need to scan large byte buffers for signatures, but hand-written scanners are usually slow, allocation-heavy, or hard to maintain.
- Signature formats used in game tooling and memory tooling frequently need wildcard bytes and post-match pointer transforms, yet many generic search helpers only return the raw match offset.
- Reading PE metadata usually pulls in a larger parser than you need when all you want is the image base and a few section boundaries.
- Moving between raw pointers, `Memory<byte>`, file-backed buffers, and section RVAs tends to create glue code that is easy to get wrong.

## The Solution

`Llama.Memory` packages those concerns into one small assembly. `PatternSearcher` parses GreyMagic-style patterns, chooses a rare anchor byte or anchor pair, and scans with `Span<byte>` and `SearchValues<T>` optimizations. `PeHeaderParser` extracts only the PE fields the scanner workflows need, and `UnmanagedMemoryManager<T>` lets you wrap existing unmanaged buffers without copying them.

```csharp
using Llama.Memory;

var bytes = File.ReadAllBytes("ffxiv_dx11.exe");
var pe = PeHeaderParser.GetPeHeaders("ffxiv_dx11.exe");
var text = pe.TextSection ?? throw new InvalidDataException("Missing .text");

var textSlice = bytes.AsMemory(
    checked((int)text.PointerToRawData),
    checked((int)text.SizeOfRawData));

var searcher = new PatternSearcher(textSlice, new IntPtr(text.VirtualAddress));
var address = searcher.Search("48 8D 0D ? ? ? ? Add 3 TraceRelative");
```

## Installation

" "bun"]}>
<Tab value="npm">
```bash
dotnet add package Llama.Memory
```
</Tab>
<Tab value="pnpm">
```bash
dotnet add package Llama.Memory
```
</Tab>
<Tab value="yarn">
```bash
dotnet add package Llama.Memory
```
</Tab>
<Tab value="bun">
```bash
dotnet add package Llama.Memory
```
</Tab>
</Tabs>

Supported target frameworks in the package are `net8.0` and `net10.0`, as declared in `Llama.Memory/Llama.Memory.csproj`.

## Quick Start

This example scans the `.text` section of a PE file and prints the first resolved RVA-like result.

```csharp
using Llama.Memory;

var exePath = "ffxiv_dx11.exe";
var pe = PeHeaderParser.GetPeHeaders(exePath);
var text = pe.TextSection ?? throw new InvalidDataException("Missing .text");

var fileBytes = File.ReadAllBytes(exePath);
var textBytes = fileBytes.AsMemory(
    checked((int)text.PointerToRawData),
    checked((int)text.SizeOfRawData));

var searcher = new PatternSearcher(textBytes, new IntPtr(text.VirtualAddress));
var match = searcher.Search(
    "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8B F0 48 85 C0 74 ? 48 83 38 Add 3 TraceRelative");

Console.WriteLine(match == IntPtr.Zero
    ? "Pattern not found"
    : $"Match found at RVA 0x{match.ToInt64():X}");
```

Expected output:

```text
Match found at RVA 0x123456
```

The exact address will vary by binary build. What matters is that the call returns a non-zero `IntPtr` when the signature resolves successfully.

## Key Features

- Fast single-pattern and multi-pattern scanning over contiguous `byte[]`, `Span<byte>`, `ReadOnlySpan<byte>`, and `Memory<byte>` inputs.
- Wildcards and post-match commands such as `Add`, `Sub`, `Read32`, `TraceRelative`, and `TraceCall`.
- Multi-pattern APIs that return either the first hit per pattern or all hits per pattern in one call.
- A focused PE parser that exposes image base information plus lightweight section records.
- An unmanaged-memory bridge for scanners that already own native pointers.
- No third-party runtime dependencies.

## Next Steps

<Cards>
  <Card title="Architecture" href="/docs/architecture">See how the scanner, parser, and memory wrappers fit together internally.</Card>
  <Card title="Core Concepts" href="/docs/pattern-syntax">Understand pattern syntax, scan execution, and PE section mapping.</Card>
  <Card title="API Reference" href="/docs/api-reference/pattern-searcher">Jump to constructor signatures, methods, and source-file level references.</Card>
</Cards>
