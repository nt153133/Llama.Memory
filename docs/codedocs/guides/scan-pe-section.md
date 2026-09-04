---
title: "Scan a PE Section"
description: "Scan the .text section of a PE file and resolve matches relative to the section RVA."
---

This guide shows the default file-backed workflow for `Llama.Memory`: parse the PE headers, isolate the `.text` section, scan only that section, and return results relative to the section's `VirtualAddress`.

## Problem

You have a Windows executable on disk and want to search for a signature without scanning the entire file image or hard-coding section boundaries.

## Solution

Use `PeHeaderParser` to discover the `.text` section, slice the raw file bytes using `PointerToRawData` and `SizeOfRawData`, then create a `PatternSearcher` with the section `VirtualAddress` as `imageBase`.

<Steps>
<Step>
### Parse the PE headers

```csharp
using Llama.Memory;

var exePath = "ffxiv_dx11.exe";
var pe = PeHeaderParser.GetPeHeaders(exePath);
var text = pe.TextSection ?? throw new InvalidDataException("The PE file does not contain a .text section.");
```
</Step>
<Step>
### Slice the raw file buffer to the section

```csharp
var fileBytes = File.ReadAllBytes(exePath);
var textBytes = fileBytes.AsMemory(
    checked((int)text.PointerToRawData),
    checked((int)text.SizeOfRawData));
```
</Step>
<Step>
### Scan with a section-relative image base

```csharp
var searcher = new PatternSearcher(textBytes, new IntPtr(text.VirtualAddress));
var result = searcher.Search(
    "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8B F0 48 85 C0 74 ? 48 83 38 Add 3 TraceRelative");

Console.WriteLine(result == IntPtr.Zero
    ? "Pattern not found"
    : $"Match found at RVA 0x{result.ToInt64():X}");
```
</Step>
</Steps>

## Complete Example

```csharp
using Llama.Memory;

var exePath = "ffxiv_dx11.exe";
var pattern = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8B F0 48 85 C0 74 ? 48 83 38 Add 3 TraceRelative";

var pe = PeHeaderParser.GetPeHeaders(exePath);
var text = pe.TextSection ?? throw new InvalidDataException("The PE file does not contain a .text section.");

var fileBytes = File.ReadAllBytes(exePath);
var textBytes = fileBytes.AsMemory(
    checked((int)text.PointerToRawData),
    checked((int)text.SizeOfRawData));

var searcher = new PatternSearcher(textBytes, new IntPtr(text.VirtualAddress));
var result = searcher.Search(pattern);

if (result == IntPtr.Zero)
{
    Console.WriteLine("Pattern not found.");
    return;
}

Console.WriteLine($"Resolved result: 0x{result.ToInt64():X}");
```

## Why This Pattern Works

The key detail is the `imageBase` argument. `PatternSearcher` adds that value after the raw match is found and any post-match commands finish. Passing `text.VirtualAddress` means the final result is expressed in the same coordinate system as the PE section's RVA, which is usually what you want for reverse-engineering notes and follow-up file-offset conversion.

If you instead pass `IntPtr.Zero`, the returned value becomes a raw offset within `textBytes`. That can be useful for some tools, but it is not the convention used by the project README or by `FfxivVersionChecker`.

## Variations

" "Many patterns"]}>
<Tab value="First hit">
```csharp
var result = searcher.Search(pattern);
```
</Tab>
<Tab value="All hits">
```csharp
var results = searcher.SearchMany(pattern);
```
</Tab>
<Tab value="Many patterns">
```csharp
var results = searcher.Search(new[]
{
    "48 8D 0D ? ? ? ? Add 3 TraceRelative",
    "E8 ? ? ? ? TraceCall"
});
```
</Tab>
</Tabs>

## Verification Tip

If a result looks wrong, convert it back to a file offset with `PeHeaderParser.RvaToFileOffset` and inspect the bytes around it. That is the fastest way to confirm whether the signature or the image-base choice is the problem.
