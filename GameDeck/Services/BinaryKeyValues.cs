using System.Text;

namespace GameDeck.Services;

/// <summary>
/// Reads Valve's binary KeyValues, the format of Steam's appcache files (the achievements in appcache\stats): a tree of named
/// values, each written as a type byte, a name ending in a zero byte, then the value. A section holds values up to its end byte.
/// </summary>
public static class BinaryKeyValues
{
    private const byte Section = 0, Text = 1, Int32 = 2, Float32 = 3, Pointer = 4, WideText = 5, Color = 6, UInt64 = 7,
                       End = 8, Int64 = 10, AlternateEnd = 11;

    /// <summary>
    /// The values of the file: a section is a Dictionary&lt;string, object&gt;, a number an int, long, ulong or float, a text a
    /// string. Throws <see cref="FormatException"/> when the data isn't in this format (or is cut short).
    /// </summary>
    public static Dictionary<string, object> Parse(byte[] data)
    {
        var position = 0;
        return ReadSection(data, ref position, topLevel: true);
    }

    private static Dictionary<string, object> ReadSection(byte[] data, ref int position, bool topLevel)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            if (position >= data.Length)
            {
                if (topLevel) return values;   // (the file may simply end after its last section)
                throw new FormatException("The data ends inside a section.");
            }

            var type = data[position++];
            if (type is End or AlternateEnd) return values;

            var name = ReadText(data, ref position);

            values[name] = type switch
            {
                Section => ReadSection(data, ref position, topLevel: false),
                Text => ReadText(data, ref position),
                Int32 or Pointer or Color => BitConverter.ToInt32(Take(data, ref position, 4)),
                Float32 => BitConverter.ToSingle(Take(data, ref position, 4)),
                UInt64 => BitConverter.ToUInt64(Take(data, ref position, 8)),
                Int64 => BitConverter.ToInt64(Take(data, ref position, 8)),
                WideText => ReadWideText(data, ref position),
                _ => throw new FormatException($"Unknown value type {type}.")
            };
        }
    }

    private static ReadOnlySpan<byte> Take(byte[] data, ref int position, int count)
    {
        if (position + count > data.Length) throw new FormatException("The data ends inside a value.");

        var span = data.AsSpan(position, count);
        position += count;
        return span;
    }

    private static string ReadText(byte[] data, ref int position)
    {
        var end = Array.IndexOf(data, (byte)0, position);
        if (end < 0) throw new FormatException("A text has no end.");

        var text = Encoding.UTF8.GetString(data, position, end - position);
        position = end + 1;
        return text;
    }

    private static string ReadWideText(byte[] data, ref int position)
    {
        var start = position;
        while (position + 1 < data.Length && (data[position] != 0 || data[position + 1] != 0))
            position += 2;
        if (position + 1 >= data.Length) throw new FormatException("A text has no end.");

        var text = Encoding.Unicode.GetString(data, start, position - start);
        position += 2;
        return text;
    }
}
