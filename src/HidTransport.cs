using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace GpwBattery
{
    // Windows can expose the short and long reports on different HID collections.
    // Listen on both before writing. A response can arrive on either collection.
    internal sealed class HidChannel : IDisposable
    {
        internal readonly SafeFileHandle Handle;
        internal readonly int InputLength, OutputLength;
        internal readonly byte OutputId;
        private HidChannel(SafeFileHandle handle, int input, int output, byte id)
        {
            Handle = handle; InputLength = input; OutputLength = output; OutputId = id;
        }

        internal static HidChannel Open(string path)
        {
            int input, output;
            byte id;
            using (var meta = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                var attributes = new Attributes { Size = Marshal.SizeOf(typeof(Attributes)) };
                IntPtr preparsed;
                if (meta.IsInvalid || !HidD_GetAttributes(meta, ref attributes) || attributes.Vendor != 0x046d ||
                    !HidD_GetPreparsedData(meta, out preparsed)) return null;
                try
                {
                    var caps = new byte[64];
                    if (HidP_GetCaps(preparsed, caps) != HidSuccess) return null;
                    int page = BitConverter.ToUInt16(caps, 2);
                    input = BitConverter.ToUInt16(caps, 4);
                    output = BitConverter.ToUInt16(caps, 6);
                    if ((page != 0xff00 && page != 0xff43) || input < 7 || input > 64 || output < 7 || output > 64) return null;
                    var report = new byte[output];
                    if (HidP_InitializeReportForID(1, 0x10, preparsed, report, output) == HidSuccess) id = 0x10;
                    else if (output >= 20 && HidP_InitializeReportForID(1, 0x11, preparsed, report, output) == HidSuccess) id = 0x11;
                    else return null;
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }
            var handle = CreateFile(path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (!handle.IsInvalid) return new HidChannel(handle, input, output, id);
            handle.Dispose();
            return null;
        }

        public void Dispose() { Handle.Dispose(); }

        internal static byte[] Exchange(HidChannel[] pipes, byte slot, byte command, byte function,
                                        byte[] args, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            var writer = pipes[0]; // short reports first; every query fits three argument bytes
            var report = new byte[writer.OutputLength];
            report[0] = writer.OutputId; report[1] = slot; report[2] = command; report[3] = function;
            Array.Copy(args, 0, report, 4, args.Length);
            var reads = new List<Transfer>();
            var timer = Stopwatch.StartNew();
            try
            {
                foreach (var pipe in pipes) reads.Add(new Transfer(pipe, null));
                using (var write = new Transfer(writer, report))
                {
                    int completed = WaitHandle.WaitAny(new WaitHandle[] { stop.WaitHandle, write.Ready }, 400);
                    if (completed != 1 || write.Finish() == null) return null;
                }
                while (reads.Count != 0 && timer.ElapsedMilliseconds < 400)
                {
                    var signals = new[] { stop.WaitHandle }.Concat(reads.Select(r => (WaitHandle)r.Ready)).ToArray();
                    int index = WaitHandle.WaitAny(signals, Math.Max(0, 400 - (int)timer.ElapsedMilliseconds));
                    if (index == WaitHandle.WaitTimeout || index == 0) return null;
                    var read = reads[index - 1];
                    byte[] reply = read.Finish();
                    var channel = read.Channel;
                    read.Dispose();
                    reads.RemoveAt(index - 1);
                    if (Matches(reply, slot, command, function)) return reply;
                    if (reply != null) reads.Add(new Transfer(channel, null));
                }
                return null;
            }
            finally { foreach (var read in reads) read.Dispose(); }
        }

        internal static bool Matches(byte[] reply, byte slot, byte command, byte function)
        {
            if (reply == null || reply.Length < 7 || (reply[0] != 0x10 && reply[0] != 0x11) ||
                reply[1] != slot) return false;
            if (reply[2] == 0x8f || reply[2] == 0xff) return reply[3] == command && reply[4] == function;
            return reply[2] == command && reply[3] == function;
        }

        // The unmanaged buffer and OVERLAPPED must outlive cancellation completion.
        private sealed class Transfer : IDisposable
        {
            internal readonly HidChannel Channel;
            internal readonly ManualResetEvent Ready = new ManualResetEvent(false);
            private readonly IntPtr buffer, overlapped;
            private readonly int capacity;
            private bool started;
            public Transfer(HidChannel channel, byte[] write)
            {
                Channel = channel;
                capacity = write == null ? channel.InputLength : write.Length;
                buffer = Marshal.AllocHGlobal(capacity);
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(OverlappedData)));
                Marshal.StructureToPtr(new OverlappedData { Event = Ready.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                if (write != null) Marshal.Copy(write, 0, buffer, write.Length);
                bool ok = write == null ? ReadFile(channel.Handle, buffer, capacity, IntPtr.Zero, overlapped)
                                        : WriteFile(channel.Handle, buffer, capacity, IntPtr.Zero, overlapped);
                started = ok || Marshal.GetLastWin32Error() == 997;
                if (ok || !started) Ready.Set();
            }

            internal byte[] Finish()
            {
                int count;
                if (!started || !GetOverlappedResult(Channel.Handle, overlapped, out count, false) || count < 0 || count > capacity)
                    return null;
                started = false;
                var data = new byte[count];
                Marshal.Copy(buffer, data, 0, count);
                return data;
            }

            public void Dispose()
            {
                if (started)
                {
                    int ignored;
                    CancelIoEx(Channel.Handle, overlapped);
                    GetOverlappedResult(Channel.Handle, overlapped, out ignored, true);
                }
                Marshal.FreeHGlobal(overlapped);
                Marshal.FreeHGlobal(buffer);
                Ready.Dispose();
            }
        }

        private const int HidSuccess = 0x00110000;
        [StructLayout(LayoutKind.Sequential)]
        private struct Attributes { public int Size; public ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)]
        private struct OverlappedData { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attributes);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, [Out] byte[] caps);
        [DllImport("hid.dll")] private static extern int HidP_InitializeReportForID(int type, byte id, IntPtr data, [Out] byte[] report, int length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, IntPtr data, int size, IntPtr count, IntPtr io);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, IntPtr data, int size, IntPtr count, IntPtr io);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr io, out int count, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr io);
    }
}
