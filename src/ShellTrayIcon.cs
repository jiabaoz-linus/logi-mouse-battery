using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GpwBattery
{
    internal sealed class ShellTrayIcon : NativeWindow, IDisposable
    {
        private const int Add = 0, Modify = 1, Delete = 2, SetFocus = 3, SetVersion = 4;
        private const uint MessageFlag = 1, IconFlag = 2, TipFlag = 4, GuidFlag = 0x20, ShowTip = 0x80;
        private const int Callback = 0x8001;
        private const string WindowTitle = "GPW 鼠标电量 · 托盘";
        private const string RegistrationProperty = "GpwBattery.Registered";
        private static readonly Guid IconGuid = new Guid("a09b9614-6df7-4298-ae4e-002f3ac59caf");
        private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private static readonly int Refresh = RegisterWindowMessage("GpwBattery.Refresh.a09b9614");

        private readonly ContextMenuStrip menu;
        private readonly Timer retry = new Timer { Interval = 1500 };
        private Icon icon;
        private string tip;
        private bool registered, version4, disposed;

        public ShellTrayIcon(ContextMenuStrip menu)
        {
            this.menu = menu;
            CreateHandle(new CreateParams { Caption = WindowTitle });
            retry.Tick += delegate { RegisterOrUpdate(); };
            menu.Closed += MenuClosed;
        }

        public void Update(Icon nextIcon, string nextTip)
        {
            if (registered && ReferenceEquals(icon, nextIcon) && tip == nextTip) return;
            icon = nextIcon;
            tip = nextTip;
            RegisterOrUpdate();
        }

        private NotifyData Identity()
        {
            return new NotifyData { Size = (uint)Marshal.SizeOf(typeof(NotifyData)),
                                    Window = Handle, Id = 1, Flags = GuidFlag, Guid = IconGuid };
        }

        private void RegisterOrUpdate()
        {
            if (disposed || icon == null) return;
            var data = Identity();
            data.Flags |= MessageFlag | IconFlag | TipFlag | ShowTip;
            data.CallbackMessage = Callback;
            data.Icon = icon.Handle;
            data.Tip = tip;
            if (registered && Shell_NotifyIcon(Modify, ref data)) return;

            // A restarted shell may have lost the icon. A delayed broadcast
            // can also arrive after the shell has already accepted it again.
            registered = Shell_NotifyIcon(Add, ref data) || Shell_NotifyIcon(Modify, ref data);
            if (registered)
            {
                var version = Identity();
                version.Version = 4;
                version4 = Shell_NotifyIcon(SetVersion, ref version);
            }
            SetProp(Handle, RegistrationProperty, registered ? new IntPtr(1) : new IntPtr(2));
            retry.Enabled = !registered;
        }

        public static void RefreshRunningInstance()
        {
            IntPtr window = FindWindow(null, WindowTitle);
            if (window != IntPtr.Zero) PostMessage(window, Refresh, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == TaskbarCreated || message.Msg == Refresh)
            {
                registered = false;
                RegisterOrUpdate();
                return;
            }
            if (message.Msg == Callback)
            {
                int action = (int)(message.LParam.ToInt64() & 0xffff);
                bool open = version4 ? action == 0x007b || action == 0x0400 || action == 0x0401
                                     : action == 0x0202 || action == 0x0205;
                if (open && !menu.Visible)
                {
                    var position = Cursor.Position;
                    int packed = unchecked((int)message.WParam.ToInt64());
                    if (version4 && packed != -1)
                        position = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
                    else if (version4)
                    {
                        Rect bounds;
                        if (GetBounds(out bounds) == 0) position = new Point(bounds.Left, bounds.Top);
                    }
                    SetForegroundWindow(Handle);
                    menu.Show(position);
                }
                return;
            }
            base.WndProc(ref message);
        }

        private void MenuClosed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            if (!registered || disposed) return;
            var data = Identity();
            Shell_NotifyIcon(SetFocus, ref data);
        }

        public void Dispose()
        {
            disposed = true;
            retry.Dispose();
            menu.Closed -= MenuClosed;
            if (registered)
            {
                var data = Identity();
                Shell_NotifyIcon(Delete, ref data);
            }
            RemoveProp(Handle, RegistrationProperty);
            DestroyHandle();
        }

        private static int GetBounds(out Rect bounds)
        {
            var id = new Identifier { Size = (uint)Marshal.SizeOf(typeof(Identifier)), Guid = IconGuid };
            return Shell_NotifyIconGetRect(ref id, out bounds);
        }

        public bool TryGetVisibleTaskbarCell(out Rectangle cell, out IntPtr taskbar)
        {
            cell = Rectangle.Empty;
            taskbar = IntPtr.Zero;
            Rect bounds;
            if (!registered || GetBounds(out bounds) != 0) return false;
            cell = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            var center = new Point(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2);
            taskbar = GetAncestor(WindowFromPoint(center), 2); // GA_ROOT
            var name = new StringBuilder(64);
            if (taskbar == IntPtr.Zero || GetClassName(taskbar, name, name.Capacity) == 0) return false;
            string kind = name.ToString();
            return kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd";
        }

        internal static int Describe(out string description)
        {
            IntPtr owner = FindWindow(null, WindowTitle);
            bool accepted = owner != IntPtr.Zero && GetProp(owner, RegistrationProperty) == new IntPtr(1);
            Rect bounds;
            int hr = GetBounds(out bounds);
            description = String.Format("owner={0} registered={1} boundsResult=0x{2:X8} rect={3},{4},{5},{6}",
                owner != IntPtr.Zero, accepted, hr, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            return accepted ? 0 : 2;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyData
        {
            public uint Size;
            public IntPtr Window;
            public uint Id, Flags, CallbackMessage;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags;
            public Guid Guid;
            public IntPtr BalloonIcon;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
        private static extern bool Shell_NotifyIcon(int command, ref NotifyData data);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect bounds);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr RemoveProp(IntPtr window, string name);
    }
}
