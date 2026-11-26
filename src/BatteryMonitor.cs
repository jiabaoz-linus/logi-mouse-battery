using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GpwBattery
{
    internal enum PowerState { Unavailable, Battery, Charging, Full, ChargeError, Inactive }
    internal enum BatteryBand { Unknown, Critical, Low, Good, High }

    internal sealed class Reading
    {
        public const string AppName = "鼠标电量";
        public readonly string Name, Detail, Key;
        public readonly int? Percent, Millivolts;
        public readonly PowerState Power;
        public readonly BatteryBand Band;

        public Reading(string name, int? percent, PowerState power, string detail,
                       string key = "", BatteryBand band = BatteryBand.Unknown, int? millivolts = null)
        {
            Name = name; Percent = percent; Power = power; Detail = detail; Key = key; Band = band; Millivolts = millivolts;
        }

        public static Reading Unavailable(string detail, string name = AppName, string key = "")
        {
            return new Reading(name, null, PowerState.Unavailable, detail, key);
        }

        public static Reading MouseInactive(string name = AppName, string key = "")
        {
            return new Reading(name, null, PowerState.Inactive, "鼠标未连接或已休眠", key);
        }

        public bool HasLevel { get { return Percent.HasValue || Band != BatteryBand.Unknown || Millivolts.HasValue; } }
        public int Priority
        {
            get { return Percent.HasValue ? 0 : HasLevel ? 1 : Power == PowerState.Inactive ? 3 : 2; }
        }

        public string Source
        {
            get
            {
                if (Key.StartsWith("bt/")) return "蓝牙";
                if (Key.StartsWith("hid/")) return "HID++";
                if (Key.StartsWith("gh/")) return "G HUB";
                return "";
            }
        }

        public string StatusText
        {
            get
            {
                if (Percent.HasValue) return Percent.Value + "% · " + Detail;
                if (Millivolts.HasValue) return (Millivolts.Value / 1000.0).ToString("0.00") + " V · " + Detail;
                switch (Band)
                {
                    case BatteryBand.Critical: return "电量极低 · " + Detail;
                    case BatteryBand.Low: return "电量偏低 · " + Detail;
                    case BatteryBand.Good: return "电量良好 · " + Detail;
                    case BatteryBand.High: return "电量充足 · " + Detail;
                    default: return Detail;
                }
            }
        }
    }

    internal sealed class BatterySnapshot
    {
        public readonly Reading[] Devices;
        private readonly Reading fallback;

        public BatterySnapshot(IEnumerable<Reading> readings)
        {
            var all = readings.ToArray();
            Devices = all.Where(r => r.Key.Length != 0)
                .OrderBy(r => r.Priority).ThenBy(r => r.Source == "G HUB" ? 0 : r.Source == "蓝牙" ? 1 : 2)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal)
                .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            fallback = all.FirstOrDefault(r => r.Key.Length == 0 && r.Power == PowerState.Inactive)
                ?? Reading.Unavailable("未发现可读取电量的鼠标");
        }

        public Reading Select(string preferred, string lastName)
        {
            if (!String.IsNullOrEmpty(preferred))
                return Devices.FirstOrDefault(r => r.Key == preferred) ?? Reading.MouseInactive(lastName ?? Reading.AppName, preferred);
            return Devices.FirstOrDefault() ?? fallback;
        }
    }

    internal static class BatteryMonitor
    {
        public static void Run(Action<BatterySnapshot> publish, CancellationToken stop, bool once)
        {
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var token = lifetime.Token;
            var gate = new object();
            var sources = new Reading[3][] { new Reading[0], new Reading[0], new Reading[0] };
            Action<int, Reading[]> update = (index, rows) =>
            {
                lock (gate)
                {
                    if (token.IsCancellationRequested) return;
                    sources[index] = rows;
                    if (!once) publish(new BatterySnapshot(sources.SelectMany(r => r)));
                }
            };
            if (once) lifetime.CancelAfter(10000);
            var providers = new[] {
                Task.Run(() => GHub.Run(r => update(0, r), token, once)),
                Task.Run(() => Poll(BluetoothMice.Scan, r => update(1, r), token, once, 15)),
                Task.Run(() => Poll(LogitechHid.Scan, r => update(2, r), token, once, 30))
            };
            try { Task.WaitAll(providers, token); }
            catch (OperationCanceledException) { }
            catch (AggregateException errors) { errors.Handle(e => e is OperationCanceledException); }
            if (once) lock (gate) publish(new BatterySnapshot(sources.SelectMany(r => r)));
            lifetime.Cancel();
            Task.WhenAll(providers).ContinueWith(done =>
            {
                if (done.IsFaulted) done.Exception.Handle(error => true);
                lifetime.Dispose();
            }, TaskScheduler.Default);
        }

        private static async Task Poll(Func<CancellationToken, Task<Reading[]>> scan, Action<Reading[]> publish,
                                       CancellationToken stop, bool once, int seconds)
        {
            do
            {
                using (var probe = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    probe.CancelAfter(8500);
                    try { publish(await scan(probe.Token).ConfigureAwait(false)); }
                    catch (Exception) { if (!stop.IsCancellationRequested) publish(new Reading[0]); }
                }
                if (once || stop.IsCancellationRequested) return;
                try { await Task.Delay(seconds * 1000, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            } while (!stop.IsCancellationRequested);
        }
    }
}
