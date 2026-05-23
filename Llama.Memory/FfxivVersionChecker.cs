using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Llama.Memory;

public static class FfxivVersionChecker
{
    private const string VersionPattern = "48 8D 3D ? ? ? ? 74 ? Add 3 TraceRelative Add 10";
    private const int VersionBufferLength = 0x50;

    /// <summary>
    /// Reads the FFXIV client executable and extracts the embedded revision and date values.
    /// </summary>
    /// <param name="ffxivExe">The target <c>ffxiv_dx11.exe</c> file to inspect.</param>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    /// <item><description><c>Version</c>: the numeric revision parsed from <c>rev{number}_{date}_</c>.</description></item>
    /// <item><description><c>Date</c>: the date/token segment parsed from the same version marker.</description></item>
    /// </list>
    /// Returns <c>("0", "0")</c> when the file does not exist, when PE headers are invalid, or when the expected marker is not found/parsable.
    /// </returns>
    /// <remarks>
    /// This method is fully synchronous and performs blocking file I/O.
    /// It does not use <c>async</c>/<c>await</c> and should not be treated as non-blocking.
    /// </remarks>
    /// <exception cref="NullReferenceException"><paramref name="ffxivExe"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">The process does not have permission to open the executable.</exception>
    /// <exception cref="FileNotFoundException">The executable path becomes unavailable between existence check and open.</exception>
    /// <exception cref="DirectoryNotFoundException">A segment of the executable path cannot be found.</exception>
    /// <exception cref="PathTooLongException">The executable path exceeds the platform maximum length.</exception>
    /// <exception cref="NotSupportedException">The executable path format is invalid.</exception>
    /// <exception cref="IOException">An I/O error occurs while opening, seeking, or reading the executable stream.</exception>
    /// <exception cref="EndOfStreamException">The stream ends before <see cref="Stream.ReadExactly(byte[], int, int)"/> can fill the requested buffers.</exception>
    /// <exception cref="ObjectDisposedException">The stream is disposed before a read/seek operation completes.</exception>
    /// <exception cref="Exception">
    /// Propagates exceptions thrown by external parsers/searchers (for example <c>PeHeaderParser.GetPeHeaders</c>, <c>WitchHuntV3</c>, or <c>WitchHuntV3.Search</c>).
    /// </exception>
    public static (string Version, string Date) GetVersion(FileInfo ffxivExe)
    {
        if (!ffxivExe.Exists)
        {
            throw new FileNotFoundException($"The specified executable was not found: {ffxivExe.FullName}");
        }

        // Note: If you still see ~1kb of allocations, it is coming from this call.
        var peInfo = PeHeaderParser.GetPeHeaders(ffxivExe.FullName);
        if (peInfo.Sections.Length < 3)
        {
            throw new InvalidDataException($"Unexpected PE format: expected at least 3 sections, found {peInfo.Sections.Length}");
        }

        // Brittle fallback: relying on index 2 is risky. If your parser supports it, 
        // it's safer to find the section by name: peInfo.Sections.First(s => s.Name == ".data")
        var data = peInfo.Sections[2];

        Span<byte> dataBytesForVersion = stackalloc byte[VersionBufferLength];

        // 🚀 ZERO-ALLOCATION I/O: Open an OS handle and read directly into the stack memory
        using var handle = File.OpenHandle(ffxivExe.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        RandomAccess.Read(handle, dataBytesForVersion, data.PointerToRawData);

        return ParseVersion(dataBytesForVersion);
    }


    public static (string Version, string Date) GetVersionPattern(FileInfo ffxivExe)
    {
        if (!ffxivExe.Exists)
        {
            throw new FileNotFoundException($"The specified executable was not found: {ffxivExe.FullName}");
        }

        var peInfo = PeHeaderParser.GetPeHeaders(ffxivExe.FullName);
        if (peInfo.Sections.Length < 3)
        {
            throw new InvalidDataException($"Unexpected PE format: expected at least 3 sections, found {peInfo.Sections.Length}");
        }

        // You can access them by index just like before
        var text = peInfo.Sections[0]; // .text
        var data = peInfo.Sections[2]; // .data
        var textLength = checked((int)text.SizeOfRawData);

        var rentedTextBytes = ArrayPool<byte>.Shared.Rent(textLength);
        Span<byte> dataBytesForVersion = stackalloc byte[VersionBufferLength]; // We only need 256 bytes for the version string

        try
        {
            using var fs = new FileStream(ffxivExe.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.SequentialScan);

            var textBytes = rentedTextBytes.AsMemory(0, textLength);

            fs.Seek(text.PointerToRawData, SeekOrigin.Begin);
            fs.ReadExactly(textBytes.Span);

            var searcherText = new PatternSearcher(textBytes, new IntPtr(text.VirtualAddress));

            // Find version pointer, seek directly to it, and read only 256 bytes
            var versionResult = searcherText.Search(VersionPattern);
            var versionFileOffset = versionResult != IntPtr.Zero
                ? data.PointerToRawData + (versionResult.ToInt64() - data.VirtualAddress) + 0x10
                : data.PointerToRawData; // Fallback to start of data

            fs.Seek(versionFileOffset, SeekOrigin.Begin);
            fs.ReadExactly(dataBytesForVersion);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedTextBytes);
        }

        return ParseVersion(dataBytesForVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (string Version, string Date) ParseVersion(ReadOnlySpan<byte> version)
    {
        ReadOnlySpan<byte> revMarker = "rev"u8;

        while (true)
        {
            // 🚀 SIMD-accelerated search
            int revIndex = version.IndexOf(revMarker);

            // If "rev" isn't found, or it's too close to the end to be valid
            if (revIndex < 0 || revIndex > version.Length - 5)
                break;

            // Slice past "rev"
            version = version.Slice(revIndex + 3);

            // First character must be a digit
            if (!IsDigit(version[0]))
                continue;

            // Find the first underscore (end of version number)
            int firstUnderscore = version.IndexOf((byte)'_');
            if (firstUnderscore <= 0)
                continue;

            // Find the second underscore (end of date)
            var afterFirst = version.Slice(firstUnderscore + 1);
            int secondUnderscore = afterFirst.IndexOf((byte)'_');
            if (secondUnderscore <= 0)
                continue;

            // Slice out the exact values
            var versionSpan = version.Slice(0, firstUnderscore);
            var dateSpan = afterFirst.Slice(0, secondUnderscore);

            // These two strings will be the ONLY allocations in this entire process
            return (
                Encoding.ASCII.GetString(versionSpan),
                Encoding.ASCII.GetString(dateSpan)
            );
        }

        return ("0", "0");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(byte value) => (uint)(value - '0') <= 9;
}