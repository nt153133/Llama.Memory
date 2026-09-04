---
title: "PeHeaderParser"
description: "Reference for the PE parser and the methods used to decode sections and convert RVAs to file offsets."
---

Source file: `Llama.Memory/PeHeaderParser.cs`

Import path:

```csharp
using Llama.Memory;
```

## Signature

```csharp
public static class PeHeaderParser
```

## Methods

### GetPeHeaders

```csharp
public static unsafe PeHeaderInfo GetPeHeaders(string filePath)
```

Parses the DOS header, PE header, optional-header image base, and section table from a PE file.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `filePath` | `string` | — | Relative or absolute path to the PE file. The file is opened with `FileShare.ReadWrite`. |

Return type: `PeHeaderInfo`

Example:

```csharp
var pe = PeHeaderParser.GetPeHeaders("ffxiv_dx11.exe");
Console.WriteLine(pe.ImageBase);
```

### RvaToFileOffset

```csharp
public static uint RvaToFileOffset(uint rva, SimpleSectionHeader[] sections)
```

Maps an RVA into a raw file offset by finding the section that contains the RVA.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `rva` | `uint` | — | The RVA to convert. |
| `sections` | `SimpleSectionHeader[]` | — | Section list returned by `GetPeHeaders`. |

Return type: `uint`

Example:

```csharp
var pe = PeHeaderParser.GetPeHeaders("ffxiv_dx11.exe");
var fileOffset = PeHeaderParser.RvaToFileOffset(0x123456, pe.Sections);
```

## Usage Pattern

```csharp
var pe = PeHeaderParser.GetPeHeaders(path);
var text = pe.TextSection ?? throw new InvalidDataException();

var bytes = File.ReadAllBytes(path);
var textSlice = bytes.AsMemory(
    checked((int)text.PointerToRawData),
    checked((int)text.SizeOfRawData));

var searcher = new PatternSearcher(textSlice, new IntPtr(text.VirtualAddress));
```

## Parameter and Return Semantics

`GetPeHeaders` returns enough information to separate file coordinates from in-memory coordinates. That distinction matters because `PatternSearcher` usually scans a raw file slice but returns values in a section-relative RVA-like space once you pass the section `VirtualAddress` as `imageBase`. `PeHeaderInfo.ImageBase` is still useful when you need the executable's preferred load address, but it is not automatically consumed by `PatternSearcher`.

`RvaToFileOffset` expects the exact `SimpleSectionHeader[]` returned by the parser or an equivalent section list. It does a straightforward containment check by subtracting each section's `VirtualAddress` from the requested RVA and verifying that the delta falls inside `VirtualSize`. That means the method is fast and predictable, but it assumes your section metadata is already correct.

## Example: convert a scanner result back to file coordinates

```csharp
var pe = PeHeaderParser.GetPeHeaders(path);
var text = pe.TextSection ?? throw new InvalidDataException();

var fileBytes = File.ReadAllBytes(path);
var textSlice = fileBytes.AsMemory(
    checked((int)text.PointerToRawData),
    checked((int)text.SizeOfRawData));

var searcher = new PatternSearcher(textSlice, new IntPtr(text.VirtualAddress));
var result = searcher.Search("48 8D 0D ? ? ? ? Add 3 TraceRelative");

if (result != IntPtr.Zero)
{
    var fileOffset = PeHeaderParser.RvaToFileOffset((uint)result.ToInt64(), pe.Sections);
    Console.WriteLine($"File offset: 0x{fileOffset:X}");
}
```

## Notes

- `GetPeHeaders` is synchronous.
- The parser intentionally reads only a subset of PE metadata.
- `RvaToFileOffset` throws `ArgumentOutOfRangeException` if the RVA does not fall inside any section.

## Related APIs

- `/docs/api-reference/pe-types`
- `/docs/pe-metadata`
- `/docs/guides/scan-pe-section`
