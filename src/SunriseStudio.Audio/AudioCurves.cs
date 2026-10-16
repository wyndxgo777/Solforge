namespace SunriseStudio.Audio;

/** Curve identifiers are stored in authored fade parameters, not chosen by the mixer. */
public static class AudioCurves
{
    /** Fade curve value at progress 0..1; fade-outs mirror the curve index except the symmetric ones. */
    public static double Interpolate(int curve, double progress, bool fadeIn)
    {
        if (!double.IsFinite(progress)) throw new InvalidDataException("Music fade progress is not finite.");
        if (curve is < 0 or > 8) throw new NotSupportedException($"Music fade curve {curve} is invalid.");
        int selected = fadeIn || curve is 3 or 5 ? curve : 8 - curve;
        return Evaluate(selected, (float)Math.Clamp(progress, 0, 1), fadeIn ? 0 : 1, fadeIn ? 1 : 0);
    }

    /** Authored graph curves retain their single-precision operation order and polynomial coefficients. */
    public static float Evaluate(int curve, float x, float from, float to)
    {
        if (!float.IsFinite(x) || !float.IsFinite(from) || !float.IsFinite(to) || x is < 0 or > 1)
            throw new InvalidDataException("Music curve inputs are invalid.");
        switch (curve)
        {
            case 0:
                float inverse = 1f - x;
                return inverse * inverse * inverse * (from - to) + to;
            case 1:
                float angle = x * 1.5707963705062866f, square = angle * angle;
                float sine = .008306325413286686f - square * .00018363654089625925f;
                sine = sine * square - .1666482836008072f;
                sine = sine * square + .9999966025352478f;
                return sine * angle * (to - from) + from;
            case 2: return (x - 3f) * x * .5f * (from - to) + from;
            case 3:
                float halfAngle = x * 3.1415927410125732f;
                if (x > .5f) halfAngle = 3.1415927410125732f - halfAngle;
                float halfSquare = halfAngle * halfAngle;
                float halfSine = .004153162706643343f - halfSquare * .00009181827044812962f;
                halfSine = halfSine * halfSquare - .0833241418004036f;
                halfSine = halfSine * halfSquare + .4999983012676239f;
                halfSine *= halfAngle;
                if (x > .5f) halfSine = 1f - halfSine;
                return halfSine * (to - from) + from;
            case 4: return (to - from) * x + from;
            case 5:
                float fullAngle = x * 3.1415927410125732f, fullSquare = fullAngle * fullAngle;
                float smooth = fullSquare * .00048483398859389126f - .0196138396859169f;
                smooth = smooth * fullSquare + .24767479300498962f;
                smooth = smooth * fullSquare + .0006967021618038416f;
                return smooth * (to - from) + from;
            case 6: return (x + 1f) * x * .5f * (to - from) + from;
            case 7:
                float cosineAngle = x * 1.5707963705062866f, cosineSquare = cosineAngle * cosineAngle;
                float cosine = .04148774966597557f - cosineSquare * .0012712094467133284f;
                cosine = cosine * cosineSquare - .49991244077682495f;
                cosine = cosine * cosineSquare + .9999933242797852f;
                return cosine * (from - to) + to;
            case 8: return x * x * x * (to - from) + from;
            case 9: return from;
            default: throw new NotSupportedException($"Music graph curve {curve} is invalid.");
        }
    }
}
