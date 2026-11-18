namespace NHL27Kit.Core;

/// <summary>
/// Loudness (ITU-R BS.1770 / EBU R128 integrated LUFS: K-weighting, 400 ms blocks, -70 LUFS and relative -10 LU gates)
/// and a matching step: a gain to a target loudness, then a look-ahead peak limiter so nothing clips. The game's
/// Creation Zone songs are mastered at about -9 LUFS (39 measured: -9.2 .. -6.4, peaks at 0 dBFS); a song at -19 LUFS
/// plays far quieter than them at the same volume setting.
/// </summary>
public static class Loudness
{
    /// <summary>Integrated loudness of 48 kHz interleaved PCM, in LUFS (-70 for silence).</summary>
    public static double Lufs(short[] pcm, int channels)
    {
        var x = ToFloat(pcm);
        return Lufs(x, channels);
    }

    /// <summary>
    /// <paramref name="pcm"/> (48 kHz) brought to <paramref name="targetLufs"/>: a gain, then a limiter (5 ms look-ahead,
    /// 80 ms release) holding peaks at <paramref name="ceilingDb"/> dBFS. Limiting lowers the loudness a little, so the
    /// gain is nudged up to four times. Returns the new PCM, the loudness before and after, and the most the limiter took.
    /// </summary>
    public static (short[] Pcm, double Before, double After, double MaxLimitDb) Match(short[] pcm, int channels, double targetLufs = -10, double ceilingDb = -1)
    {
        var x = ToFloat(pcm);
        double before = Lufs(x, channels);
        if (before <= -69) return (pcm, before, before, 0);
        double ceil = Math.Pow(10, ceilingDb / 20), gainDb = targetLufs - before, after = before, maxLimit = 0;
        float[] y = x;
        for (int pass = 0; pass < 4; pass++)
        {
            (y, maxLimit) = Limit(x, channels, (float)Math.Pow(10, gainDb / 20), (float)ceil);
            after = Lufs(y, channels);
            if (Math.Abs(after - targetLufs) < 0.2) break;
            gainDb += targetLufs - after;
        }
        var outPcm = new short[y.Length];
        for (int i = 0; i < y.Length; i++) outPcm[i] = (short)Math.Clamp(Math.Round(y[i] * 32767f), -32768, 32767);
        return (outPcm, before, after, maxLimit);
    }

    private static float[] ToFloat(short[] pcm)
    {
        var x = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++) x[i] = pcm[i] / 32768f;
        return x;
    }

    private static double Lufs(float[] x, int channels)
    {
        int frames = x.Length / channels;
        // K-weighting at 48 kHz: high shelf, then high pass (BS.1770 coefficients); mean square per frame over channels
        var ms = new double[frames];
        for (int c = 0; c < channels; c++)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0, u1 = 0, u2 = 0, v1 = 0, v2 = 0;
            for (int f = 0; f < frames; f++)
            {
                double s = x[f * channels + c];
                double y = 1.53512485958697 * s - 2.69169618940638 * x1 + 1.19839281085285 * x2 + 1.69065929318241 * y1 - 0.73248077421585 * y2;
                x2 = x1; x1 = s; y2 = y1; y1 = y;
                double v = y - 2 * u1 + u2 + 1.99004745483398 * v1 - 0.99007225036621 * v2;
                u2 = u1; u1 = y; v2 = v1; v1 = v;
                ms[f] += v * v;
            }
        }
        // 400 ms blocks every 100 ms
        int blk = 19200, hop = 4800;
        var prefix = new double[frames + 1];
        for (int f = 0; f < frames; f++) prefix[f + 1] = prefix[f] + ms[f];
        var blocks = new List<double>();
        for (int s = 0; s + blk <= frames; s += hop) blocks.Add((prefix[s + blk] - prefix[s]) / blk);
        if (blocks.Count == 0) return -70;
        static double L(double z) => -0.691 + 10 * Math.Log10(z + 1e-12);
        var abs = blocks.Where(z => L(z) > -70).ToList();
        if (abs.Count == 0) return -70;
        double rel = L(abs.Average()) - 10;
        var gated = abs.Where(z => L(z) > rel).ToList();
        return L(gated.Count > 0 ? gated.Average() : abs.Average());
    }

    private static (float[] Y, double MaxLimitDb) Limit(float[] x, int channels, float gain, float ceil)
    {
        int frames = x.Length / channels, look = 240;            // 5 ms
        var need = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float pk = 0;
            for (int c = 0; c < channels; c++) pk = Math.Max(pk, Math.Abs(x[f * channels + c] * gain));
            need[f] = pk > ceil ? ceil / pk : 1f;
        }
        // hold the lowest gain needed within the look-ahead on both sides (a sliding minimum)
        var held = new float[frames];
        var dq = new LinkedList<int>();
        for (int i = 0, j = 0; i < frames; i++)
        {
            for (; j < frames && j <= i + look; j++)
            {
                while (dq.Count > 0 && need[dq.Last!.Value] >= need[j]) dq.RemoveLast();
                dq.AddLast(j);
            }
            while (dq.First!.Value < i - look) dq.RemoveFirst();
            held[i] = need[dq.First.Value];
        }
        // release over ~80 ms, then smooth the attack over the look-ahead
        double rel = Math.Exp(-1 / (0.08 * 48000)), cur = 1;
        var env = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            cur = held[i] < cur ? held[i] : held[i] + (cur - held[i]) * rel;
            env[i] = (float)cur;
        }
        var smooth = new float[frames];
        double sum = 0;
        for (int i = 0; i < frames; i++)
        {
            sum += env[i];
            if (i >= look) sum -= env[i - look];
            smooth[i] = Math.Min(env[i], (float)(sum / Math.Min(i + 1, look)));
        }
        var y = new float[x.Length];
        float minEnv = 1;
        for (int f = 0; f < frames; f++)
        {
            minEnv = Math.Min(minEnv, smooth[f]);
            for (int c = 0; c < channels; c++)
                y[f * channels + c] = Math.Clamp(x[f * channels + c] * gain * smooth[f], -ceil, ceil);
        }
        return (y, -20 * Math.Log10(minEnv));
    }
}
