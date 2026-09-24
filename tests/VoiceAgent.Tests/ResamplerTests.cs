using VoiceAgent.Audio;

namespace VoiceAgent.Tests;

public class ResamplerTests
{
    private static short[] Tone(double hz, double peak, int samples, int rate) =>
        Enumerable.Range(0, samples).Select(n => (short)Math.Round(peak * Math.Sin(2 * Math.PI * hz * n / rate))).ToArray();

    private static double Rms(ReadOnlySpan<short> s)
    {
        double acc = 0;
        foreach (var v in s) acc += (double)v * v;
        return Math.Sqrt(acc / s.Length);
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio);

    [Fact]
    public void Downsampler_passes_speech_band_at_unity_gain()
    {
        var input = Tone(1000, 10000, 24000, 24000);
        var output = new Downsampler24To8().Process(input);
        Assert.Equal(8000, output.Length);
        double gainDb = Db(Rms(output.AsSpan(100)) / Rms(input));
        Assert.InRange(gainDb, -0.5, 0.5);
    }

    [Fact]
    public void Downsampler_rejects_content_that_would_alias_into_the_band()
    {
        // 6 kHz at 24 kHz folds to 2 kHz after decimation to 8 kHz.
        var input = Tone(6000, 10000, 24000, 24000);
        var filtered = new Downsampler24To8().Process(input);
        var naive = NaiveThreeTapAverage(input);

        double filteredDb = Db(Rms(filtered.AsSpan(100)) / Rms(input));
        double naiveDb = Db(Rms(naive.AsSpan(100)) / Rms(input));

        Assert.True(filteredDb < -40, $"filtered alias at {filteredDb:F1} dB");
        Assert.InRange(naiveDb, -10.5, -8.5); // what the 3-tap average actually leaves
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    [InlineData(4801)]
    public void Downsampler_output_does_not_depend_on_chunking(int seed)
    {
        var input = Tone(700, 12000, 7200, 24000);
        var whole = new Downsampler24To8().Process(input);

        var rng = new Random(seed);
        var chunked = new Downsampler24To8();
        var pieces = new List<short>();
        for (int i = 0; i < input.Length;)
        {
            int n = Math.Min(rng.Next(1, 900), input.Length - i);
            pieces.AddRange(chunked.Process(input.AsSpan(i, n)));
            i += n;
        }
        Assert.Equal(whole, pieces.ToArray());
    }

    [Fact]
    public void Upsampler_output_does_not_depend_on_chunking_and_has_no_frame_edge_steps()
    {
        var input = Tone(440, 12000, 1600, 8000);
        var whole = new Upsampler8To24().Process(input);

        var framed = new Upsampler8To24();
        var pieces = new List<short>();
        for (int i = 0; i < input.Length; i += 160)
            pieces.AddRange(framed.Process(input.AsSpan(i, 160)));

        Assert.Equal(whole, pieces.ToArray());
        Assert.Equal(input.Length * 3, whole.Length);

        // A 440 Hz tone at 24 kHz never moves more than ~1,400 counts per sample at this level.
        int maxStep = 0;
        for (int i = 1; i < whole.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(whole[i] - whole[i - 1]));
        Assert.True(maxStep < 1500, $"max step {maxStep}");
    }

    [Fact]
    public void Byte_carry_reassembles_samples_split_across_deltas()
    {
        short[] samples = [1, -2, 300, -32768, 32767];
        var bytes = samples.SelectMany(s => new[] { (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF) }).ToArray();

        var carry = new Pcm16ByteCarry();
        var result = new List<short>();
        result.AddRange(carry.Push(bytes.AsSpan(0, 3)));
        result.AddRange(carry.Push(bytes.AsSpan(3, 4)));
        result.AddRange(carry.Push(bytes.AsSpan(7, 3)));

        Assert.Equal(samples, result.ToArray());
    }

    private static short[] NaiveThreeTapAverage(short[] input)
    {
        var output = new short[input.Length / 3];
        for (int i = 0; i < output.Length; i++)
            output[i] = (short)Math.Round((input[i * 3] + input[i * 3 + 1] + input[i * 3 + 2]) / 3.0);
        return output;
    }
}
