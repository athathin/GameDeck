using System.IO;
using System.Windows.Media;

namespace GameDeck.Services;

public enum NavSound { Move, Select, Back, Launch }

/// <summary>
/// The small sounds of Big Picture mode: a soft tick when the selection moves, a brighter blip for "select", a lower one for
/// "back", and a short rising chord when a game starts. They are made here out of sine waves (no sound files to ship), saved once as small .wav files in GameDeck's data
/// folder, and played with a media player per sound, made fresh each time Big Picture opens (so GameDeck shows up in the Windows volume mixer while it plays).
/// </summary>
public static class NavSounds
{
    private const int SampleRate = 22050;

    /// <summary>How long the launch sound takes (a little more than the sound itself): Big Picture waits this long before it closes.</summary>
    public const int LaunchMilliseconds = 650;

    /// <summary>One sound's player. A sound asked for before its file has finished opening is played as soon as it has.</summary>
    private sealed class Voice
    {
        public readonly MediaPlayer Player = new() { Volume = 1.0 };
        public bool Opened;
        public bool PlayWhenOpened;
    }

    private static readonly Dictionary<NavSound, Voice> Voices = [];
    private static bool _failureReported;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "sounds");

    /// <summary>
    /// Opens all the sounds from scratch (call when Big Picture opens, on the UI thread: a media player belongs to it). Players
    /// kept from an earlier visit are closed first: one that has sat idle through a window closing, a game starting or the audio
    /// device changing can stay silent, so every visit starts with fresh ones.
    /// </summary>
    public static void Preload()
    {
        Release();

        foreach (var sound in Enum.GetValues<NavSound>())
            VoiceFor(sound);
    }

    // players put aside (Big Picture closed or opened again) that may still be finishing a sound
    private static readonly List<Voice> Retired = [];
    private static System.Windows.Threading.DispatcherTimer? _retireTimer;

    /// <summary>
    /// Puts the players away (Big Picture is closing). A sound still playing, like the blip of the "Exit" button, is not cut off:
    /// the players are closed a little later.
    /// </summary>
    public static void Release()
    {
        Retired.AddRange(Voices.Values);
        Voices.Clear();

        if (Retired.Count == 0) return;

        _retireTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retireTimer.Tick -= CloseRetired;
        _retireTimer.Tick += CloseRetired;
        _retireTimer.Stop();
        _retireTimer.Start();   // (again from now: whatever was put away last gets its full time)
    }

    private static void CloseRetired(object? sender, EventArgs e)
    {
        _retireTimer?.Stop();

        foreach (var voice in Retired)
        {
            try { voice.Player.Close(); }
            catch (Exception ex) { Report(ex); }
        }

        Retired.Clear();
    }

    public static void Play(NavSound sound)
    {
        if (!SettingsService.NavSounds) return;

        try
        {
            var voice = VoiceFor(sound);
            if (voice is null) return;

            if (sound == NavSound.Launch)   // the launch sound stands alone: nothing else rings over it
            {
                foreach (var other in Voices.Where(v => v.Key != sound))
                    other.Value.Player.Stop();
            }

            if (!voice.Opened)
                voice.PlayWhenOpened = true;
            else
                Restart(voice);
        }
        catch (Exception ex)
        {
            Voices.Remove(sound);   // the next time starts from a fresh player
            Report(ex);             // (Big Picture simply stays quiet meanwhile)
        }
    }

    private static void Restart(Voice voice)
    {
        voice.Player.Position = TimeSpan.Zero;
        voice.Player.Play();
    }

    private static Voice? VoiceFor(NavSound sound)
    {
        if (Voices.TryGetValue(sound, out var existing))
            return existing;

        try
        {
            Directory.CreateDirectory(Folder);

            var file = Path.Combine(Folder, sound.ToString().ToLowerInvariant() + ".wav");
            var wav = Build(sound);
            if (!File.Exists(file) || new FileInfo(file).Length != wav.Length)
                File.WriteAllBytes(file, wav);

            var voice = new Voice();
            voice.Player.MediaOpened += (_, _) =>
            {
                voice.Opened = true;
                if (voice.PlayWhenOpened)
                {
                    voice.PlayWhenOpened = false;
                    Restart(voice);
                }
            };
            voice.Player.MediaFailed += (_, e) =>
            {
                Voices.Remove(sound);
                Report(e.ErrorException);
            };
            voice.Player.Open(new Uri(file));

            Voices[sound] = voice;
            return voice;
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
