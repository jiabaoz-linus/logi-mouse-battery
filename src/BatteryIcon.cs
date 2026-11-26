using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpwBattery
{
    internal enum IconStyle { Apple, Material, Aurora, Rail, Horizon, Moon, Flip, Squircle }

    internal sealed class TrayArtwork : IDisposable
    {
        public readonly Icon Native, Center;
        public readonly Bitmap Border;

        public TrayArtwork(Reading reading, IconStyle style, TrayTheme theme, int size)
        {
            try
            {
                Native = BatteryIcon.Create(reading, style, theme, size);
                if (style == IconStyle.Apple && reading.Power != PowerState.Inactive) return;
                // Equal integer margins align the shell's pixels with the artwork at every DPI.
                int margin = Math.Max(1, (int)Math.Round(size * 0.14));
                Border = BatteryIcon.RenderFace(reading, style, theme, size + 2 * margin);
                var middle = new Rectangle(margin, margin, size, size);
                using (var crop = Border.Clone(middle, PixelFormat.Format32bppPArgb))
                    Center = BatteryIcon.ToIcon(crop);
                using (var g = Graphics.FromImage(Border))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.FillRectangle(Brushes.Transparent, middle);
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Border != null) Border.Dispose();
            if (Center != null) Center.Dispose();
            if (Native != null) Native.Dispose();
        }
    }

    internal struct TrayTheme
    {
        public readonly Color Ink, Paper;
        public readonly bool Dark, HighContrast;

        public TrayTheme(Color ink, Color paper, bool dark, bool highContrast)
        {
            Ink = ink;
            Paper = paper;
            Dark = dark;
            HighContrast = highContrast;
        }

        public static TrayTheme Current()
        {
            if (SystemInformation.HighContrast)
                return new TrayTheme(SystemColors.WindowText, SystemColors.Window,
                                     SystemColors.Window.GetBrightness() < 0.5f, true);
            object light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                                             "SystemUsesLightTheme", 1);
            return Object.Equals(light, 0)
                ? new TrayTheme(Color.FromArgb(245, 245, 247), Color.FromArgb(19, 20, 23), true, false)
                : new TrayTheme(Color.FromArgb(34, 35, 39), Color.White, false, false);
        }
    }

    internal static partial class BatteryIcon
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int metric, uint dpi);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        public static int TraySize()
        {
            uint dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
            int size = GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi); // SM_CXSMICON
            return Math.Max(16, Math.Min(size, 128));
        }

        public static Icon Create(Reading reading, IconStyle style, TrayTheme theme, int size)
        {
            using (var bitmap = RenderFace(reading, style, theme, size)) return ToIcon(bitmap);
        }

        internal static Bitmap RenderFace(Reading reading, IconStyle style, TrayTheme theme, int size)
        {
            return Render(size, delegate(Graphics g)
            {
                if (reading.Power == PowerState.Inactive) DrawSleepingMouse(g, theme, size);
                else if (reading.Band != BatteryBand.Unknown || reading.Millivolts.HasValue)
                    DrawReportedLevel(g, reading, theme, size);
                else if (style == IconStyle.Apple) DrawCell(g, reading, theme, size);
                else if (style == IconStyle.Material)
                {
                    DrawRing(g, reading, theme, size);
                    DrawRingNumber(g, reading, theme, size);
                }
                else
                {
                    g.ScaleTransform(size / 100f, size / 100f);
                    DrawVariant(g, reading, style, theme, size);
                }
            });
        }

        private static void DrawReportedLevel(Graphics g, Reading reading, TrayTheme theme, int size)
        {
            float unit = size / 20f;
            if (reading.Millivolts.HasValue)
            {
                using (var pen = new Pen(theme.Ink, 2.3f * unit))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(4 * unit, 4 * unit), new PointF(10 * unit, 16 * unit),
                                             new PointF(16 * unit, 4 * unit) });
                }
                return;
            }
            for (int i = 0; i < 4; i++)
            {
                float height = (5 + i * 3) * unit;
                var box = new RectangleF((1.5f + 4.5f * i) * unit, 17 * unit - height, 3.3f * unit, height);
                using (var bar = Rounded(box, 1.1f * unit))
                using (var ink = new SolidBrush(theme.Ink))
                using (var outline = new Pen(theme.Ink, Math.Max(0.75f, 0.8f * unit)))
                {
                    if (i < (int)reading.Band) g.FillPath(ink, bar);
                    else g.DrawPath(outline, bar);
                }
            }
        }

        internal static Icon ToIcon(Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
            }
            finally { DestroyIcon(handle); }
        }

        private static Bitmap Render(int size, Action<Graphics> draw)
        {
            const int sampling = 4;
            using (var source = new Bitmap(size * sampling, size * sampling, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(source))
                {
                    g.ScaleTransform(sampling, sampling);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    draw(g);
                }
                var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                try
                {
                    using (var g = Graphics.FromImage(bitmap))
                    using (var edges = new ImageAttributes())
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        edges.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0,
                                    source.Width, source.Height, GraphicsUnit.Pixel, edges);
                    }
                    return bitmap;
                }
                catch { bitmap.Dispose(); throw; }
            }
        }

        private static void DrawCell(Graphics g, Reading reading, TrayTheme theme, int size)
        {
            bool marked = reading.Power == PowerState.Charging || reading.Power == PowerState.ChargeError;
            float terminal = Math.Max(1.3f, size * (marked ? 0.16f : 0.071f));
            float gap = Math.Max(0.7f, size * 0.035f);
            float h = (float)Math.Round(size * 0.57);
            var body = new RectangleF(0.2f, (size - h) / 2f, size - terminal - gap - 0.4f, h);
            var marker = new RectangleF(body.Right + gap, (size - h * 0.50f) / 2, terminal, h * 0.50f);
            Color active = StateColor(reading, theme, theme.Ink);
            if (marked) DrawPowerMark(g, marker, reading.Power, active);
            else
            {
                using (var cap = new GraphicsPath())
                using (var brush = new SolidBrush(theme.HighContrast ? theme.Ink : Blend(theme.Ink, theme.Paper, 0.38f)))
                {
                    float y = size / 2f - h * 0.17f;
                    cap.AddBezier(marker.X, y, marker.Right + terminal * 0.24f, y + h * 0.03f,
                                  marker.Right + terminal * 0.24f, y + h * 0.31f, marker.X, y + h * 0.34f);
                    cap.CloseFigure();
                    g.FillPath(brush, cap);
                }
            }

            var lettering = new RectangleF(body.X + 1.1f, body.Y + h * 0.13f, body.Width - 2.2f, h * 0.74f);
            using (var shape = Rounded(body, h * 0.26f))
            using (var number = Number(reading.Percent, lettering, size))
            {
                if (!reading.Percent.HasValue)
                {
                    using (var line = new Pen(theme.Ink, Math.Max(1, size * 0.052f))) g.DrawPath(line, shape);
                    using (var ink = new SolidBrush(theme.Ink)) g.FillPath(ink, number);
                    return;
                }

                Color empty = theme.HighContrast ? theme.Paper : Blend(active, theme.Paper, theme.Dark ? 0.32f : 0.24f);
                using (var track = new SolidBrush(empty)) g.FillPath(track, shape);
                var charged = new RectangleF(body.X, body.Y, body.Width * reading.Percent.Value / 100f, body.Height);
                if (charged.Width > 0)
                {
                    GraphicsState saved = g.Save();
                    g.SetClip(charged);
                    using (var brush = new SolidBrush(active)) g.FillPath(brush, shape);
                    g.Restore(saved);
                }
                if (theme.HighContrast)
                {
                    using (var line = new Pen(theme.Ink, Math.Max(1, size * 0.045f))) g.DrawPath(line, shape);
                    using (var text = new SolidBrush(theme.Ink)) g.FillPath(text, number);
                    if (charged.Width <= 0) return;
                    GraphicsState saved = g.Save();
                    g.SetClip(charged);
                    using (var text = new SolidBrush(theme.Paper)) g.FillPath(text, number);
                    g.Restore(saved);
                }
                else
                {
                    // Both battery tones contrast with the same numeral, even across the fill boundary.
                    using (var text = new SolidBrush(theme.Paper)) g.FillPath(text, number);
                }
            }
        }

        private static void DrawRing(Graphics g, Reading reading, TrayTheme theme, int size)
        {
            float stroke = size * 0.15f;
            float inset = 0.4f + stroke / 2;
            var ring = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            Color signal = StateColor(reading, theme, theme.Ink);
            Color track = theme.HighContrast ? theme.Ink :
                (theme.Dark ? Color.FromArgb(69, 76, 88) : Color.FromArgb(192, 199, 208));
            bool marked = reading.Power == PowerState.Charging || reading.Power == PowerState.ChargeError;
            float opening = marked ? 60 : 0;
            float start = -90 + opening / 2;
            float sweep = 360 - opening;
            int percent = reading.Percent.GetValueOrDefault();
            float used = sweep * percent / 100f;

            using (var rail = new Pen(track, stroke))
            {
                rail.StartCap = rail.EndCap = LineCap.Round;
                if (theme.HighContrast) rail.DashStyle = DashStyle.Dot;
                DrawArc(g, ring, rail, start + used, sweep - used);
            }
            if (percent > 20 && !theme.HighContrast && reading.Power != PowerState.ChargeError)
            {
                DrawSpectrumArc(g, ring, stroke, start, used, size, theme.Dark);
            }
            else if (percent > 0)
            {
                using (var arc = new Pen(signal, stroke))
                {
                    arc.StartCap = arc.EndCap = LineCap.Round;
                    DrawArc(g, ring, arc, start, used);
                }
            }
            if (marked)
                DrawPowerMark(g, new RectangleF(size * 0.42f, size * 0.005f, size * 0.16f, size * 0.23f),
                              reading.Power, signal);
        }

        private static void DrawRingNumber(Graphics g, Reading reading, TrayTheme theme, int size)
        {
            var center = new RectangleF(size * 0.23f, size * 0.33f, size * 0.54f, size * 0.34f);
            bool marked = reading.Power == PowerState.Charging || reading.Power == PowerState.ChargeError;
            Color ink = marked ? theme.Ink : StateColor(reading, theme, theme.Ink);
            using (var digits = Number(reading.Percent, center, size))
            using (var brush = new SolidBrush(ink)) g.FillPath(brush, digits);
        }

        // Clockwise from twelve o'clock. The last sector blends back into the first at full charge.
        private static readonly Color[] Spectrum = {
            Color.FromArgb(0, 155, 169), Color.FromArgb(35, 126, 238),
            Color.FromArgb(105, 84, 230), Color.FromArgb(170, 71, 205),
            Color.FromArgb(218, 71, 137), Color.FromArgb(234, 111, 75),
            Color.FromArgb(207, 154, 37), Color.FromArgb(47, 162, 113)
        };

        private static void DrawSpectrumArc(Graphics g, RectangleF ring, float stroke, float start,
                                            float sweep, int size, bool dark)
        {
            // Texture the pen with a sweep gradient so GDI+ draws both caps and the arc in one pass.
            using (var texture = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                double center = (size - 1) / 2.0;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        double angle = Math.Atan2(x - center, center - y);
                        if (angle < 0) angle += 2 * Math.PI;
                        double position = angle * Spectrum.Length / (2 * Math.PI);
                        int sector = (int)position;
                        Color pixel = Blend(Spectrum[sector], Spectrum[(sector + 1) % Spectrum.Length],
                                            (float)(position - sector));
                        texture.SetPixel(x, y, dark ? Blend(pixel, Color.White, 0.28f) : pixel);
                    }
                }
                using (var brush = new TextureBrush(texture, WrapMode.Clamp))
                using (var arc = new Pen(brush, stroke))
                {
                    arc.StartCap = arc.EndCap = LineCap.Round;
                    DrawArc(g, ring, arc, start, sweep);
                }
            }
        }

        private static void DrawArc(Graphics g, RectangleF ring, Pen pen, float start, float sweep)
        {
            if (sweep <= 0) return;
            if (sweep >= 359.99f) { g.DrawEllipse(pen, ring); return; }
            // The active and inactive arcs have rounded ends with a small clear gap.
            float trim = Math.Min(sweep * 0.44f, (pen.Width + 0.65f) / ring.Width * 180f / (float)Math.PI);
            g.DrawArc(pen, ring.X, ring.Y, ring.Width, ring.Height, start + trim, sweep - 2 * trim);
        }

        private static Color StateColor(Reading reading, TrayTheme theme, Color normal)
        {
            if (theme.HighContrast) return theme.Ink;
            if (!reading.Percent.HasValue) return normal;
            if (reading.Power == PowerState.Charging)
                return theme.Dark ? Color.FromArgb(120, 220, 153) : Color.FromArgb(21, 103, 62);
            if (reading.Power == PowerState.ChargeError || reading.Percent <= 10)
                return theme.Dark ? Color.FromArgb(255, 155, 150) : Color.FromArgb(161, 43, 53);
            if (reading.Percent <= 20)
                return theme.Dark ? Color.FromArgb(249, 207, 126) : Color.FromArgb(124, 84, 19);
            return normal;
        }

        private static void DrawPowerMark(Graphics g, RectangleF box, PowerState power, Color color)
        {
            using (var brush = new SolidBrush(color))
            {
                if (power == PowerState.ChargeError)
                {
                    g.FillRectangle(brush, box.X + box.Width * 0.34f, box.Y, box.Width * 0.32f, box.Height * 0.62f);
                    g.FillEllipse(brush, box.X + box.Width * 0.32f, box.Y + box.Height * 0.76f,
                                  box.Width * 0.36f, box.Height * 0.22f);
                    return;
                }
                using (var bolt = new GraphicsPath())
                using (var transform = new Matrix(box.Width, 0, 0, box.Height, box.X, box.Y))
                {
                    bolt.AddPolygon(new[] { new PointF(0.71f, 0), new PointF(0, 0.59f), new PointF(0.39f, 0.59f),
                                            new PointF(0.24f, 1), new PointF(1, 0.36f), new PointF(0.59f, 0.36f) });
                    bolt.Transform(transform);
                    g.FillPath(brush, bolt);
                }
            }
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = 2 * radius;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Blend(Color first, Color second, float amount)
        {
            return Color.FromArgb((int)Math.Round(first.R + (second.R - first.R) * amount),
                                  (int)Math.Round(first.G + (second.G - first.G) * amount),
                                  (int)Math.Round(first.B + (second.B - first.B) * amount));
        }

        private static GraphicsPath Number(int? percent, RectangleF area, int size)
        {
            var path = new GraphicsPath(FillMode.Winding);
            float cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
            if (!percent.HasValue)
            {
                float w = area.Width * 0.44f, h = Math.Max(1, size * 0.057f);
                path.AddRectangle(new RectangleF(cx - w / 2, cy - h / 2, w, h));
                return path;
            }
            string label = percent.Value.ToString(CultureInfo.InvariantCulture);
            using (var format = StringFormat.GenericTypographic)
                path.AddString(label, NumeralFamily, (int)FontStyle.Regular, 64, PointF.Empty, format);
            RectangleF b = path.GetBounds();
            float scale = Math.Min(area.Height / b.Height, area.Width / b.Width);
            using (var m = new Matrix(scale, 0, 0, scale, cx - (b.X + b.Width / 2) * scale,
                                      cy - (b.Y + b.Height / 2) * scale))
                path.Transform(m);
            return path;
        }

        private static readonly FontFamily NumeralFamily = FindNumeralFamily();

        private static FontFamily FindNumeralFamily()
        {
            foreach (string name in new[] { "MiSans Semibold", "MiSans", "Segoe UI Semibold" })
            {
                try { return new FontFamily(name); }
                catch (ArgumentException) { }
            }
            return FontFamily.GenericSansSerif;
        }
    }
}
