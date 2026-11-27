using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GpwBattery
{
    internal static class BatteryTests
    {
        private static int checks;
        private static void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            checks++;
        }

        [STAThread]
        public static int Main()
        {
            TestGHub();
            TestBluetooth();
            TestHid();
            TestSelection();
            TestMenu();
            TestTrayArtwork();
            Console.WriteLine("Passed " + checks + " checks.");
            return 0;
        }

        private static void TestGHub()
        {
            var wireless = new GHub.Device { id = "new", displayName = "Future model", deviceType = "MOUSE", state = "ACTIVE" };
            var wired = new GHub.Device { id = "wired", displayName = "G502 Wired", deviceType = "MOUSE", state = "ACTIVE" };
            var keyboard = new GHub.Device { id = "k", deviceType = "KEYBOARD", state = "ACTIVE" };
            var inactive = new GHub.Device { id = "new", deviceType = "MOUSE", state = "INACTIVE" };
            var mice = GHub.FindMice(new[] { keyboard, null, wired, inactive, wireless }).ToArray();
            Check(mice.Length == 2 && mice.Contains(wireless), "model-independent discovery and active duplicate");
            var empty = GHub.ParseReading(wired, null);
            var battery = GHub.ParseReading(wireless, new GHub.Payload { deviceId = "new", percentage = 0 });
            Check(battery.Percent == 0 && empty.Percent == null, "real zero and missing are distinct");
            Check(GHub.ParseReading(wireless, new GHub.Payload { deviceId = "other", percentage = 70 }).Percent == null, "foreign broadcast");
            foreach (int invalid in new[] { -1, 101, 255 })
                Check(!GHub.ParseReading(wireless, new GHub.Payload { percentage = invalid }).HasLevel, "invalid percentage");
            Check(GHub.ParseReading(wireless, new GHub.Payload { percentage = 93, charging = true }).Power == PowerState.Charging, "charging");
            Check(new BatterySnapshot(new[] { empty, battery }).Select(null, null).Key == battery.Key, "wired device cannot mask wireless");
        }

        private static void TestBluetooth()
        {
            Check(BluetoothMice.IsMouse(false, 0x03c2, null, null), "BLE mouse of any vendor");
            Check(BluetoothMice.IsMouse(false, null, 5, 0x20), "classic pointing device");
            Check(BluetoothMice.IsMouse(true, 0x03c0, null, null), "generic appearance with mouse interface");
            Check(BluetoothMice.IsMouse(true, 0x0080, null, null), "non-HID appearance with a Windows mouse interface");
            Check(!BluetoothMice.IsMouse(true, 0x03c1, null, null), "explicit keyboard advertising a companion mouse");
            Check(!BluetoothMice.IsMouse(false, null, 4, 0x20), "headset");
            Check(!BluetoothMice.IsMouse(false, 0x03c4, null, null), "gamepad");
            Check(!BluetoothMice.IsMouse(false, null, 5, 0x10), "classic keyboard");
            Check(BluetoothMice.Number((byte)0, 100) == 0 && BluetoothMice.Number((uint)100, 100) == 100, "Windows battery boundaries");
            Check(BluetoothMice.Number((byte)255, 100) == null && BluetoothMice.Number(null, 100) == null &&
                  BluetoothMice.Number(true, 100) == null, "unknown and unrelated properties");
            var collision = Guid.NewGuid();
            var external = Guid.NewGuid();
            var host = new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF");
            Check(BluetoothMice.PnpAddress(@"BTHLE\DEV_102030AABBCC\fixture") == "102030AABBCC", "BLE physical node");
            Check(BluetoothMice.PnpAddress(@"BTHENUM\Dev_102030AAbbCC\fixture") == "102030AABBCC", "classic physical node");
            Check(BluetoothMice.PnpAddress(@"BTHENUM\{service}\DEV_102030AABBCC") == null, "service suffix cannot identify a mouse");
            Check(BluetoothMice.NormalizeAddress("00:00:00:00:00:00") == null, "invalid peer address");
            Check(BluetoothMice.PhysicalContainer(collision, new[] { external }) == external, "match physical device by address when association ID differs");
            Check(BluetoothMice.PhysicalContainer(collision, new[] { Guid.NewGuid(), external }) == Guid.Empty, "ambiguous physical match");
            Check(BluetoothMice.PhysicalContainer(host, new Guid[0]) == Guid.Empty, "never use host battery");

            TestBluetoothConnection();
        }

        private static void TestBluetoothConnection()
        {
            int windowsCalls = 0, gattCalls = 0;
            int? cached = 70;
            BluetoothMice.GattSample fresh = new BluetoothMice.GattSample(38);
            Func<CancellationToken, Task<int?>> windows = token => { windowsCalls++; return Task.FromResult(cached); };
            Func<CancellationToken, Task<BluetoothMice.GattSample>> gatt = token => { gattCalls++; return Task.FromResult(fresh); };
            Func<bool?, bool, Reading> read = (connected, visible) => BluetoothMice.ReadLevel("Test mouse", "bt/x",
                connected, visible, windows, gatt, CancellationToken.None).GetAwaiter().GetResult();

            bool? offline = BluetoothMice.ResolveConnection(false, false, false, null, false);
            Check(offline == false && read(offline, false).Power == PowerState.Inactive && windowsCalls == 0 && gattCalls == 0,
                  "corroborated disconnect hides a cached percentage and does not open GATT");
            bool? inconclusive = BluetoothMice.ResolveConnection(false, false, null, null, false);
            Check(inconclusive == null && read(inconclusive, false).Power == PowerState.ConnectionUnknown && windowsCalls == 0 && gattCalls == 0,
                  "one negative association record cannot prove sleep");
            Check(BluetoothMice.ResolveConnection(true, false, false, false, false) == true, "connected Windows enumeration wins");
            Check(BluetoothMice.ResolveConnection(false, false, false, true, false) == true, "live Bluetooth link wins");
            Check(BluetoothMice.ResolveConnection(false, true, null, false, false) == true, "positive association report");
            bool? online = BluetoothMice.ResolveConnection(false, false, true, null, false);
            Check(online == true && read(online, false).Percent == 70 && gattCalls == 0, "connected device container overrides stale endpoint");
            cached = 0;
            Check(read(online, false).Percent == 0 && gattCalls == 0, "actual Windows zero is a reading");
            cached = null;
            Check(read(online, false).Percent == 38 && gattCalls == 1, "fresh standard service when Windows has no percentage");
            fresh = null;
            var noBattery = read(online, false);
            Check(noBattery.Power == PowerState.ConnectedNoLevel && !noBattery.HasLevel &&
                  noBattery.StatusText == "蓝牙已连接 · 暂未读到电量", "paired endpoint false, Windows connected, no usable battery");
            Check(BluetoothMice.ReadLevel("classic", "bt/c", online, false, windows, null, CancellationToken.None).Result.Power ==
                  PowerState.ConnectedNoLevel, "classic mouse connected without a battery property");
            bool? mixed = BluetoothMice.ResolveConnection(false, false, false, false, true);
            Check(mixed == null, "enabled input interface conflicts with offline reports");
            int previousReads = windowsCalls;
            cached = 94;
            Check(read(mixed, true).Power == PowerState.ConnectionUnknown && windowsCalls == previousReads, "presence alone cannot validate cached battery");
            fresh = new BluetoothMice.GattSample(0);
            Check(read(mixed, true).Percent == 0 && windowsCalls == previousReads, "uncached BLE reply overrides stale offline status");
            fresh = new BluetoothMice.GattSample(null);
            Check(read(mixed, true).Percent == 94, "live GATT discovery validates same-device Windows property");
            cached = null;
            Check(read(mixed, true).Power == PowerState.ConnectedNoLevel, "live link with no battery service");
            Func<CancellationToken, Task<int?>> denied = token => { throw new UnauthorizedAccessException(); };
            Check(BluetoothMice.ReadLevel("mouse", "bt/c", true, true, denied, null, CancellationToken.None).Result.Power ==
                  PowerState.ConnectedNoLevel, "denied battery property does not discard a positive link");
            var slow = new TaskCompletionSource<int?>();
            Check(BluetoothMice.Probe<int?>(token => slow.Task, 15, CancellationToken.None).Result == null,
                  "a driver ignoring cancellation cannot indefinitely block other devices");
            slow.SetResult(31);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    BluetoothMice.ReadLevel("mouse", "bt/c", true, true, windows, gatt, cancelled.Token).GetAwaiter().GetResult();
                    Check(false, "cancelled scan must stop");
                }
                catch (OperationCanceledException) { Check(true, "scan cancellation propagated"); }
            }
        }

        private static byte[] Frame(byte slot, byte cmd, byte fn, params byte[] payload)
        {
            var frame = new byte[payload.Length > 3 ? 20 : 7];
            frame[0] = (byte)(frame.Length == 7 ? 0x10 : 0x11);
            frame[1] = slot; frame[2] = cmd; frame[3] = fn;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        private static void TestHid()
        {
            var p = LogitechHid.Unified("test", "hid/t", new byte[] { 15, 2 }, new byte[] { 0, 0, 0 });
            Check(p.Percent == 0, "HID++ exact zero");
            Check(LogitechHid.Unified("test", "hid/t", new byte[] { 15, 2 }, new byte[] { 200, 0, 0 }) == null, "out-of-range HID++");
            var band = LogitechHid.Unified("test", "hid/t", new byte[] { 15, 0 }, new byte[] { 0, 4, 0 });
            Check(band.Band == BatteryBand.Good && !band.Percent.HasValue && !band.StatusText.Contains("%"), "level-only capability");
            Check(!HidChannel.Matches(Frame(2, 5, 0x1b, 70), 1, 5, 0x1b), "other receiver slot");
            Check(!HidChannel.Matches(Frame(1, 5, 0x1c, 70), 1, 5, 0x1b), "another application's software ID");
            Check(HidChannel.Matches(Frame(1, 0x8f, 0, 0x1b, 8), 1, 0, 0x1b), "offline error correlation");
            byte kind = 3;
            LogitechHid.Request ask = (slot, cmd, fn, args) =>
            {
                if (cmd == 0 && fn == 0x1b) return Frame(slot, cmd, fn, 4, 2, args[2]);
                if (cmd == 0 && fn == 0x0b)
                    return Frame(slot, cmd, fn, (byte)(args[1] == 5 ? 2 : args[0] == 0x10 && args[1] == 4 ? 9 : 0));
                if (cmd == 2 && fn == 0x2b) return Frame(slot, cmd, fn, kind);
                if (cmd == 2 && fn == 0x0b) return Frame(slot, cmd, fn, 8);
                if (cmd == 2 && fn == 0x1b) return Frame(slot, cmd, fn, Encoding.UTF8.GetBytes("MX Mouse"));
                if (cmd == 9 && fn == 0x0b) return Frame(slot, cmd, fn, 15, 2);
                if (cmd == 9 && fn == 0x1b) return Frame(slot, cmd, fn, 70, 0, 1);
                return null;
            };
            var mx = LogitechHid.ReadMouse(ask, 2, 11, "hid/test/2");
            Check(mx.Name == "MX Mouse" && mx.Percent == 70 && mx.Power == PowerState.Charging, "full HID++2 identity, battery and state");
            kind = 0;
            Check(LogitechHid.ReadMouse(ask, 2, 11, "hid/test/2") == null, "receiver keyboard filtered");
            bool unreachable = false;
            LogitechHid.Request old = (slot, cmd, fn, args) =>
            {
                if (slot == 255 && cmd == 0x83 && args[0] == 0x20)
                    return Frame(slot, cmd, fn, 0x20, 0, 0, 0, 0, 0, 0, 2);
                if (slot == 1 && cmd == 0x81 && fn == 7 && !unreachable) return Frame(slot, cmd, fn, 3, 0, 0);
                return Frame(slot, 0x8f, cmd, fn, (byte)(unreachable ? 8 : 2));
            };
            var legacy = LogitechHid.ReadMouse(old, 1, 11, "hid/test/1");
            Check(legacy.Band == BatteryBand.Low && !legacy.Percent.HasValue, "legacy level only");
            unreachable = true;
            Check(LogitechHid.ReadMouse(old, 1, 11, "hid/test/1").Power == PowerState.Inactive, "sleep on paired receiver");
        }

        private static void TestSelection()
        {
            var a = Reading.MouseInactive("GPW", "gh/1");
            var b = new Reading("Office mouse", 48, PowerState.Battery, "蓝牙电量", "bt/one");
            var c = new Reading("Office mouse", 71, PowerState.Battery, "蓝牙电量", "bt/two");
            var snapshot = new BatterySnapshot(new[] { a, b, c, Reading.Unavailable("未读取") });
            Check(snapshot.Devices.Length == 3, "same display name can represent two physical mice");
            Check(snapshot.Select(null, null) == b, "working Bluetooth mouse ahead of sleeping G HUB mouse");
            Check(snapshot.Select(c.Key, null) == c, "manual choice");
            Check(snapshot.Select("bt/missing", "Saved").Power == PowerState.Unavailable, "absent saved key does not prove sleep");
            Check(snapshot.Select("BT/ONE", "Office mouse") == b, "saved key is case insensitive");
            Check(snapshot.Select(a.Key, a.Name).Power == PowerState.Inactive, "positively offline selected mouse still sleeps");
            var known = new Reading("Mouse", null, PowerState.ConnectedNoLevel, "蓝牙已连接 · 暂未读到电量", "bt/same");
            var stale = Reading.MouseInactive("Mouse", "bt/same");
            var unknown = new Reading("Old pairing", null, PowerState.ConnectionUnknown, "待确认", "bt/old");
            Check(new BatterySnapshot(new[] { stale, unknown, known }).Select(null, null) == known, "live endpoint wins without a number");
            Check(new BatterySnapshot(new[] { a, unknown }).Select(null, null) == a, "unconfirmed old pairing does not replace known sleeping mouse");
            Check(new BatterySnapshot(new[] { Reading.Unavailable("failed") }).Select(null, null).Power == PowerState.Unavailable,
                  "provider failure is distinct from confirmed sleep");
        }

        private static void TestMenu()
        {
            Check(TrayApp.StylesCaption == "图标样式", "menu caption without sample annotation");
            var offline = TrayApp.PreviewFor(Reading.MouseInactive());
            Check(offline.Percent == 70, "offline preview uses 70%");
            var online = new Reading("connected", 0, PowerState.Battery, "");
            Check(TrayApp.PreviewFor(online) == online, "live 0% is not replaced by preview");
            var noNumber = new Reading("mouse", null, PowerState.ConnectedNoLevel, "蓝牙已连接 · 暂未读到电量");
            Check(TrayApp.PreviewFor(noNumber).Percent == 70, "a connected mouse without a number uses the style preview");
            int styles = 0;
            foreach (IconStyle style in Enum.GetValues(typeof(IconStyle)))
            {
                var theme = new TrayTheme(Color.White, Color.Black, true, false);
                using (var face = BatteryIcon.RenderFace(offline, style, theme, 32))
                using (var asleep = BatteryIcon.RenderFace(Reading.MouseInactive(), style, theme, 24))
                using (var connected = BatteryIcon.RenderFace(noNumber, style, theme, 24))
                {
                    Check(face.Width == 32 && asleep.Width == 24, "render style " + style);
                    Check(Enumerable.Range(0, 24 * 24).Any(i => connected.GetPixel(i % 24, i / 24) != asleep.GetPixel(i % 24, i / 24)),
                          "connected mouse without a percentage must not draw the sleeping icon: " + style);
                }
                styles++;
            }
            Check(styles == 8, "eight styles");
        }

        private static void TestTrayArtwork()
        {
            var reading = new Reading("mouse", 54, PowerState.Battery, "");
            var theme = new TrayTheme(Color.FromArgb(34, 35, 39), Color.White, false, false);
            // Windows may place its own icon one physical pixel off the tray button center.
            // Both the enlarged face and the native fallback must stand alone.
            foreach (int size in new[] { 16, 20, 24, 28, 32 })
            {
                int fullSize = size + 2 * (int)Math.Round(size * 0.14);
                using (var artwork = new TrayArtwork(reading, IconStyle.Material, theme, size))
                using (var expected = BatteryIcon.RenderFace(reading, IconStyle.Material, theme, fullSize))
                using (var native = BatteryIcon.Create(reading, IconStyle.Material, theme, size))
                using (var expectedNative = native.ToBitmap())
                using (var actualNative = artwork.Native.ToBitmap())
                {
                    Check(SamePixels(artwork.Enlarged, expected), "complete enlarged 54% ring at " + size);
                    Check(SamePixels(actualNative, expectedNative), "complete native fallback at " + size);
                }
            }

            using (var placeholder = BatteryIcon.CreateTransparent())
            using (var surface = new Bitmap(36, 36))
            using (var g = Graphics.FromImage(surface))
            {
                foreach (var background in new[] { Color.White, Color.FromArgb(34, 35, 39) })
                {
                    g.Clear(background);
                    g.DrawIcon(placeholder, new Rectangle(5, 4, 28, 28));
                    Check(Enumerable.Range(0, 36 * 36).All(i =>
                        surface.GetPixel(i % 36, i / 36).ToArgb() == background.ToArgb()),
                        "offset shell icon leaves every pixel of the enlarged surface untouched");
                }
            }
        }

        private static bool SamePixels(Bitmap actual, Bitmap expected)
        {
            return actual.Size == expected.Size && Enumerable.Range(0, actual.Width * actual.Height)
                .All(i => actual.GetPixel(i % actual.Width, i / actual.Width).ToArgb() ==
                          expected.GetPixel(i % actual.Width, i / actual.Width).ToArgb());
        }
    }
}
