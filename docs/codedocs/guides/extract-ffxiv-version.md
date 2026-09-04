---
title: "Extract an FFXIV Version Marker"
description: "Use the built-in helper to read Final Fantasy XIV version metadata directly or through a pattern-guided lookup."
---

`FfxivVersionChecker` is the one application-specific helper in the library. Even if you do not care about FFXIV, it is a useful guide because it demonstrates two different file-inspection strategies built from the same low-level components.

## Problem

You want to extract the embedded version marker from `ffxiv_dx11.exe` with as little extra parsing work as possible.

## Solution

Choose between the two built-in methods:

- `GetVersion(FileInfo ffxivExe)` reads a fixed chunk from the `.data` section and looks for `rev{number}_{date}_`.
- `GetVersionPattern(FileInfo ffxivExe)` scans the `.text` section for a pointer to the version data, then seeks to the corresponding location in `.data`.

<Steps>
<Step>
### Create a FileInfo for the executable

```csharp
using Llama.Memory;

var exe = new FileInfo("ffxiv_dx11.exe");
```
</Step>
<Step>
### Read the direct version marker

```csharp
var direct = FfxivVersionChecker.GetVersion(exe);
Console.WriteLine($"Direct: rev{direct.Version}_{direct.Date}_");
```
</Step>
<Step>
### Compare it with the pattern-driven lookup

```csharp
var viaPattern = FfxivVersionChecker.GetVersionPattern(exe);
Console.WriteLine($"Pattern: rev{viaPattern.Version}_{viaPattern.Date}_");
```
</Step>
</Steps>

## Complete Example

```csharp
using Llama.Memory;

var exe = new FileInfo("ffxiv_dx11.exe");

var direct = FfxivVersionChecker.GetVersion(exe);
var viaPattern = FfxivVersionChecker.GetVersionPattern(exe);

Console.WriteLine($"Direct  => Version={direct.Version} Date={direct.Date}");
Console.WriteLine($"Pattern => Version={viaPattern.Version} Date={viaPattern.Date}");

if (direct != viaPattern)
{
    Console.WriteLine("Results differ; inspect section assumptions before trusting the output.");
}
```

## How the Two Methods Differ

`GetVersion` is the lower-overhead path. It parses the PE headers, assumes the third section is `.data`, reads `0x50` bytes from `PointerToRawData`, and parses the first `rev..._..._` marker found in that buffer (`Llama.Memory/FfxivVersionChecker.cs`, lines 40-65 and 117-160). The method uses `File.OpenHandle` and `RandomAccess.Read` to read directly into a stack-allocated buffer.

`GetVersionPattern` is more dynamic. It parses the PE headers, reads the `.text` section into a rented array, scans with the pattern `48 8D 3D ? ? ? ? 74 ? Add 3 TraceRelative Add 10`, converts the result back toward the `.data` section, and then reads the candidate version bytes (`FfxivVersionChecker.cs`, lines 68-115). This is a good reference for combining PE metadata, scanning, and offset translation in one workflow.

## When to Use Which

- Use `GetVersion` when you trust the target binary layout and want the simpler, cheaper path.
- Use `GetVersionPattern` when you want a pattern-based lookup that more closely models how you would discover the version location in a changing build.

The current implementation still assumes section positions by index, so neither method is fully generic. The point of the helper is targeted convenience, not a universal PE metadata strategy.

## Verification Tip

If `GetVersionPattern` returns `("0", "0")`, log the raw search result first. A zero result often means the signature is no longer valid for the current binary, not that the downstream parsing logic is broken.
