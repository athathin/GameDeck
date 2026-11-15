using System.IO;
using System.Windows.Media;

namespace GameDeck.Services;

public enum NavSound { Move, Select, Back, Launch }

/// <summary>
/// The small sounds of Big Picture mode: a soft tick when the selection moves, a brighter blip for "select", a lower one for
/// "back", and a short rising chord when a game starts. They are made here out of sine waves (no sound files to ship), saved once as small .wav files in GameDeck's data
/// folder, and played with a media player per sound (so GameDeck shows up in the Windows volume mixer while it plays).
/// </summary>
public static class NavSounds
{
    private const int SampleRate = 22050;

    private static readonly Dictionary<NavSound, MediaPlayer> Players = [];
    private static bool _failureReported;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "sounds");

    /// <summary>Opens the three sounds, so the first one is not late. Call on the UI thread (a media player belongs to it).</summary>
    public static void Preload()
    {
        foreach (var sound in Enum.GetValues<NavSound>())
            PlayerFor(sound);
    }

    public static void Play(NavSound sound)
    {
        if (!SettingsService.NavSounds) return;

        try
        {
            var player = PlayerFor(sound);
            if (player is null) return;

            if (sound == NavSound.Launch)   // the launch sound stands alone: nothing else rings over it
            {
                foreach (var other in Players.Where(p => p.Key != sound))
                    other.Value.Stop();
            }

            player.Stop();
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (Exception ex)
        {
            Report(ex);   // (Big Picture simply stays quiet)
        }
    }

    private static MediaPlayer? PlayerFor(NavSound sound)
    {
        if (Players.TryGetValue(sound, out var existing))
            return existing;

        try
        {
            Directory.CreateDirectory(Folder);

            var file = Path.Combine(Folder, sound.ToString().ToLowerInvariant() + ".wav");
            var wav = Build(sound);
            if (!File.Exists(file) || new FileInfo(file).Length != wav.Length)
                File.WriteAllBytes(file, wav);

            var player = new MediaPlayer { Volume = 1.0 };
            player.MediaFailed += (_, e) => Report(e.ErrorException);
            player.Open(new Uri(file));

            Players[sound] = player;
            return player;
        }
        catch (Exception ex)
        {
            Report(ex);
            return null;
        }
    }

    // a sound that can't play is written to the error log once, so there is something to look at
    private static void Report(Exception? ex)
    {
        if (_failureReported) return;

        _failureReported = true;
        CrashLog.Write(ex, "Navigation sound");
    }

    /// <summary>A mono 16-bit WAV file in memory: a few soft notes, each with a gentle start and a fade-out.</summary>
    internal static byte[] Build(NavSound sound)
    {
        // (frequency in Hz, length in ms) per note; low and mellow, nothing shrill
        (double Frequency, int Milliseconds)[] notes;
        double volume;

        switch (sound)
        {
            case NavSound.Move:
                notes = [(440.0, 70)];
                volume = 0.45;
                break;
            case NavSound.Select:   // up a little: "yes"
                notes = [(330.0, 80), (494.0, 130)];
                volume = 0.55;
                break;
            case NavSound.Back:     // down a little: "back"
                notes = [(392.0, 80), (294.0, 130)];
                volume = 0.50;
                break;
            default:                // launch: a rising chord, C - E - G - C
                notes = [(262.0, 110), (330.0, 110), (392.0, 110), (523.0, 260)];
                volume = 0.55;
                break;
        }

        var data = new List<short>();

        foreach (var (frequency, milliseconds) in notes)
        {
            var samples = SampleRate * milliseconds / 1000;
            var phase = 0.0;

            for (var i = 0; i < samples; i++)
            {
                var t = (double)i / samples;
                phase += 2 * Math.PI * frequency / SampleRate;

                var tone = Math.Sin(phase) + 0.25 * Math.Sin(2 * phase);   // a touch of the octave above: rounder than a bare sine
                var fade = Math.Pow(1 - t, 1.5);                           // fade-out: no click between notes or at the end
                var attack = Math.Min(1, i / 120.0);                       // soft start

                data.Add((short)(tone / 1.25 * fade * attack * volume * short.MaxValue));
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var bytes = data.Count * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + bytes);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);            // PCM
            writer.Write((short)1);            // mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);      // bytes per second
            writer.Write((short)2);            // bytes per sample
            writer.Write((short)16);           // bits per sample
            writer.Write("data"u8);
            writer.Write(bytes);
            foreach (var sample in data)
                writer.Write(sample);
        }

        return stream.ToArray();
    }
}
