using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;

namespace GpwBattery
{
    internal static class LogitechHid
    {
        internal delegate byte[] Request(byte slot, byte command, byte function, params byte[] arguments);

        public static async Task<Reading[]> Scan(CancellationToken stop)
        {
            const string selector = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\"";
            const string vendor = "System.DeviceInterface.Hid.VendorId";
            var items = await BluetoothMice.Find(selector, new[] { BluetoothMice.Container, vendor },
                                                 DeviceInformationKind.DeviceInterface, stop).ConfigureAwait(false);
            var groups = items.Where(d => BluetoothMice.Number(BluetoothMice.Property(d, vendor), 65535) == 0x046d ||
                                     d.Id.IndexOf("VID_046D", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     d.Id.IndexOf("046D&PID", StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(d => BluetoothMice.ContainerId(d, BluetoothMice.Container))
                .Where(g => g.Key != Guid.Empty).ToArray();
            var tasks = groups.Select(group => Task.Run(() => ScanContainer(group.Key, group.Select(d => d.Id), stop))).ToArray();
            return (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(r => r).ToArray();
        }

        private static Reading[] ScanContainer(Guid container, IEnumerable<string> paths, CancellationToken stop)
        {
            var rows = new List<Reading>();
            var channels = new List<HidChannel>();
            try
            {
                foreach (string path in paths)
                {
                    var pipe = HidChannel.Open(path);
                    if (pipe != null) channels.Add(pipe);
                }
                if (channels.Count == 0) return rows.ToArray();
                var pipes = channels.OrderBy(p => p.OutputId).ToArray();
                byte software = (byte)(8 + (Guid.NewGuid().ToByteArray()[0] & 7));
                Request ask = (slot, cmd, fn, args) => HidChannel.Exchange(pipes, slot, cmd, fn, args, stop);
                foreach (byte slot in new byte[] { 1, 2, 3, 4, 5, 6, 0xff })
                {
                    stop.ThrowIfCancellationRequested();
                    var reading = ReadMouse(ask, slot, software, "hid/" + container.ToString("D") + "/" + slot);
                    if (reading != null) rows.Add(reading);
                }
            }
            catch (OperationCanceledException) { /* Return devices that answered within this scan. */ }
            finally { foreach (var channel in channels) channel.Dispose(); }
            return rows.ToArray();
        }

        private static byte[] Data(byte[] frame)
        {
            if (frame == null || frame.Length < 7 || frame[2] == 0x8f || frame[2] == 0xff) return null;
            int length = frame[0] == 0x10 ? 7 : 20;
            return frame.Skip(4).Take(length - 4).ToArray();
        }

        private static byte Feature(Request ask, byte slot, byte software, int id)
        {
            var value = Data(ask(slot, 0, software, (byte)(id >> 8), (byte)id));
            return value == null ? (byte)0 : value[0];
        }

        internal static Reading ReadMouse(Request ask, byte slot, byte software, string key)
        {
            byte nonce = software;
            var version = Data(ask(slot, 0, (byte)(0x10 | software), 0, 0, nonce));
            if (version == null || version[0] < 2 || version[2] != nonce)
                return slot >= 1 && slot <= 6 ? ReadLegacy(ask, slot, key) : null;

            byte identity = Feature(ask, slot, software, 0x0005);
            if (identity == 0) return null;
            var type = Data(ask(slot, identity, (byte)(0x20 | software)));
            if (type == null || (type[0] != 3 && type[0] != 5)) return null;
            string name = "罗技鼠标";
            var length = Data(ask(slot, identity, software));
            if (length != null && length[0] > 0 && length[0] <= 96)
            {
                var bytes = new List<byte>();
                while (bytes.Count < length[0])
                {
                    var part = Data(ask(slot, identity, (byte)(0x10 | software), (byte)bytes.Count));
                    if (part == null) break;
                    bytes.AddRange(part.Take(length[0] - bytes.Count));
                }
                if (bytes.Count == length[0]) name = CleanName(bytes.ToArray(), name);
            }

            byte feature = Feature(ask, slot, software, 0x1004);
            if (feature != 0)
            {
                var caps = Data(ask(slot, feature, software));
                var status = Data(ask(slot, feature, (byte)(0x10 | software)));
                if (caps != null && status != null)
                {
                    var reading = Unified(name, key, caps, status);
                    if (reading != null) return reading;
                }
            }
            feature = Feature(ask, slot, software, 0x1000);
            if (feature != 0)
            {
                var status = Data(ask(slot, feature, software));
                var caps = Data(ask(slot, feature, (byte)(0x10 | software)));
                if (status != null && status[0] <= 100)
                {
                    PowerState power = Charge(status[2], false);
                    if (power == PowerState.Full) return Battery(name, key, 100, power);
                    if (status[0] == 0 && power != PowerState.Battery) return Battery(name, key, null, power);
                    if (caps != null && (caps[0] >= 10 || (caps[1] & 2) != 0))
                        return Battery(name, key, status[0], power);
                    int p = status[0];
                    return Battery(name, key, null, power, p <= 10 ? BatteryBand.Critical :
                                   p < 30 ? BatteryBand.Low : p < 81 ? BatteryBand.Good : BatteryBand.High);
                }
            }
            feature = Feature(ask, slot, software, 0x1001);
            if (feature != 0)
            {
                var voltage = Data(ask(slot, feature, software));
                if (voltage != null)
                {
                    int mv = voltage[0] * 256 + voltage[1];
                    if (mv > 0 && mv < 10000)
                        return new Reading(name, null, PowerState.Battery, "设备上报电压", key, BatteryBand.Unknown, mv);
                }
            }
            return Reading.Unavailable("已识别鼠标 · 未提供可读取的电量", name, key);
        }

        internal static Reading Unified(string name, string key, byte[] caps, byte[] status)
        {
            if (caps.Length < 2 || status.Length < 3) return null;
            PowerState power = Charge(status[2], true);
            if ((caps[1] & 2) != 0)
                return status[0] <= 100 ? Battery(name, key, status[0], power) : null;
            int flags = caps[0] & status[1];
            BatteryBand band = (flags & 8) != 0 ? BatteryBand.High : (flags & 4) != 0 ? BatteryBand.Good :
                               (flags & 2) != 0 ? BatteryBand.Low : (flags & 1) != 0 ? BatteryBand.Critical : BatteryBand.Unknown;
            return band == BatteryBand.Unknown ? null : Battery(name, key, null, power, band);
        }

        private static Reading ReadLegacy(Request ask, byte slot, string key)
        {
            // The receiver's pairing record identifies the occupant of this slot even when it is asleep.
            byte selector = (byte)(0x20 + slot - 1);
            var pairing = Data(ask(0xff, 0x83, 0xb5, selector));
            if (pairing == null || pairing.Length < 8 || pairing[0] != selector ||
                (pairing[7] != 2 && pairing[7] != 8)) return null;
            string name = "罗技鼠标 " + slot;
            byte nameSelector = (byte)(0x40 + slot - 1);
            var label = Data(ask(0xff, 0x83, 0xb5, nameSelector));
            if (label != null && label.Length > 2 && label[0] == nameSelector && label[1] <= label.Length - 2)
                name = CleanName(label.Skip(2).Take(label[1]).ToArray(), name);
            byte[] mileageFrame = ask(slot, 0x81, 0x0d);
            var mileage = Data(mileageFrame);
            if (mileage != null && mileage[0] <= 100)
            {
                int flag = mileage[2] >> 6;
                PowerState power = flag == 0 ? PowerState.Battery : flag == 1 ? PowerState.Charging :
                                   flag == 2 ? PowerState.Full : PowerState.ChargeError;
                return Battery(name, key, mileage[0] == 0 && power == PowerState.Charging ? (int?)null : mileage[0], power);
            }
            byte[] statusFrame = ask(slot, 0x81, 0x07);
            var status = Data(statusFrame);
            if (status != null)
            {
                int level = status[0];
                BatteryBand band = level == 7 ? BatteryBand.High : level >= 5 && level <= 6 ? BatteryBand.Good :
                                   level == 3 || level == 4 ? BatteryBand.Low : level == 1 || level == 2 ? BatteryBand.Critical : BatteryBand.Unknown;
                PowerState power = status[1] == 0x22 || status[1] == 0x26 ? PowerState.Full :
                    status[1] == 0x21 || status[1] == 0x24 || status[1] == 0x25 ? PowerState.Charging :
                    status[1] == 0x23 ? PowerState.ChargeError : PowerState.Battery;
                return Battery(name, key, null, power, band);
            }
            // Some receivers report an unknown device (0x08) while its pairing record persists.
            if (Unreachable(mileageFrame) || Unreachable(statusFrame) || (mileageFrame == null && statusFrame == null))
                return Reading.MouseInactive(name, key);
            return Reading.Unavailable("已配对 · 暂无电量读数", name, key);
        }

        private static bool Unreachable(byte[] frame)
        {
            return frame != null && frame.Length >= 7 && frame[2] == 0x8f && (frame[5] == 0x08 || frame[5] == 0x09);
        }

        private static string CleanName(byte[] bytes, string fallback)
        {
            string name = new string(Encoding.UTF8.GetString(bytes).Where(c => !Char.IsControl(c)).ToArray()).Trim();
            return String.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        private static PowerState Charge(byte code, bool unified)
        {
            if (code == 0) return PowerState.Battery;
            if (code == 3) return PowerState.Full;
            if (code == 1 || code == 2 || (!unified && code == 4)) return PowerState.Charging;
            return PowerState.ChargeError;
        }

        private static Reading Battery(string name, string key, int? percent, PowerState power, BatteryBand band = BatteryBand.Unknown)
        {
            string detail = power == PowerState.Battery ? "电池供电" : power == PowerState.Charging ? "充电中" :
                            power == PowerState.Full ? "已充满" : "充电异常";
            return new Reading(name, percent, power, detail, key, band);
        }
    }
}
