using System;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("鼠标电量")]
[assembly: AssemblyDescription("Windows 托盘蓝牙与罗技无线鼠标电量工具")]
[assembly: AssemblyProduct("Logi Mouse Battery")]
[assembly: AssemblyVersion("2.0.2.0")]

namespace GpwBattery
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && (args[0] == "--status" || args[0] == "--list"))
            {
                BatterySnapshot snapshot = null;
                BatteryMonitor.Run(value => snapshot = value, CancellationToken.None, true);
                var result = snapshot.Select(TrayApp.Preference("MouseKey"), TrayApp.Preference("MouseName"));
                if (args[0] == "--list" && snapshot.Devices.Length != 0)
                {
                    foreach (var mouse in snapshot.Devices)
                        Print(mouse.Name + " · " + mouse.Source + " · " + mouse.StatusText);
                    return 0;
                }
                Print(result.Name + " · " + result.StatusText);
                return result.HasLevel ? 0 : 2;
            }
            if (args.Length == 1 && args[0] == "--tray-status")
            {
                string description;
                int code = ShellTrayIcon.Describe(out description);
                Print(description);
                return code;
            }

            bool first;
            using (var instance = new Mutex(true, @"Local\GpwBatteryTray.9D3080AF", out first))
            {
                if (!first)
                {
                    ShellTrayIcon.RefreshRunningInstance();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var tray = new TrayApp()) Application.Run(tray);
                return 0;
            }
        }

        private static void Print(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + Environment.NewLine);
            using (var output = Console.OpenStandardOutput()) output.Write(bytes, 0, bytes.Length);
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "GpwBatteryTray";
        private const string Preferences = @"HKEY_CURRENT_USER\Software\GpwBattery";
        private readonly Control dispatcher = new Control();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem model = new ToolStripMenuItem();
        private readonly ToolStripMenuItem status = new ToolStripMenuItem();
        internal const string StylesCaption = "图标样式";
        private readonly ToolStripMenuItem styles = new ToolStripMenuItem(StylesCaption);
        private readonly ToolStripMenuItem mice = new ToolStripMenuItem("显示哪只鼠标");
        private static readonly Reading PreviewReading = new Reading("样式预览", 70, PowerState.Battery, "");
        private readonly ToolStripMenuItem startup = new ToolStripMenuItem("随 Windows 登录启动");
        private readonly ShellTrayIcon tray;
        private readonly System.Windows.Forms.Timer placement = new System.Windows.Forms.Timer { Interval = 350 };
        private readonly Thread reader;
        private TrayArtwork artwork;
        private string tooltip;
        private string iconKey;
        private Reading currentReading = Reading.Unavailable("正在查找鼠标");
        private BatterySnapshot latest;
        private string preferred = Preference("MouseKey"), preferredName = Preference("MouseName");
        private IconStyle? sessionStyle;

        public TrayApp()
        {
            dispatcher.CreateControl();
            model.Enabled = false;
            status.Enabled = false;
            menu.Items.Add(model);
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            mice.Visible = !String.IsNullOrEmpty(preferred);
            menu.Items.Add(mice);
            AddStyle(IconStyle.Apple, "Apple 横向电池");
            AddStyle(IconStyle.Material, "Google Material 炫彩圆环");
            AddStyle(IconStyle.Aurora, "极光环");
            AddStyle(IconStyle.Rail, "侧标数字");
            AddStyle(IconStyle.Horizon, "地平弧");
            AddStyle(IconStyle.Moon, "月相");
            AddStyle(IconStyle.Flip, "分格数字");
            AddStyle(IconStyle.Squircle, "圆角方环");
            var choices = (ToolStripDropDownMenu)styles.DropDown;
            choices.ShowCheckMargin = choices.ShowImageMargin = true;
            styles.DropDownOpening += delegate { RefreshStylePreviews(); };
            menu.Items.Add(styles);
            menu.Items.Add(startup);
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            menu.Opening += delegate { startup.Checked = StartsAtLogin(); RefreshMouseMenu(); };
            startup.Click += delegate { ToggleStartup(); };
            tray = new ShellTrayIcon(menu);
            placement.Tick += delegate { tray.Update(artwork, tooltip); };
            dispatcher.BeginInvoke((Action)delegate { Apply(currentReading); });
            reader = new Thread(delegate() { BatteryMonitor.Run(PostReading, stopping.Token, false); });
            reader.Name = "Mouse battery monitor";
            reader.IsBackground = true;
            reader.Start();
        }

        private void AddStyle(IconStyle style, string caption)
        {
            var item = new ToolStripMenuItem(caption) { Tag = style };
            item.Click += delegate { ChooseStyle(style); };
            styles.DropDownItems.Add(item);
        }

        private void RefreshStylePreviews()
        {
            var choices = styles.DropDown;
            Color paper = choices.BackColor;
            var theme = new TrayTheme(choices.ForeColor, paper, paper.GetBrightness() < 0.5f, SystemInformation.HighContrast);
            int size = BatteryIcon.TraySize();
            size += 2 * Math.Max(1, (int)Math.Round(size * 0.14));
            Reading preview = PreviewFor(currentReading);
            choices.ImageScalingSize = new Size(size, size);
            foreach (ToolStripMenuItem item in styles.DropDownItems)
            {
                Image previous = item.Image;
                item.Image = BatteryIcon.RenderFace(preview, (IconStyle)item.Tag, theme, size);
                if (previous != null) previous.Dispose();
            }
        }

        internal static Reading PreviewFor(Reading reading)
        {
            return reading.Percent.HasValue ? reading : PreviewReading;
        }

        private void RefreshMouseMenu()
        {
            while (mice.DropDownItems.Count != 0) mice.DropDownItems[0].Dispose();
            AddMouseChoice("自动选择", null, null);
            if (latest == null) return;
            foreach (var mouse in latest.Devices)
                AddMouseChoice(mouse.Name + " · " + mouse.Source + " · " + mouse.StatusText, mouse.Key, mouse.Name);
        }

        private void AddMouseChoice(string caption, string key, string name)
        {
            var item = new ToolStripMenuItem(caption) { Checked = String.Equals(preferred ?? "", key ?? "", StringComparison.OrdinalIgnoreCase) };
            item.Click += delegate
            {
                preferred = key;
                preferredName = name;
                try
                {
                    Registry.SetValue(Preferences, "MouseKey", key ?? "");
                    Registry.SetValue(Preferences, "MouseName", name ?? "");
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.IO.IOException) { }
                if (latest != null) Apply(latest.Select(preferred, preferredName));
            };
            mice.DropDownItems.Add(item);
        }

        internal static string Preference(string key)
        {
            try { return Registry.GetValue(Preferences, key, null) as string; }
            catch (System.Security.SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        private void PostReading(BatterySnapshot snapshot)
        {
            if (stopping.IsCancellationRequested) return;
            try
            {
                dispatcher.BeginInvoke((Action)delegate
                {
                    if (stopping.IsCancellationRequested) return;
                    latest = snapshot;
                    mice.Visible = snapshot.Devices.Length > 1 || !String.IsNullOrEmpty(preferred);
                    Apply(snapshot.Select(preferred, preferredName));
                });
            }
            catch (InvalidOperationException)
            {
                if (!stopping.IsCancellationRequested) throw;
            }
        }

        private void Apply(Reading value)
        {
            currentReading = value;
            IconStyle style = CurrentStyle();
            foreach (ToolStripMenuItem item in styles.DropDownItems)
                item.Checked = (IconStyle)item.Tag == style;
            TrayTheme theme = TrayTheme.Current();
            int size = BatteryIcon.TraySize();
            string key = value.Percent + "/" + value.Band + "/" + value.Millivolts.HasValue + "/" + value.Power + "/" + style + "/" + size + "/" +
                         theme.Ink.ToArgb() + "/" + theme.Paper.ToArgb() + "/" + theme.HighContrast;
            TrayArtwork previous = artwork;
            if (key != iconKey)
            {
                artwork = new TrayArtwork(value, style, theme, size);
                iconKey = key;
                if (styles.DropDown.Visible) RefreshStylePreviews();
            }
            model.Text = value.Name;
            status.Text = value.StatusText;
            string fullTip = value.Name + Environment.NewLine + value.StatusText;
            tooltip = fullTip.Substring(0, Math.Min(fullTip.Length, 127));
            placement.Enabled = artwork.Enlarged != null;
            tray.Update(artwork, tooltip);
            if (previous != null && previous != artwork) previous.Dispose();
        }

        private IconStyle CurrentStyle()
        {
            if (sessionStyle.HasValue) return sessionStyle.Value;
            try
            {
                var saved = Registry.GetValue(Preferences, "IconStyle", null) as string;
                return ParseStyle(saved);
            }
            catch (System.Security.SecurityException) { return IconStyle.Apple; }
            catch (UnauthorizedAccessException) { return IconStyle.Apple; }
            catch (System.IO.IOException) { return IconStyle.Apple; }
        }

        internal static IconStyle ParseStyle(string saved)
        {
            // v1.6 stored the names of the six original faces in the user's preferences.
            switch ((saved ?? "").ToLowerInvariant())
            {
                case "segments": return IconStyle.Aurora;
                case "minimal": return IconStyle.Rail;
                case "gauge": return IconStyle.Horizon;
                case "tide": return IconStyle.Moon;
                case "bars": return IconStyle.Flip;
                case "mouse": return IconStyle.Squircle;
            }
            IconStyle parsed;
            return Enum.TryParse(saved, true, out parsed) &&
                   String.Equals(saved, Enum.GetName(typeof(IconStyle), parsed), StringComparison.OrdinalIgnoreCase)
                   ? parsed : IconStyle.Apple;
        }

        private void ChooseStyle(IconStyle chosen)
        {
            sessionStyle = chosen;
            try
            {
                Registry.SetValue(Preferences, "IconStyle", chosen.ToString(), RegistryValueKind.String);
                sessionStyle = null;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
            Apply(currentReading);
        }

        private static string StartupCommand { get { return "\"" + Application.ExecutablePath + "\""; } }

        private static bool StartsAtLogin()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && String.Equals(key.GetValue(RunName) as string,
                                                    StartupCommand, StringComparison.OrdinalIgnoreCase);
        }

        private void ToggleStartup()
        {
            try
            {
                bool enabled = StartsAtLogin();
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) key.DeleteValue(RunName, false);
                    else key.SetValue(RunName, StartupCommand, RegistryValueKind.String);
                }
                startup.Checked = !enabled;
            }
            catch (Exception)
            {
                MessageBox.Show("Windows 未能保存登录启动设置。", Reading.AppName,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stopping.Cancel();
                placement.Dispose();
                tray.Dispose();
                if (artwork != null) artwork.Dispose();
                foreach (ToolStripMenuItem item in styles.DropDownItems)
                {
                    Image preview = item.Image;
                    item.Image = null;
                    if (preview != null) preview.Dispose();
                }
                menu.Dispose();
                if (reader.Join(1000)) stopping.Dispose();
                dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
