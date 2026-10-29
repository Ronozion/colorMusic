// Тест: синтез двойного клика в WndProcHook GpuParticleHost (без окон и дисплея).
using System;
using System.Reflection;
using System.Threading;
using TrafficLight.Visuals.Gpu;

static class TestHook
{
    [STAThread]
    static int Main()
    {
        var host = new GpuParticleHost();
        int fired = 0;
        host.DoubleClick += () => fired++;

        var m = typeof(GpuParticleHost).GetMethod("WndProcHook",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        object Call(int msg, IntPtr wParam, int x, int y)
        {
            object[] a = { IntPtr.Zero, msg, wParam, new IntPtr((y << 16) | (x & 0xFFFF)), false };
            object r = m.Invoke(host, a);
            return r;
        }

        // 1) Два одиночных клика с коротким интервалом → синтез должен сработать (один раз)
        Call(0x0201 /*WM_LBUTTONDOWN*/, IntPtr.Zero, 100, 100);
        Call(0x0202 /*WM_LBUTTONUP*/, IntPtr.Zero, 100, 100);
        Thread.Sleep(60);
        Call(0x0201, IntPtr.Zero, 102, 101);
        Call(0x0202, IntPtr.Zero, 102, 101);
        bool pairWorks = fired == 1;
        Console.WriteLine($"two-single-clicks fired={fired} -> {(pairWorks ? "OK" : "FAIL")}");

        // 2) WM_LBUTTONDBLCLK тоже должен сработать (если класс с CS_DBLCLKS)
        Call(0x0203 /*WM_LBUTTONDBLCLK*/, IntPtr.Zero, 50, 50);
        bool dblWorks = fired == 2;
        Console.WriteLine($"dblclk-msg fired={fired} -> {(dblWorks ? "OK" : "FAIL")}");

        // 3) Одиночные клики с большим интервалом — не должны
        Call(0x0201, IntPtr.Zero, 10, 10);
        Call(0x0202, IntPtr.Zero, 10, 10);
        Thread.Sleep(900); // больше GetDoubleClickTime()
        Call(0x0201, IntPtr.Zero, 11, 10);
        Call(0x0202, IntPtr.Zero, 11, 10);
        bool gapOk = fired == 2;
        Console.WriteLine($"long-gap fired={fired} -> {(gapOk ? "OK" : "FAIL")}");

        // 4) Два клика далеко друг от друга — не должны
        Thread.Sleep(900);
        Call(0x0201, IntPtr.Zero, 10, 10);
        Call(0x0202, IntPtr.Zero, 10, 10);
        Thread.Sleep(60);
        Call(0x0201, IntPtr.Zero, 400, 300);
        Call(0x0202, IntPtr.Zero, 400, 300);
        bool distOk = fired == 2;
        Console.WriteLine($"far-clicks fired={fired} -> {(distOk ? "OK" : "FAIL")}");

        return (pairWorks && dblWorks && gapOk && distOk) ? 0 : 1;
    }
}
