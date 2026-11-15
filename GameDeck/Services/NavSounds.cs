using System.IO;
using System.Media;

namespace GameDeck.Services;

public enum NavSound { Move, Select, Back }

/// <summary>
/// The small sounds of Big Picture mode: a soft tick when the selection moves, a brighter blip for "select", a lower one for
/// "back". They are made here out of sine waves (no sound files to ship) and played through the normal Windows audio.
/// </summary>
public static class NavSounds
{
    private const int SampleRate = 22050;

    private static readonly Dictionary<NavSound, SoundPlayer> Players = [];

    public static void Play(NavSound sound)
    {
        if (!SettingsService.NavSounds) return;

        try
        {
            if (!Players.TryGetValue(sound, out var player))
            {
                player = new SoundPlayer(new MemoryStream(Build(sound)));
                player.Load();
                Players[sound] = player;
            }

            player.Play();   // asynchronous: a new sound simply replaces one that is still playing
        }
        catch
        {
            // no audio device or a blocked sound system: Big Picture simply stays quiet
        }
    }

    /// <summary>A mono 16-bit WAV file in memory: a short tone (or two) with a quick fade-out.</summary>
    internal static byte[] Build(NavSound sound)
    {
        var (first, second, milliseconds, volume) = sound switch
        {
            NavSound.Move => (1100.0, 1100.0, 80, 0.55),
            NavSound.Select => (880.0, 1320.0, 190, 0.65),
            _ => (660.0, 440.0, 190, 0.60)
        };

        var samples = SampleRate * milliseconds / 1000;
        var data = new short[samples];
        var phase = 0.0;

        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / samples;
            var frequency = t < 0.5 ? first : second;   // two steps: "select" rises, "back" falls

            phase += 2 * Math.PI * frequency / SampleRate;
            var fade = Math.Pow(1 - t, 1.5);            // fade-out: no click at the end
            var attack = Math.Min(1, i / 60.0);         // and none at the start

            data[i] = (short)(Math.Sin(phase) * fade * attack * volume * short.MaxValue);
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var bytes = samples * 2;
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
