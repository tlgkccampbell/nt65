namespace Norristown.Analysis.Text
{
    /// <summary>
    /// An immutable snapshot of one source file's text.
    /// </summary>
    public abstract class SourceText
    {
        private Int32[]? _lineStarts;

        /// <summary>
        /// Initializes a new instance of the <see cref="SourceText"/> type.
        /// </summary>
        protected SourceText(Int64 revision)
        {
            this.Revision = revision;
        }

        /// <summary>
        /// Creates a <see cref="SourceText"/> from the specified string.
        /// </summary>
        /// <param name="text">The string from which to create a new <see cref="SourceText"/> instance.</param>
        /// <returns>The new <see cref="SourceText"/> instance that was created.</returns>
        public static SourceText From(String text) => new StringSourceText(text);

        /// <summary>
        /// Copies a span of text into a buffer.
        /// </summary>
        /// <param name="span">The span to copy.</param>
        /// <param name="destination">The buffer that will receive the copied text.</param>
        public void CopyTo(TextSpan span, Span<Char> destination)
        {
            ValidateSpan(span);

            if (destination.Length < span.Length)
            {
                throw new ArgumentException(
                    $"The destination holds {destination.Length} characters, but {span.Length} were requested.", nameof(destination));
            }

            CopyToCore(span, destination);
        }

        /// <summary>
        /// Gets the index of the line containing the specified position.
        /// </summary>
        /// <param name="position">A zero-based offset into the source text, at most <see cref="Length"/>.</param>
        /// <returns>The zero-based index of the line that contains <paramref name="position"/>.</returns>
        public Int32 GetLineIndex(Int32 position)
        {
            ValidatePosition(position);

            var index = Array.BinarySearch(LineStarts, position);

            return index >= 0 ? index : ~index - 1;
        }

        /// <summary>
        /// Gets the position at which the specified line begins.
        /// </summary>
        /// <param name="lineIndex">The zero-based index of the line for which to retrieve a position.</param>
        /// <returns>The position of the specified line's first character.</returns>
        public Int32 GetLineStart(Int32 lineIndex)
        {
            ValidateLineIndex(lineIndex);

            return LineStarts[lineIndex];
        }

        /// <summary>
        /// Gets the span of a line, excluding its terminator.
        /// </summary>
        /// <param name="lineIndex">The zero-based index of the line for which to retrieve a span.</param>
        /// <returns>The span of the specified line, excluding its terminator.</returns>
        public TextSpan GetLineSpan(Int32 lineIndex)
        {
            var span = GetLineSpanIncludingLineBreak(lineIndex);
            var end = span.End;

            if (end > span.Start && this[end - 1] == '\n')
            {
                end--;
            }

            if (end > span.Start && this[end - 1] == '\r')
            {
                end--;
            }

            return new TextSpan(span.Start, end - span.Start);
        }

        /// <summary>
        /// Gets the span of a line, including its terminator.
        /// </summary>
        /// <param name="lineIndex">The zero-based index of the line for which to retrieve a span.</param>
        /// <returns>The span of the specified line, including its terminator.</returns>
        public TextSpan GetLineSpanIncludingLineBreak(Int32 lineIndex)
        {
            ValidateLineIndex(lineIndex);

            var start = LineStarts[lineIndex];
            var end = lineIndex + 1 < LineStarts.Length ? LineStarts[lineIndex + 1] : Length;

            return new TextSpan(start, end - start);
        }

        /// <summary>
        /// Produces a new <see cref="SourceText"/> that is the result of applying the specified
        /// change to the current <see cref="SourceText"/> instance.
        /// </summary>
        /// <param name="change">The change to apply, in this text's coordinates and in any order.</param>
        /// <returns>The new <see cref="SourceText"/> snapshot, or this instance if no changes were applied.</returns>
        public SourceText WithChange(TextChange change)
        {
            ValidateChange(change, 0, nameof(change));

            return ApplyChanges([change]);
        }

        /// <summary>
        /// Produces a new <see cref="SourceText"/> that is the result of applying the specified
        /// set of changes to the current <see cref="SourceText"/> instance.
        /// </summary>
        /// <param name="changes">The changes to apply, in this text's coordinates and in any order.</param>
        /// <returns>The new <see cref="SourceText"/> snapshot, or this instance if no changes were applied.</returns>
        public SourceText WithChanges(params TextChange[] changes)
        {
            ArgumentNullException.ThrowIfNull(changes);

            if (changes.Length == 0)
            {
                return this;
            }

            var orderedChanges = changes
                .Select((change, index) => (Change: change, Index: index))
                .OrderBy(entry => entry.Change.Span.Start)
                .ThenBy(entry => entry.Change.Span.Length)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Change)
                .ToArray();

            var previousEnd = 0;

            foreach (var change in orderedChanges)
            {
                ValidateChange(change, previousEnd);

                previousEnd = change.Span.End;
            }

            return ApplyChanges(orderedChanges);
        }

        /// <summary>
        /// Gets a span of text as a string.
        /// </summary>
        /// <param name="span">The span to materialize as a string.</param>
        /// <returns>A string containing the text covered by the span.</returns>
        public String ToString(TextSpan span)
        {
            ValidateSpan(span);

            if (span.IsEmpty)
            {
                return String.Empty;
            }

            var buffer = new Char[span.Length];
            CopyToCore(span, buffer);

            return new String(buffer);
        }

        /// <inheritdoc/>
        public override String ToString() => ToString(new(0, Length));

        /// <summary>
        /// Gets the character at the specified position within the source text.
        /// </summary>
        /// <param name="position">The position of the character to retrieve.</param>
        /// <returns>The character at <paramref name="position"/>.</returns>
        public Char this[Int32 position]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(position);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, Length);

                return GetCharCore(position);
            }
        }

        /// <summary>
        /// The snapshot's revision number. This value increments every time a change
        /// is applied to the source text.
        /// </summary>
        public Int64 Revision { get; }

        /// <summary>
        /// Gets the number of lines in the source text.
        /// </summary>
        public Int32 LineCount => LineStarts.Length;

        /// <summary>
        /// Gets the number of characters in the source text.
        /// </summary>
        public abstract Int32 Length { get; }

        /// <summary>
        /// Copies a span of text into a buffer.
        /// </summary>
        /// <param name="span">The span to copy.</param>
        /// <param name="destination">The buffer that will receive the copied text.</param>
        protected abstract void CopyToCore(TextSpan span, Span<Char> destination);

        /// <summary>
        /// Gets the character at the specified position in the source text,
        /// which is already known to be in range.
        /// </summary>
        /// <param name="position">A valid, zero-based position within the source text.</param>
        /// <returns>The character the <paramref name="position"/> within the source text.</returns>
        protected abstract Char GetCharCore(Int32 position);

        /// <summary>
        /// Produces a new <see cref="SourceText"/> that is the result of applying the specified
        /// set of changes to the current <see cref="SourceText"/> instance.
        /// </summary>
        /// <param name="changes">The changes to apply, in this text's coordinates. The changes are sorted by
        /// start, non-overlapping, and known to lie within this text..</param>
        /// <returns>The new <see cref="SourceText"/> snapshot, or this instance if no changes were applied.</returns>
        protected abstract SourceText ApplyChanges(ReadOnlySpan<TextChange> changes);

        /// <summary>
        /// Validates a position parameter.
        /// </summary>
        private void ValidatePosition(Int32 position, String? parameterName = "position")
        {
            ArgumentOutOfRangeException.ThrowIfNegative(position, parameterName);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Length, parameterName);
        }

        /// <summary>
        /// Validates a line index parameter.
        /// </summary>
        private void ValidateLineIndex(Int32 lineIndex, String? parameterName = "lineIndex")
        {
            ArgumentOutOfRangeException.ThrowIfNegative(lineIndex, parameterName);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lineIndex, LineCount, parameterName);
        }

        /// <summary>
        /// Validates a text span parameter.
        /// </summary>
        private void ValidateSpan(TextSpan span)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(span.End, Length, nameof(span));
        }

        /// <summary>
        /// Validates a change being applied to the text.
        /// </summary>
        private void ValidateChange(TextChange change, Int32 previousEnd, String? parameterName = "changes")
        {
            if (change.Span.End > Length)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName, change, $"The change runs past the end of a text of {Length} characters.");
            }

            if (change.Span.Start < previousEnd)
            {
                throw new ArgumentException(
                    $"The change {change} overlaps another change in the same batch.", parameterName);
            }
        }

        /// <summary>
        /// Gets an array containing the starting position of each line in the source text.
        /// </summary>
        private Int32[] LineStarts
        {
            get
            {
                var starts = _lineStarts;
                if (starts is null)
                {
                    var computed = ComputeLineStarts();
                    starts = Interlocked.CompareExchange(ref _lineStarts, computed, null) ?? computed;
                }

                return starts;
            }
        }

        /// <summary>
        /// Calculates the starting position of each line in the source text.
        /// </summary>
        /// <returns>An array containing the starting position of each line in the source text.</returns>
        private Int32[] ComputeLineStarts()
        {
            var position = 0;
            var length = Length;
            var starts = new List<int>() { 0 };

            while (position < length)
            {
                var character = this[position++];
                switch (character)
                {
                    case '\n':
                        starts.Add(position);
                        break;

                    case '\r':
                        if (position < length && this[position] == '\n')
                        {
                            position++;
                        }
                        starts.Add(position);
                        break;
                }
            }

            return [.. starts];
        }
    }
}
