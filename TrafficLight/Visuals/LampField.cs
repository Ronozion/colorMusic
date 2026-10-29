using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TrafficLight.Visuals;

/// <summary>
/// Рисует светофор прямо в DrawingContext: горизонтальный ряд ламп,
/// масштаб под весь хост. Порядок ламп — по частоте: низы → середина → верхи
/// (красный, зелёный, жёлтый). Палитра «под OLED»: почти чёрный корпус и
/// выключенные лампы, насыщенные яркие вспышки.
/// </summary>
public static class LampField
{
    private sealed record LampLook(Color Glass0, Color Glass1,
                                   Color Hot, Color Core, Color Deep, Color Base, string Label);

    // Порядок ламп: красный (басы), зелёный (середина), жёлтый (верха) — по частоте
    private static readonly LampLook[] Looks =
    {
        new(Color.FromRgb(0x1E, 0x0A, 0x08), Color.FromRgb(0x0E, 0x04, 0x03),
            Color.FromRgb(0xFF, 0xE8, 0xDE), Color.FromRgb(0xFF, 0x54, 0x3C),
            Color.FromRgb(0xE0, 0x23, 0x14), Color.FromRgb(0xFF, 0x41, 0x36), "БАСЫ"),

        new(Color.FromRgb(0x0A, 0x26, 0x14), Color.FromRgb(0x04, 0x11, 0x09),
            Color.FromRgb(0xD8, 0xFF, 0xE2), Color.FromRgb(0x42, 0xE6, 0x66),
            Color.FromRgb(0x16, 0x9C, 0x3A), Color.FromRgb(0x37, 0xD4, 0x5A), "СЕРЕДИНА"),

        new(Color.FromRgb(0x24, 0x1B, 0x05), Color.FromRgb(0x10, 0x0B, 0x02),
            Color.FromRgb(0xFF, 0xF2, 0xC2), Color.FromRgb(0xFF, 0xCB, 0x1A),
            Color.FromRgb(0xD0, 0x96, 0x00), Color.FromRgb(0xFF, 0xC4, 0x00), "ВЕРХА"),
    };

    private static readonly Brush Recess = Frozen(Color.FromRgb(0x00, 0x00, 0x00));
    private static readonly Pen RingPen = FrozenPen(Color.FromRgb(0x1F, 0x23, 0x2A), 1.6);
    private static readonly Pen HousingPen = FrozenPen(Color.FromRgb(0x1F, 0x23, 0x2A), 2);
    private static readonly Brush Specular = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BarTrack = Frozen(Color.FromArgb(0x55, 0x2A, 0x2E, 0x36));
    private static readonly Brush LabelBrush = Frozen(Color.FromArgb(0xE0, 0xC9, 0xCF, 0xD8));

    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static void Render(DrawingContext dc, double w, double h,
                              double[] levels, double[] lit, double[] flicker, double dpi)
    {
        double r = Math.Min(w * 0.135, h * 0.32);
        Point[] centers = [new Point(w * 0.16, h / 2), new Point(w * 0.5, h / 2), new Point(w * 0.84, h / 2)];

        // Корпус: почти чёрный, только тонкая грань — на OLED он исчезает, остаётся свет
        Rect housing = new(centers[0].X - r * 1.45, h / 2 - r * 1.62, (centers[2].X - centers[0].X) + r * 2.9, r * 3.24);

        dc.DrawRoundedRectangle(Recess, HousingPen, housing, r * 0.5, r * 0.5);

        double font = Math.Clamp(r * 0.20, 11, 30);

        for (int i = 0; i < 3; i++)
            DrawLamp(dc, centers[i], r, lit[i], flicker[i], Looks[i]);

        // Подписи, проценты и мини-индикаторы уровня
        for (int i = 0; i < 3; i++)
        {
            var c = centers[i];
            var look = Looks[i];

            string text = $"{look.Label} · {levels[i]:P0}";
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                UiFace, font, LabelBrush, dpi);
            double ty = c.Y + r * 1.32;
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, ty));

            double barW = r * 1.5, barH = Math.Max(2, r * 0.07);
            var barTop = new Point(c.X - barW / 2, ty + ft.Height + barH * 0.8);
            dc.DrawRoundedRectangle(BarTrack, null,
                new Rect(barTop.X, barTop.Y, barW, barH), barH / 2, barH / 2);
            double v = Math.Clamp(levels[i], 0, 1) * barW;
            if (v > 1)
                dc.DrawRoundedRectangle(new SolidColorBrush(look.Base), null,
                    new Rect(barTop.X, barTop.Y, v, barH), barH / 2, barH / 2);
        }
    }

    private static void DrawLamp(DrawingContext dc, Point c, double r, double lit, double flicker, LampLook look)
    {
        // Углубление (чистый чёрный) с тонким ободком
        dc.DrawEllipse(Recess, RingPen, c, r * 1.13, r * 1.13);

        // Выключенное «стекло» — почти чёрное, лишь с оттенком цвета
        var glass = new RadialGradientBrush(
            Color.FromArgb(0xFF, look.Glass0.R, look.Glass0.G, look.Glass0.B),
            Color.FromArgb(0xFF, look.Glass1.R, look.Glass1.G, look.Glass1.B))
        {
            GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.5, 0.5),
            RadiusX = 0.75, RadiusY = 0.75
        };
        dc.DrawEllipse(glass, null, c, r, r);

        if (lit > 0.02)
        {
            double op = Math.Clamp(lit * flicker, 0.0, 1.0);
            dc.PushOpacity(op);

            // Ореол: плотный в центре, полностью растворяется к краям
            var halo = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5),
                RadiusX = 0.5, RadiusY = 0.5
            };
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, look.Base.R, look.Base.G, look.Base.B), 0));
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, look.Base.R, look.Base.G, look.Base.B), 0.55));
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, look.Base.R, look.Base.G, look.Base.B), 1));
            dc.DrawEllipse(halo, null, c, r * 1.9, r * 1.9);

            // Горячая сердцевина — белёсый центр, насыщенный край
            var core = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.45), Center = new Point(0.5, 0.5),
                RadiusX = 0.55, RadiusY = 0.55
            };
            core.GradientStops.Add(new GradientStop(look.Hot, 0));
            core.GradientStops.Add(new GradientStop(look.Core, 0.45));
            core.GradientStops.Add(new GradientStop(look.Deep, 1));
            dc.DrawEllipse(core, null, c, r, r);

            dc.Pop();
        }

        // Тонкий блик стекла
        dc.DrawEllipse(Specular, null, new Point(c.X - r * 0.32, c.Y - r * 0.45), r * 0.36, r * 0.2);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness);
        p.Freeze();
        return p;
    }
}
