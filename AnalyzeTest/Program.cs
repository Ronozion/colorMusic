using TrafficLight.Audio;

// Синтетическая проверка анализатора:
//  0–3 c   : тишина (гейт должен держать лампы выключенными)
//  3–9 c   : «громко»: бас 100 Гц, середина 1 кГц, тарелки — всплески 6 кГц+шум каждые 250 мс
//  9–15 c  : то же самое, но в 30 раз тише (-30 дБ) — автономизация должна дать те же уровни
//  15–18 c : снова громко
//  18–27 c : почти тишина (самый тихий шум) — лампы должны погаснуть
//  27–30 c : снова громко — быстрый возврат
const int sr = 48000;
var an = new FftAnalyzer(sr);
var rnd = new Random(42);

float[] chunk = new float[2048];
long n0 = 0;
long lastPrint = -1;

while (n0 < (long)sr * 30)
{
    double amp = n0 < (long)sr * 3 ? 0.0
               : n0 < (long)sr * 9 ? 1.0
               : n0 < (long)sr * 15 ? 0.033
               : n0 < (long)sr * 18 ? 1.0
               : n0 < (long)sr * 27 ? 0.0004
               : 1.0;

    for (int i = 0; i < chunk.Length; i++)
    {
        long n = n0 + i;
        double t = n / (double)sr;
        double v = amp * (0.30 * Math.Sin(2 * Math.PI * 100 * t)
                        + 0.15 * Math.Sin(2 * Math.PI * 1000 * t));
        if (amp > 0 && n % (sr / 4) < sr / 20) // 50 мс каждые 250 мс
            v += amp * (0.02 * (rnd.NextDouble() * 2 - 1) + 0.03 * Math.Sin(2 * Math.PI * 6000 * t));
        chunk[i] = (float)v;
    }

    an.AddSamples(chunk, chunk.Length);

    long halfSec = n0 / (sr / 2);
    if (halfSec != lastPrint)
    {
        lastPrint = halfSec;
        var (b, m, h) = an.GetLevels();
        Console.WriteLine($"t={n0 / (double)sr,5:F1}s  bass={b,5:P0}  mid={m,5:P0}  high={h,5:P0}");
    }
    n0 += chunk.Length;
}
