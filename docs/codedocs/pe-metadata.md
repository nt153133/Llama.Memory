---
title: "PE Metadata"
description: "Understand how Llama.Memory models PE image metadata and uses section information to drive scanner workflows."
---

`Llama.Memory` is not just a scanner. It also includes a deliberately minimal Portable Executable parser that gives you the exact metadata most scanning workflows need: image base information and section boundaries.

## What It Is

The PE metadata layer consists of three public types:

- `PeHeaderParser`
- `PeHeaderInfo`
- `SimpleSectionHeader`

Together they let you inspect a Windows PE file, find sections such as `.text` or `.data`, convert RVAs to file offsets, and slice file buffers correctly before scanning.

## Why It Exists

Pattern scanners rarely operate over an entire executable byte-for-byte. In most workflows, you only want to search the `.text` section, or you want to resolve a result from an RVA-like value back to a file offset inside `.data`. Pulling a full PE library into a small scanner tool can be unnecessary overhead when the problem only needs section metadata. `Llama.Memory` solves that by parsing only the headers it cares about.

## How It Relates to Other Concepts

- `PatternSearcher` consumes section slices and an origin `ImageBase`.
- `FfxivVersionChecker` shows how `.text` and `.data` sections work together in a real workflow.
- `SimpleSectionHeader.VirtualAddress` and `PointerToRawData` are the bridge between file layout and in-memory layout.

## How It Works Internally

`GetPeHeaders` in `Llama.Memory/PeHeaderParser.cs` opens the file with `FileShare.ReadWrite`, reads an initial 4 KB header buffer from an `ArrayPool<byte>`, validates the DOS signature and PE signature, and then decodes the `IMAGE_FILE_HEADER` and section table (`PeHeaderParser.cs`, lines 174-257). The parser reads just enough of the optional header to determine whether the file is PE32 or PE32+ and then extracts `ImageBase` accordingly (`PeHeaderParser.cs`, lines 202-210).

If the initial 4 KB read is not enough to cover the full section table, the parser grows the buffer and reads more (`PeHeaderParser.cs`, lines 215-229). Each section header is converted to a `SimpleSectionHeader` record with:

- `Name`
- `VirtualSize`
- `VirtualAddress`
- `SizeOfRawData`
- `PointerToRawData`

`PeHeaderInfo` then exposes convenience properties `TextSection`, `RdataSection`, and `DataSection` that search the section array by name (`PeHeaderParser.cs`, lines 21-30). `RvaToFileOffset` performs a linear scan over the section array and maps an RVA into file coordinates (`PeHeaderParser.cs`, lines 261-273). The linear scan is intentional; the comment notes that typical PE files only have a small number of sections, so a more complex structure would not buy much.

```mermaid
flowchart TD
  A[Open PE file] --> B[Read DOS header and e_lfanew]
  B --> C[Validate PE signature]
  C --> D[Read file header]
  D --> E[Inspect optional-header magic]
  E --> F[Read image base]
  F --> G[Read section table]
  G --> H[Build SimpleSectionHeader[]]
  H --> I[Return PeHeaderInfo]
  I --> J[RVA to file offset conversion]
```

## Basic Usage

```csharp
using Llama.Memory;

var pe = PeHeaderParser.GetPeHeaders("ffxiv_dx11.exe");
var text = pe.TextSection ?? throw new InvalidDataException("Missing .text");

Console.WriteLine($"ImageBase: 0x{pe.ImageBase:X}");
Console.WriteLine($".text RVA: 0x{text.VirtualAddress:X}");
Console.WriteLine($".text raw file offset: 0x{text.PointerToRawData:X}");
```

This is the simplest way to establish the scanning bounds for a file-backed search.

## Advanced Usage

```csharp
using Llama.Memory;

var pe = PeHeaderParser.GetPeHeaders("ffxiv_dx11.exe");
var sections = pe.Sections;

uint targetRva = 0x123456;
uint fileOffset = PeHeaderParser.RvaToFileOffset(targetRva, sections);

using var stream = File.OpenRead("ffxiv_dx11.exe");
stream.Position = fileOffset;

Span<byte> value = stackalloc byte[16];
stream.ReadExactly(value);

Console.WriteLine(BitConverter.ToString(value.ToArray()));
```

This pattern is common when a previous scan returns an RVA-like result and you need to inspect the corresponding bytes in the file image.

## Common Pitfalls

<Callout type="warn">
`PeHeaderInfo.Sections[0]` and `Sections[2]` happen to be used in `FfxivVersionChecker`, but that indexing is application-specific and brittle. In general-purpose code, prefer `TextSection`, `DataSection`, or a name-based lookup over hard-coded section positions. PE section order is not guaranteed across unrelated binaries.
</Callout>

You should also keep file layout and memory layout separate in your head. `PointerToRawData` is a file offset. `VirtualAddress` is an RVA. Passing one where the other is expected is the most common source of off-by-large-offset bugs in PE scanning code.

## Trade-Offs

<Accordions>
<Accordion title="Why parse only the image base and section table?">
The parser is intentionally narrow because the rest of the library only needs a small slice of the PE format. That keeps the code small, dependency-free, and easy to audit for performance-sensitive tooling. The cost is that `Llama.Memory` is not a general PE analysis package: if you need import tables, relocations, data directories, or certificate parsing, you will need another library or your own parser. For scanner-oriented workflows, though, the narrow scope removes a lot of unnecessary complexity.

```csharp
var pe = PeHeaderParser.GetPeHeaders(path);
var text = pe.TextSection;
```
</Accordion>
<Accordion title="Why return lightweight records instead of richer section objects?">
`PeHeaderInfo` and `SimpleSectionHeader` are plain data carriers, which keeps allocation behavior and API surface straightforward. They are easy to serialize, log, and pass around, and they do not hide any mutable state or lazy parsing. The trade-off is convenience: some callers may want richer helpers, like direct span slicing or file-reading methods on the section object itself. The library chooses simpler records so the caller remains in control of file I/O and memory ownership.

```csharp
public readonly record struct SimpleSectionHeader(
    string Name,
    uint VirtualSize,
    uint VirtualAddress,
    uint SizeOfRawData,
    uint PointerToRawData);
```
</Accordion>
</Accordions>

## Related Pages

- `/docs/guides/scan-pe-section` for the standard parser-plus-scanner workflow.
- `/docs/api-reference/pe-header-parser` for exact signatures.
- `/docs/api-reference/pe-types` for the record shapes exposed by the parser.
