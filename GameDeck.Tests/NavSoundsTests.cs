using System.Text;
using GameDeck.Services;

namespace GameDeck.Tests;

public class NavSoundsTests
{
    [Theory]
    [InlineData(NavSound.Move)]
    [InlineData(NavSound.Select)]
    [InlineData(NavSound.Back)]
    [InlineData(NavSound.Launch)]
    [InlineData(NavSound.Enter)]
    [InlineData(NavSound.Exit)]
    public void Each_sound_is_a_valid_wav_file(NavSound sound)
    {
        var wav = NavSounds.Build(sound);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        var dataBytes = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, dataBytes);                  // the header's length matches the samples
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4)); // and so does the RIFF size
        Assert.InRange(dataBytes, 1000, 40000);                    // a short blip, not silence and not a long tune
    }

    [Fact]
    public void The_launch_sound_is_over_before_big_picture_stops_waiting_for_it()
    {
        var wav = NavSounds.Build(NavSound.Launch);
        var milliseconds = (wav.Length - 44) / 2 * 1000 / 22050;

        Assert.InRange(milliseconds, 300, NavSounds.LaunchMilliseconds);
    }

    [Fact]
    public void A_sound_fades_out_to_silence_so_it_does_not_click()
    {
        var wav = NavSounds.Build(NavSound.Select);
        var last = BitConverter.ToInt16(wav, wav.Length - 2);

        Assert.InRange(Math.Abs((int)last), 0, 200);
    }
}
