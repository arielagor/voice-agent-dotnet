namespace VoiceAgent.Audio;

/// <summary>
/// ITU-T G.711 mu-law codec. Twilio Media Streams carry 8 kHz mono mu-law, base64 encoded,
/// in 20 ms frames (160 bytes). The VAD needs linear PCM, so every inbound frame is decoded here
/// before it is measured. Audio to the model is then resampled to 24 kHz PCM16, the path the
/// production line uses, so the port and the line compare like for like. OpenAI and xAI also accept
/// 8 kHz mu-law directly, which would skip both resamples; that is a measured trade-off left open.
/// </summary>
public static class MuLaw
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    private static readonly short[] DecodeTable = BuildDecodeTable();

    public static short Decode(byte encoded) => DecodeTable[encoded];

    public static byte Encode(short pcm)
    {
        int sample = pcm;
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = -sample;
        if (sample > Clip) sample = Clip;
        sample += Bias;

        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1)
            exponent--;

        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static void Decode(ReadOnlySpan<byte> encoded, Span<short> pcm)
    {
        if (pcm.Length < encoded.Length)
            throw new ArgumentException("PCM buffer is shorter than the encoded input.", nameof(pcm));
        for (int i = 0; i < encoded.Length; i++)
            pcm[i] = DecodeTable[encoded[i]];
    }

    public static void Encode(ReadOnlySpan<short> pcm, Span<byte> encoded)
    {
        if (encoded.Length < pcm.Length)
            throw new ArgumentException("Encoded buffer is shorter than the PCM input.", nameof(encoded));
        for (int i = 0; i < pcm.Length; i++)
            encoded[i] = Encode(pcm[i]);
    }

    private static short[] BuildDecodeTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int u = ~i & 0xFF;
            int sign = u & 0x80;
            int exponent = (u >> 4) & 0x07;
            int mantissa = u & 0x0F;
            int sample = (((mantissa << 3) + Bias) << exponent) - Bias;
            table[i] = (short)(sign != 0 ? -sample : sample);
        }
        return table;
    }
}
