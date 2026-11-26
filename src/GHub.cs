using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GpwBattery
{

    // The same reader drives the tray and the one-shot command line check.
    internal static class GHub
    {
        private const string DevicesPath = "/devices/list";
        private const string BatteryEvents = "/battery/state/changed";
        private const int PollMilliseconds = 5000;
        private static readonly Uri Endpoint = new Uri("ws://127.0.0.1:9010");

        public static void Run(Action<Reading[]> publish, CancellationToken stop, bool once)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    RunSession(publish, stop, once);
                }
                catch (Exception error)
                {
                    if (stop.IsCancellationRequested) return;
                    string detail = error is WebSocketException || error is OperationCanceledException
                        ? "等待 G HUB 后台连接" : "G HUB 电量读取中断";
                    publish(new[] { Reading.Unavailable(detail) });
                }
                if (once || stop.WaitHandle.WaitOne(3000)) return;
            }
        }

        private static void RunSession(Action<Reading[]> publish, CancellationToken stop, bool once)
        {
            using (var socket = new ClientWebSocket())
            using (var io = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                socket.Options.Proxy = null;
                socket.Options.AddSubProtocol("json");
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    connect.CancelAfter(3000);
                    socket.ConnectAsync(Endpoint, connect.Token).GetAwaiter().GetResult();
                }

                var json = new JavaScriptSerializer();
                var devices = new Dictionary<string, Device>();
                var rows = new Dictionary<string, Reading>();
                var pending = new HashSet<string>();
                Reading[] lastKnown = new Reading[0];
                Task<Message> receive = null;
                try
                {
                    if (!once) Send(socket, json, "SUBSCRIBE", BatteryEvents, stop);
                    Send(socket, json, "GET", DevicesPath, stop);
                    var poll = Stopwatch.StartNew();
                    bool awaitingList = true;
                    while (!stop.IsCancellationRequested)
                    {
                        if (poll.ElapsedMilliseconds >= PollMilliseconds)
                        {
                            if (awaitingList) throw new TimeoutException();
                            if (pending.Count != 0)
                            {
                                publish(rows.Values.ToArray());
                                if (once) return;
                            }
                            pending.Clear();
                            Send(socket, json, "GET", DevicesPath, stop);
                            awaitingList = true;
                            poll.Restart();
                        }
                        if (receive == null) receive = ReceiveAsync(socket, json, io.Token);
                        int remaining = (int)Math.Max(0L, PollMilliseconds - poll.ElapsedMilliseconds);
                        if (Task.WaitAny(new Task[] { receive }, remaining, stop) < 0) continue;
                        var message = receive.GetAwaiter().GetResult();
                        receive = null;
                        if (message == null) continue;

                        if (message.path == DevicesPath)
                        {
                            if (!message.Succeeded || message.payload == null || message.payload.deviceInfos == null)
                                throw new InvalidDataException("Missing G HUB device list");
                            awaitingList = false;
                            devices.Clear();
                            rows.Clear();
                            pending.Clear();
                            foreach (var mouse in FindMice(message.payload.deviceInfos))
                            {
                                devices[mouse.BatteryPath] = mouse;
                                if (mouse.Active)
                                {
                                    rows[mouse.id] = ParseReading(mouse, null);
                                    pending.Add(mouse.BatteryPath);
                                    Send(socket, json, "GET", mouse.BatteryPath, stop);
                                }
                                else rows[mouse.id] = Reading.MouseInactive(mouse.Name, mouse.Key);
                            }
                            if (rows.Count > 0)
                                lastKnown = devices.Values.Select(d => Reading.MouseInactive(d.Name, d.Key)).ToArray();
                        }
                        else if (message.path == BatteryEvents && message.verb == "BROADCAST")
                        {
                            var payload = message.payload;
                            if (payload != null && !String.IsNullOrEmpty(payload.deviceId))
                            {
                                Device mouse;
                                if (devices.TryGetValue("/battery/" + payload.deviceId + "/state", out mouse) && mouse.Active)
                                {
                                    rows[mouse.id] = ParseReading(mouse, payload);
                                    publish(rows.Values.ToArray());
                                }
                            }
                            Send(socket, json, "SUBSCRIBE", BatteryEvents, stop);
                            continue;
                        }
                        else
                        {
                            Device mouse;
                            if (message.path == null || !pending.Remove(message.path) ||
                                !devices.TryGetValue(message.path, out mouse)) continue;
                            rows[mouse.id] = ParseReading(mouse, message.Succeeded ? message.payload : null);
                        }
                        if (pending.Count != 0) continue;
                        publish(rows.Count == 0 ? lastKnown : rows.Values.ToArray());
                        if (once) return;
                    }
                }
                finally
                {
                    io.Cancel();
                    socket.Abort();
                    if (receive != null)
                    {
                        try { receive.GetAwaiter().GetResult(); }
                        catch (Exception) { /* The pending receive ends when the socket closes. */ }
                    }
                }
            }
        }

        internal static IEnumerable<Device> FindMice(IEnumerable<Device> devices)
        {
            return devices.Where(d => d != null && !String.IsNullOrWhiteSpace(d.id) &&
                         String.Equals(d.deviceType, "MOUSE", StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => d.id, StringComparer.Ordinal).Select(g => g.OrderByDescending(d => d.Active).First());
        }

        internal static Reading ParseReading(Device device, Payload data)
        {
            if (data == null || (data.deviceId != null && data.deviceId != device.id) ||
                data.isBatteryRemoved || !data.percentage.HasValue ||
                data.percentage.Value < 0 || data.percentage.Value > 100)
                return Reading.Unavailable("等待鼠标上报电量", device.Name, device.Key);

            if (data.chargingError)
                return new Reading(device.Name, data.percentage, PowerState.ChargeError, "充电异常", device.Key);
            if (data.fullyCharged)
                return new Reading(device.Name, data.percentage, PowerState.Full, "已充满", device.Key);
            if (data.charging)
                return new Reading(device.Name, data.percentage, PowerState.Charging, "充电中", device.Key);
            return new Reading(device.Name, data.percentage, PowerState.Battery, "电池供电", device.Key);
        }

        private static void Send(ClientWebSocket socket, JavaScriptSerializer json, string verb,
                                 string path, CancellationToken stop)
        {
            var bytes = Encoding.UTF8.GetBytes(json.Serialize(new { verb = verb, path = path }));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                timeout.CancelAfter(3000);
                socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                                 timeout.Token).GetAwaiter().GetResult();
            }
        }

        private static async Task<Message> ReceiveAsync(ClientWebSocket socket,
                                                        JavaScriptSerializer json, CancellationToken stop)
        {
            var buffer = new byte[8192];
            using (var stream = new MemoryStream())
            {
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("G HUB disconnected");
                    if (part.MessageType != WebSocketMessageType.Text || stream.Length + part.Count > 1024 * 1024)
                        throw new InvalidDataException("Invalid G HUB message");
                    stream.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                string text = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                return json.Deserialize<Message>(text);
            }
        }

        public sealed class Device
        {
            public string id { get; set; }
            public string displayName { get; set; }
            public string deviceType { get; set; }
            public string state { get; set; }
            public bool Active { get { return String.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase); } }
            public string Name { get { return String.IsNullOrWhiteSpace(displayName) ? "罗技鼠标" : displayName; } }
            public string Key { get { return "gh/" + id; } }
            public string BatteryPath { get { return "/battery/" + id + "/state"; } }
        }

        public sealed class Payload
        {
            public Device[] deviceInfos { get; set; }
            public string deviceId { get; set; }
            public int? percentage { get; set; }
            public bool charging { get; set; }
            public bool fullyCharged { get; set; }
            public bool chargingError { get; set; }
            public bool isBatteryRemoved { get; set; }
        }

        public sealed class Result { public string code { get; set; } }

        public sealed class Message
        {
            public string path { get; set; }
            public string verb { get; set; }
            public Payload payload { get; set; }
            public Result result { get; set; }
            public bool Succeeded { get { return result != null && result.code == "SUCCESS"; } }
        }
    }
}
