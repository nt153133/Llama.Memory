using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Llama.Memory;

public class PatternSearcher : ISearcher
{
    public readonly Memory<byte> Data;

    private const int ParallelMinDataSize = 256 * 1024;
    private const int ParallelTargetChunkSize = 4 * 1024 * 1024;

    private readonly object _cacheLock = new();

    private string[]? _cachedPatternsRef;
    private CompiledPattern[]? _cachedCompiled;
    private PatternBucketTable? _cachedTable;

    private int[]? _byteFrequency;
    private int[]? _pairFrequency;
    
    
    public PatternSearcher(byte[] assemblyData, IntPtr imageBase)
    {
        Data = assemblyData;
        ImageBase = imageBase;
    }

    public PatternSearcher(Span<byte> assemblyData, IntPtr imageBase)
    {
        Data = assemblyData.ToArray();
        ImageBase = imageBase;
    }

    public PatternSearcher(ref ReadOnlySpan<byte> assemblyData, IntPtr imageBase)
    {
        Data = assemblyData.ToArray();
        ImageBase = imageBase;
    }

    public PatternSearcher(ReadOnlySpan<byte> assemblyData, IntPtr imageBase)
    {
        Data = assemblyData.ToArray();
        ImageBase = imageBase;
    }

    public PatternSearcher(Memory<byte> assemblyData, IntPtr imageBase)
    {
        Data = assemblyData;
        ImageBase = imageBase;
    }


    private enum Keywords
    {
        Add,
        Sub,
        Read8,
        Read16,
        Read32,
        Read64,
        Tracerelative,
        Tracecall,
    }

    public IntPtr ImageBase { get; }

    public IntPtr Search(string pattern)
    {
        if (!GetPatternBytes(pattern, out var parsedPattern))
        {
            return IntPtr.Zero;
        }

        var freq = GetFrequencyTables();
        var compiled = BuildCompiled(parsedPattern, freq.ByteFreq, freq.PairFreq);

        return FindSingle(in compiled, IntPtr.Zero, Data.Length);
    }

    public IntPtr Search(string pattern, IntPtr start, int maxSearchLength)
    {
        if (!GetPatternBytes(pattern, out var parsedPattern))
        {
            return IntPtr.Zero;
        }

        var freq = GetFrequencyTables();
        var compiled = BuildCompiled(parsedPattern, freq.ByteFreq, freq.PairFreq);

        return FindSingle(in compiled, start, maxSearchLength);
    }

    public IntPtr[] SearchMany(string pattern)
    {
        return FindMany(pattern);
    }

    public IntPtr[] Search(string[] patterns)
    {
        var (compiled, table) = GetOrBuildCompiled(patterns);
        if (compiled.Length == 0)
        {
            return Array.Empty<IntPtr>();
        }

        return FindManyPatternsFirstHits(compiled, table);
    }

    public IntPtr[][] SearchMany(string[] patterns)
    {
        var (compiled, table) = GetOrBuildCompiled(patterns);
        if (compiled.Length == 0)
        {
            return Array.Empty<IntPtr[]>();
        }

        return FindAllPatternHits(compiled, table);
    }

    public ReadOnlySpan<byte> GetSlice(int start, int length)
    {
        return Data.Span.Slice(start, length);
    }

    private (int[] ByteFreq, int[] PairFreq) GetFrequencyTables()
    {
        var byteFreq = Volatile.Read(ref _byteFrequency);
        var pairFreq = Volatile.Read(ref _pairFrequency);

        if (byteFreq != null && pairFreq != null)
        {
            return (byteFreq, pairFreq);
        }

        byteFreq = new int[256];
        pairFreq = new int[65536];

        var data = Data.Span;

        // Optimization: Single-pass frequency computation
        // Combines two separate loops (one for bytes, one for pairs) into a single loop.
        // This reduces array bounds checks, loop iterations, and memory reads,
        // resulting in a ~9% performance improvement on large memory buffers.
        if (data.Length > 0)
        {
            byte prev = data[0];
            byteFreq[prev]++;

            for (var i = 1; i < data.Length; i++)
            {
                byte curr = data[i];
                byteFreq[curr]++;
                var pair = prev | (curr << 8);
                pairFreq[pair]++;
                prev = curr;
            }
        }

        Volatile.Write(ref _pairFrequency, pairFreq);
        Volatile.Write(ref _byteFrequency, byteFreq);

        return (byteFreq, pairFreq);
    }

    private (CompiledPattern[] Compiled, PatternBucketTable Table) GetOrBuildCompiled(string[] patterns)
    {
        lock (_cacheLock)
        {
            if (ReferenceEquals(patterns, _cachedPatternsRef) && _cachedCompiled != null)
            {
                return (_cachedCompiled, _cachedTable)!;
            }

            var freq = GetFrequencyTables();
            var compiled = CompilePatternsOrThrow(patterns, freq.ByteFreq, freq.PairFreq);
            var table = compiled.Length > 0 ? BuildBucketTable(compiled) : null;

            _cachedPatternsRef = patterns;
            _cachedCompiled = compiled;
            _cachedTable = table;

            return (compiled, table);
        }
    }

    private struct ParsedPattern
    {
        public byte[] BytesToSearch;
        public int BytesLength;
        public byte[] Mask;
        public string[] PostPattern;
        public int PostPatternLength;
        public int FirstFullByteIndex;
    }

    private struct CompiledPattern
    {
        public ParsedPattern Pattern;

        public int AnchorOffset;
        public ushort AnchorPair;
        public bool HasPairAnchor;

        public byte SingleAnchorByte;
        public bool HasSingleAnchor;

        public ulong PrefixBytes;
        public ulong PrefixMask;
        public int PrefixLength;
    }

    private static bool GetPatternBytes(string pattern, out ParsedPattern parsedPattern)
    {
        parsedPattern = default;

        if (pattern == null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        var scratchLen = (pattern.Length / 2) + 1;
        var bytesScratch = ArrayPool<byte>.Shared.Rent(scratchLen);
        var maskScratch = ArrayPool<byte>.Shared.Rent(scratchLen);
        List<string> post = null;
        var length = 0;

        try
        {
            var span = pattern.AsSpan();
            var enumerator = span.Split().GetEnumerator();

            while (enumerator.MoveNext())
            {
                var cur = enumerator.Current;
                var curLen = cur.Length;

                if (curLen == 0)
                {
                    continue;
                }

                if (cur.Equals("Search", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsPatternByteToken(cur))
                {
                    post = new List<string>(4);
                    post.Add(new string(cur));
                    break;
                }

                bytesScratch[length] = cur.GetByte();
                maskScratch[length] = cur.GetMask();
                length++;
            }

            if (post != null && enumerator.WordPos <= enumerator.Input.Length + 1)
            {
                while (enumerator.MoveNext())
                {
                    var cur = enumerator.Current;
                    if (cur.Length != 0)
                    {
                        post.Add(new string(cur));
                    }
                }

                ValidatePostPattern(post, pattern);
            }

            if (length == 0)
            {
                throw new ArgumentException("Pattern must contain at least one byte token.", nameof(pattern));
            }

            var bytes = new byte[length];
            var mask = new byte[length];
            Array.Copy(bytesScratch, bytes, length);
            Array.Copy(maskScratch, mask, length);

            var firstFull = -1;
            for (var i = 0; i < length; i++)
            {
                if (mask[i] == 0xFF)
                {
                    firstFull = i;
                    break;
                }
            }

            parsedPattern.BytesToSearch = bytes;
            parsedPattern.Mask = mask;
            parsedPattern.BytesLength = length;
            parsedPattern.FirstFullByteIndex = firstFull;

            if (post != null)
            {
                parsedPattern.PostPattern = post.ToArray();
                parsedPattern.PostPatternLength = parsedPattern.PostPattern.Length;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytesScratch);
            ArrayPool<byte>.Shared.Return(maskScratch);
        }
    }

    private static bool IsPatternByteToken(ReadOnlySpan<char> token)
    {
        if (token is ['?'] or ['?', '?'])
        {
            return true;
        }

        return token.Length == 2 && token.IsValidHex();
    }

    private static void ValidatePostPattern(List<string> postPattern, string pattern)
    {
        for (var i = 0; i < postPattern.Count; i++)
        {
            var token = postPattern[i];

            if (!Enum.TryParse<Keywords>(token, true, out var keyword))
            {
                throw new ArgumentException(
                    $"Unknown post-match command '{token}' in pattern '{pattern}'. Commands must be separated from byte tokens by whitespace.",
                    nameof(pattern));
            }

            switch (keyword)
            {
                case Keywords.Add:
                case Keywords.Sub:
                    if (i + 1 >= postPattern.Count)
                    {
                        throw new ArgumentException(
                            $"{token} is missing its operand in pattern '{pattern}'.",
                            nameof(pattern));
                    }

                    var operand = postPattern[i + 1];
                    if (!TryParseOffset(operand, out _))
                    {
                        throw new ArgumentException(
                            $"Invalid {token} operand '{operand}' in pattern '{pattern}'.",
                            nameof(pattern));
                    }

                    i++;
                    break;

                case Keywords.Read8:
                case Keywords.Read16:
                case Keywords.Read32:
                case Keywords.Read64:
                case Keywords.Tracerelative:
                case Keywords.Tracecall:
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private IntPtr[] FindMany(string pattern)
    {
        if (!GetPatternBytes(pattern, out var parsedPattern))
        {
            return Array.Empty<IntPtr>();
        }

        var freq = GetFrequencyTables();
        var compiled = BuildCompiled(parsedPattern, freq.ByteFreq, freq.PairFreq);

        var final = new List<IntPtr>();
        var dataLength = Data.Length;
        var offset = 0;

        while (true)
        {
            var rawOffset = FindSingleRawOffset(in compiled, new IntPtr(offset), dataLength - offset);
            if (rawOffset < 0)
            {
                break;
            }

            var result = ApplyPostPattern(in compiled.Pattern, new IntPtr(rawOffset), Data.Span);
            final.Add(result);

            var nextOffset = rawOffset + compiled.Pattern.BytesLength;
            if (nextOffset >= dataLength || nextOffset <= offset)
            {
                break;
            }

            offset = nextOffset;
        }

        return final.ToArray();
    }

    private static CompiledPattern[] CompilePatternsOrThrow(
        string[] patterns,
        int[] byteFreq,
        int[] pairFreq)
    {
        if (patterns == null)
        {
            throw new ArgumentNullException(nameof(patterns));
        }

        if (patterns.Length == 0)
        {
            return Array.Empty<CompiledPattern>();
        }

        var compiled = new CompiledPattern[patterns.Length];

        for (var i = 0; i < patterns.Length; i++)
        {
            var pattern = patterns[i];

            if (pattern == null)
            {
                throw new ArgumentException($"Pattern at index {i} is null.", nameof(patterns));
            }

            if (!GetPatternBytes(pattern, out var parsedPattern))
            {
                throw new ArgumentException($"Pattern at index {i} is invalid: '{pattern}'.", nameof(patterns));
            }

            compiled[i] = BuildCompiled(parsedPattern, byteFreq, pairFreq);
        }

        return compiled;
    }

    private static CompiledPattern BuildCompiled(
        ParsedPattern parsed,
        int[]? byteFreq = null,
        int[]? pairFreq = null)
    {
        var cp = new CompiledPattern {Pattern = parsed};

        var bestPairIdx = -1;
        ushort bestPair = 0;
        long bestPairScore = long.MaxValue;

        for (var i = 0; i + 1 < parsed.BytesLength; i++)
        {
            if (parsed.Mask[i] != 0xFF || parsed.Mask[i + 1] != 0xFF)
            {
                continue;
            }

            var b0 = parsed.BytesToSearch[i];
            var b1 = parsed.BytesToSearch[i + 1];
            var pair = (ushort)(b0 | (b1 << 8));

            long score;

            if (byteFreq != null && pairFreq != null)
            {
                score = ((long)byteFreq[b0] << 32) | (uint)pairFreq[pair];
            }
            else
            {
                score = i;
            }

            if (score < bestPairScore)
            {
                bestPairScore = score;
                bestPairIdx = i;
                bestPair = pair;
            }
        }

        if (bestPairIdx >= 0)
        {
            cp.AnchorOffset = bestPairIdx;
            cp.AnchorPair = bestPair;
            cp.HasPairAnchor = true;
        }
        else
        {
            var bestSingleIdx = -1;
            var bestSingleScore = int.MaxValue;

            for (var i = 0; i < parsed.BytesLength; i++)
            {
                if (parsed.Mask[i] != 0xFF)
                {
                    continue;
                }

                var b = parsed.BytesToSearch[i];
                var score = byteFreq != null ? byteFreq[b] : i;

                if (score < bestSingleScore)
                {
                    bestSingleScore = score;
                    bestSingleIdx = i;
                }
            }

            if (bestSingleIdx >= 0)
            {
                cp.AnchorOffset = bestSingleIdx;
                cp.SingleAnchorByte = parsed.BytesToSearch[bestSingleIdx];
                cp.HasSingleAnchor = true;
            }
        }

        var prefLen = Math.Min(parsed.BytesLength, 8);
        ulong pb = 0;
        ulong pm = 0;

        for (var i = 0; i < prefLen; i++)
        {
            pb |= (ulong)parsed.BytesToSearch[i] << (i * 8);
            pm |= (ulong)parsed.Mask[i] << (i * 8);
        }

        cp.PrefixBytes = pb;
        cp.PrefixMask = pm;
        cp.PrefixLength = prefLen;

        return cp;
    }

    private sealed class PatternBucketTable
    {
        public readonly Dictionary<ushort, int[]> PairBuckets;
        public readonly byte[] PairFirstBytes;
        public readonly SearchValues<byte>? PairFirstSearchValues;

        public readonly int[][] SingleBuckets;
        public readonly byte[] SingleAnchorBytes;
        public readonly SearchValues<byte>? SingleAnchorSearchValues;

        public readonly int[] NoAnchor;
        public readonly int MaxAnchorOffset;

        public PatternBucketTable(
            Dictionary<ushort, int[]> pairBuckets,
            byte[] pairFirstBytes,
            int[][] singleBuckets,
            byte[] singleAnchorBytes,
            int[] noAnchor,
            int maxAnchorOffset)
        {
            PairBuckets = pairBuckets;
            PairFirstBytes = pairFirstBytes;
            PairFirstSearchValues = pairFirstBytes.Length > 1
                ? SearchValues.Create(pairFirstBytes)
                : null;

            SingleBuckets = singleBuckets;
            SingleAnchorBytes = singleAnchorBytes;
            SingleAnchorSearchValues = singleAnchorBytes.Length > 1
                ? SearchValues.Create(singleAnchorBytes)
                : null;

            NoAnchor = noAnchor;
            MaxAnchorOffset = maxAnchorOffset;
        }
    }

    private static PatternBucketTable BuildBucketTable(CompiledPattern[] compiled)
    {
        var pairCountPool = ArrayPool<int>.Shared.Rent(65536);
        Array.Clear(pairCountPool, 0, 65536);

        Span<int> singleCounts = stackalloc int[256];
        Span<bool> pairFirstSeen = stackalloc bool[256];
        Span<bool> singleAnchorSeen = stackalloc bool[256];

        var noAnchorCount = 0;
        var maxAnchorOffset = 0;
        var distinctPairCount = 0;
        var pairFirstCount = 0;
        var singleAnchorCount = 0;

        try
        {
            for (var i = 0; i < compiled.Length; i++)
            {
                ref var cp = ref compiled[i];

                if (cp.HasPairAnchor)
                {
                    if (pairCountPool[cp.AnchorPair]++ == 0)
                    {
                        distinctPairCount++;
                    }

                    var firstByte = (byte)(cp.AnchorPair & 0xFF);
                    if (!pairFirstSeen[firstByte])
                    {
                        pairFirstSeen[firstByte] = true;
                        pairFirstCount++;
                    }

                    if (cp.AnchorOffset > maxAnchorOffset)
                    {
                        maxAnchorOffset = cp.AnchorOffset;
                    }
                }
                else if (cp.HasSingleAnchor)
                {
                    singleCounts[cp.SingleAnchorByte]++;

                    if (!singleAnchorSeen[cp.SingleAnchorByte])
                    {
                        singleAnchorSeen[cp.SingleAnchorByte] = true;
                        singleAnchorCount++;
                    }

                    if (cp.AnchorOffset > maxAnchorOffset)
                    {
                        maxAnchorOffset = cp.AnchorOffset;
                    }
                }
                else
                {
                    noAnchorCount++;
                }
            }

            var pairBuckets = new Dictionary<ushort, int[]>(distinctPairCount);
            var singleBuckets = new int[256][];

            for (var b = 0; b < 256; b++)
            {
                if (singleCounts[b] > 0)
                {
                    singleBuckets[b] = new int[singleCounts[b]];
                }
            }

            Span<int> singleCursors = stackalloc int[256];

            var pairFirstBytes = new byte[pairFirstCount];
            var pfIdx = 0;
            for (var b = 0; b < 256; b++)
            {
                if (pairFirstSeen[b])
                {
                    pairFirstBytes[pfIdx++] = (byte)b;
                }
            }

            var singleAnchorBytes = new byte[singleAnchorCount];
            var saIdx = 0;
            for (var b = 0; b < 256; b++)
            {
                if (singleAnchorSeen[b])
                {
                    singleAnchorBytes[saIdx++] = (byte)b;
                }
            }

            var noAnchor = noAnchorCount == 0 ? Array.Empty<int>() : new int[noAnchorCount];
            var noAnchorCursor = 0;

            var pairCursors = new Dictionary<ushort, int>(distinctPairCount);

            for (var i = 0; i < compiled.Length; i++)
            {
                ref var cp = ref compiled[i];

                if (cp.HasPairAnchor)
                {
                    if (!pairBuckets.TryGetValue(cp.AnchorPair, out var bucket))
                    {
                        bucket = new int[pairCountPool[cp.AnchorPair]];
                        pairBuckets[cp.AnchorPair] = bucket;
                        pairCursors[cp.AnchorPair] = 0;
                    }

                    var cur = pairCursors[cp.AnchorPair];
                    bucket[cur] = i;
                    pairCursors[cp.AnchorPair] = cur + 1;
                }
                else if (cp.HasSingleAnchor)
                {
                    var b = cp.SingleAnchorByte;
                    singleBuckets[b][singleCursors[b]++] = i;
                }
                else
                {
                    noAnchor[noAnchorCursor++] = i;
                }
            }

            return new PatternBucketTable(
                pairBuckets,
                pairFirstBytes,
                singleBuckets,
                singleAnchorBytes,
                noAnchor,
                maxAnchorOffset);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pairCountPool);
        }
    }

    private IntPtr[] FindManyPatternsFirstHits(CompiledPattern[] compiled, PatternBucketTable table)
    {
        var data = Data.Span;
        var dataLength = data.Length;
        var rawOffsets = ArrayPool<int>.Shared.Rent(compiled.Length);

        try
        {
            rawOffsets.AsSpan(0, compiled.Length).Fill(int.MaxValue);

            if (dataLength < ParallelMinDataSize || Environment.ProcessorCount < 2)
            {
                FindManyPatternsFirstHits_Serial(compiled, table, data, 0, dataLength, rawOffsets, earlyExit: true);
                return BuildFirstHitPointers(compiled, rawOffsets, data);
            }

            FindManyPatternsFirstHits_Parallel(compiled, table, dataLength, rawOffsets);
            return BuildFirstHitPointers(compiled, rawOffsets, data);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rawOffsets);
        }
    }

    private void FindManyPatternsFirstHits_Parallel(
        CompiledPattern[] compiled,
        PatternBucketTable table,
        int dataLength,
        int[] rawOffsets)
    {
        var procCount = Environment.ProcessorCount;
        var targetChunks = Math.Max(
            2,
            Math.Min(procCount, (dataLength + ParallelTargetChunkSize - 1) / ParallelTargetChunkSize));

        var chunkSize = (dataLength + targetChunks - 1) / targetChunks;

        var maxPatternLen = 0;
        for (var i = 0; i < compiled.Length; i++)
        {
            if (compiled[i].Pattern.BytesLength > maxPatternLen)
            {
                maxPatternLen = compiled[i].Pattern.BytesLength;
            }
        }

        var overlap = Math.Max(1, maxPatternLen);
        var dataMemory = Data;

        Parallel.For(0, targetChunks, chunkIdx =>
        {
            var start = chunkIdx * chunkSize;
            var end = Math.Min(start + chunkSize + overlap, dataLength);
            var span = dataMemory.Span;

            FindManyPatternsFirstHits_Serial(
                compiled,
                table,
                span,
                start,
                end,
                rawOffsets,
                earlyExit: false);
        });
    }

    private void FindManyPatternsFirstHits_Serial(
        CompiledPattern[] compiled,
        PatternBucketTable table,
        ReadOnlySpan<byte> data,
        int rangeStart,
        int rangeEnd,
        int[] rawOffsets,
        bool earlyExit)
    {
        bool[]? found = earlyExit ? new bool[compiled.Length] : null;
        var remaining = compiled.Length;

        var noAnchor = table.NoAnchor;
        if (noAnchor.Length > 0)
        {
            for (var pos = rangeStart; pos < rangeEnd && (!earlyExit || remaining > 0); pos++)
            {
                for (var i = 0; i < noAnchor.Length; i++)
                {
                    var idx = noAnchor[i];
                    if (earlyExit && found[idx])
                    {
                        continue;
                    }

                    ref var cp = ref compiled[idx];
                    var len = cp.Pattern.BytesLength;

                    if ((uint)pos + (uint)len > (uint)data.Length)
                    {
                        continue;
                    }

                    if (!PrefixReject(data, pos, in cp) &&
                        MatchAtRaw(data, pos, cp.Pattern.BytesToSearch, cp.Pattern.Mask, len))
                    {
                        if (TryPublishEarliestRawOffset(rawOffsets, idx, pos) && earlyExit)
                        {
                            found[idx] = true;
                            remaining--;
                        }
                    }
                }
            }

            if (earlyExit && remaining == 0)
            {
                return;
            }
        }

        var pairFirstBytes = table.PairFirstBytes;
        if (pairFirstBytes.Length > 0)
        {
            var pairBuckets = table.PairBuckets;
            var cursor = rangeStart;
            var scanEnd = rangeEnd - 1;

            if (scanEnd > data.Length - 1)
            {
                scanEnd = data.Length - 1;
            }

            while (cursor < scanEnd && (!earlyExit || remaining > 0))
            {
                var remainingSpan = data.Slice(cursor, scanEnd - cursor + 1);

                int hit = pairFirstBytes.Length == 1
                    ? remainingSpan.IndexOf(pairFirstBytes[0])
                    : remainingSpan.IndexOfAny(table.PairFirstSearchValues);

                if (hit < 0)
                {
                    break;
                }

                var pos = cursor + hit;
                if (pos + 1 >= data.Length)
                {
                    break;
                }

                var pair = (ushort)(data[pos] | (data[pos + 1] << 8));

                if (pairBuckets.TryGetValue(pair, out var bucket))
                {
                    for (var i = 0; i < bucket.Length; i++)
                    {
                        var idx = bucket[i];

                        if (earlyExit && found[idx])
                        {
                            continue;
                        }

                        ref var cp = ref compiled[idx];
                        var start = pos - cp.AnchorOffset;

                        if (start < rangeStart)
                        {
                            continue;
                        }

                        var len = cp.Pattern.BytesLength;

                        if ((uint)start + (uint)len > (uint)data.Length)
                        {
                            continue;
                        }

                        if (PrefixReject(data, start, in cp))
                        {
                            continue;
                        }

                        if (!MatchAtRaw(data, start, cp.Pattern.BytesToSearch, cp.Pattern.Mask, len))
                        {
                            continue;
                        }

                        if (TryPublishEarliestRawOffset(rawOffsets, idx, start) && earlyExit)
                        {
                            found[idx] = true;
                            remaining--;

                            if (remaining == 0)
                            {
                                return;
                            }
                        }
                    }
                }

                cursor = pos + 1;
            }
        }

        var singleAnchorBytes = table.SingleAnchorBytes;
        if (singleAnchorBytes.Length > 0 && (!earlyExit || remaining > 0))
        {
            var cursor = rangeStart;
            var scanEnd = rangeEnd;

            if (scanEnd > data.Length)
            {
                scanEnd = data.Length;
            }

            while (cursor < scanEnd && (!earlyExit || remaining > 0))
            {
                var remainingSpan = data.Slice(cursor, scanEnd - cursor);

                int hit = singleAnchorBytes.Length == 1
                    ? remainingSpan.IndexOf(singleAnchorBytes[0])
                    : remainingSpan.IndexOfAny(table.SingleAnchorSearchValues);

                if (hit < 0)
                {
                    break;
                }

                var pos = cursor + hit;
                var bucket = table.SingleBuckets[data[pos]];

                if (bucket != null)
                {
                    for (var i = 0; i < bucket.Length; i++)
                    {
                        var idx = bucket[i];

                        if (earlyExit && found[idx])
                        {
                            continue;
                        }

                        ref var cp = ref compiled[idx];
                        var start = pos - cp.AnchorOffset;

                        if (start < rangeStart)
                        {
                            continue;
                        }

                        var len = cp.Pattern.BytesLength;

                        if ((uint)start + (uint)len > (uint)data.Length)
                        {
                            continue;
                        }

                        if (PrefixReject(data, start, in cp))
                        {
                            continue;
                        }

                        if (!MatchAtRaw(data, start, cp.Pattern.BytesToSearch, cp.Pattern.Mask, len))
                        {
                            continue;
                        }

                        if (TryPublishEarliestRawOffset(rawOffsets, idx, start) && earlyExit)
                        {
                            found[idx] = true;
                            remaining--;

                            if (remaining == 0)
                            {
                                return;
                            }
                        }
                    }
                }

                cursor = pos + 1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryPublishEarliestRawOffset(int[] rawOffsets, int idx, int rawOffset)
    {
        ref var slot = ref rawOffsets[idx];

        while (true)
        {
            var current = Volatile.Read(ref slot);

            if (rawOffset >= current)
            {
                return false;
            }

            var previous = Interlocked.CompareExchange(ref slot, rawOffset, current);
            if (previous == current)
            {
                return true;
            }
        }
    }

    private IntPtr[] BuildFirstHitPointers(
        CompiledPattern[] compiled,
        int[] rawOffsets,
        ReadOnlySpan<byte> data)
    {
        var pointers = new IntPtr[compiled.Length];

        for (var i = 0; i < compiled.Length; i++)
        {
            var rawOffset = rawOffsets[i];

            if (rawOffset != int.MaxValue)
            {
                pointers[i] = ApplyPostPattern(in compiled[i].Pattern, new IntPtr(rawOffset), data);
            }
        }

        return pointers;
    }

    private IntPtr[][] FindAllPatternHits(CompiledPattern[] compiled, PatternBucketTable table)
    {
        var data = Data.Span;
        var dataLength = data.Length;

        if (dataLength < ParallelMinDataSize || Environment.ProcessorCount < 2)
        {
            var serial = FindAllPatternHits_Serial(compiled, table, data, 0, dataLength, dataLength);
            return ToArrayOfArrays(serial);
        }

        return FindAllPatternHits_Parallel(compiled, table, dataLength);
    }

    private IntPtr[][] FindAllPatternHits_Parallel(
        CompiledPattern[] compiled,
        PatternBucketTable table,
        int dataLength)
    {
        var procCount = Environment.ProcessorCount;
        var targetChunks = Math.Max(
            2,
            Math.Min(procCount, (dataLength + ParallelTargetChunkSize - 1) / ParallelTargetChunkSize));

        var chunkSize = (dataLength + targetChunks - 1) / targetChunks;

        var maxPatternLen = 0;
        for (var i = 0; i < compiled.Length; i++)
        {
            if (compiled[i].Pattern.BytesLength > maxPatternLen)
            {
                maxPatternLen = compiled[i].Pattern.BytesLength;
            }
        }

        var overlap = Math.Max(1, maxPatternLen);
        var chunkResults = new List<IntPtr>[targetChunks][];
        var dataMemory = Data;

        Parallel.For(0, targetChunks, chunkIdx =>
        {
            var start = chunkIdx * chunkSize;
            var end = Math.Min(start + chunkSize + overlap, dataLength);
            var ownedEnd = Math.Min(start + chunkSize, dataLength);
            var span = dataMemory.Span;

            chunkResults[chunkIdx] = FindAllPatternHits_Serial(
                compiled,
                table,
                span,
                start,
                end,
                ownedEnd);
        });

        var final = new IntPtr[compiled.Length][];

        for (var p = 0; p < compiled.Length; p++)
        {
            var total = 0;

            for (var c = 0; c < chunkResults.Length; c++)
            {
                total += chunkResults[c][p].Count;
            }

            if (total == 0)
            {
                final[p] = Array.Empty<IntPtr>();
                continue;
            }

            var arr = new IntPtr[total];
            var idx = 0;
            var last = IntPtr.Zero;

            for (var c = 0; c < chunkResults.Length; c++)
            {
                var list = chunkResults[c][p];

                for (var i = 0; i < list.Count; i++)
                {
                    var v = list[i];

                    if (v == last)
                    {
                        continue;
                    }

                    arr[idx++] = v;
                    last = v;
                }
            }

            if (idx != total)
            {
                var trimmed = new IntPtr[idx];
                Array.Copy(arr, trimmed, idx);
                final[p] = trimmed;
            }
            else
            {
                final[p] = arr;
            }
        }

        return final;
    }

    private List<IntPtr>[] FindAllPatternHits_Serial(
        CompiledPattern[] compiled,
        PatternBucketTable table,
        ReadOnlySpan<byte> data,
        int rangeStart,
        int rangeEnd,
        int reportEnd)
    {
        var results = new List<IntPtr>[compiled.Length];

        for (var i = 0; i < results.Length; i++)
        {
            results[i] = new List<IntPtr>(4);
        }

        var noAnchor = table.NoAnchor;
        if (noAnchor.Length > 0)
        {
            for (var pos = rangeStart; pos < rangeEnd; pos++)
            {
                for (var i = 0; i < noAnchor.Length; i++)
                {
                    var idx = noAnchor[i];
                    ref var cp = ref compiled[idx];
                    var len = cp.Pattern.BytesLength;

                    if ((uint)pos + (uint)len > (uint)data.Length)
                    {
                        continue;
                    }

                    if (PrefixReject(data, pos, in cp))
                    {
                        continue;
                    }

                    if (!MatchAtRaw(data, pos, cp.Pattern.BytesToSearch, cp.Pattern.Mask, len))
                    {
                        continue;
                    }

                    if (pos >= reportEnd)
                    {
                        continue;
                    }

                    results[idx].Add(ApplyPostPattern(in cp.Pattern, new IntPtr(pos), data));
                }
            }
        }

        var pairFirstBytes = table.PairFirstBytes;
        if (pairFirstBytes.Length > 0)
        {
            var pairBuckets = table.PairBuckets;
            var cursor = rangeStart;
            var scanEnd = rangeEnd - 1;

            if (scanEnd > data.Length - 1)
            {
                scanEnd = data.Length - 1;
            }

            while (cursor < scanEnd)
            {
                var remainingSpan = data.Slice(cursor, scanEnd - cursor + 1);

                int hit = pairFirstBytes.Length == 1
                    ? remainingSpan.IndexOf(pairFirstBytes[0])
                    : remainingSpan.IndexOfAny(table.PairFirstSearchValues);

                if (hit < 0)
                {
                    break;
                }

                var pos = cursor + hit;

                if (pos + 1 >= data.Length)
                {
                    break;
                }

                var pair = (ushort)(data[pos] | (data[pos + 1] << 8));

                if (pairBuckets.TryGetValue(pair, out var bucket))
                {
                    for (var i = 0; i < bucket.Length; i++)
                    {
                        var idx = bucket[i];
                        ref var cp = ref compiled[idx];
                        var start = pos - cp.AnchorOffset;

                        if (start < rangeStart)
                        {
                            continue;
                        }

                        var len = cp.Pattern.BytesLength;

                        if ((uint)start + (uint)len > (uint)data.Length)
                        {
                            continue;
                        }

                        if (PrefixReject(data, start, in cp))
                        {
                            continue;
                        }

                        if (!MatchAtRaw(data, start, cp.Pattern.BytesToSearch, cp.Pattern.Mask, len))
                        {
                            continue;
                        }

                        if (start >= reportEnd)
                        {
                            continue;
                        }

                        results[idx].Add(ApplyPostPattern(in cp.Pattern, new IntPtr(start), data));
                    }
                }

                cursor = pos + 1;
            }
        }

        var singleAnchorBytes = table.SingleAnchorBytes;
        if (singleAnchorBytes.Length > 0)
        {
            var cursor = rangeStart;
            var scanEnd = rangeEnd;

            if (scanEnd > data.Length)
            {
                scanEnd = data.Length;
            }

            while (cursor < scanEnd)
            {
                var remainingSpan = data.Slice(cursor, scanEnd - cursor);

                int hit = singleAnchorBytes.Length == 1
                    ? remainingSpan.IndexOf(singleAnchorBytes[0])
                    : remainingSpan.IndexOfAny(table.SingleAnchorSearchValues);

                if (hit < 0)
                {
                    break;
                }

                var pos = cursor + hit;
                var bucket = table.SingleBuckets[data[pos]];

                if (bucket != null)
                {
                    for (var i = 0; i < bucket.Length; i++)
                    {
                        var idx = bucket[i];
                        ref var cp = ref compiled[idx];
                        var start = pos - cp.AnchorOffset;

                        if (start < rangeStart)
                        {
                            continue;
                        }

                        var len = cp.Pattern.BytesLength;

                        if ((uint)start + (uint)len > (uint)data.Length)
                        {
                            continue;
                        }

                        if (PrefixReject(data, start, in cp))
                        {
                            continue;
                        }

                        if (!MatchAtRaw(data, start, cp.Pattern.BytesToSearch, cp.Pattern.Mask, len))
                        {
                            continue;
                        }

                        if (start >= reportEnd)
                        {
                            continue;
                        }

                        results[idx].Add(ApplyPostPattern(in cp.Pattern, new IntPtr(start), data));
                    }
                }

                cursor = pos + 1;
            }
        }

        return results;
    }

    private static IntPtr[][] ToArrayOfArrays(List<IntPtr>[] lists)
    {
        var final = new IntPtr[lists.Length][];

        for (var i = 0; i < lists.Length; i++)
        {
            final[i] = lists[i].ToArray();
        }

        return final;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool PrefixReject(ReadOnlySpan<byte> data, int index, in CompiledPattern cp)
    {
        var prefLen = cp.PrefixLength;

        if (prefLen == 0)
        {
            return false;
        }

        if ((uint)index + (uint)prefLen > (uint)data.Length)
        {
            return true;
        }

        ulong dataWord;
        ref var dRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), index);

        if (prefLen == 8)
        {
            dataWord = Unsafe.ReadUnaligned<ulong>(ref dRef);
        }
        else
        {
            dataWord = 0;

            for (var i = 0; i < prefLen; i++)
            {
                dataWord |= (ulong)Unsafe.Add(ref dRef, i) << (i * 8);
            }
        }

        return ((dataWord ^ cp.PrefixBytes) & cp.PrefixMask) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchAtRaw(
        ReadOnlySpan<byte> data,
        int index,
        byte[] bytesToMatch,
        byte[] masks,
        int len)
    {
        if ((uint)index + (uint)len > (uint)data.Length)
        {
            return false;
        }

        ref var dRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), index);
        ref var pRef = ref bytesToMatch[0];
        ref var mRef = ref masks[0];

        var i = 0;

        while (i + 8 <= len)
        {
            var dW = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dRef, i));
            var pW = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pRef, i));
            var mW = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref mRef, i));

            if (((dW ^ pW) & mW) != 0)
            {
                return false;
            }

            i += 8;
        }

        for (; i < len; i++)
        {
            var m = Unsafe.Add(ref mRef, i);

            if (((Unsafe.Add(ref dRef, i) ^ Unsafe.Add(ref pRef, i)) & m) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private IntPtr FindSingle(in CompiledPattern compiledPattern, IntPtr start, int max)
    {
        var matchingIndex = FindSingleRawOffset(in compiledPattern, start, max);
        if (matchingIndex < 0)
        {
            return IntPtr.Zero;
        }

        return ApplyPostPattern(in compiledPattern.Pattern, new IntPtr(matchingIndex), Data.Span);
    }

    private int FindSingleRawOffset(in CompiledPattern compiledPattern, IntPtr start, int max)
    {
        if (max <= 0)
        {
            return -1;
        }

        var parsedPattern = compiledPattern.Pattern;
        var start64 = start.ToInt64();

        if (start64 < 0 || start64 > int.MaxValue)
        {
            return -1;
        }

        var startInt = (int)start64;
        var index = startInt;
        var bytesToSearchLength = parsedPattern.BytesLength;
        var data = Data.Span;

        var bytesToSearch = new ReadOnlySpan<byte>(
            parsedPattern.BytesToSearch,
            0,
            bytesToSearchLength);

        var masks = new ReadOnlySpan<byte>(
            parsedPattern.Mask,
            0,
            bytesToSearchLength);

        var dataLength = data.Length;
        var endLimit64 = (long)startInt + max;

        if (endLimit64 > dataLength)
        {
            endLimit64 = dataLength;
        }

        var endLimit = (int)endLimit64;
        var searchEnd = endLimit - bytesToSearchLength;

        if (searchEnd < index)
        {
            return -1;
        }

        var matchingIndex = -1;

        if (compiledPattern.HasPairAnchor)
        {
            var pairIdx = compiledPattern.AnchorOffset;

            Span<byte> needle = stackalloc byte[2];
            needle[0] = bytesToSearch[pairIdx];
            needle[1] = bytesToSearch[pairIdx + 1];

            while (index <= searchEnd)
            {
                var scanStart = index + pairIdx;

                // Important:
                // searchEnd is the last valid pattern-start index.
                // The last valid 2-byte anchor can start at searchEnd + pairIdx.
                // Since Slice end is exclusive and the anchor is 2 bytes,
                // we need +2 here, not +1.
                var scanEnd = searchEnd + pairIdx + 2;

                if (scanEnd > dataLength)
                {
                    scanEnd = dataLength;
                }

                if (scanEnd - scanStart < 2)
                {
                    break;
                }

                var slice = data.Slice(scanStart, scanEnd - scanStart);
                var found = slice.IndexOf((ReadOnlySpan<byte>)needle);

                if (found < 0)
                {
                    break;
                }

                index = scanStart + found - pairIdx;

                if (index < startInt)
                {
                    index = startInt;
                    continue;
                }

                if (MatchAt(data, index, bytesToSearch, masks))
                {
                    matchingIndex = index;
                    break;
                }

                index++;
            }
        }
        else if (compiledPattern.HasSingleAnchor)
        {
            var anchorIdx = compiledPattern.AnchorOffset;
            var anchor = compiledPattern.SingleAnchorByte;

            while (index <= searchEnd)
            {
                var scanStart = index + anchorIdx;
                var scanEnd = searchEnd + anchorIdx;

                if (scanStart > scanEnd)
                {
                    break;
                }

                var slice = data.Slice(scanStart, scanEnd - scanStart + 1);
                var found = slice.IndexOf(anchor);

                if (found < 0)
                {
                    break;
                }

                index = scanStart + found - anchorIdx;

                if (MatchAt(data, index, bytesToSearch, masks))
                {
                    matchingIndex = index;
                    break;
                }

                index++;
            }
        }
        else
        {
            while (index <= searchEnd)
            {
                var match = Match(data, index, bytesToSearch, masks);

                if (match < 0)
                {
                    index += -match;
                    continue;
                }

                if (match == 0)
                {
                    index += bytesToSearchLength;
                    continue;
                }

                matchingIndex = index;
                break;
            }
        }

        if (matchingIndex < 0)
        {
            return -1;
        }

        return matchingIndex;
    }

    private IntPtr ApplyPostPattern(
        in ParsedPattern parsedPattern,
        IntPtr matchingPtr,
        ReadOnlySpan<byte> data)
    {
        var resultPointer = matchingPtr;
        var postPatternLength = parsedPattern.PostPatternLength;

        if (postPatternLength == 0)
        {
            return new IntPtr(matchingPtr.ToInt64() + ImageBase.ToInt64());
        }

        var postPattern = parsedPattern.PostPattern;

        for (var i = 0; i < postPatternLength; i++)
        {
            var token = postPattern[i];

            if (token.Length <= 2)
            {
                continue;
            }

            if (!Enum.TryParse<Keywords>(token, true, out var keyword))
            {
                continue;
            }

            switch (keyword)
            {
                case Keywords.Add:
                {
                    if (i + 1 >= postPatternLength)
                    {
                        throw new ArgumentException("Add is missing its operand.");
                    }

                    var next = postPattern[i + 1];

                    if (!TryParseOffset(next, out var addValue))
                    {
                        throw new ArgumentException($"Invalid Add operand '{next}'.");
                    }

                    i++;
                    resultPointer += addValue;
                    break;
                }

                case Keywords.Sub:
                {
                    if (i + 1 >= postPatternLength)
                    {
                        throw new ArgumentException("Sub is missing its operand.");
                    }

                    if (!TryParseOffset(postPattern[i + 1], out var subValue))
                    {
                        throw new ArgumentException($"Invalid Sub operand '{postPattern[i + 1]}'.");
                    }

                    i++;
                    resultPointer -= subValue;
                    break;
                }

                case Keywords.Read8:
                    return new IntPtr(data[resultPointer.ToInt32()]);

                case Keywords.Read16:
                    return new IntPtr(BitConverter.ToInt16(data.Slice(resultPointer.ToInt32(), 2)));

                case Keywords.Read32:
                    return new IntPtr(BitConverter.ToInt32(data.Slice(resultPointer.ToInt32(), 4)));

                case Keywords.Read64:
                    return new IntPtr(BitConverter.ToInt64(data.Slice(resultPointer.ToInt32(), 8)));

                case Keywords.Tracerelative:
                    return new IntPtr(
                        resultPointer.ToInt32() +
                        4 +
                        BitConverter.ToInt32(data.Slice(resultPointer.ToInt32(), 4)) +
                        ImageBase.ToInt64());

                case Keywords.Tracecall:
                    return new IntPtr(
                        resultPointer.ToInt32() +
                        5 +
                        BitConverter.ToInt32(data.Slice(resultPointer.ToInt32() + 1, 4)) +
                        ImageBase.ToInt64());

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return new IntPtr(resultPointer.ToInt32() + ImageBase.ToInt64());
    }

    private static bool TryParseOffset(string value, out int offset)
    {
        if (int.TryParse(value, out offset))
        {
            return true;
        }

        if (Utilities.IsValidHex(value) &&
            int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out offset))
        {
            return true;
        }

        offset = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchAt(
        ReadOnlySpan<byte> data,
        int index,
        ReadOnlySpan<byte> bytesToMatch,
        ReadOnlySpan<byte> masks)
    {
        if ((uint)index + (uint)bytesToMatch.Length > (uint)data.Length)
        {
            return false;
        }

        ref var dRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), index);
        ref var pRef = ref MemoryMarshal.GetReference(bytesToMatch);
        ref var mRef = ref MemoryMarshal.GetReference(masks);

        var len = bytesToMatch.Length;
        var i = 0;

        while (i + 8 <= len)
        {
            var dW = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dRef, i));
            var pW = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pRef, i));
            var mW = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref mRef, i));

            if (((dW ^ pW) & mW) != 0)
            {
                return false;
            }

            i += 8;
        }

        for (; i < len; i++)
        {
            var m = Unsafe.Add(ref mRef, i);

            if (((Unsafe.Add(ref dRef, i) ^ Unsafe.Add(ref pRef, i)) & m) != 0)
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Match(
        ReadOnlySpan<byte> data,
        int index,
        ReadOnlySpan<byte> bytesToMatch,
        ReadOnlySpan<byte> masks)
    {
        if (index + bytesToMatch.Length > data.Length)
        {
            return 0;
        }

        var dataBuffer = data.Slice(index, bytesToMatch.Length);

        int i;
        for (i = 0; i < bytesToMatch.Length; i++)
        {
            if (((dataBuffer[i] ^ bytesToMatch[i]) & masks[i]) != 0)
            {
                break;
            }
        }

        if (i == bytesToMatch.Length)
        {
            return 1;
        }

        var mask = masks[0];
        var bmo = (byte)(bytesToMatch[0] & mask);

        if (mask == 0xFF)
        {
            var remaining = dataBuffer.Slice(1);
            var nextIdx = remaining.IndexOf(bmo);
            return nextIdx < 0 ? -dataBuffer.Length : -(nextIdx + 1);
        }

        var indexOf = 1;

        for (; indexOf < dataBuffer.Length; indexOf++)
        {
            if ((dataBuffer[indexOf] & mask) == bmo)
            {
                break;
            }
        }

        return -indexOf;
    }
}
