namespace VoiceAgent.Audio;

public enum VadEvent { None, SpeechStarted, SpeechStopped }

public sealed record VadOptions
{
    /// <summary>Samples per frame. 160 = 20 ms at 8 kHz, one Twilio media frame.</summary>
    public int FrameSamples { get; init; } = 160;
    public int SampleRate { get; init; } = 8000;

    /// <summary>A frame is voiced when it is this many dB above the tracked noise floor...</summary>
    public double SnrThresholdDb { get; init; } = 9.0;

    /// <summary>...and above this absolute level, so a silent line never self-triggers.</summary>
    public double AbsoluteFloorDbfs { get; init; } = -48.0;

    /// <summary>Consecutive voiced frames needed to declare speech. Rejects clicks and line pops.</summary>
    public int OnsetFrames { get; init; } = 3;

    /// <summary>Consecutive unvoiced frames needed to declare the turn over (the hangover).</summary>
    public int HangoverFrames { get; init; } = 25;

    /// <summary>How fast the noise floor may rise per unvoiced frame. It falls immediately.</summary>
    public double FloorRiseDbPerFrame { get; init; } = 0.05;

    /// <summary>
    /// Frames at the start of a call used only to measure the line. The floor is seeded with
    /// the quietest of them and no events fire. Without this, a call that connects into steady
    /// noise louder than the initial floor reads as speech at once, and because the floor is
    /// frozen during speech it never recovers. Inbound calls open with the agent's greeting,
    /// so the first 200 ms of caller audio is almost always line noise.
    /// </summary>
    public int CalibrationFrames { get; init; } = 10;

    /// <summary>Only used when <see cref="CalibrationFrames"/> is 0.</summary>
    public double InitialFloorDbfs { get; init; } = -70.0;
}

/// <summary>
/// Energy VAD with an adaptive noise floor, onset debounce and a hangover.
///
/// This is deliberately not the turn detector. The realtime model runs its own server-side
/// turn detection; this VAD exists for the one decision that cannot wait for a network round
/// trip: barge-in. When the caller starts talking over the agent, the bridge has to flush the
/// audio already queued at Twilio within a frame or two, or the caller hears the agent keep
/// talking over them. The floor tracks line noise (car cabins, dealership floors, speakerphone
/// hiss) so a steady background does not read as speech.
/// </summary>
public sealed class EnergyVad
{
    private readonly VadOptions _options;
    private double _floorDbfs;
    private int _voicedRun;
    private int _unvoicedRun;
    private long _framesSeen;

    public EnergyVad(VadOptions? options = null)
    {
        _options = options ?? new VadOptions();
        _floorDbfs = _options.CalibrationFrames > 0 ? double.PositiveInfinity : _options.InitialFloorDbfs;
    }

    public bool Calibrating => _framesSeen < _options.CalibrationFrames;

    public bool InSpeech { get; private set; }
    public double NoiseFloorDbfs => _floorDbfs;
    public double LastFrameDbfs { get; private set; } = double.NegativeInfinity;

    /// <summary>Elapsed audio time, derived from frames processed rather than the wall clock.</summary>
    public TimeSpan AudioTime => TimeSpan.FromSeconds((double)_framesSeen * _options.FrameSamples / _options.SampleRate);

    public VadEvent Process(ReadOnlySpan<short> frame)
    {
        double level = Dbfs(frame);
        LastFrameDbfs = level;

        if (Calibrating)
        {
            _framesSeen++;
            _floorDbfs = Math.Min(_floorDbfs, level);
            return VadEvent.None;
        }
        _framesSeen++;

        bool voiced = level > _options.AbsoluteFloorDbfs && level - _floorDbfs >= _options.SnrThresholdDb;

        if (voiced)
        {
            _voicedRun++;
            _unvoicedRun = 0;
        }
        else
        {
            _unvoicedRun++;
            _voicedRun = 0;
            // Asymmetric tracking: drop to a quieter floor at once, creep up slowly, and never
            // adapt while someone is talking, or a long sentence would teach the floor to be speech.
            if (!InSpeech)
                _floorDbfs = level < _floorDbfs ? level : Math.Min(level, _floorDbfs + _options.FloorRiseDbPerFrame);
        }

        if (!InSpeech && _voicedRun >= _options.OnsetFrames)
        {
            InSpeech = true;
            return VadEvent.SpeechStarted;
        }

        if (InSpeech && _unvoicedRun >= _options.HangoverFrames)
        {
            InSpeech = false;
            return VadEvent.SpeechStopped;
        }

        return VadEvent.None;
    }

    public static double Dbfs(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty) return double.NegativeInfinity;
        double sumSquares = 0;
        foreach (short s in frame) sumSquares += (double)s * s;
        double rms = Math.Sqrt(sumSquares / frame.Length);
        return rms <= 0 ? -120.0 : 20.0 * Math.Log10(rms / 32768.0);
    }
}
