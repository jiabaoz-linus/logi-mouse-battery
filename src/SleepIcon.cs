using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace GpwBattery
{
    internal static partial class BatteryIcon
    {
        private static void DrawSleepingMouse(Graphics g, TrayTheme theme, int size)
        {
            g.ScaleTransform(size / 100f, size / 100f);
            float pixel = 100f / size;
            bool compact = size <= 20;
            Color contour = theme.HighContrast ? theme.Ink : Tone(theme, 0x685B80, 0xAFA1C5);
            Color faceInk = theme.HighContrast ? theme.Ink : Tone(theme, 0x4A3C59, 0x44354E);
            Color lilac = theme.HighContrast ? theme.Paper : Tone(theme, 0xDFD7F3, 0xE4DAF7);
            Color shade = theme.HighContrast ? theme.Paper : Tone(theme, 0xBBAFD9, 0xBDACD8);
            Color rose = theme.HighContrast ? theme.Ink : Tone(theme, 0xE7AEB7, 0xEAB6BE);
            Color tailInk = theme.HighContrast ? theme.Ink : Tone(theme, 0xA07A90, 0xCDA4B6);

            using (var tail = new GraphicsPath())
            using (var line = RoundPen(tailInk, Math.Max(4.8f, pixel)))
            {
                tail.AddBezier(31, 74, 21, 87, 5.8f, 82, 5.5f, 70);
                tail.AddBezier(5.5f, 70, 5, 63, 10.5f, 60, 9.5f, 54.5f);
                g.DrawPath(line, tail);
            }

            using (var outline = RoundPen(contour, Math.Max(3.4f, pixel * 0.91f)))
            {
                if (!compact)
                {
                    var farEar = new RectangleF(63, 22, 17, 21);
                    using (var fur = new SolidBrush(shade)) g.FillEllipse(fur, farEar);
                    g.DrawEllipse(outline, farEar);
                }
                using (var body = new GraphicsPath())
                {
                    body.AddBezier(15.5f, 62, 14, 46, 23, 32, 39, 29.5f);
                    body.AddBezier(39, 29.5f, 53, 26, 66, 32, 75, 44);
                    body.AddBezier(75, 44, 81, 51, 84, 59, 93, 63.5f);
                    body.AddBezier(93, 63.5f, 99, 67, 91, 73, 83, 74.5f);
                    body.AddBezier(83, 74.5f, 73, 76, 69, 82, 58, 84);
                    body.AddBezier(58, 84, 36, 88, 16, 82, 15.5f, 62);
                    body.CloseFigure();
                    using (var coat = new LinearGradientBrush(new PointF(36, 28), new PointF(50, 86), lilac, shade))
                    {
                        coat.WrapMode = WrapMode.TileFlipXY;
                        g.FillPath(coat, body);
                    }
                    g.DrawPath(outline, body);
                }
                using (var ear = new GraphicsPath())
                {
                    ear.AddBezier(51, 43, 44, 39, 43, 27, 50, 20);
                    ear.AddBezier(50, 20, 56, 13, 67, 15, 71, 23);
                    ear.AddBezier(71, 23, 75, 31, 70, 40, 64, 43);
                    using (var fur = new SolidBrush(lilac)) g.FillPath(fur, ear);
                    g.DrawPath(outline, ear);
                }
            }

            using (var earPink = new GraphicsPath())
            {
                earPink.AddBezier(53, 37, 49, 34, 49, 26, 54, 23.5f);
                earPink.AddBezier(54, 23.5f, 62, 19, 68, 25, 66, 31);
                earPink.AddBezier(66, 31, 64, 38, 57, 40, 53, 37);
                earPink.CloseFigure();
                using (var blush = new SolidBrush(rose))
                {
                    g.FillPath(blush, earPink);
                    if (!compact && !theme.HighContrast) g.FillEllipse(blush, 66.5f, 63, 9, 4.8f);
                }
            }

            if (!compact)
            {
                Color fold = theme.HighContrast ? theme.Ink : Blend(shade, faceInk, 0.43f);
                using (var haunch = RoundPen(fold, Math.Max(2.9f, pixel * 0.82f)))
                    g.DrawBezier(haunch, 31, 76, 43, 80, 53, 73, 48, 64);
            }
            using (var eye = RoundPen(faceInk, Math.Max(4, pixel * 1.05f)))
                g.DrawBezier(eye, 65.5f, 54.5f, 68, 60.8f, 74.5f, 61.5f, 78.5f, 55.7f);
            using (var nose = new SolidBrush(faceInk)) g.FillEllipse(nose, 90.2f, 62, 7.8f, 6.6f);

            Color dreamInk = theme.HighContrast ? theme.Ink : Tone(theme, 0x73669E, 0xBFB2E8);
            float left = compact ? 78 : 81;
            using (var z = RoundPen(dreamInk, Math.Max(3.8f, pixel * 0.86f)))
                g.DrawLines(z, new[] { new PointF(left, 10), new PointF(94, 8),
                                       new PointF(left + 1, 22), new PointF(95, 20) });
        }
    }
}
