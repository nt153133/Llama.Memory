# Llama.Memory

[![NuGet Version](https://img.shields.io/nuget/v/Llama.Memory.svg)](https://www.nuget.org/packages/Llama.Memory)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Llama.Memory.svg)](https://www.nuget.org/packages/Llama.Memory)

`Llama.Memory` is a lightweight byte-pattern scanning and PE header utility library for .NET.

It provides GreyMagic-style pattern strings with wildcard bytes and post-match cursor commands, plus a dependency-free PE header parser for locating image sections in unmanaged memory snapshots or raw executable files.

---

## Features

* **Pattern searching:** Scans contiguous byte buffers using `Span<T>`, `SearchValues<T>`, and optimized anchor selection.
* **Multi-pattern scanning:** Resolves the first hit for each pattern, or all hits for each pattern, in one scanner call.
* **PE header parsing:** Reads DOS/NT headers, image base, and section data without third-party PE parsing dependencies.
* **Cursor commands in patterns:** Append address adjustment, reads, and relative tracing commands after the byte pattern.
* **Unmanaged memory support:** `UnmanagedMemoryManager<T>` can wrap raw unmanaged memory as `Memory<T>` for APIs that consume `Memory<T>`.

## Pattern Format

Patterns are space-separated tokens:

```text
{hex bytes and wildcards} {optional commands}
```

Wildcard bytes can be written as `??` or `?`. Half-byte wildcards such as `4?` and `?F` are also accepted.

Commands are executed sequentially after the initial byte pattern is matched:

* **`Add #`** - Shifts the result pointer forward by `#` bytes. Decimal and hex operands are supported.
* **`Sub #`** - Shifts the result pointer backward by `#` bytes. Decimal and hex operands are supported.
* **`Read8`** - Reads one byte from the current result offset.
* **`Read16`** - Reads a little-endian `Int16` from the current result offset.
* **`Read32`** - Reads a little-endian `Int32` from the current result offset.
* **`Read64`** - Reads a little-endian `Int64` from the current result offset.
* **`TraceRelative`** - Reads a 32-bit relative displacement at the current result offset and returns `current + 4 + displacement + ImageBase`.
* **`TraceCall`** - Resolves an `E8 rel32`-style call by reading the 32-bit displacement at `current + 1` and returning `current + 5 + displacement + ImageBase`.

Example:

```text
48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8B F0 48 85 C0 74 ? 48 83 38 Add 3 TraceRelative
```

## Usage Examples

### Offline Analysis From a File

```csharp
using Llama.Memory;

var peHeader = PeHeaderParser.GetPeHeaders(@"ffxiv_dx11.exe");
var textSection = peHeader.TextSection
    ?? throw new InvalidDataException("The PE file does not contain a .text section.");

var fileBytes = File.ReadAllBytes(@"ffxiv_dx11.exe");
var sectionData = fileBytes.AsMemory(
    checked((int)textSection.PointerToRawData),
    checked((int)textSection.SizeOfRawData));

var scanner = new PatternSearcher(
    sectionData,
    new IntPtr(textSection.VirtualAddress));

var result = scanner.Search(
    "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8B F0 48 85 C0 74 ? 48 83 38 Add 3 TraceRelative");

Console.WriteLine($"Match found at: 0x{result.ToInt64():X}");
```

The `ImageBase` constructor argument is added to raw match offsets when results are returned. For PE section scans, pass the section `VirtualAddress` if you want returned values expressed as RVAs for that section.

### Live Memory Snapshot

For an external process, read the target memory range into a byte buffer first, then scan that buffer:

```csharp
using System.Diagnostics;
using Llama.Memory;

var process = Process.GetCurrentProcess();
var module = process.MainModule
    ?? throw new InvalidOperationException("The process has no main module.");

var memorySize = module.ModuleMemorySize;
var baseAddress = module.BaseAddress;
var bytes = new byte[memorySize];

// Fill 'bytes' with the target range, for example via ReadProcessMemory.

var scanner = new PatternSearcher(bytes, baseAddress);

var result = scanner.Search(
    "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8B F0 48 85 C0 74 ? 48 83 38 Add 3 TraceRelative");
```

For an internal or injected scenario with an unmanaged pointer, wrap the pointer in `UnmanagedMemoryManager<byte>` and pass its `Memory` to `PatternSearcher`.

## API Reference

### `PatternSearcher`

Constructors:

* **`PatternSearcher(byte[] assemblyData, IntPtr imageBase)`**
* **`PatternSearcher(Span<byte> assemblyData, IntPtr imageBase)`**
* **`PatternSearcher(ref ReadOnlySpan<byte> assemblyData, IntPtr imageBase)`**
* **`PatternSearcher(ReadOnlySpan<byte> assemblyData, IntPtr imageBase)`**
* **`PatternSearcher(Memory<byte> assemblyData, IntPtr imageBase)`**

Search methods:

* **`IntPtr Search(string pattern)`** - Returns the first transformed match, or `IntPtr.Zero`.
* **`IntPtr Search(string pattern, IntPtr start, int maxSearchLength)`** - Returns the first transformed match in a raw buffer sub-range. `start` is a zero-based buffer offset, not an address with `ImageBase` already applied.
* **`IntPtr[] SearchMany(string pattern)`** - Returns every transformed match for one pattern.
* **`IntPtr[] Search(string[] patterns)`** - Returns the first transformed match for each pattern. Results are index-aligned with the input array.
* **`IntPtr[][] SearchMany(string[] patterns)`** - Returns every transformed match for each pattern. Results are index-aligned with the input array.
* **`ReadOnlySpan<byte> GetSlice(int start, int length)`** - Returns a read-only span over a raw buffer sub-range.

### `PeHeaderParser`

* **`PeHeaderInfo GetPeHeaders(string filePath)`** - Parses the PE headers and returns the image base plus all section headers.
* **`uint RvaToFileOffset(uint rva, SimpleSectionHeader[] sections)`** - Converts an RVA to a file offset using the parsed section table.

### `PeHeaderInfo`

`PeHeaderInfo` contains:

* **`ulong ImageBase`**
* **`SimpleSectionHeader[] Sections`**
* **`SimpleSectionHeader this[int index]`**
* **`SimpleSectionHeader? TextSection`**
* **`SimpleSectionHeader? RdataSection`**
* **`SimpleSectionHeader? DataSection`**

### `SimpleSectionHeader`

Represents a PE section with:

* **`Name`**
* **`VirtualSize`**
* **`VirtualAddress`**
* **`SizeOfRawData`**
* **`PointerToRawData`**

### `FfxivVersionChecker`

* **`(string Version, string Date) GetVersion(FileInfo ffxivExe)`** - Reads the expected version marker from the data section.
* **`(string Version, string Date) GetVersionPattern(FileInfo ffxivExe)`** - Locates the version marker via pattern scanning, then reads and parses it.

### `UnmanagedMemoryManager<T>`

Wraps an unmanaged pointer and length as a `Memory<T>` source. The caller owns the lifetime of the unmanaged memory.
