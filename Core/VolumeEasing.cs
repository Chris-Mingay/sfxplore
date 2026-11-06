namespace SoundShelf.Core;

public enum VolumeMode { None, EaseIn, EaseOut, Both }
public enum EasingMethod
{
    Linear, easeInSine, easeOutSine, easeInOutSine,
    easeInQuad, easeOutQuad, easeInOutQuad,
    easeInCubic, easeOutCubic, easeInOutCubic,
    easeInCirc, easeOutCirc, easeInOutCirc
}

public sealed class VolumeEasing
{
    private VolumeMode _mode;
    public VolumeMode Mode
    {
        get => _mode;
        set { _mode = value; if (value == VolumeMode.Both && _inEnd > _outStart) _inEnd = _outStart; }
    }
    public float StartGain { get; set; }
    public float EndGain { get; set; }
    private double _inEnd = .2, _outStart = .8;
    public double InEnd { get => _inEnd; set => _inEnd = Math.Clamp(value, 0, 1); }
    public double OutStart { get => _outStart; set => _outStart = Math.Clamp(value, 0, 1); }
    public EasingMethod InMethod { get; set; }
    public EasingMethod OutMethod { get; set; }
    public bool HasIn => Mode is VolumeMode.EaseIn or VolumeMode.Both;
    public bool HasOut => Mode is VolumeMode.EaseOut or VolumeMode.Both;
}

public static class TrackVolume
{
    public static readonly float MaximumGain = (float)Math.Pow(10, 12.0 / 20);
    public static double Decibels(float gain) => gain <= 0 ? -60 : Math.Clamp(20 * Math.Log10(gain), -60, 12);
    public static float FromDecibels(double db) => db <= -60 ? 0 : (float)Math.Pow(10, Math.Clamp(db, -60, 12) / 20);
    public static string Describe(float gain) => gain <= 0 ? "−∞ dB (silent)" : $"{20 * Math.Log10(gain):0.0} dB";

    public static double Ease(EasingMethod method, double progress)
    {
        double x = Math.Clamp(progress, 0, 1);
        if (x is 0 or 1) return x;
        return method switch
        {
            EasingMethod.easeInSine => 1 - Math.Cos(x * Math.PI / 2),
            EasingMethod.easeOutSine => Math.Sin(x * Math.PI / 2),
            EasingMethod.easeInOutSine => (1 - Math.Cos(Math.PI * x)) / 2,
            EasingMethod.easeInQuad => x * x,
            EasingMethod.easeOutQuad => 1 - (1 - x) * (1 - x),
            EasingMethod.easeInOutQuad => x < .5 ? 2 * x * x : 1 - Math.Pow(-2 * x + 2, 2) / 2,
            EasingMethod.easeInCubic => x * x * x,
            EasingMethod.easeOutCubic => 1 - Math.Pow(1 - x, 3),
            EasingMethod.easeInOutCubic => x < .5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2,
            EasingMethod.easeInCirc => 1 - Math.Sqrt(1 - x * x),
            EasingMethod.easeOutCirc => Math.Sqrt(1 - (x - 1) * (x - 1)),
            EasingMethod.easeInOutCirc => x < .5 ? (1 - Math.Sqrt(1 - 4 * x * x)) / 2 : (Math.Sqrt(1 - Math.Pow(-2 * x + 2, 2)) + 1) / 2,
            _ => x
        };
    }

    // Complete track gain, shared by the waveform and both rendering paths.
    public static float Gain(MixTrack track, double sourceSeconds)
    {
        var v = track.Volume;
        if (v == null) return track.Gain * MixRenderer.EnvelopeGain(track.Envelope, sourceSeconds);
        double span = track.TrimEnd - track.TrimStart;
        double x = span <= 0 ? 0 : Math.Clamp((sourceSeconds - track.TrimStart) / span, 0, 1);
        if (v.HasIn && v.InEnd > 0 && x < v.InEnd)
            return (float)(v.StartGain + (track.Gain - v.StartGain) * Ease(v.InMethod, x / v.InEnd));
        if (v.HasOut && v.OutStart < 1 && x > v.OutStart)
            return (float)(track.Gain + (v.EndGain - track.Gain) * Ease(v.OutMethod, (x - v.OutStart) / (1 - v.OutStart)));
        return track.Gain;
    }
}
