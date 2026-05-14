---
title: "PE Types"
description: "Reference for the PE metadata records exposed by Llama.Memory."
---

Source file: `Llama.Memory/PeHeaderParser.cs`

Import path:

```csharp
using Llama.Memory;
```

## PeHeaderInfo

```csharp
public record PeHeaderInfo(
    ulong ImageBase,
    SimpleSectionHeader[] Sections)
{
    public SimpleSectionHeader this[int index] => Sections[index];
    public SimpleSectionHeader? TextSection => Sections.FirstOrDefault(s => s.Name.Equals(".text"));
    public SimpleSectionHeader? RdataSection => Sections.FirstOrDefault(s => s.Name.Equals(".rdata"));
    public SimpleSectionHeader? DataSection => Sections.FirstOrDefault(s => s.Name.Equals(".data"));
}
```

Represents the parsed output of `PeHeaderParser.GetPeHeaders`.

| Member | Type | Description |
|--------|------|-------------|
| `ImageBase` | `ulong` | Preferred load address read from the PE optional header. |
| `Sections` | `SimpleSectionHeader[]` | Ordered section table. |
| `this[int index]` | `SimpleSectionHeader` | Indexer over `Sections`. |
| `TextSection` | `SimpleSectionHeader?` | First section whose name is `.text`, or `null`. |
| `RdataSection` | `SimpleSectionHeader?` | First section whose name is `.rdata`, or `null`. |
| `DataSection` | `SimpleSectionHeader?` | First section whose name is `.data`, or `null`. |

Example:

```csharp
var pe = PeHeaderParser.GetPeHeaders(path);
var text = pe.TextSection;
```

## SimpleSectionHeader

```csharp
public readonly record struct SimpleSectionHeader(
    string Name,
    uint VirtualSize,
    uint VirtualAddress,
    uint SizeOfRawData,
    uint PointerToRawData);
```

Lightweight representation of one PE section.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Section name such as `.text` or `.data`. |
| `VirtualSize` | `uint` | In-memory size of the section. |
| `VirtualAddress` | `uint` | RVA where the section begins. |
| `SizeOfRawData` | `uint` | On-disk byte length of the section. |
| `PointerToRawData` | `uint` | File offset where the section data begins. |

Example:

```csharp
var pe = PeHeaderParser.GetPeHeaders(path);

foreach (var section in pe.Sections)
{
    Console.WriteLine($"{section.Name}: RVA=0x{section.VirtualAddress:X} Raw=0x{section.PointerToRawData:X}");
}
```

## Notes

These types are plain records, not service objects. They do not hold file handles or lazily load bytes for you. That keeps them easy to pass between parsing, slicing, and scanning code.

## Related APIs

- `/docs/api-reference/pe-header-parser`
- `/docs/pe-metadata`
