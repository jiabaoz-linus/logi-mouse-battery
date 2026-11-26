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
        private const string Present = "System.Devices.Aep.IsPresent";
        private const string PhysicalConnected = "System.Devices.Connected";
        private const string InterfaceEnabled = "System.Devices.InterfaceEnabled";
        private static readonly Guid ComputerContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff");
        private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
        private const string Major = "System.Devices.Aep.Bluetooth.Cod.Major";
        private const string Minor = "System.Devices.Aep.Bluetooth.Cod.Minor";
        private const string BatteryLife = "System.Devices.BatteryLife";
        private const string BluetoothBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private static readonly string[] BatteryProperties = { BatteryLife, BluetoothBattery };
        private static readonly string[] ContainerProperties = { PhysicalConnected, BatteryLife, "System.Devices.LocalMachine" };
        private static readonly string[] EndpointProperties = {
            AepContainer, Connected, Present, Address, Appearance, Major, Minor, BatteryLife
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

        internal static string NormalizeAddress(string value)
        {
            string address = (value ?? "").Replace(":", "").Replace("-", "");
            return address.Length == 12 && address.All(Uri.IsHexDigit) && address.Any(c => c != '0')
                ? address.ToUpperInvariant() : null;
        }

        internal static string PnpAddress(string id)
        {
            // Match only the remote Bluetooth device itself, never a radio or a service's instance suffix.
            var parts = id.Split('\\');
            if (parts.Length < 3 || !(parts[0].Equals("BTHLE", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Equals("BTHENUM", StringComparison.OrdinalIgnoreCase)) ||
                !parts[1].StartsWith("DEV_", StringComparison.OrdinalIgnoreCase)) return null;
            return NormalizeAddress(parts[1].Substring(4));
        }

        internal static Guid PhysicalContainer(Guid association, IEnumerable<Guid> byAddress)
        {
            var matches = byAddress.Where(IsPeripheral).Distinct().ToArray();
            if (matches.Length == 1) return matches[0];
            if (IsPeripheral(association) && (matches.Length == 0 || matches.Contains(association))) return association;
            return Guid.Empty;
        }

        private static bool IsPeripheral(Guid id)
        {
            return id != Guid.Empty && id != ComputerContainer;
        }

        public static async Task<Reading[]> Scan(CancellationToken stop)
        {
            const string mouseSelector = "System.Devices.InterfaceClassGuid:=\"{378DE44C-56EF-11D1-BC8C-00A0C91405DD}\"";
            const string bluetoothClass = "System.Devices.ClassGuid:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\"";
            var mouseInterfaces = Find(mouseSelector, new[] { Container, InterfaceEnabled }, DeviceInformationKind.DeviceInterface, stop);
            var btNodes = Find(bluetoothClass, new[] { Container }, DeviceInformationKind.Device, stop);
            var le = Endpoints(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), stop);
            var classic = Endpoints(BluetoothDevice.GetDeviceSelectorFromPairingState(true), stop);
            var liveLe = Endpoints(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), stop);
            var liveClassic = Endpoints(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), stop);
            await Task.WhenAll(mouseInterfaces, btNodes, le, classic, liveLe, liveClassic).ConfigureAwait(false);
            var mouseIds = mouseInterfaces.Result.Where(d => IsPeripheral(ContainerId(d, Container)))
                .ToLookup(d => ContainerId(d, Container), d => Object.Equals(Property(d, InterfaceEnabled), true));
            var containers = btNodes.Result.ToLookup(d => PnpAddress(d.Id), d => ContainerId(d, Container));
            var tasks = ReadEndpoints(le.Result, liveLe.Result, true, mouseIds, containers, stop)
                .Concat(ReadEndpoints(classic.Result, liveClassic.Result, false, mouseIds, containers, stop)).ToArray();
            var readings = await Task.WhenAll(tasks).ConfigureAwait(false);
            return readings.Where(r => r != null).OrderBy(r => r.Priority).GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                           .Select(g => g.First()).ToArray();
        }

        private static Task<DeviceInformation[]> Endpoints(string query, CancellationToken stop)
        {
            return Find(query, EndpointProperties, DeviceInformationKind.AssociationEndpoint, stop);
        }

        private static IEnumerable<Task<Reading>> ReadEndpoints(DeviceInformation[] paired, DeviceInformation[] live,
            bool lowEnergy, ILookup<Guid, bool> mouseIds, ILookup<string, Guid> containers, CancellationToken stop)
        {
            var liveIds = new HashSet<string>(live.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            var liveAddresses = new HashSet<string>(live.Select(d => NormalizeAddress(Property(d, Address) as string))
                .Where(a => a != null), StringComparer.OrdinalIgnoreCase);
            foreach (var match in paired.Concat(live).GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
            {
                var endpoint = match.First();
                string address = NormalizeAddress(Property(endpoint, Address) as string);
                Guid container = PhysicalContainer(ContainerId(endpoint, AepContainer),
                    address == null ? Enumerable.Empty<Guid>() : containers[address]);
                bool connected = liveIds.Contains(endpoint.Id) || (address != null && liveAddresses.Contains(address));
                if (!IsMouse(mouseIds.Contains(container), Number(Property(endpoint, Appearance), 65535),
                             Number(Property(endpoint, Major), 255), Number(Property(endpoint, Minor), 255))) continue;
                yield return Read(endpoint, address, container, lowEnergy, connected, mouseIds[container].Any(v => v), stop);
            }
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

        private static async Task<Reading> Read(DeviceInformation endpoint, string address, Guid container,
                                                bool lowEnergy, bool listedConnected, bool enabledMouse, CancellationToken stop)
        {
            string key = "bt/" + (address ?? (container != Guid.Empty ? container.ToString("D") : endpoint.Id));
            string name = String.IsNullOrWhiteSpace(endpoint.Name) ? "蓝牙鼠标" : endpoint.Name;
            bool? association = Property(endpoint, Connected) as bool?;
            var device = container == Guid.Empty ? null : await Probe(token => DeviceInformation.CreateFromIdAsync(
                container.ToString("B"), ContainerProperties, DeviceInformationKind.DeviceContainer).AsTask(token),
                1200, stop).ConfigureAwait(false);
            if (device != null && Object.Equals(Property(device, "System.Devices.LocalMachine"), true))
            {
                device = null;
                container = Guid.Empty;
                enabledMouse = false;
            }
            bool? physical = device == null ? null : Property(device, PhysicalConnected) as bool?;
            bool? transport = null;
            if (!listedConnected && association != true && physical != true)
                transport = await Probe(token => ConnectionStatus(endpoint.Id, address, lowEnergy, token), 1600, stop).ConfigureAwait(false);
            bool? connected = ResolveConnection(listedConnected, association, physical, transport, enabledMouse);
            bool visible = enabledMouse || Object.Equals(Property(endpoint, Present), true);
            return await ReadLevel(name, key, connected, visible,
                token => ReadWindows(endpoint, container, device, token),
                lowEnergy ? (Func<CancellationToken, Task<GattSample>>)(token => ReadGatt(endpoint.Id, address, token)) : null,
                stop).ConfigureAwait(false);
        }

        internal static bool? ResolveConnection(bool liveEndpoint, bool? association, bool? physical,
                                                bool? transport, bool enabledMouse)
        {
            if (liveEndpoint || association == true || physical == true || transport == true) return true;
            int negatives = (association == false ? 1 : 0) + (physical == false ? 1 : 0) + (transport == false ? 1 : 0);
            // An enabled mouse interface may linger after disconnect. It warrants a live check,
            // but cannot turn a cached percentage into evidence that the mouse is online.
            if (negatives >= 2 && !enabledMouse) return false;
            return null;
        }

        private static async Task<T> OpenDevice<T>(string address, Func<Task<T>> byId,
                                                   Func<ulong, Task<T>> byAddress, CancellationToken stop) where T : class
        {
            try
            {
                var device = await byId().ConfigureAwait(false);
                if (device != null) return device;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Some association IDs cannot be opened by the Bluetooth API. */ }
            stop.ThrowIfCancellationRequested();
            return address == null ? null : await byAddress(Convert.ToUInt64(address, 16)).ConfigureAwait(false);
        }

        private static Task<BluetoothLEDevice> OpenLe(string id, string address, CancellationToken stop)
        {
            return OpenDevice(address, () => BluetoothLEDevice.FromIdAsync(id).AsTask(stop),
                n => BluetoothLEDevice.FromBluetoothAddressAsync(n).AsTask(stop), stop);
        }

        private static async Task<bool?> ConnectionStatus(string id, string address, bool lowEnergy, CancellationToken stop)
        {
            if (lowEnergy)
            {
                using (var d = await OpenLe(id, address, stop).ConfigureAwait(false))
                    return d == null ? (bool?)null : d.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
            using (var d = await OpenDevice(address, () => BluetoothDevice.FromIdAsync(id).AsTask(stop),
                    n => BluetoothDevice.FromBluetoothAddressAsync(n).AsTask(stop), stop).ConfigureAwait(false))
                return d == null ? (bool?)null : d.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }

        // A successful uncached service discovery confirms a link even if no battery characteristic exists.
        internal sealed class GattSample
        {
            public readonly int? Percent;
            public GattSample(int? percent) { Percent = percent; }
        }

        internal static async Task<Reading> ReadLevel(string name, string key, bool? connected, bool visible,
            Func<CancellationToken, Task<int?>> windows, Func<CancellationToken, Task<GattSample>> gatt, CancellationToken stop)
        {
            if (connected == false) return Reading.MouseInactive(name, key);

            int? percent = null;
            if (connected == true) percent = await Probe(windows, 1200, stop).ConfigureAwait(false);
            if (!percent.HasValue && gatt != null && (connected == true || visible))
            {
                var sample = await Probe(gatt, 2800, stop).ConfigureAwait(false);
                if (sample != null)
                {
                    percent = sample.Percent;
                    if (connected != true && !percent.HasValue)
                        percent = await Probe(windows, 1200, stop).ConfigureAwait(false);
                    connected = true;
                }
            }
            if (percent.HasValue) return new Reading(name, percent, PowerState.Battery, "蓝牙电量", key);
            if (connected == true) return new Reading(name, null, PowerState.ConnectedNoLevel, "蓝牙已连接 · 暂未读到电量", key);
            return new Reading(name, null, PowerState.ConnectionUnknown, "已配对 · 连接状态待确认", key);
        }

        // A slow WinRT request for one mouse must not consume the scan budget for all other mice.
        // Cancellation can be acknowledged by the driver after our deadline.
        internal static async Task<T> Probe<T>(Func<CancellationToken, Task<T>> operation, int milliseconds, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Task<T> pending = null;
            try
            {
                pending = operation(budget.Token);
                if (await Task.WhenAny(pending, Task.Delay(milliseconds, budget.Token)).ConfigureAwait(false) == pending)
                    return await pending.ConfigureAwait(false);
            }
            catch (Exception) { stop.ThrowIfCancellationRequested(); }
            finally
            {
                budget.Cancel();
                if (pending == null) budget.Dispose();
                else DisposeAfterCompletion(pending, budget);
            }
            stop.ThrowIfCancellationRequested();
            return default(T);
        }

        private static void DisposeAfterCompletion(Task pending, CancellationTokenSource budget)
        {
            pending.ContinueWith(done =>
            {
                if (done.IsFaulted) done.Exception.Handle(error => true);
                budget.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static async Task<int?> ReadWindows(DeviceInformation endpoint, Guid container, DeviceInformation device,
                                                    CancellationToken stop)
        {
            int? level = Number(Property(endpoint, BatteryLife), 100);
            if (!level.HasValue && device != null) level = Number(Property(device, BatteryLife), 100);
            if (level.HasValue || container == Guid.Empty) return level;
            // Some Bluetooth drivers publish the battery property on a child PnP node.
            string filter = Container + ":=\"" + container.ToString("B") + "\"";
            var nodes = await Find(filter, BatteryProperties, DeviceInformationKind.Device, stop).ConfigureAwait(false);
            foreach (var node in nodes)
            {
                level = Number(Property(node, BluetoothBattery), 100) ?? Number(Property(node, BatteryLife), 100);
                if (level.HasValue) break;
            }
            return level;
        }

        private static async Task<GattSample> ReadGatt(string id, string address, CancellationToken stop)
        {
            using (var device = await OpenLe(id, address, stop).ConfigureAwait(false))
            {
                if (device == null) return null;
                var result = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
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
                                if (percent.HasValue) return new GattSample(percent);
                            }
                        }
                    }
                    return new GattSample(null);
                }
                finally { foreach (var service in result.Services) service.Dispose(); }
            }
        }
    }
}
