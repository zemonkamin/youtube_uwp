using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YouTube.Discord.Bridge
{
    internal static class Program
    {
        private const string PresenceFileName = "discord-presence.json";
        private const int HandshakeOpcode = 0;
        private const int FrameOpcode = 1;
        private const int PingOpcode = 3;
        private const int PongOpcode = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder familyName);

        [STAThread]
        private static void Main()
        {
            bool ownsMutex;
            using (var mutex = new Mutex(true, "Local\\YouTubeDiscordBridge", out ownsMutex))
            {
                if (!ownsMutex)
                    return;
                var presencePath = GetPresencePath();
                if (!string.IsNullOrEmpty(presencePath))
                    RunPresenceLoop(presencePath);
            }
        }

        private static void RunPresenceLoop(string presencePath)
        {
            NamedPipeClientStream pipe = null;
            string lastPayload = null;
            var appProcessId = 0;
            while (true)
            {
                try
                {
                    // A packaged full-trust process is not terminated automatically with its
                    // UWP invoker. Stop as soon as that invoker disappears so the executable in
                    // bin\x86\Debug\AppX is not left locked for the next Visual Studio deploy.
                    if (appProcessId > 0 && !IsProcessRunning(appProcessId))
                    {
                        DisposePipe(ref pipe);
                        return;
                    }

                    var payload = ReadPresenceFile(presencePath);
                    var payloadProcessId = ExtractProcessId(payload);
                    if (payloadProcessId > 0)
                    {
                        appProcessId = payloadProcessId;
                        if (!IsProcessRunning(appProcessId))
                        {
                            DisposePipe(ref pipe);
                            return;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(payload) && !string.Equals(payload, lastPayload, StringComparison.Ordinal))
                    {
                        if (pipe == null || !pipe.IsConnected)
                        {
                            DisposePipe(ref pipe);
                            pipe = ConnectDiscordPipe();
                            if (pipe == null)
                            {
                                Thread.Sleep(3000);
                                continue;
                            }
                        }
                        WriteFrame(pipe, FrameOpcode, payload);
                        var acknowledgement = ReadAcknowledgement(pipe);
                        if (IsErrorAcknowledgement(acknowledgement))
                        {
                            // Most commonly Discord's activity rate limit. Do not lose the
                            // newest page/pause state merely because this attempt was rejected.
                            Thread.Sleep(4500);
                            continue;
                        }
                        lastPayload = payload;
                    }
                    Thread.Sleep(400);
                }
                catch
                {
                    DisposePipe(ref pipe);
                    // A Discord restart can leave IsConnected true until the next write.
                    // Force the latest state to be replayed after reconnecting.
                    lastPayload = null;
                    Thread.Sleep(1500);
                }
            }
        }

        private static int ExtractProcessId(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return 0;

            const string marker = "\"pid\":";
            var start = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return 0;

            start += marker.Length;
            while (start < payload.Length && char.IsWhiteSpace(payload[start]))
                start++;

            var end = start;
            while (end < payload.Length && char.IsDigit(payload[end]))
                end++;

            int processId;
            return end > start && int.TryParse(payload.Substring(start, end - start), out processId)
                ? processId
                : 0;
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static NamedPipeClientStream ConnectDiscordPipe()
        {
            for (var index = 0; index < 10; index++)
            {
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", "discord-ipc-" + index, PipeDirection.InOut, PipeOptions.None);
                    pipe.Connect(120);
                    WriteFrame(
                        pipe,
                        HandshakeOpcode,
                        "{\"v\":" + YouTube.Discord.Config.RpcVersion
                        + ",\"client_id\":\"" + YouTube.Discord.Config.ApplicationId + "\"}");
                    ReadAcknowledgement(pipe);
                    return pipe;
                }
                catch
                {
                    if (pipe != null) pipe.Dispose();
                }
            }
            return null;
        }

        private static string ReadAcknowledgement(Stream pipe)
        {
            while (true)
            {
                var header = ReadExactly(pipe, 8);
                var opcode = BitConverter.ToInt32(header, 0);
                var length = BitConverter.ToInt32(header, 4);
                var payload = length > 0 ? ReadExactly(pipe, length) : new byte[0];
                if (opcode == PingOpcode)
                {
                    WriteFrame(pipe, PongOpcode, Encoding.UTF8.GetString(payload));
                    continue;
                }
                return Encoding.UTF8.GetString(payload);
            }
        }

        private static bool IsErrorAcknowledgement(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return true;
            return payload.IndexOf("\"evt\":\"ERROR\"", StringComparison.OrdinalIgnoreCase) >= 0
                || payload.IndexOf("\"evt\": \"ERROR\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void WriteFrame(Stream pipe, int opcode, string json)
        {
            var payload = Encoding.UTF8.GetBytes(json ?? string.Empty);
            var header = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, header, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, 4, 4);
            pipe.Write(header, 0, header.Length);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
        }

        private static byte[] ReadExactly(Stream stream, int length)
        {
            var result = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(result, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return result;
        }

        private static string ReadPresenceFile(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
            catch (IOException) { return null; }
        }

        private static string GetPresencePath()
        {
            uint length = 0;
            GetCurrentPackageFamilyName(ref length, null);
            if (length == 0) return null;
            var familyName = new StringBuilder((int)length);
            if (GetCurrentPackageFamilyName(ref length, familyName) != 0) return null;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Packages", familyName.ToString(), "LocalState", PresenceFileName);
        }

        private static void DisposePipe(ref NamedPipeClientStream pipe)
        {
            if (pipe == null) return;
            try { pipe.Dispose(); } catch { }
            pipe = null;
        }
    }
}
