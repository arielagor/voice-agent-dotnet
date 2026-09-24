using VoiceAgent.Audio;

namespace VoiceAgent.Tests;

public class MuLawTests
{
    [Theory]
    [InlineData(0xFF, 0)]
    [InlineData(0x7F, 0)]
    [InlineData(0x80, 32124)]
    [InlineData(0x00, -32124)]
    [InlineData(0xFE, 8)]
    [InlineData(0x7E, -8)]
    public void Decode_matches_the_G711_table(int encoded, int expected)
    {
        Assert.Equal(expected, MuLaw.Decode((byte)encoded));
    }

    [Fact]
    public void Every_code_except_negative_zero_survives_a_round_trip()
    {
        for (int b = 0; b < 256; b++)
        {
            if (b == 0x7F) continue; // -0 decodes to 0, which re-encodes as +0 (0xFF)
            Assert.Equal((byte)b, MuLaw.Encode(MuLaw.Decode((byte)b)));
        }
    }

    [Fact]
    public void Encoding_clips_instead_of_wrapping_at_full_scale()
    {
        Assert.Equal(0x80, MuLaw.Encode(short.MaxValue));
        Assert.Equal(0x00, MuLaw.Encode(short.MinValue));
    }

    [Fact]
    public void Quantization_error_stays_inside_the_segment_step()
    {
        // mu-law is logarithmic: the step size doubles each segment, so the error bound is relative.
        for (int pcm = -32000; pcm <= 32000; pcm += 37)
        {
            int roundTrip = MuLaw.Decode(MuLaw.Encode((short)pcm));
            int error = Math.Abs(roundTrip - pcm);
            Assert.True(error <= Math.Max(8, Math.Abs(pcm) / 16), $"pcm={pcm} roundTrip={roundTrip}");
        }
    }
}

public class EnergyVadTests
{
    private const int Frame = 160;

    [Fact]
    public void Silence_never_triggers()
    {
        var vad = new EnergyVad();
        foreach (var frame in Signal.Noise(dbfs: -65, frames: 200, seed: 1))
            Assert.Equal(VadEvent.None, vad.Process(frame));
    }

    [Fact]
    public void Speech_burst_yields_exactly_one_start_then_one_stop_after_the_hangover()
    {
        var vad = new EnergyVad();
        var events = new List<(int frame, VadEvent evt)>();
        var frames = Signal.Noise(-65, 25, seed: 2)
            .Concat(Signal.Tone(440, -18, 20))      // 400 ms of "speech"
            .Concat(Signal.Noise(-65, 50, seed: 3)) // 1 s of line noise
            .ToList();

        for (int i = 0; i < frames.Count; i++)
        {
            var evt = vad.Process(frames[i]);
            if (evt != VadEvent.None) events.Add((i, evt));
        }

        Assert.Equal(2, events.Count);
        Assert.Equal((25 + 2, VadEvent.SpeechStarted), events[0]); // onset debounce: 3rd voiced frame
        Assert.Equal((45 + 24, VadEvent.SpeechStopped), events[1]); // hangover: 25th unvoiced frame
    }

    [Fact]
    public void A_single_click_is_rejected_by_the_onset_debounce()
    {
        var vad = new EnergyVad();
        var frames = Signal.Noise(-65, 20, 4).Concat(Signal.Tone(1000, -10, 1)).Concat(Signal.Noise(-65, 20, 5));
        Assert.DoesNotContain(frames.Select(f => vad.Process(f)), e => e == VadEvent.SpeechStarted);
    }

    [Fact]
    public void Steady_background_noise_is_learned_as_floor_and_speech_above_it_still_triggers()
    {
        var vad = new EnergyVad();
        // The call connects straight into 6 s of loud steady noise (a dealership floor, a car cabin).
        var noiseEvents = Signal.Noise(-32, 300, 6).Select(f => vad.Process(f)).Where(e => e != VadEvent.None).ToList();

        Assert.Empty(noiseEvents);
        Assert.False(vad.InSpeech);
        Assert.InRange(vad.NoiseFloorDbfs, -36, -29);

        var speech = Signal.Tone(300, -14, 10).Select(f => vad.Process(f)).ToList();
        Assert.Contains(VadEvent.SpeechStarted, speech);
    }

    [Fact]
    public void No_events_fire_while_calibrating_even_if_the_caller_talks_at_pickup()
    {
        var vad = new EnergyVad();
        var events = Signal.Tone(300, -14, 10).Select(f => vad.Process(f)).ToList();
        Assert.All(events, e => Assert.Equal(VadEvent.None, e));
        Assert.False(vad.Calibrating);
    }

    [Fact]
    public void Floor_does_not_adapt_upward_during_a_long_utterance()
    {
        var vad = new EnergyVad();
        foreach (var f in Signal.Noise(-65, 25, 7)) vad.Process(f);
        double floorBefore = vad.NoiseFloorDbfs;
        foreach (var f in Signal.Tone(220, -18, 250)) vad.Process(f); // 5 s monologue
        Assert.True(vad.InSpeech);
        Assert.Equal(floorBefore, vad.NoiseFloorDbfs, precision: 6);
    }
}

internal static class Signal
{
    public static IEnumerable<short[]> Tone(double hz, double dbfs, int frames, int rate = 8000)
    {
        double amplitude = 32768.0 * Math.Pow(10, dbfs / 20.0) * Math.Sqrt(2); // RMS -> peak
        long n = 0;
        for (int f = 0; f < frames; f++)
        {
            var frame = new short[160];
            for (int i = 0; i < frame.Length; i++, n++)
                frame[i] = (short)Math.Clamp(amplitude * Math.Sin(2 * Math.PI * hz * n / rate), short.MinValue, short.MaxValue);
            yield return frame;
        }
    }

    public static IEnumerable<short[]> Noise(double dbfs, int frames, int seed)
    {
        var rng = new Random(seed);
        double sigma = 32768.0 * Math.Pow(10, dbfs / 20.0);
        for (int f = 0; f < frames; f++)
        {
            var frame = new short[160];
            for (int i = 0; i < frame.Length; i++)
            {
                double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                double gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                frame[i] = (short)Math.Clamp(sigma * gaussian, short.MinValue, short.MaxValue);
            }
            yield return frame;
        }
    }
}
