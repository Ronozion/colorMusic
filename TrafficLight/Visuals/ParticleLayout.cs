using System.Windows;

namespace TrafficLight.Visuals;

/// <summary>Раскладка пластин: один расчёт на всех (CPU-хост, GPU-хост, подписи).</summary>
public static class ParticleLayout
{
    public static Rect[] Compute(double w, double h, ParticleField field)
    {
        var plates = new Rect[3];

        if (field == ParticleField.SingleField)
        {
            var single = new Rect(0, 0, w, h); // поле на всю площадь
            plates[0] = plates[1] = plates[2] = single;
        }
        else
        {
            double g = w * 0.02;
            double side = Math.Min((w - 4 * g) / 3.0, h * 0.84);
            double y = (h - side) / 2.0 + Math.Min(h * 0.03, side * 0.06);
            for (int i = 0; i < 3; i++)
                plates[i] = new Rect((w - 3 * side - 2 * g) / 2.0 + i * (side + g), y, side, side);
        }

        return plates;
    }
}
