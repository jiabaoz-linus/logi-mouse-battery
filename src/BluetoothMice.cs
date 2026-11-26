using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace GpwBattery
{
    internal static class BluetoothMice
    {
        internal const string Container = "System.Devices.ContainerId";
        private const string AepContainer = "System.Devices.Aep.ContainerId";
        private const string Connected = "System.Devices.Aep.IsConnected";
        private const string Address = "System.Devices.Aep.DeviceAddress";
        private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
        private const string Major = "System.Devices.Aep.Bluetooth.Cod.Major";
        private const string Minor = "System.Devices.Aep.Bluetooth.Cod.Minor";
        private const string BatteryLife = "System.Devices.BatteryLife";
        private const string BluetoothBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private static readonly string[] BatteryProperties = { BatteryLife, BluetoothBattery };
        private static readonly string[] EndpointProperties = {
            AepContainer, Connected, Address, Appearance, Major, Minor, BatteryLife
        };

        internal static object Property(DeviceInformation info, string key)
        {
            object value;
            return info.Properties.TryGetValue(key, out value) ? value : null;
        }

        internal static Guid ContainerId(DeviceInformation info, string key)
        {
            Guid id;
            return Guid.TryParse(Convert.ToString(Property(info, key)), out id) ? id : Guid.Empty;
        }

        internal static int? Number(object value, int maximum)
        {
            if (!(value is byte || value is ushort || value is uint || value is int)) return null;
            long n = Convert.ToInt64(value);
            return n >= 0 && n <= maximum ? (int?)n : null;
        }

        internal static bool IsMouse(bool mouseInterface, int? appearance, int? major, int? minor)
        {
            // HID appearance 0x03C0 is generic; 0x03C2 specifically identifies a mouse.
            if (appearance == 0x03c2) return true;
            if (appearance > 0x03c0 && appearance < 0x0400) return false;
            return mouseInterface || (major == 5 && minor == 0x20);
        }

        public static async Task<Reading[]> Scan(CancellationToken stop)
        {
            const string mouseSelector = "System.Devices.InterfaceClassGuid:=\"{378DE44C-56EF-11D1-BC8C-00A0C91405DD}\"";
            var mouseInterfaces = Find(mouseSelector, new[] { Container }, DeviceInformationKind.DeviceInterface, stop);
            var le = Find(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), EndpointProperties,
                          DeviceInformationKind.AssociationEndpoint, stop);
            var classic = Find(BluetoothDevice.GetDeviceSelectorFromPairingState(true), EndpointProperties,
                               DeviceInformationKind.AssociationEndpoint, stop);
            await Task.WhenAll(mouseInterfaces, le, classic).ConfigureAwait(false);
            var mouseIds = new HashSet<Guid>(mouseInterfaces.Result.Select(d => ContainerId(d, Container)));
            mouseIds.Remove(Guid.Empty);
            var tasks = le.Result.Select(d => Read(d, true, mouseIds, stop))
                .Concat(classic.Result.Select(d => Read(d, false, mouseIds, stop))).ToArray();
            var readings = await Task.WhenAll(tasks).ConfigureAwait(false);
            return readings.Where(r => r != null).OrderBy(r => r.Priority).GroupBy(r => r.Key)
                           .Select(g => g.First()).ToArray();
        }

        internal static async Task<DeviceInformation[]> Find(string query, string[] properties,
                                                               DeviceInformationKind kind, CancellationToken stop)
        {
            try
            {
                var found = await DeviceInformation.FindAllAsync(query, properties, kind).AsTask(stop).ConfigureAwait(false);
                return found.ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new DeviceInformation[0]; }
        }

        private static async Task<Reading> Read(DeviceInformation endpoint, bool lowEnergy,
                                                HashSet<Guid> mouseIds, CancellationToken stop)
        {
            Guid container = ContainerId(endpoint, AepContainer);
            if (!IsMouse(mouseIds.Contains(container), Number(Property(endpoint, Appearance), 65535),
                         Number(Property(endpoint, Major), 255), Number(Property(endpoint, Minor), 255))) return null;
            string address = Convert.ToString(Property(endpoint, Address)).Replace(":", "").Replace("-", "");
            bool validAddress = address.Length == 12 && address.All(Uri.IsHexDigit) && address.Any(c => c != '0');
            string key = "bt/" + (validAddress ? address.ToUpperInvariant() :
                                  container != Guid.Empty ? container.ToString("D") : endpoint.Id);
            string name = String.IsNullOrWhiteSpace(endpoint.Name) ? "蓝牙鼠标" : endpoint.Name;
            object connected = Property(endpoint, Connected);
            if (!(connected is bool)) connected = await ConnectionStatus(endpoint.Id, lowEnergy, stop).ConfigureAwait(false);
            return await ReadLevel(name, key, connected, () => ReadWindows(endpoint, container, stop),
                lowEnergy ? (Func<CancellationToken, Task<int?>>)(token => ReadGatt(endpoint.Id, token)) : null, stop).ConfigureAwait(false);
        }

        private static async Task<bool?> ConnectionStatus(string id, bool lowEnergy, CancellationToken stop)
        {
            try
            {
                if (lowEnergy)
                {
                    using (var d = await BluetoothLEDevice.FromIdAsync(id).AsTask(stop).ConfigureAwait(false))
                        if (d != null) return d.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                else
                {
                    using (var d = await BluetoothDevice.FromIdAsync(id).AsTask(stop).ConfigureAwait(false))
                        if (d != null) return d.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
            return null;
        }

        internal static async Task<Reading> ReadLevel(string name, string key, object connected, Func<Task<int?>> windows,
                                                       Func<CancellationToken, Task<int?>> gatt, CancellationToken stop)
        {
            if (Object.Equals(connected, false)) return Reading.MouseInactive(name, key);
            if (!Object.Equals(connected, true)) return Reading.Unavailable("等待蓝牙连接状态", name, key);
            int? level = null;
            try { level = await windows().ConfigureAwait(false); }
            catch (Exception) { stop.ThrowIfCancellationRequested(); }
            if (!level.HasValue && gatt != null)
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    timeout.CancelAfter(3500);
                    try { level = await gatt(timeout.Token).ConfigureAwait(false); }
                    catch (Exception) { stop.ThrowIfCancellationRequested(); }
                }
            }
            return level.HasValue ? new Reading(name, level, PowerState.Battery, "蓝牙电量", key)
                                  : Reading.Unavailable("已连接 · 设备未提供可读取的电量", name, key);
        }

        private static async Task<int?> ReadWindows(DeviceInformation endpoint, Guid container, CancellationToken stop)
        {
            int? level = Number(Property(endpoint, BatteryLife), 100);
            if (!level.HasValue && container != Guid.Empty)
            {
                // Some Bluetooth drivers put the battery on a child PnP node, others on the container.
                string filter = Container + ":=\"" + container.ToString("B") + "\"";
                var nodes = await Find(filter, BatteryProperties, DeviceInformationKind.Device, stop).ConfigureAwait(false);
                foreach (var node in nodes)
                {
                    level = Number(Property(node, BluetoothBattery), 100) ?? Number(Property(node, BatteryLife), 100);
                    if (level.HasValue) break;
                }
                if (!level.HasValue)
                {
                    try
                    {
                        var info = await DeviceInformation.CreateFromIdAsync(container.ToString("B"), new[] { BatteryLife },
                            DeviceInformationKind.DeviceContainer).AsTask(stop).ConfigureAwait(false);
                        if (info != null) level = Number(Property(info, BatteryLife), 100);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* Windows may only implement the standard GATT service. */ }
                }
            }
            return level;
        }

        private static async Task<int?> ReadGatt(string id, CancellationToken stop)
        {
            using (var device = await BluetoothLEDevice.FromIdAsync(id).AsTask(stop).ConfigureAwait(false))
            {
                if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
                var result = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery)
                    .AsTask(stop).ConfigureAwait(false);
                try
                {
                    if (result.Status != GattCommunicationStatus.Success) return null;
                    foreach (var service in result.Services)
                    {
                        var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel)
                            .AsTask(stop).ConfigureAwait(false);
                        if (chars.Status != GattCommunicationStatus.Success) continue;
                        foreach (var ch in chars.Characteristics)
                        {
                            var data = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(stop).ConfigureAwait(false);
                            if (data.Status != GattCommunicationStatus.Success || data.Value == null || data.Value.Length != 1) continue;
                            using (var bytes = DataReader.FromBuffer(data.Value))
                            {
                                int? percent = Number(bytes.ReadByte(), 100);
                                if (percent.HasValue) return percent;
                            }
                        }
                    }
                }
                finally { foreach (var service in result.Services) service.Dispose(); }
            }
            return null;
        }
    }
}
