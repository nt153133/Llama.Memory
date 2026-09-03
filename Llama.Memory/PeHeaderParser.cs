using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
// ReSharper disable InconsistentNaming

namespace Llama.Memory;

/// <summary>
/// Holds the parsed PE header information extracted from a portable executable file.
/// </summary>
/// <param name="ImageBase">
/// The preferred load address of the image in virtual memory, as declared in the Optional Header.
/// For 64-bit (PE32+) executables this is a <see cref="ulong"/>; for 32-bit (PE32) executables
/// it is widened from a <see cref="uint"/>.
/// </param>
/// <param name="Sections">
/// An ordered list of every section found in the PE Section Table (e.g. <c>.text</c>, <c>.rdata</c>,
/// <c>.data</c>). The order matches the on-disk layout of the section headers.
/// </param>
public record PeHeaderInfo(
    ulong ImageBase,
    SimpleSectionHeader[] Sections)
{
    /// <summary>
    /// Gets the <see cref="SimpleSectionHeader"/> at the specified section index.
    /// </summary>
    /// <param name="index">The zero-based index of the section.</param>
    /// <returns>The section header at the specified index.</returns>
    public SimpleSectionHeader this[int index] => Sections[index];

    /// <summary>
    /// Gets the first section named <c>.text</c>, or <see langword="null"/> if not found.
    /// </summary>
    public SimpleSectionHeader? TextSection => Sections.FirstOrDefault(s => s.Name.Equals(".text"));

    /// <summary>
    /// Gets the first section named <c>.rdata</c>, or <see langword="null"/> if not found.
    /// </summary>
    public SimpleSectionHeader? RdataSection => Sections.FirstOrDefault(s => s.Name.Equals(".rdata"));

    /// <summary>
    /// Gets the first section named <c>.data</c>, or <see langword="null"/> if not found.
    /// </summary>
    public SimpleSectionHeader? DataSection => Sections.FirstOrDefault(s => s.Name.Equals(".data"));
}

/// <summary>
/// A lightweight representation of a single PE section header entry.
/// </summary>
/// <param name="Name">
/// The null-terminated ASCII name of the section, trimmed to at most 8 characters
/// (e.g. <c>".text"</c>, <c>".rdata"</c>).
/// </param>
/// <param name="VirtualSize">
/// The total size, in bytes, of the section when it is loaded into memory.
/// May be larger than <paramref name="SizeOfRawData"/> due to BSS-style zero-padding.
/// </param>
/// <param name="VirtualAddress">
/// The RVA (Relative Virtual Address) of the section — i.e. the offset from the
/// <see cref="PeHeaderInfo.ImageBase"/> at which the section begins in virtual memory.
/// </param>
/// <param name="SizeOfRawData">
/// The size, in bytes, of the initialised data for this section as it appears on disk,
/// aligned to the file alignment declared in the Optional Header.
/// </param>
/// <param name="PointerToRawData">
/// The file offset (in bytes from the start of the file) at which the raw section data begins.
/// Use this together with <paramref name="SizeOfRawData"/> to slice the correct region out of the
/// loaded file buffer.
/// </param>
public readonly record struct SimpleSectionHeader(
    string Name,
    uint VirtualSize,
    uint VirtualAddress,
    uint SizeOfRawData,
    uint PointerToRawData);

/// <summary>
/// Provides a minimal, dependency-free PE header parser that extracts the image base address
/// and the complete section table from a Portable Executable file.
/// </summary>
/// <remarks>
/// Only the fields required by <c>WitchHunt</c> pattern scanning are read; the full Optional
/// Header and Data Directory entries are intentionally skipped for performance.
/// This parser supports both PE32 (32-bit) and PE32+ (64-bit) executables.
/// All I/O is performed synchronously using a <see cref="FileStream"/> opened in
/// <see cref="FileShare.ReadWrite"/> mode so the target process can keep the file locked.
/// </remarks>
public static class PeHeaderParser
{
    private const int HeaderReadSize = 4096; // 4KB is enough to cover DOS header, NT headers, Optional Header, and Section Table for typical PE files

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct IMAGE_FILE_HEADER
    {
        public ushort Machine;
        public ushort NumberOfSections;
        public uint TimeDateStamp;
        public uint PointerToSymbolTable;
        public uint NumberOfSymbols;
        public ushort SizeOfOptionalHeader;
        public ushort Characteristics;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct IMAGE_SECTION_HEADER
    {
        public fixed byte Name[8];
        public uint VirtualSize;
        public uint VirtualAddress;
        public uint SizeOfRawData;
        public uint PointerToRawData;
        public uint PointerToRelocations;
        public uint PointerToLinenumbers;
        public ushort NumberOfRelocations;
        public ushort NumberOfLinenumbers;
        public uint Characteristics;
    }

    /// <summary>
    /// Opens the specified PE file, parses its DOS header, NT headers, Optional Header,
    /// and Section Table, then returns a <see cref="PeHeaderInfo"/> containing the image base
    /// and all section descriptors.
    /// </summary>
    /// <param name="filePath">
    /// The absolute or relative path to the PE file to parse (e.g. <c>ffxiv_dx11.exe</c>).
    /// The file must be readable; it may still be open by another process because the stream
    /// is opened with <see cref="FileShare.ReadWrite"/>.
    /// </param>
    /// <returns>
    /// A <see cref="PeHeaderInfo"/> record containing:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="PeHeaderInfo.ImageBase"/> — the preferred load address declared in the
    ///     PE Optional Header (<c>0x140000000</c> for most 64-bit Windows executables).
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="PeHeaderInfo.Sections"/> — a <see cref="List{T}"/> of
    ///     <see cref="SimpleSectionHeader"/> records, one per section, in the order they appear
    ///     in the Section Table. Index 0 is typically the <c>.text</c> (code) section.
    ///   </description></item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown by <see cref="FileStream"/> if <paramref name="filePath"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown by <see cref="FileStream"/> if <paramref name="filePath"/> is an empty string,
    /// contains only white-space, or contains one or more invalid path characters.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown by <see cref="FileStream"/> when no file exists at <paramref name="filePath"/>.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown by <see cref="FileStream"/> if the caller does not have read permission to the file,
    /// or if the path refers to a directory rather than a file.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown if an I/O error occurs while reading the file — for example, if the file is
    /// truncated and a <see cref="BinaryReader"/> read call returns fewer bytes than expected,
    /// or if the underlying stream position cannot be set (e.g. on a non-seekable stream).
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the four bytes at the offset pointed to by the DOS <c>e_lfanew</c> field do
    /// not equal the expected PE signature (<c>0x00004550</c> / <c>"PE\0\0"</c>), indicating
    /// the file is not a valid Portable Executable.
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown by <see cref="BinaryReader"/> if the end of the file is reached before all
    /// expected header bytes have been read — for example, if the file is corrupt or truncated.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Synchronous:</b> This method performs all file I/O synchronously on the calling thread.
    /// If you call it from a UI or async context you should wrap it in
    /// <c>await Task.Run(() =&gt; PeHeaderParser.GetPeHeaders(path))</c> to avoid blocking.
    /// </para>
    /// <para>
    /// <b>Resource management:</b> The <see cref="FileStream"/> and <see cref="BinaryReader"/>
    /// are both disposed via <see langword="using"/> blocks, so the file handle is released even
    /// when an exception is thrown.
    /// </para>
    /// <para>
    /// <b>64-bit vs 32-bit:</b> The Optional Header <c>Magic</c> field is inspected to
    /// distinguish PE32+ (<c>0x020B</c>) from PE32 (<c>0x010B</c>). If the magic value is
    /// neither of these two values, <see cref="PeHeaderInfo.ImageBase"/> will be <c>0</c>.
    /// </para>
    /// </remarks>
    public static unsafe PeHeaderInfo GetPeHeaders(string filePath)
    {
        // FileOptions.SequentialScan hints the OS for optimal caching
        using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, // we do one big read
            FileOptions.SequentialScan);

        byte[] rented = ArrayPool<byte>.Shared.Rent(HeaderReadSize);
        try
        {
            int read = fs.Read(rented, 0, HeaderReadSize);
            var span = rented.AsSpan(0, read);

            // DOS header: e_magic at 0, e_lfanew at 0x3C
            if (MemoryMarshal.Read<ushort>(span) != 0x5A4D)
                throw new InvalidDataException("Invalid DOS signature.");

            int peOffset = MemoryMarshal.Read<int>(span.Slice(0x3C));

            // PE signature "PE\0\0" = 0x00004550
            if (MemoryMarshal.Read<uint>(span.Slice(peOffset, 4)) != 0x00004550)
                throw new InvalidDataException("Invalid PE signature.");

            int fileHeaderOffset = peOffset + 4;
            ref readonly var fileHeader = ref MemoryMarshal.AsRef<IMAGE_FILE_HEADER>(
                span.Slice(fileHeaderOffset));

            int optionalHeaderOffset = fileHeaderOffset + sizeof(IMAGE_FILE_HEADER);
            ushort magic = MemoryMarshal.Read<ushort>(span.Slice(optionalHeaderOffset));

            ulong imageBase = magic switch
            {
                0x020B => MemoryMarshal.Read<ulong>(span.Slice(optionalHeaderOffset + 24)),
                0x010B => MemoryMarshal.Read<uint>(span.Slice(optionalHeaderOffset + 28)),
                _ => 0
            };

            int sectionTableOffset = optionalHeaderOffset + fileHeader.SizeOfOptionalHeader;
            int sectionCount = fileHeader.NumberOfSections;

            // If headers exceed our buffer (rare but possible), grow
            int requiredEnd = sectionTableOffset + sectionCount * sizeof(IMAGE_SECTION_HEADER);
            if (requiredEnd > read)
            {
                // Grow: read more
                if (requiredEnd > rented.Length)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = ArrayPool<byte>.Shared.Rent(requiredEnd);
                }

                fs.Position = read;
                int more = fs.Read(rented, read, requiredEnd - read);
                span = rented.AsSpan(0, read + more);
            }

            var sections = new SimpleSectionHeader[sectionCount];
            var sectionTable = MemoryMarshal.Cast<byte, IMAGE_SECTION_HEADER>(
                span.Slice(sectionTableOffset, sectionCount * sizeof(IMAGE_SECTION_HEADER)));

            for (int i = 0; i < sectionCount; i++)
            {
                ref readonly var sh = ref sectionTable[i];
                string name;
                fixed (byte* namePtr = sh.Name)
                {
                    var nameSpan = new ReadOnlySpan<byte>(namePtr, 8);
                    name = GetSectionName(nameSpan);
                    //int nullIdx = nameSpan.IndexOf((byte)0);
                    // if (nullIdx >= 0) nameSpan = nameSpan.Slice(0, nullIdx);
                }

                sections[i] = new SimpleSectionHeader(
                    name, sh.VirtualSize, sh.VirtualAddress,
                    sh.SizeOfRawData, sh.PointerToRawData);
            }

            return new PeHeaderInfo(imageBase, sections);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Converts a Relative Virtual Address (RVA) to a raw file offset based on the provided section headers.
    /// </summary>
    /// <param name="rva">The relative virtual address to convert.</param>
    /// <param name="sections">The array of section headers to resolve the RVA against.</param>
    /// <returns>The file offset corresponding to the given RVA.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="rva"/> does not fall within any of the provided sections.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RvaToFileOffset(uint rva, SimpleSectionHeader[] sections)
    {
        // Linear scan is fine for typical <= 20 sections; branchless-friendly
        for (int i = 0; i < sections.Length; i++)
        {
            var s = sections[i];
            uint delta = rva - s.VirtualAddress;
            if (delta < s.VirtualSize)
                return delta + s.PointerToRawData;
        }

        throw new ArgumentOutOfRangeException(nameof(rva), "RVA does not lie within any parsed section.");
    }

    private static string GetSectionName(ReadOnlySpan<byte> nameSpan)
    {
        // Fast path for common names via 8-byte compare
        ulong nameBytes = MemoryMarshal.Read<ulong>(nameSpan);
        return nameBytes switch
        {
            0x0000000074786574UL => ".text", // ".text\0\0\0"
            0x0000000061746164UL => ".data",
            0x0000006174616472UL => ".rdata",
            0x0000637273722EUL => ".rsrc",
            0x00636F6C65722EUL => ".reloc",
            _ => Encoding.ASCII.GetString(nameSpan.TrimEnd((byte)0))
        };
    }
}