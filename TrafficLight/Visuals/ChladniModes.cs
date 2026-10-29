namespace TrafficLight.Visuals;

/// <summary>
/// Узоры пластины (общие для CPU-движка и GPU). Mode(Fam, A, B, C).
/// Семейства: 0 = вырожденная пара мод (классические кривые Хладни),
/// 1 = та же пара, но повёрнутая на угол C (диагональные решётки),
/// 2 = гексагональная интерференция трёх плоских волн (соты),
/// 3 = полярная розетка «кольца × лучи» (аналог круговой пластины,
///     A — кольца, B — лучи, C — фаза).
/// Таблица перемешивает семейства, чтобы ход частоты «проигрывал» разнобой
/// узоров, а не перелистовывал одну серию. Порядок — по возрастанию
/// «волновой сложности» (собственная частота квадратной пластины f∝m²+n²).
/// </summary>
public static class ChladniModes
{
    public readonly record struct Mode(int Fam, int A, int B, double C);

    public static readonly Mode[] Modes =
    {
        new(0, 1, 2,  0.85), new(1, 2, 1,  0.79), new(3, 2, 3,  0.00),
        new(2, 3, 0,  0.00), new(0, 1, 3, -0.70), new(3, 3, 5,  0.40),
        new(1, 3, 2,  0.35), new(2, 4, 0,  0.26), new(0, 2, 3,  0.55),
        new(3, 4, 4,  0.00), new(1, 3, 3,  1.05), new(0, 1, 4, -0.80),
        new(2, 5, 0,  0.00), new(3, 5, 6,  0.70), new(0, 2, 4,  0.65),
        new(1, 4, 3,  0.39), new(2, 6, 0,  0.52), new(0, 3, 4, -0.55),
        new(3, 6, 5,  0.30), new(1, 4, 4,  0.65), new(0, 2, 5,  0.80),
        new(2, 7, 0,  0.15), new(0, 3, 5, -0.65), new(3, 7, 8,  0.55),
    };

    /// <summary>
    /// Логарифмическая (слуховая) карта частота→позиция в таблице (0..1): без неё
    /// басовая полоса 20–250 Гц проводит всю музыку в первых двух индексах таблицы.
    /// Позиция используется непрерывно (floor/ceil + smoothstep), без квантования.
    /// </summary>
    public static double NormFromFreq(double f, double lo, double hi)
        => Math.Clamp(Math.Log2(Math.Max(f, lo) / lo) / Math.Log2(hi / lo), 0.0, 1.0);

    /// <summary>
    /// Поле + аналитический градиент (CPU-фолбэк; HLSL-шейдер дублирует 1-в-1).
    /// </summary>
    public static void Field(in Mode mo, double x, double y,
                             out double z, out double gx, out double gy)
    {
        switch (mo.Fam)
        {
            case 1: // повёрнутая пара
            {
                double s = Math.Sin(mo.C), c = Math.Cos(mo.C);
                double dx = x - 0.5, dy = y - 0.5;
                double xr = c * dx - s * dy + 0.5, yr = s * dx + c * dy + 0.5;
                PairField(mo.A, mo.B, 0.4, xr, yr, out z, out gx, out gy);
                double gxr = gx, gyr = gy;      // градиент обратно в мир: Rᵀ
                gx = c * gxr + s * gyr;
                gy = -s * gxr + c * gyr;
                return;
            }
            case 2: // гексагональная интерференция трёх волн под 120°
            {
                double q = Math.PI * mo.A, th = mo.C;
                double px = x - 0.5, py = y - 0.5;
                z = 0; gx = 0; gy = 0;
                for (int i = 0; i < 3; i++)
                {
                    double a = th + i * (Math.PI * 2 / 3);
                    double kx = Math.Cos(a) * q, ky = Math.Sin(a) * q;
                    double ph = kx * px + ky * py;
                    z += Math.Cos(ph);
                    gx += -Math.Sin(ph) * kx;
                    gy += -Math.Sin(ph) * ky;
                }
                return;
            }
            case 3: // полярная розетка (аналог круговой пластины)
            {
                double px = x - 0.5, py = y - 0.5;
                double r = Math.Sqrt(px * px + py * py) + 1e-6;
                double r2 = 2 * r, t = Math.Atan2(py, px);
                double c1 = Math.Cos(mo.A * Math.PI * r2), s1 = Math.Sin(mo.A * Math.PI * r2);
                double c2 = Math.Cos(mo.B * t + mo.C * Math.PI), s2 = Math.Sin(mo.B * t + mo.C * Math.PI);
                z = c1 * c2;
                double dzr = -mo.A * Math.PI * 2 * s1 * c2;
                double dzt = -mo.B * c1 * s2;
                gx = dzr * px / r - dzt * py / (r * r);
                gy = dzr * py / r + dzt * px / (r * r);
                return;
            }
            default:
                PairField(mo.A, mo.B, mo.C, x, y, out z, out gx, out gy);
                return;
        }
    }

    private static void PairField(int m, int n, double c, double x, double y,
                                  out double z, out double gx, out double gy)
    {
        double u = Math.PI * x, v = Math.PI * y;
        z  = Math.Cos(n * u) * Math.Cos(m * v) + c * Math.Cos(m * u) * Math.Cos(n * v);
        gx = Math.PI * (-n * Math.Sin(n * u) * Math.Cos(m * v) - c * m * Math.Sin(m * u) * Math.Cos(n * v));
        gy = Math.PI * (-m * Math.Cos(n * u) * Math.Sin(m * v) - c * n * Math.Cos(m * u) * Math.Sin(n * v));
    }
}
