namespace VoiceAgent.Audio;

/// <summary>
/// 8 kHz to 24 kHz, 3x linear interpolation, carrying the previous sample across calls.
///
/// The TypeScript bridge this ports interpolated each 20 ms frame in isolation and held the
/// last sample flat at every frame edge, which puts a small step into the signal 50 times a
/// second. Carrying one sample of state removes it, at the cost of 125 microseconds of delay.
/// </summary>
public sealed class Upsampler8To24
{
    private short _previous;
    private bool _primed;

    public short[] Process(ReadOnlySpan<short> input)
    {
        var output = new short[input.Length * 3];
        for (int i = 0; i < input.Length; i++)
        {
            short current = input[i];
            if (!_primed)
            {
                _previous = current;
                _primed = true;
            }
            int a = _previous, delta = current - _previous;
            output[i * 3] = (short)a;
            output[i * 3 + 1] = (short)Math.Round(a + delta / 3.0);
            output[i * 3 + 2] = (short)Math.Round(a + 2.0 * delta / 3.0);
            _previous = current;
        }
        return output;
    }
}

/// <summary>
/// 24 kHz to 8 kHz: a 31-tap Hamming-windowed sinc low-pass (cutoff 3.4 kHz, the top of the
/// telephone band) followed by 3:1 decimation. Filter history and decimation phase both carry
/// across calls, so the output does not depend on how the model happened to chunk its audio.
///
/// The ported version averaged three samples, which leaves a 6 kHz component only 9.5 dB down
/// and folds it to 2 kHz, the middle of the band a caller actually hears. This filter puts it
/// more than 40 dB down for 0.6 ms of added delay.
/// </summary>
public sealed class Downsampler24To8
{
    public const int Taps = 31;
    private static readonly double[] Coefficients = Design(Taps, cutoffHz: 3400, sampleRate: 24000);

    private readonly short[] _history = new short[Taps - 1];
    private long _inputIndex;

    public short[] Process(ReadOnlySpan<short> input)
    {
        int historyLength = _history.Length;
        var work = new short[historyLength + input.Length];
        _history.CopyTo(work, 0);
        input.CopyTo(work.AsSpan(historyLength));

        var output = new List<short>(input.Length / 3 + 1);
        for (int i = 0; i < input.Length; i++)
        {
            if ((_inputIndex + i) % 3 != 0) continue;
            int newest = historyLength + i;
            double acc = 0;
            for (int k = 0; k < Taps; k++)
                acc += Coefficients[k] * work[newest - k];
            output.Add((short)Math.Clamp(Math.Round(acc), short.MinValue, short.MaxValue));
        }

        _inputIndex += input.Length;
        work.AsSpan(work.Length - historyLength).CopyTo(_history);
        return output.ToArray();
    }

    internal static double[] Design(int taps, double cutoffHz, double sampleRate)
    {
        var h = new double[taps];
        double fc = cutoffHz / sampleRate;
        int mid = (taps - 1) / 2;
        for (int n = 0; n < taps; n++)
        {
            int m = n - mid;
            double sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (taps - 1));
            h[n] = sinc * window;
        }
        double sum = h.Sum();
        for (int n = 0; n < taps; n++) h[n] /= sum; // unity gain at DC
        return h;
    }
}

/// <summary>
/// Base64 PCM16 deltas from the model are not guaranteed to split on a sample boundary.
/// A dangling odd byte is held for the next delta instead of being dropped or misaligned,
/// which would turn every subsequent sample into noise.
/// </summary>
public sealed class Pcm16ByteCarry
{
    private byte? _pending;

    public short[] Push(ReadOnlySpan<byte> bytes)
    {
        int total = bytes.Length + (_pending.HasValue ? 1 : 0);
        var samples = new short[total / 2];
        int s = 0, i = 0;

        if (_pending.HasValue && bytes.Length > 0)
        {
            samples[s++] = (short)(_pending.Value | (bytes[0] << 8));
            _pending = null;
            i = 1;
        }
        for (; i + 1 < bytes.Length; i += 2)
            samples[s++] = (short)(bytes[i] | (bytes[i + 1] << 8));
        if (i < bytes.Length)
            _pending = bytes[i];

        return s == samples.Length ? samples : samples[..s];
    }
}
