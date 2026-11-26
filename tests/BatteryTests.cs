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
            int windowsCalls = 0, gattCalls = 0;
            int? cached = 70;
            Func<Task<int?>> windows = () => { windowsCalls++; return Task.FromResult(cached); };
            Func<CancellationToken, Task<int?>> gatt = token => { gattCalls++; return Task.FromResult((int?)38); };
            Func<object, Reading> read = connected => BluetoothMice.ReadLevel("Any mouse", "bt/x", connected,
                windows, gatt, CancellationToken.None).GetAwaiter().GetResult();
            Check(read(false).Power == PowerState.Inactive && windowsCalls == 0 && gattCalls == 0, "ignore cached battery while disconnected");
            Check(read(null).Power == PowerState.Unavailable && windowsCalls == 0, "unknown connection state");
            Check(read(true).Percent == 70 && gattCalls == 0, "Windows driver provides the level");
            cached = 0;
            Check(read(true).Percent == 0 && gattCalls == 0, "genuine Windows zero");
            cached = null;
            Check(read(true).Percent == 38 && gattCalls == 1, "BLE falls back to the standard battery service");
            Check(!BluetoothMice.ReadLevel("classic", "bt/c", true, windows, null, CancellationToken.None).Result.HasLevel,
                  "classic mouse without a Windows battery property");
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
            Check(snapshot.Select("bt/missing", "Saved").Power == PowerState.Inactive, "selected mouse disconnected");
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
            int styles = 0;
            foreach (IconStyle style in Enum.GetValues(typeof(IconStyle)))
            {
                var theme = new TrayTheme(Color.White, Color.Black, true, false);
                using (var face = BatteryIcon.RenderFace(offline, style, theme, 32))
                using (var asleep = BatteryIcon.RenderFace(Reading.MouseInactive(), style, theme, 24))
                    Check(face.Width == 32 && asleep.Width == 24, "render style " + style);
                styles++;
            }
            Check(styles == 8, "eight styles");
        }
    }
}
