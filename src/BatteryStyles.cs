using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace GpwBattery
{
    internal static partial class BatteryIcon
    {
        // Geometry uses a 100-unit square. A physical pixel sets the lower bound for fine strokes.
        private static void DrawVariant(Graphics g, Reading r, IconStyle style, TrayTheme t, int pixels)
        {
            float pixel = 100f / pixels;
            switch (style)
            {
                case IconStyle.Aurora: DrawAurora(g, r, t, pixel); break;
                case IconStyle.Rail: DrawRail(g, r, t, pixel); break;
                case IconStyle.Horizon: DrawHorizon(g, r, t, pixel); break;
                case IconStyle.Moon: DrawMoon(g, r, t, pixel); break;
                case IconStyle.Flip: DrawFlip(g, r, t); break;
                case IconStyle.Squircle: DrawSquircle(g, r, t, pixel); break;
                default: throw new ArgumentOutOfRangeException("style");
            }
        }

        private static Color Tone(TrayTheme t, int light, int dark)
        {
            int rgb = t.Dark ? dark : light;
            return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        private static bool Marked(Reading r)
        {
            return r.Power == PowerState.Charging || r.Power == PowerState.ChargeError;
        }

        private static float Level(Reading r) { return r.Percent.GetValueOrDefault() / 100f; }

        private static Color RailColor(TrayTheme t)
        {
            return t.HighContrast ? t.Ink : Tone(t, 0xBFC6D2, 0x505663);
        }

        private static GraphicsPath Glyph(int? number, RectangleF box)
        {
            if (number.HasValue) return Number(number, box, 100);
            float width = Math.Min(16, box.Width * 0.34f), height = 5.3f;
            return Rounded(new RectangleF(box.X + (box.Width-width)/2, box.Y + (box.Height-height)/2, width, height), height/2);
        }

        private static void Letter(Graphics g, Reading r, TrayTheme t, RectangleF box)
        {
            Color ink = Marked(r) ? t.Ink : StateColor(r, t, t.Ink);
            if (!r.Percent.HasValue && !t.HighContrast) ink = Blend(ink, t.Paper, 0.21f);
            using (var glyph = Glyph(r.Percent, box))
            using (var brush = new SolidBrush(ink)) g.FillPath(brush, glyph);
        }

        private static void StatusSign(Graphics g, Reading r, TrayTheme t, RectangleF box)
        {
            if (Marked(r)) DrawPowerMark(g, box, r.Power, StateColor(r, t, t.Ink));
        }

        private static Pen RoundPen(Color color, float width)
        {
            return new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        }

        private static Brush Accent(Reading r, TrayTheme t, Color left, Color right)
        {
            Color a = StateColor(r, t, left), b = StateColor(r, t, right);
            if (a == b) return new SolidBrush(a);
            return new LinearGradientBrush(new PointF(5, 50), new PointF(95, 50), a, b);
        }

        // The arclength, rather than the curve's control-point spacing, sets the charge level.
        private static void StrokeLevel(Graphics g, GraphicsPath guide, float level, Color track, Brush active,
                                        float trackWidth, float activeWidth)
        {
            using (var rail = RoundPen(track, trackWidth)) g.DrawPath(rail, guide);
            if (level <= 0) return;
            using (var pen = new Pen(active, activeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round,
                                                            LineJoin = LineJoin.Round })
            {
                if (level >= 1) { g.DrawPath(pen, guide); return; }
                using (var flat = (GraphicsPath)guide.Clone())
                {
                    flat.Flatten(null, 0.20f);
                    PointF[] points = flat.PathPoints;
                    var lengths = new float[points.Length];
                    for (int i=1; i<points.Length; i++)
                    {
                        float dx = points[i].X - points[i-1].X, dy = points[i].Y - points[i-1].Y;
                        lengths[i] = lengths[i-1] + (float)Math.Sqrt(dx*dx + dy*dy);
                    }
                    float target = lengths[lengths.Length-1] * level;
                    var portion = new List<PointF> { points[0] };
                    for (int i=1; i<points.Length; i++)
                    {
                        if (lengths[i] <= target) { portion.Add(points[i]); continue; }
                        float f = (target-lengths[i-1]) / (lengths[i]-lengths[i-1]);
                        portion.Add(new PointF(points[i-1].X + (points[i].X-points[i-1].X)*f,
                                               points[i-1].Y + (points[i].Y-points[i-1].Y)*f));
                        break;
                    }
                    if (portion.Count > 1) g.DrawLines(pen, portion.ToArray());
                }
            }
        }

        private static PointF Orbit(float radius, double angle)
        {
            return new PointF(50 + radius*(float)Math.Cos(angle), 50 + radius*(float)Math.Sin(angle));
        }

        private static float RibbonWidth(float turn)
        {
            return 7f + 8f * (0.5f - 0.5f * (float)Math.Cos(turn * 2 * Math.PI));
        }

        // One silhouette for the entire ribbon and both caps avoids seams between color stops.
        private static GraphicsPath AuroraRibbon(float start, float sweep, float level)
        {
            const float radius = 41.9f;
            int steps = Math.Max(2, (int)Math.Ceiling(sweep * level / 3));
            var points = new PointF[2 * (steps + 1)];
            for (int i=0; i<=steps; i++)
            {
                float turn = level*i/steps;
                double a = (start+sweep*turn)*Math.PI/180;
                float half = RibbonWidth(turn)/2;
                points[i] = Orbit(radius+half, a);
                points[points.Length-1-i] = Orbit(radius-half, a);
            }
            var path = new GraphicsPath(FillMode.Winding);
            path.AddPolygon(points);
            if (level < 1 || sweep < 360)
            {
                foreach (float end in new[] { 0f, level })
                {
                    PointF p = Orbit(radius, (start+sweep*end)*Math.PI/180);
                    float width = RibbonWidth(end);
                    path.AddEllipse(p.X-width/2, p.Y-width/2, width, width);
                }
            }
            return path;
        }

        private static void DrawAurora(Graphics g, Reading r, TrayTheme t, float pixel)
        {
            float gap = Marked(r) ? 46 : 0, start = -90 + gap/2, sweep = 360-gap;
            float level = Level(r);
            var guide = new RectangleF(8.1f, 8.1f, 83.8f, 83.8f);
            if (!r.Percent.HasValue)
            {
                // Two symmetrical strokes identify standby without suggesting any charge level.
                Color north = t.HighContrast ? t.Ink : Tone(t, 0x857AA1, 0xAE9DBC);
                Color south = t.HighContrast ? t.Ink : Tone(t, 0x528C91, 0x79AEB1);
                using (var wash = new LinearGradientBrush(guide, north, south, 80f))
                using (var rest = new Pen(wash, 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(rest, guide, -78, 156);
                    g.DrawArc(rest, guide, 102, 156);
                }
                Letter(g, r, t, new RectangleF(22, 34, 56, 32));
                return;
            }
            using (var rail = RoundPen(RailColor(t), Math.Max(pixel, t.HighContrast ? 4.3f : 5.3f)))
            {
                if (gap == 0) g.DrawEllipse(rail, guide);
                else g.DrawArc(rail, guide, start, sweep);
            }
            if (level > 0)
            {
                using (var ribbon = AuroraRibbon(start, sweep, level))
                {
                    if (t.HighContrast || r.Percent <= 20 || Marked(r))
                    {
                        using (var signal = new SolidBrush(StateColor(r, t, t.Ink))) g.FillPath(signal, ribbon);
                    }
                    else
                    {
                        Color violet = Tone(t, 0x8758D7, 0xC3A0FF), blue = Tone(t, 0x476FE4, 0x8EAAFF);
                        Color sea = Tone(t, 0x008E9A, 0x62D8CB);
                        using (var brush = new LinearGradientBrush(new PointF(39,4), new PointF(56,96), violet, sea))
                        {
                            brush.WrapMode = WrapMode.TileFlipXY;
                            brush.InterpolationColors = new ColorBlend {
                                Colors = new[] { violet, blue, sea }, Positions = new[] { 0f, 0.50f, 1f }
                            };
                            g.FillPath(brush, ribbon);
                        }
                    }
                }
            }
            Letter(g, r, t, new RectangleF(23, 34, 54, 32));
            StatusSign(g, r, t, new RectangleF(43.7f, 1, 12.6f, 21));
        }

        private static void DrawRail(Graphics g, Reading r, TrayTheme t, float pixel)
        {
            bool marked = Marked(r);
            var slot = new RectangleF(8, marked ? 36 : 16, 10.5f, marked ? 48 : 68);
            Color active = StateColor(r, t, Tone(t, 0x4A63BF, 0xA4B4FF));
            using (var body = Rounded(slot, slot.Width/2))
            {
                if (t.HighContrast)
                {
                    using (var edge = RoundPen(t.Ink, Math.Max(2.2f, pixel*0.72f))) g.DrawPath(edge, body);
                }
                else
                {
                    Color track = r.Percent.HasValue ? RailColor(t) : Tone(t, 0x8C98B4, 0x91A0BC);
                    using (var rail = new SolidBrush(track)) g.FillPath(rail, body);
                }
                if (Level(r) > 0)
                {
                    var saved = g.Save();
                    g.SetClip(new RectangleF(slot.Left, slot.Bottom-slot.Height*Level(r), slot.Width, slot.Height*Level(r)), CombineMode.Intersect);
                    using (var fill = new SolidBrush(active)) g.FillPath(fill, body);
                    g.Restore(saved);
                }
            }
            Letter(g, r, t, new RectangleF(26, 29, 70, 42));
            StatusSign(g, r, t, new RectangleF(6, 10, 15, 23));
        }

        private static void DrawHorizon(Graphics g, Reading r, TrayTheme t, float pixel)
        {
            // A broad, shallow arch leaves the readout inside the dial.
            using (var arch = new GraphicsPath())
            using (var sunset = Accent(r, t, Tone(t, 0xAB596E, 0xE9A3B4), Tone(t, 0xCE8854, 0xF4C18B)))
            {
                arch.AddArc(new RectangleF(8.5f, 19, 83, 74), 154, 232);
                Color track = r.Percent.HasValue || t.HighContrast ? RailColor(t) : Tone(t, 0xAB9091, 0xBC9F9B);
                StrokeLevel(g, arch, Level(r), track, sunset, Math.Max(pixel, r.Percent.HasValue ? 5.2f : 7.5f), 12);
            }
            Letter(g, r, t, new RectangleF(22, Marked(r) ? 40 : 43, 56, 31));
            StatusSign(g, r, t, new RectangleF(44, 76, 12, 20));
        }

        // Each horizontal chord is split by the same fraction, so the lit area equals the charge.
        private static GraphicsPath MoonPhase(float level)
        {
            const float c=50, radius=44, k=0.55228475f;
            float rim=radius*k, shade=radius*(1-2*level), curve=shade*k;
            var p = new GraphicsPath();
            p.AddBezier(c, c-radius, c+rim, c-radius, c+radius, c-rim, c+radius, c);
            p.AddBezier(c+radius, c, c+radius, c+rim, c+rim, c+radius, c, c+radius);
            p.AddBezier(c, c+radius, c+curve, c+radius, c+shade, c+rim, c+shade, c);
            p.AddBezier(c+shade, c, c+shade, c-rim, c+curve, c-radius, c, c-radius);
            p.CloseFigure();
            return p;
        }

        private static void DrawMoon(Graphics g, Reading r, TrayTheme t, float pixel)
        {
            float level = Level(r);
            var disc = new RectangleF(6, 6, 88, 88);
            Color shade = t.HighContrast ? t.Paper : Tone(t, 0xDCE1EB, 0x303645);
            Color north = t.HighContrast ? t.Ink : Tone(t, 0xBBC0EB, 0x797CA5);
            Color south = t.HighContrast ? t.Ink : Tone(t, 0x9992D4, 0x585C8A);
            if (!t.HighContrast && (r.Percent <= 20 || Marked(r)))
                north = south = Blend(StateColor(r, t, t.Ink), t.Paper, t.Dark ? 0.60f : 0.52f);
            using (var surface = new SolidBrush(shade)) g.FillEllipse(surface, disc);
            if (!r.Percent.HasValue && !t.HighContrast)
                using (var rim = RoundPen(Blend(shade, t.Ink, 0.24f), Math.Max(2, pixel * 0.82f)))
                    g.DrawEllipse(rim, disc);
            using (var phase = MoonPhase(level))
            using (var glyph = Glyph(r.Percent, new RectangleF(20, Marked(r) ? 41 : 33, 60, 34)))
            {
                if (level > 0)
                    using (var fill = new LinearGradientBrush(disc, north, south, 82f)) g.FillPath(fill, phase);
                using (var ink = new SolidBrush(t.Ink)) g.FillPath(ink, glyph);
                var sign = new RectangleF(44, 16, 12, 20);
                if (Marked(r)) DrawPowerMark(g, sign, r.Power, t.Ink);
                if (t.HighContrast)
                {
                    using (var outline = RoundPen(t.Ink, Math.Max(3, pixel*0.85f))) g.DrawEllipse(outline, disc);
                    if (level > 0)
                    {
                        var saved = g.Save();
                        g.SetClip(phase, CombineMode.Intersect);
                        using (var inverse = new SolidBrush(t.Paper)) g.FillPath(inverse, glyph);
                        if (Marked(r)) DrawPowerMark(g, sign, r.Power, t.Paper);
                        g.Restore(saved);
                    }
                }
            }
        }

        private static void DrawFlip(Graphics g, Reading r, TrayTheme t)
        {
            string value = r.Percent.HasValue ? r.Percent.Value.ToString("00", CultureInfo.InvariantCulture) : "--";
            float total = value.Length == 3 ? 94 : 87, gap = 4.9f;
            float width = (total-gap*(value.Length-1))/value.Length;
            Color surface = t.HighContrast ? t.Ink : Tone(t, 0x353945, 0xD6DBE8);
            Color faceInk = t.HighContrast ? t.Paper : Tone(t, 0xF7F8FD, 0x262B38);
            var face = new TrayTheme(faceInk, surface, !t.Dark, t.HighContrast);
            Color ink = StateColor(r, face, faceInk);
            for (int i=0; i<value.Length; i++)
            {
                var tile = new RectangleF((100-total)/2 + i*(width+gap), 21, width, 59);
                using (var body = Rounded(tile, value.Length==3 ? 6.5f : 9))
                {
                    using (var brush = new SolidBrush(surface)) g.FillPath(brush, body);
                    if (!t.HighContrast)
                    {
                        var saved = g.Save();
                        g.SetClip(new RectangleF(tile.X, 51, tile.Width, 31), CombineMode.Intersect);
                        using (var lower = new SolidBrush(Blend(surface, faceInk, 0.052f))) g.FillPath(lower, body);
                        g.Restore(saved);
                    }
                }
                float h = value.Length==3 ? 28 : 34;
                int? digit = value[i]=='-' ? (int?)null : value[i]-'0';
                using (var glyph = Glyph(digit, new RectangleF(tile.X+3, 50.5f-h/2, tile.Width-6, h)))
                using (var brush = new SolidBrush(ink)) g.FillPath(brush, glyph);
            }
            StatusSign(g, r, t, new RectangleF(76, 1, 12, 18));
        }

        private static void DrawSquircle(Graphics g, Reading r, TrayTheme t, float pixel)
        {
            // A superellipse changes curvature continuously from the side into the corner.
            const double exponent = 3.8;
            float gap = Marked(r) ? 18 : (r.Percent.HasValue ? 0 : 10);
            int samples = (int)Math.Ceiling((360 - 2 * gap) / 3);
            var points = new PointF[samples + 1];
            for (int i = 0; i <= samples; i++)
            {
                double angle = (-90 + gap + (360 - 2 * gap) * i / samples) * Math.PI / 180;
                double x = Math.Cos(angle), y = Math.Sin(angle);
                double radius = 41 / Math.Pow(Math.Pow(Math.Abs(x), exponent) + Math.Pow(Math.Abs(y), exponent), 1 / exponent);
                points[i] = new PointF(50 + (float)(radius * x), 50 + (float)(radius * y));
            }
            using (var frame = new GraphicsPath())
            using (var jade = Accent(r, t, Tone(t, 0x368C84, 0x90DCC6), Tone(t, 0x1E7369, 0x68C1BE)))
            {
                frame.AddLines(points);
                if (gap == 0) frame.CloseFigure();
                Color track = r.Percent.HasValue || t.HighContrast ? RailColor(t) : Tone(t, 0x73978F, 0x86AAA2);
                StrokeLevel(g, frame, Level(r), track, jade, Math.Max(pixel, r.Percent.HasValue ? 5.4f : 7.4f), 10.8f);
            }
            Letter(g, r, t, new RectangleF(20, 33, 60, 34));
            StatusSign(g, r, t, new RectangleF(43.5f, 0.8f, 13, 20));
        }
    }
}
