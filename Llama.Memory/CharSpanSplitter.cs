using System.Runtime.CompilerServices;

namespace Llama.Memory;

/// <summary>
/// A zero-allocation ref struct for splitting a character span on whitespace delimiters.
/// </summary>
public readonly ref struct CharSpanSplitter
{
    private readonly ReadOnlySpan<char> input;

    /// <summary>
    /// Initializes a new instance of the <see cref="CharSpanSplitter"/> struct with the specified input character span.
    /// </summary>
    /// <param name="input">The span of characters to split.</param>
    public CharSpanSplitter(ReadOnlySpan<char> input) => this.input = input;

    /// <summary>
    /// Returns an enumerator that iterates through whitespace-separated segments of the character span.
    /// </summary>
    /// <returns>An <see cref="Enumerator"/> for the character span.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new Enumerator(input);

    /// <summary>
    /// Enumerates whitespace-separated segments of a character span.
    /// </summary>
    public ref struct Enumerator
    {
        /// <summary>
        /// The source character span being enumerated.
        /// </summary>
        public readonly ReadOnlySpan<char> Input;

        /// <summary>
        /// The current position within the input span.
        /// </summary>
        public int WordPos;

        /// <summary>
        /// Initializes a new instance of the <see cref="Enumerator"/> struct with the specified input character span.
        /// </summary>
        /// <param name="input">The span of characters to enumerate.</param>
        public Enumerator(ReadOnlySpan<char> input)
        {
            Input = input;
            WordPos = 0;
            Current = default;
        }

        /// <summary>
        /// Gets the character span element at the current position of the enumerator.
        /// </summary>
        public ReadOnlySpan<char> Current { get; private set; }

        /// <summary>
        /// Advances the enumerator to the next whitespace-separated segment of the character span.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator successfully advanced to the next element; <see langword="false"/> if the end of the span has been reached.</returns>
        public bool MoveNext()
        {
            for (var i = WordPos; i <= Input.Length; i++)
            {
                if (i != Input.Length && !IsWhiteSpace(Input[i]))
                {
                    continue;
                }

                Current = Input[WordPos..i];
                WordPos = i + 1;
                return true;
            }

            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhiteSpace(char c)
    {
        return c == ' ' || c == '\t';
    }
}

/// <summary>
/// Extension methods for splitting character spans without allocations.
/// </summary>
public static class CharSpanExtensions
{
    /// <summary>
    /// Creates a <see cref="CharSpanSplitter"/> for enumerating whitespace-separated segments of the specified read-only character span.
    /// </summary>
    /// <param name="input">The read-only span of characters to split.</param>
    /// <returns>A <see cref="CharSpanSplitter"/> over the specified span.</returns>
    public static CharSpanSplitter Split(this ReadOnlySpan<char> input)
        => new CharSpanSplitter(input);

    /// <summary>
    /// Creates a <see cref="CharSpanSplitter"/> for enumerating whitespace-separated segments of the specified character span.
    /// </summary>
    /// <param name="input">The span of characters to split.</param>
    /// <returns>A <see cref="CharSpanSplitter"/> over the specified span.</returns>
    public static CharSpanSplitter Split(this Span<char> input)
        => new CharSpanSplitter(input);
}