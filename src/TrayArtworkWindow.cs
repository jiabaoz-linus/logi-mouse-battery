using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpwBattery
{
    // Explorer displays the center and handles input. This window adds only the surrounding pixels.
    internal sealed class TrayArtworkWindow : NativeWindow, IDisposable
    {
        private const int Popup = unchecked((int)0x80000000), Disabled = 0x08000000;
        private const int Layered = 0x80000, Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
        private const uint NoSize = 1, NoZOrder = 4, NoFocus = 0x10, Show = 0x40;
        private Bitmap uploaded;
        private Point location;
        private bool visible;

        public bool TryShow(Bitmap image, Rectangle cell, IntPtr taskbar)
        {
            if (cell.Width < image.Width + 2 || cell.Height < image.Height + 2) return false;
            var point = new Point(cell.Left + (cell.Width - image.Width) / 2,
                                  cell.Top + (cell.Height - image.Height) / 2);
            try
            {
                if (Handle == IntPtr.Zero)
                {
                    CreateHandle(new CreateParams { Caption = "GPW 鼠标电量 · 放大图标", Style = Popup | Disabled,
                                                    ExStyle = Layered | Transparent | ToolWindow | NoActivate });
                }
                if (uploaded != image)
                {
                    if (!Upload(image, point)) return false;
                    uploaded = image;
                }
            }
            catch (ExternalException) { return false; }

            // Insert directly above the taskbar, leaving flyouts and other windows above it.
            IntPtr above = GetWindow(taskbar, 3); // GW_HWNDPREV
            if (!visible || point != location || above != Handle)
            {
                uint flags = NoSize | NoFocus | Show;
                if (above == Handle) flags |= NoZOrder;
                if (above == IntPtr.Zero && (GetWindowLong(taskbar, -20) & 8) != 0) above = new IntPtr(-1);
                if (!SetWindowPos(Handle, above, point.X, point.Y, 0, 0, flags)) return false;
                location = point;
                visible = true;
            }
            return true;
        }

        private bool Upload(Bitmap image, Point position)
        {
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            IntPtr bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                bitmap = image.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(dc, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return false;
                Point origin = Point.Empty;
                Size size = image.Size;
                var blend = new BlendFunction { Opacity = 255, AlphaFormat = 1 };
                return UpdateLayeredWindow(Handle, IntPtr.Zero, ref position, ref size, dc, ref origin,
                                           0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(dc);
            }
        }

        public void Hide()
        {
            if (visible) ShowWindow(Handle, 0);
            visible = false;
            uploaded = null;
        }

        public void Dispose()
        {
            Hide();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction { public byte Op, Flags, Opacity, AlphaFormat; }

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dest, ref Point position, ref Size size,
                                                      IntPtr source, ref Point origin, uint key, ref BlendFunction blend, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr source);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
    }
}
