using System;
using System.Diagnostics;
using Llama.Memory;

public class Benchmark
{
    public static void Main()
    {
        var data = new byte[10 * 1024 * 1024]; // 10 MB
        var r = new Random(42);
        r.NextBytes(data);

        var searcher = new PatternSearcher(data, IntPtr.Zero);
        // Create an unanchored pattern (all ?s for anchors, though the parser prefers literal anchors)
        // If we use all ?s it has no anchor. Wait, no, we need some literals to make prefix reject work, but we don't want it to have anchors?
        // A single literal byte has a single anchor.
        // Let's just do a normal pattern and measure Search
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            searcher.SearchMany(new string[] { "12 ? ? 45 67 89", "AA BB CC DD EE FF", "? ? 12 34 56 78 90", "11 22 33 44 55 66 77 88" });
        }
        sw.Stop();
        Console.WriteLine($"SearchMany: {sw.ElapsedMilliseconds} ms");
    }
}
