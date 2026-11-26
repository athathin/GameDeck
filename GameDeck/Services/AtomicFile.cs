using System.IO;

namespace GameDeck.Services;

/// <summary>
/// Saves a file so that it is never left half written: the text goes to a temporary file first, which then replaces the
/// real one in a single step. A crash or a power cut in the middle of a save keeps the old, complete file.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
