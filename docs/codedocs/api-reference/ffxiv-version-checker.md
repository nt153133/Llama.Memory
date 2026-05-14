---
title: "FfxivVersionChecker"
description: "Reference for the helper that extracts FFXIV version markers from the game executable."
---

Source file: `Llama.Memory/FfxivVersionChecker.cs`

Import path:

```csharp
using Llama.Memory;
```

## Signature

```csharp
public static class FfxivVersionChecker
```

## Methods

### GetVersion

```csharp
public static (string Version, string Date) GetVersion(FileInfo ffxivExe)
```

Reads a small buffer from the `.data` section and parses the first `rev{number}_{date}_` marker.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `ffxivExe` | `FileInfo` | — | The `ffxiv_dx11.exe` file to inspect. |

Return type: `(string Version, string Date)`

Example:

```csharp
var version = FfxivVersionChecker.GetVersion(new FileInfo("ffxiv_dx11.exe"));
Console.WriteLine(version.Version);
```

### GetVersionPattern

```csharp
public static (string Version, string Date) GetVersionPattern(FileInfo ffxivExe)
```

Scans the `.text` section for a pointer to the version data, then reads the candidate bytes from the file.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `ffxivExe` | `FileInfo` | — | The `ffxiv_dx11.exe` file to inspect. |

Return type: `(string Version, string Date)`

Example:

```csharp
var version = FfxivVersionChecker.GetVersionPattern(new FileInfo("ffxiv_dx11.exe"));
Console.WriteLine($"{version.Version} {version.Date}");
```

## Combined Usage

```csharp
var exe = new FileInfo("ffxiv_dx11.exe");
var direct = FfxivVersionChecker.GetVersion(exe);
var pattern = FfxivVersionChecker.GetVersionPattern(exe);

Console.WriteLine($"Direct={direct.Version}/{direct.Date}");
Console.WriteLine($"Pattern={pattern.Version}/{pattern.Date}");
```

## Method Behavior

`GetVersion` is the simpler path. It parses the PE headers, assumes the data you need is near the start of the `.data` section, and reads a fixed-size stack buffer using `RandomAccess.Read`. That keeps allocations very low and is the method to reach for if you trust the binary layout.

`GetVersionPattern` performs more work but models a more typical reverse-engineering flow. It reads the `.text` section into a rented buffer, scans for a pointer-bearing instruction using `PatternSearcher`, derives a file offset from the resolved result, and then reads the candidate version bytes from disk. This method is useful as a real example of how the rest of the library composes in application code.

## Example: detect disagreement between the two strategies

```csharp
var exe = new FileInfo("ffxiv_dx11.exe");
var direct = FfxivVersionChecker.GetVersion(exe);
var viaPattern = FfxivVersionChecker.GetVersionPattern(exe);

if (direct == ("0", "0") || viaPattern == ("0", "0"))
{
    Console.WriteLine("At least one lookup failed.");
}
else if (direct != viaPattern)
{
    Console.WriteLine("The binary layout may have changed.");
}
```

## Notes

- Both methods are synchronous.
- Both methods throw if the file is missing.
- The implementation currently assumes specific section positions for `.text` and `.data`, so it is intentionally specialized rather than generic.

## Related APIs

- `/docs/guides/extract-ffxiv-version`
- `/docs/api-reference/pattern-searcher`
- `/docs/api-reference/pe-header-parser`
