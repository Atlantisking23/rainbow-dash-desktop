using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace RainbowDashDesktop
{
    internal interface ILiveQuotaReader : IDisposable
    {
        bool TryRead(out QuotaReading reading, out string status);
    }

    // The only child-process protocol used here is initialize, initialized and
    // account/rateLimits/read. Authentication remains entirely inside Codex.
    internal sealed class LiveQuotaReader : ILiveQuotaReader
    {
        private readonly Func<ILiveQuotaTransport> factory;
        private readonly int timeoutMilliseconds;
        private readonly object gate = new object();
        private ILiveQuotaTransport active;
        private bool disposed;
        private int reading;
        private static readonly object ownedGate = new object();
        private static readonly HashSet<int> ownedProcesses = new HashSet<int>();

        internal LiveQuotaReader() : this(delegate { return CodexQuotaTransport.Start(); }, 15000) { }
        internal LiveQuotaReader(Func<ILiveQuotaTransport> transportFactory, int totalTimeoutMilliseconds)
        {
            if (transportFactory == null) throw new ArgumentNullException("transportFactory");
            if (totalTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("totalTimeoutMilliseconds");
            factory = transportFactory;
            timeoutMilliseconds = totalTimeoutMilliseconds;
        }

        internal static bool IsOwnedProcess(int processId)
        {
            lock (ownedGate) return ownedProcesses.Contains(processId);
        }
        internal static int StartOwnedProcess(Process process)
        {
            // A process enumerator can see a freshly started child immediately.
            // Its ownership check must wait until the PID has been registered.
            lock (ownedGate)
            {
                if (!process.Start()) return 0;
                int processId = process.Id;
                ownedProcesses.Add(processId);
                return processId;
            }
        }
        internal static void RegisterOwnedProcess(int processId) { lock (ownedGate) ownedProcesses.Add(processId); }
        internal static void RemoveOwnedProcess(int processId) { lock (ownedGate) ownedProcesses.Remove(processId); }

        public bool TryRead(out QuotaReading result, out string status)
        {
            result = null;
            status = "stopped";
            lock (gate) if (disposed) return false;
            if (Interlocked.CompareExchange(ref reading, 1, 0) != 0) { status = "busy"; return false; }
            ILiveQuotaTransport transport = null;
            Stopwatch elapsed = Stopwatch.StartNew();
            // Reserve a bounded part of the total budget for closing the child.
            int protocolBudget = timeoutMilliseconds - Math.Min(500, timeoutMilliseconds / 4);
            try
            {
                lock (gate) if (disposed) return false;
                transport = factory();
                if (transport == null) { status = "unavailable"; return false; }
                lock (gate)
                {
                    if (disposed) return false;
                    active = transport;
                }
                transport.SendLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"rainbow_dash_desktop\",\"title\":\"Rainbow Dash Desktop\",\"version\":\"1.1.0\"}}}");
                object initialized;
                if (!ReadResponse(transport, 1, elapsed, protocolBudget, out initialized, out status)) return false;
                if (TelemetryParser.Map(initialized) == null) { status = "protocol-error"; return false; }
                transport.SendLine("{\"method\":\"initialized\",\"params\":{}}");
                transport.SendLine("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                object quota;
                if (!ReadResponse(transport, 2, elapsed, protocolBudget, out quota, out status)) return false;
                if (!TelemetryParser.TryQuota(quota, DateTime.UtcNow, out result)) { status = "invalid-quota"; return false; }
                status = "live";
                return true;
            }
            catch (Exception error)
            {
                // No exception text, account information, server error payload or
                // stderr is copied into diagnostics or persisted anywhere.
                if (!(error is IOException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception ||
                    error is UnauthorizedAccessException || error is ObjectDisposedException || error is ArgumentException)) throw;
                lock (gate) status = disposed ? "stopped" : "transport";
                return false;
            }
            finally
            {
                lock (gate) if (Object.ReferenceEquals(active, transport)) active = null;
                if (transport != null) transport.Dispose();
                Interlocked.Exchange(ref reading, 0);
            }
        }

        private bool ReadResponse(ILiveQuotaTransport transport, int expectedId, Stopwatch elapsed, int budget, out object result, out string status)
        {
            result = null;
            status = "timeout";
            for (int messages = 0; messages < 256; messages++)
            {
                lock (gate) if (disposed) { status = "stopped"; return false; }
                int remaining = budget - (int)Math.Min(Int32.MaxValue, elapsed.ElapsedMilliseconds);
                if (remaining <= 0) return false;
                string line;
                if (!transport.TryReadLine(remaining, out line))
                {
                    lock (gate) if (disposed) { status = "stopped"; return false; }
                    status = transport.Ended ? "transport" : "timeout";
                    return false;
                }
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 65536) { status = "protocol-error"; return false; }
                Dictionary<string, object> message = TelemetryParser.Json(line);
                if (message == null) { status = "protocol-error"; return false; }
                object id = TelemetryParser.Get(message, "id");
                // Notifications and responses to another request cannot satisfy
                // the handshake. String ids are distinct JSON-RPC identities.
                if (!(id is int || id is long) || Convert.ToInt64(id, CultureInfo.InvariantCulture) != expectedId) continue;
                if (TelemetryParser.Get(message, "error") != null) { status = "service-error"; return false; }
                if (!message.TryGetValue("result", out result)) { status = "protocol-error"; return false; }
                return true;
            }
            status = "protocol-error";
            return false;
        }

        public void Dispose()
        {
            ILiveQuotaTransport transport;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                transport = active;
            }
            if (transport != null) transport.Dispose();
        }
    }

    internal interface ILiveQuotaTransport : IDisposable
    {
        void SendLine(string line);
        bool TryReadLine(int timeoutMilliseconds, out string line);
        bool Ended { get; }
    }

    internal sealed class CodexQuotaTransport : ILiveQuotaTransport
    {
        private readonly Process child;
        private readonly int childId;
        private readonly object gate = new object();
        private readonly Queue<string> lines = new Queue<string>();
        private readonly AutoResetEvent ready = new AutoResetEvent(false);
        private bool ended, disposed, readyDisposed;
        private int waiters;

        private CodexQuotaTransport(Process process)
        {
            child = process;
            childId = process.Id;
            child.OutputDataReceived += Receive;
            // Drain the pipe without retaining or displaying its contents.
            child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs data) { };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }

        internal static ILiveQuotaTransport Start()
        {
            string executable = FindExecutable();
            if (executable == null) return null;
            Process process = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, Arguments = "app-server --stdio", UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            } };
            int processId = 0;
            try
            {
                processId = LiveQuotaReader.StartOwnedProcess(process);
                if (processId == 0) { process.Dispose(); return null; }
                return new CodexQuotaTransport(process);
            }
            catch
            {
                if (processId != 0)
                {
                    try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    LiveQuotaReader.RemoveOwnedProcess(processId);
                }
                process.Dispose();
                throw;
            }
        }

        internal static bool IsOfficialExecutable(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin"));
                string candidate = Path.GetFullPath(path);
                string folder = Path.GetDirectoryName(candidate);
                return String.Equals(Path.GetFileName(candidate), "codex.exe", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(Path.GetFileName(folder), "^[0-9a-fA-F]{16}$");
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private static bool InstalledExecutable(string path)
        {
            if (!IsOfficialExecutable(path) || !File.Exists(path)) return false;
            try
            {
                string version = Path.GetDirectoryName(Path.GetFullPath(path));
                string root = Path.GetDirectoryName(version);
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                    (File.GetAttributes(version) & FileAttributes.ReparsePoint) == 0 &&
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string FindExecutable()
        {
            // Prefer the exact installed backend currently used by the desktop.
            foreach (Process process in Process.GetProcessesByName("codex"))
            {
                using (process)
                {
                    try
                    {
                        if (LiveQuotaReader.IsOwnedProcess(process.Id)) continue;
                        string path = process.MainModule.FileName;
                        if (InstalledExecutable(path)) return path;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (!Directory.Exists(root)) return null;
            try
            {
                return Directory.GetDirectories(root).Select(folder => Path.Combine(folder, "codex.exe"))
                    .Where(InstalledExecutable).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private void Receive(object sender, DataReceivedEventArgs data)
        {
            lock (gate)
            {
                if (disposed) return;
                if (data.Data == null) ended = true;
                else if (lines.Count >= 256 || data.Data.Length > 65536) ended = true;
                else lines.Enqueue(data.Data);
                ready.Set();
            }
        }

        public bool Ended { get { lock (gate) return ended || disposed; } }
        public void SendLine(string line)
        {
            child.StandardInput.WriteLine(line);
            child.StandardInput.Flush();
        }
        public bool TryReadLine(int timeoutMilliseconds, out string line)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            line = null;
            lock (gate)
            {
                if (disposed) return false;
                waiters++;
            }
            try
            {
                while (true)
                {
                    lock (gate)
                    {
                        if (lines.Count > 0) { line = lines.Dequeue(); return true; }
                        if (ended || disposed) return false;
                    }
                    int remaining = timeoutMilliseconds - (int)Math.Min(Int32.MaxValue, elapsed.ElapsedMilliseconds);
                    if (remaining <= 0 || !ready.WaitOne(remaining)) return false;
                }
            }
            finally
            {
                lock (gate)
                {
                    waiters--;
                    if (disposed && waiters == 0 && !readyDisposed) { ready.Dispose(); readyDisposed = true; }
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                lines.Clear();
                ready.Set();
                if (waiters == 0) { ready.Dispose(); readyDisposed = true; }
            }
            try
            {
                try { child.StandardInput.Close(); } catch (IOException) { } catch (InvalidOperationException) { }
                try { if (!child.WaitForExit(200)) { child.Kill(); child.WaitForExit(200); } }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            finally
            {
                LiveQuotaReader.RemoveOwnedProcess(childId);
                child.Dispose();
            }
        }
    }
}
