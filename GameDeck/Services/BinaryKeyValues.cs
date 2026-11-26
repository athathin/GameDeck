using System.Text;

namespace GameDeck.Services;

/// <summary>
/// Reads Valve's binary KeyValues, the format of Steam's appcache files (the achievements in appcache\stats, appinfo.vdf): a tree
/// of named values, each written as a type byte, a name, then the value. A section holds values up to its end byte. A name is
/// text ending in a zero byte, or (appinfo.vdf since 2024) a number pointing into a table of names kept at the end of the file.
/// </summary>
public static class BinaryKeyValues
{
    private const byte Section = 0, Text = 1, Int32 = 2, Float32 = 3, Pointer = 4, WideText = 5, Color = 6, UInt64 = 7,
                       End = 8, Int64 = 10, AlternateEnd = 11;

    /// <summary>
    /// The values of the file: a section is a Dictionary&lt;string, object&gt;, a number an int, long, ulong or float, a text a
    /// string. Throws <see cref="FormatException"/> when the data isn't in this format (or is cut short).
    /// </summary>
    public static Dictionary<string, object> Parse(byte[] data) => Parse(data, 0, data.Length, null);

    /// <summary>
    /// The values of <paramref name="length"/> bytes from <paramref name="start"/>; with <paramref name="names"/>, each name is
    /// a 4-byte number pointing into that table instead of text.
    /// </summary>
    public static Dictionary<string, object> Parse(byte[] data, int start, int length, IReadOnlyList<string>? names)
    {
        if (start < 0 || length < 0 || start + length > data.Length) throw new FormatException("The data is cut short.");

        var reader = new Reader(data, start, start + length, names);
        return reader.ReadSection(topLevel: true);
    }

    private sealed class Reader(byte[] data, int position, int end, IReadOnlyList<string>? names)
    {
        public Dictionary<string, object> ReadSection(bool topLevel)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                if (position >= end)
                {
                    if (topLevel) return values;   // (the data may simply end after its last section)
                    throw new FormatException("The data ends inside a section.");
                }

                var type = data[position++];
                if (type is End or AlternateEnd) return values;

                var name = ReadName();

                values[name] = type switch
                {
                    Section => ReadSection(topLevel: false),
                    Text => ReadText(),
                    Int32 or Pointer or Color => BitConverter.ToInt32(Take(4)),
                    Float32 => BitConverter.ToSingle(Take(4)),
                    UInt64 => BitConverter.ToUInt64(Take(8)),
                    Int64 => BitConverter.ToInt64(Take(8)),
                    WideText => ReadWideText(),
                    _ => throw new FormatException($"Unknown value type {type}.")
                };
            }
        }

        private string ReadName()
        {
            if (names is null) return ReadText();

            var index = BitConverter.ToInt32(Take(4));
            if (index < 0 || index >= names.Count) throw new FormatException("A name points outside the table of names.");
            return names[index];
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (position + count > end) throw new FormatException("The data ends inside a value.");

            var span = data.AsSpan(position, count);
            position += count;
            return span;
        }

        private string ReadText()
        {
            var stop = Array.IndexOf(data, (byte)0, position, end - position);
            if (stop < 0) throw new FormatException("A text has no end.");

            var text = Encoding.UTF8.GetString(data, position, stop - position);
            position = stop + 1;
            return text;
        }

        private string ReadWideText()
        {
            var start = position;
            while (position + 1 < end && (data[position] != 0 || data[position + 1] != 0))
                position += 2;
            if (position + 1 >= end) throw new FormatException("A text has no end.");

            var text = Encoding.Unicode.GetString(data, start, position - start);
            position += 2;
            return text;
        }
    }
}
