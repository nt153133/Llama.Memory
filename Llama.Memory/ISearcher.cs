namespace WitchHunt
{
    using System;

    /// <summary>
    /// Defines the contract for a byte-pattern scanner that operates over a contiguous region of
    /// memory (e.g. the <c>.text</c> section of a loaded PE file).
    /// </summary>
    /// <remarks>
    /// <para>
    /// All search methods are <b>synchronous</b> and execute entirely on the calling thread.
    /// They do not start background work, return <see cref="System.Threading.Tasks.Task"/>s, or
    /// raise events. If you need to keep a UI or async pipeline responsive, wrap calls in
    /// <c>await Task.Run(() =&gt; searcher.Search(pattern))</c>.
    /// </para>
    /// <para>
    /// <b>Pattern syntax</b> — patterns are space-separated hex tokens where each token is either:
    /// <list type="bullet">
    ///   <item><description>
    ///     A two-digit hexadecimal byte value (case-insensitive), e.g. <c>48</c>, <c>8B</c>, <c>ff</c>.
    ///   </description></item>
    ///   <item><description>
    ///     A wildcard token (<c>??</c> or <c>?</c>) that matches any single byte.
    ///   </description></item>
    /// </list>
    /// Optionally, one or more <b>post-match commands</b> may be appended after the hex bytes
    /// to transform the raw match address before it is returned (see individual method docs for
    /// the full command reference).
    /// </para>
    /// <para>
    /// <b>Thread safety</b> — implementations are not required to be thread-safe unless their
    /// documentation states otherwise. Do not call search methods concurrently on the same
    /// instance without external synchronisation.
    /// </para>
    /// </remarks>
    public interface ISearcher
    {
        /// <summary>
        /// Gets the virtual base address (image base) that is added to every raw offset when
        /// constructing the returned <see cref="IntPtr"/> values.
        /// </summary>
        /// <value>
        /// The RVA origin of the scanned region. Typically set to the
        /// <c>VirtualAddress</c> of the PE section that was passed to the constructor, so that
        /// returned pointers represent absolute RVAs rather than zero-based buffer offsets.
        /// A value of <see cref="IntPtr.Zero"/> means addresses are returned as raw buffer offsets.
        /// </value>
        public IntPtr ImageBase { get; }

        /// <summary>
        /// Scans the entire memory region for the first occurrence of <paramref name="pattern"/>
        /// and returns a transformed address according to any post-match commands embedded in the
        /// pattern string.
        /// </summary>
        /// <param name="pattern">
        /// A space-separated hex pattern string, optionally followed by one or more post-match
        /// commands. Supported commands (appended after the hex bytes, in order):
        /// <list type="table">
        ///   <listheader><term>Command</term><description>Effect</description></listheader>
        ///   <item>
        ///     <term><c>Add &lt;n&gt;</c></term>
        ///     <description>
        ///       Advances the result pointer by <c>n</c> bytes from the start of the match.
        ///       <c>Add 1</c> points to the second byte, <c>Add 3</c> to the fourth, etc.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <term><c>Sub &lt;n&gt;</c></term>
        ///     <description>
        ///       Moves the result pointer <c>n</c> bytes <em>before</em> the start of the match.
        ///       <c>Sub 1</c> points one byte before the match, <c>Sub 2</c> two bytes before, etc.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <term><c>Read8</c></term>
        ///     <description>Dereferences the result pointer and returns the single byte value found there.</description>
        ///   </item>
        ///   <item>
        ///     <term><c>Read16</c></term>
        ///     <description>Dereferences the result pointer and returns the little-endian <c>Int16</c> value found there.</description>
        ///   </item>
        ///   <item>
        ///     <term><c>Read32</c></term>
        ///     <description>Dereferences the result pointer and returns the little-endian <c>Int32</c> value found there.</description>
        ///   </item>
        ///   <item>
        ///     <term><c>Read64</c></term>
        ///     <description>Dereferences the result pointer and returns the little-endian <c>Int64</c> value found there.</description>
        ///   </item>
        ///   <item>
        ///     <term><c>TraceRelative</c></term>
        ///     <description>
        ///       Resolves a 32-bit RIP-relative offset: reads the <c>Int32</c> at the result pointer,
        ///       then computes <c>pointer + 4 + offset</c> to follow <c>lea</c>/<c>mov</c> style
        ///       relative references.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <term><c>TraceCall</c></term>
        ///     <description>
        ///       Equivalent to <c>Add 1 TraceRelative</c>. Intended for <c>E8 ?? ?? ?? ??</c> call
        ///       patterns. Note: may not resolve correctly in all cases — prefer explicit
        ///       <c>Add</c>/<c>TraceRelative</c> when precision matters.
        ///     </description>
        ///   </item>
        /// </list>
        /// Example: <c>"48 8D 05 ?? ?? ?? ?? Add 3 TraceRelative"</c>
        /// </param>
        /// <returns>
        /// An <see cref="IntPtr"/> representing the transformed address of the first match,
        /// with <see cref="ImageBase"/> applied. Returns <see cref="IntPtr.Zero"/> if no match
        /// is found.
        /// </returns>
        /// <remarks>
        /// This overload always scans from the beginning of the region to the end.
        /// Use the <see cref="Search(string, IntPtr, int)"/> overload to restrict the search range.
        /// The method is <b>synchronous</b> — it blocks the calling thread for the full scan
        /// duration, which can be tens of milliseconds on large (40+ MB) sections.
        /// </remarks>
        public IntPtr Search(string pattern);

        /// <summary>
        /// Scans a sub-range of the memory region for the first occurrence of
        /// <paramref name="pattern"/>, starting at <paramref name="start"/> and reading at most
        /// <paramref name="maxSearchLength"/> bytes.
        /// </summary>
        /// <param name="pattern">
        /// A space-separated hex pattern string with optional post-match commands.
        /// See <see cref="Search(string)"/> for the full command reference.
        /// </param>
        /// <param name="start">
        /// The absolute RVA (or buffer offset when <see cref="ImageBase"/> is zero) at which to
        /// begin scanning. Must fall within the bounds of the scanned region. Passing a value
        /// outside the region results in implementation-defined behaviour (typically no match).
        /// </param>
        /// <param name="maxSearchLength">
        /// The maximum number of bytes to inspect starting from <paramref name="start"/>.
        /// The scan stops at whichever comes first: <c>start + maxSearchLength</c> or the end of
        /// the region. Pass <c>int.MaxValue</c> to scan to the end of the region.
        /// </param>
        /// <returns>
        /// An <see cref="IntPtr"/> representing the transformed address of the first match within
        /// the specified sub-range, with <see cref="ImageBase"/> applied.
        /// Returns <see cref="IntPtr.Zero"/> if no match is found within the range.
        /// </returns>
        /// <remarks>
        /// This overload is useful for incremental or windowed scanning — e.g. finding the
        /// <em>second</em> occurrence of a pattern by passing the address of the first hit
        /// (plus one) as <paramref name="start"/>.
        /// The method is <b>synchronous</b>; it does not return until the sub-range has been
        /// fully scanned or a match is found.
        /// </remarks>
        public IntPtr Search(string pattern, IntPtr start, int maxSearchLength);

        /// <summary>
        /// Scans the entire memory region and returns the address of <em>every</em> occurrence
        /// of <paramref name="pattern"/>.
        /// </summary>
        /// <param name="pattern">
        /// A space-separated hex pattern string with optional post-match commands.
        /// Post-match commands (e.g. <c>Add</c>, <c>TraceRelative</c>) are applied independently
        /// to each match before it is added to the result array.
        /// See <see cref="Search(string)"/> for the full command reference.
        /// </param>
        /// <returns>
        /// A non-null <see cref="IntPtr"/> array containing the transformed address of every match
        /// found, in ascending address order, with <see cref="ImageBase"/> applied to each entry.
        /// Returns an empty array (length 0) if no matches are found — never returns
        /// <see langword="null"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Because this method must traverse the entire region (worst-case: no early exit), it is
        /// significantly slower than <see cref="Search(string)"/> on large sections. Budget
        /// accordingly on 40+ MB regions.
        /// </para>
        /// <para>
        /// The method is <b>synchronous</b>. To avoid blocking a UI thread, wrap in
        /// <c>await Task.Run(() =&gt; searcher.SearchMany(pattern))</c>.
        /// </para>
        /// </remarks>
        public IntPtr[] SearchMany(string pattern);

        /// <summary>
        /// Scans the memory region for the <em>first</em> occurrence of each pattern in
        /// <paramref name="patterns"/> and returns the results in a single pass.
        /// </summary>
        /// <param name="patterns">
        /// A non-null array of pattern strings. Each element must follow the same syntax as the
        /// <paramref name="pattern"/> parameter of <see cref="Search(string)"/>.
        /// Duplicate entries are permitted and are treated as independent searches — each will
        /// produce its own result slot.
        /// An empty array is valid and produces an empty result array.
        /// </param>
        /// <returns>
        /// A non-null <see cref="IntPtr"/> array of the same length as <paramref name="patterns"/>.
        /// <c>result[i]</c> is the transformed address of the first match for <c>patterns[i]</c>,
        /// or <see cref="IntPtr.Zero"/> if <c>patterns[i]</c> produced no match. The index
        /// alignment is guaranteed even when duplicates are present.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Implementations are free to run the searches sequentially or in parallel; callers
        /// must not rely on a specific execution order. The <em>results</em> are always
        /// index-aligned to the input regardless of execution order.
        /// </para>
        /// <para>
        /// This method is <b>synchronous</b> — it blocks until all patterns have been resolved.
        /// On large memory regions with many patterns, consider wrapping in
        /// <c>await Task.Run(() =&gt; searcher.SearchManyPatterns(patterns))</c>.
        /// </para>
        /// <para>
        /// An invalid pattern string in <paramref name="patterns"/> will cause an exception to be
        /// thrown; the specific exception type is implementation-defined.
        /// </para>
        /// </remarks>
        public IntPtr[] SearchManyPatterns(string[] patterns);

        /// <summary>
        /// Scans the memory region for <em>all</em> occurrences of each pattern in
        /// <paramref name="patterns"/> and returns a jagged array of hit lists.
        /// </summary>
        /// <param name="patterns">
        /// A non-null array of pattern strings. Each element must follow the same syntax as the
        /// <paramref name="pattern"/> parameter of <see cref="Search(string)"/>.
        /// Duplicate entries are permitted and produce independent hit lists.
        /// An empty array is valid and produces an empty jagged result array.
        /// </param>
        /// <returns>
        /// A non-null jagged <see cref="IntPtr"/> array of the same length as
        /// <paramref name="patterns"/>.
        /// <c>result[i]</c> is a non-null array of every transformed address that matched
        /// <c>patterns[i]</c>, in ascending address order, with <see cref="ImageBase"/> applied.
        /// If <c>patterns[i]</c> produced no matches, <c>result[i]</c> is an empty array
        /// (never <see langword="null"/>). Index alignment is guaranteed even when duplicates
        /// are present.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is the most expensive overload: for each pattern the entire region must be
        /// scanned with no early exit. On large (40+ MB) sections with many patterns, execution
        /// time scales roughly as <c>O(patterns.Length × regionSize)</c> in the worst case.
        /// </para>
        /// <para>
        /// Implementations are free to parallelise across patterns. Callers must not depend on
        /// a particular intra-pattern or inter-pattern ordering of execution.
        /// </para>
        /// <para>
        /// This method is <b>synchronous</b>. Wrap in
        /// <c>await Task.Run(() =&gt; searcher.SearchAllPatterns(patterns))</c> to avoid blocking
        /// an async or UI context.
        /// </para>
        /// <para>
        /// An invalid pattern string in <paramref name="patterns"/> will cause an exception to be
        /// thrown; the specific exception type is implementation-defined.
        /// </para>
        /// </remarks>
        public IntPtr[][] SearchAllPatterns(string[] patterns);

        /// <summary>
        /// Returns a read-only view of a contiguous sub-region of the underlying memory buffer
        /// without allocating a copy.
        /// </summary>
        /// <param name="start">
        /// The zero-based byte offset within the buffer at which the slice begins.
        /// Must be ≥ 0 and less than the total length of the buffer.
        /// </param>
        /// <param name="length">
        /// The number of bytes to include in the slice. Must be ≥ 0.
        /// <c>start + length</c> must not exceed the total length of the buffer.
        /// </param>
        /// <returns>
        /// A <see cref="ReadOnlySpan{T}"/> of <see cref="byte"/> that points directly into the
        /// underlying buffer — no heap allocation is made. The span is valid only for the lifetime
        /// of the <see cref="ISearcher"/> instance (or the underlying pinned memory).
        /// </returns>
        /// <remarks>
        /// <para>
        /// Because a <see cref="ReadOnlySpan{T}"/> is a stack-only (ref struct) type, it cannot
        /// be stored in a field, boxed, or passed across <c>async</c>/<c>await</c> boundaries.
        /// Consume the returned span synchronously within the same method frame.
        /// </para>
        /// <para>
        /// Passing out-of-range values for <paramref name="start"/> or <paramref name="length"/>
        /// will throw an <see cref="ArgumentOutOfRangeException"/> (implementation-defined).
        /// </para>
        /// </remarks>
        public ReadOnlySpan<byte> GetSlice(int start, int length);
    }
}