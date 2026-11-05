using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RainbowDashDesktop
{
    public class PetSnapshot
    {
        public string State = "idle", Source = "", Detail = "";
        public double? FiveHourRemaining, WeekRemaining;
        public DateTime? FiveHourResetUtc, WeekResetUtc, QuotaUpdatedUtc;
        public bool CodexRunning;
        public int ActiveTasks;
        public int SessionCount;
        public string ConnectionStatus = "waiting", ProcessSource = "", LastReadFailure = "";
        public DateTime? LastObservedUtc, LastReadFailureUtc;
        public string QuotaSource = "", QuotaStatus = "unavailable", QuotaLastFailure = "";
        public DateTime? QuotaLastAttemptUtc;

        internal PetSnapshot Copy() { return (PetSnapshot)MemberwiseClone(); }
    }

    // Task state comes from local metadata. An independent worker asks the official
    // signed-in app-server for quota; no turn is created and no auth/config is read.
    public class CodexMonitor : IDisposable
    {
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Dictionary<string, SessionTrack> sessions = new Dictionary<string, SessionTrack>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> ignoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object discoveryGate = new object();
        private readonly HashSet<string> changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher watcher;
        private readonly string sessionRoot;
        private readonly Thread worker;
        private readonly Thread quotaWorker;
        private readonly ILiveQuotaReader quotaReader;
        private readonly object quotaGate = new object();
        private readonly AutoResetEvent quotaWake = new AutoResetEvent(false);
        private readonly QuotaRefreshSchedule quotaSchedule = new QuotaRefreshSchedule();
        private QuotaReading liveQuota;
        private string quotaFailure = "";
        private readonly Func<ProcessReading> processReader;
        private readonly HashSet<string> unreadablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private volatile bool disposed;
        private int refreshRequested;
        private PetSnapshot snapshot = new PetSnapshot { Source = "Codex 本地日志", Detail = "正在读取任务状态…" };
        private QuotaReading quota;
        private DateTime nextDiscovery = DateTime.MinValue, nextProcessCheck = DateTime.MinValue;
        private DateTime nextFullDiscovery = DateTime.MinValue;
        private bool running;
        private DateTime? processStartedUtc;
        private DateTime? processMissingSinceUtc, lastObservedUtc, lastReadFailureUtc;
        private string processSource = "", lastReadFailure = "";
        private bool watcherNeedsRebuild, fullScanRequested, discoveryFailed, processCheckFailed;
        private bool firstPoll = true;
        private string transientState;
        private DateTime transientUntil, transientAt;
        private const int TailBytes = 2 * 1024 * 1024;

        private static string DefaultSessionRoot()
        {
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(codexHome, "sessions");
        }

        public CodexMonitor() : this(DefaultSessionRoot(), ReadProcesses, true, new LiveQuotaReader(), true) { }

        // The deterministic constructor exercises reconnection against isolated fixture
        // files. The normal adapter still reads only the configured local sessions.
        internal CodexMonitor(string root, Func<ProcessReading> reader, bool startWorker)
            : this(root, reader, startWorker, null, false) { }

        internal CodexMonitor(string root, Func<ProcessReading> reader, bool startWorker, ILiveQuotaReader liveReader, bool startQuotaWorker)
        {
            sessionRoot = root;
            processReader = reader;
            quotaReader = liveReader;
            if (startWorker)
            {
                worker = new Thread(Work) { IsBackground = true, Name = "Rainbow Dash Codex metadata" };
                worker.Start();
            }
            if (quotaReader != null && startQuotaWorker)
            {
                quotaWorker = new Thread(QuotaWork) { IsBackground = true, Name = "Rainbow Dash account quota" };
                quotaWorker.Start();
            }
        }

        public PetSnapshot Read()
        {
            lock (gate)
            {
                PetSnapshot result = snapshot.Copy();
                DateTime now = DateTime.UtcNow;
                ApplyQuota(result, now);
                return result;
            }
        }

        public void RefreshQuota()
        {
            if (disposed) return;
            // Server quota is independent of task/log activity; repeated clicks share
            // one worker and cannot start more than one request every two seconds.
            lock (quotaGate) quotaSchedule.Request(DateTime.UtcNow);
            SignalQuota();
            lock (discoveryGate) { fullScanRequested = true; watcherNeedsRebuild = true; }
            Interlocked.Exchange(ref refreshRequested, 1);
            try { wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void Work()
        {
            while (!disposed)
            {
                try { Poll(DateTime.UtcNow); }
                catch (Exception error)
                {
                    RecordFailure("poll", error, DateTime.UtcNow);
                    lock (gate)
                    {
                        snapshot.Detail = "暂时无法读取 Codex 元数据，正在重试";
                        snapshot.ConnectionStatus = "retrying";
                        snapshot.LastReadFailure = lastReadFailure;
                        snapshot.LastReadFailureUtc = lastReadFailureUtc;
                    }
                }
                if (!disposed) wake.WaitOne(500);
            }
        }

        internal void PollForTest(DateTime now) { Poll(now); }

        private void Poll(DateTime now)
        {
            bool refresh = Interlocked.Exchange(ref refreshRequested, 0) != 0;
            if (now >= nextProcessCheck || refresh)
            {
                ReadProcessStatus(now);
                nextProcessCheck = now.AddSeconds(3);
            }
            if (now >= nextDiscovery || refresh)
            {
                Discover(now);
                nextDiscovery = now.AddSeconds(2);
            }
            bool rootAvailable = Directory.Exists(sessionRoot);
            if (!rootAvailable && sessions.Count > 0) discoveryFailed = true;
            foreach (SessionTrack session in sessions.Values.ToArray())
            {
                if (!rootAvailable) { session.ReplayRequested = true; continue; }
                try
                {
                    ReadChanges(session, firstPoll, now);
                    unreadablePaths.Remove(session.Path);
                }
                catch (FileNotFoundException) { sessions.Remove(session.Path); unreadablePaths.Remove(session.Path); }
                catch (DirectoryNotFoundException)
                {
                    if (Directory.Exists(sessionRoot)) { sessions.Remove(session.Path); unreadablePaths.Remove(session.Path); }
                    else { session.ReplayRequested = true; discoveryFailed = true; }
                }
                catch (IOException error) { unreadablePaths.Add(session.Path); RecordFailure("session-read", error, now); }
                catch (UnauthorizedAccessException error) { unreadablePaths.Add(session.Path); RecordFailure("session-read", error, now); }
            }
            firstPoll = false;
            // A task may arrive immediately after a cached "Codex is closed" check.
            // Recheck before discarding its freshly observed task_started event.
            if (!running && sessions.Values.Any(s => s.IsUser && s.Active)) ReadProcessStatus(now);
            if (!running)
            {
                // Process death ends our observation. Never pretend a missing terminal
                // event means successful completion, and do not leave an abandoned turn busy.
                foreach (SessionTrack session in sessions.Values) session.Active = false;
            }
            List<SessionTrack> active = sessions.Values.Where(s => s.IsUser && s.Active).ToList();
            PetSnapshot current = Compose(active, running, transientState, transientUntil, now);
            current.SessionCount = sessions.Count;
            current.ProcessSource = processSource;
            current.LastObservedUtc = lastObservedUtc;
            current.LastReadFailureUtc = lastReadFailureUtc;
            current.LastReadFailure = lastReadFailure;
            current.ConnectionStatus = discoveryFailed || processCheckFailed || unreadablePaths.Count > 0 ? "retrying" : running && sessions.Count > 0 ? "connected" : "waiting";
            ApplyQuota(current, now);
            lock (gate) snapshot = current;
        }

        private void SignalQuota() { try { quotaWake.Set(); } catch (ObjectDisposedException) { } }

        private void QuotaWork()
        {
            while (!disposed)
            {
                bool shouldRead;
                lock (quotaGate)
                {
                    DateTime now = DateTime.UtcNow;
                    quotaSchedule.ObserveResets(ChooseQuota(), now);
                    shouldRead = quotaSchedule.TryStart(now);
                }
                if (shouldRead)
                {
                    QuotaReading reading = null;
                    string status = "unavailable";
                    bool success = false;
                    try { success = quotaReader.TryRead(out reading, out status) && reading != null; }
                    catch (Exception) { status = "reader-error"; }
                    lock (quotaGate)
                    {
                        if (success && (liveQuota == null || reading.UpdatedUtc >= liveQuota.UpdatedUtc)) liveQuota = reading;
                        quotaFailure = success ? "" : status ?? "unavailable";
                        quotaSchedule.Finish(DateTime.UtcNow, success);
                    }
                    try { wake.Set(); } catch (ObjectDisposedException) { }
                }
                if (!disposed) quotaWake.WaitOne(1000);
            }
        }

        // quotaGate protects both immutable readings and refresh scheduling.
        private QuotaReading ChooseQuota()
        {
            return liveQuota != null && (quota == null || liveQuota.UpdatedUtc >= quota.UpdatedUtc) ? liveQuota : quota;
        }

        private void ApplyQuota(PetSnapshot target, DateTime now)
        {
            bool resetRequested;
            lock (quotaGate)
            {
                QuotaReading selected = ChooseQuota();
                resetRequested = quotaSchedule.ObserveResets(selected, now);
                target.QuotaLastAttemptUtc = quotaSchedule.LastAttemptUtc;
                target.QuotaLastFailure = quotaFailure;
                target.FiveHourRemaining = target.WeekRemaining = null;
                target.FiveHourResetUtc = target.WeekResetUtc = target.QuotaUpdatedUtc = null;
                target.QuotaSource = "";
                bool expired = false;
                if (selected != null)
                {
                    target.QuotaSource = Object.ReferenceEquals(selected, liveQuota) ? "account/rateLimits/read" : "local-log";
                    target.FiveHourResetUtc = selected.FiveHourResetUtc;
                    target.WeekResetUtc = selected.WeekResetUtc;
                    target.QuotaUpdatedUtc = selected.UpdatedUtc;
                    bool fiveExpired = selected.FiveHourResetUtc.HasValue && selected.FiveHourResetUtc.Value <= now;
                    bool weekExpired = selected.WeekResetUtc.HasValue && selected.WeekResetUtc.Value <= now;
                    target.FiveHourRemaining = fiveExpired ? null : selected.FiveHourRemaining;
                    target.WeekRemaining = weekExpired ? null : selected.WeekRemaining;
                    expired = fiveExpired || weekExpired;
                }
                target.QuotaStatus = quotaSchedule.InFlight ? "refreshing" : !String.IsNullOrEmpty(quotaFailure) ? "retrying" :
                    expired ? "expired" : selected == null ? "unavailable" : "ok";
            }
            if (resetRequested) SignalQuota();
        }

        internal static PetSnapshot Compose(List<SessionTrack> active, bool isRunning, string terminalState, DateTime terminalUntil, DateTime now)
        {
            PetSnapshot result = new PetSnapshot { CodexRunning = isRunning, ActiveTasks = active.Count, Source = "Codex 本地日志" };
            DateTime terminalStarted = String.IsNullOrEmpty(terminalState) ? DateTime.MinValue : terminalUntil.AddSeconds(terminalState == "interrupted" ? -8 : -6);
            bool briefTerminal = now < terminalUntil && !String.IsNullOrEmpty(terminalState) &&
                (active.Count == 0 || (now < terminalStarted.AddSeconds(3) && active.Max(s => s.StartedUtc) < terminalStarted));
            if (briefTerminal)
            {
                result.State = terminalState;
                result.Detail = terminalState == "completed" ? "任务完成！" : "任务已中断，随时再出发";
                if (active.Count > 0) result.Detail += " · 另有 " + active.Count.ToString(CultureInfo.InvariantCulture) + " 个任务进行中";
            }
            else if (active.Count > 0)
            {
                DateTime latestStart = active.Max(s => s.StartedUtc);
                result.State = latestStart > now.AddSeconds(-2) ? "received" : "working";
                result.Detail = result.State == "received" ? "收到任务，准备起飞！" : "正在处理 " + active.Count.ToString(CultureInfo.InvariantCulture) + " 个任务";
            }
            else result.Detail = isRunning ? "待机中，等你派任务" : "等待 Codex 启动";
            return result;
        }

        private void RecordFailure(string stage, Exception error, DateTime now)
        {
            // Diagnostics deliberately omit exception messages, paths and log contents.
            lastReadFailure = stage + ": " + error.GetType().Name;
            lastReadFailureUtc = now;
        }

        private void ReadProcessStatus(DateTime now)
        {
            ProcessReading observed = processReader();
            processCheckFailed = observed == null || (!observed.Complete && !observed.Running);
            if (processCheckFailed) { processMissingSinceUtc = null; return; }
            if (observed.Running)
            {
                bool reconnecting = !running;
                running = true;
                processMissingSinceUtc = null;
                processStartedUtc = observed.StartedUtc;
                processSource = observed.Source ?? "";
                if (reconnecting)
                {
                    // A prior confirmed exit may have cleared Active while a turn was
                    // still present in the log. Reconstruct it instead of waiting for a
                    // future task_started that might never arrive during a tool wait.
                    foreach (SessionTrack session in sessions.Values) session.ReplayRequested = true;
                    lock (discoveryGate) fullScanRequested = true;
                    nextDiscovery = DateTime.MinValue;
                }
                return;
            }
            if (!processMissingSinceUtc.HasValue) processMissingSinceUtc = now;
            // Frontend/backend launches and process enumeration can briefly disagree.
            // This timer concerns confirmed process absence, never task/log inactivity.
            if (running && now - processMissingSinceUtc.Value < TimeSpan.FromSeconds(15)) return;
            running = false;
            processSource = "";
            processStartedUtc = null;
        }

        internal static bool IsOfficialChatGptExecutable(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable)) return false;
            try
            {
                string path = Path.GetFullPath(executable).Replace('/', '\\');
                if (!String.Equals(Path.GetFileName(path), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
                string packageRoot = programFiles + "\\WindowsApps\\";
                if (!path.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)) return false;
                string relative = path.Substring(packageRoot.Length);
                int slash = relative.IndexOf('\\');
                if (slash < 0) return false;
                string package = relative.Substring(0, slash);
                return package.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                    package.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(relative.Substring(slash + 1), "app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private static ProcessReading ReadProcesses()
        {
            ProcessReading result = new ProcessReading { Complete = true };
            foreach (string name in new[] { "codex", "ChatGPT" })
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch (System.ComponentModel.Win32Exception) { result.Complete = false; continue; }
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        bool accepted = name == "codex";
                        try
                        {
                            if (name == "codex" && LiveQuotaReader.IsOwnedProcess(process.Id)) continue;
                            if (process.HasExited) continue;
                            if (!accepted) accepted = IsOfficialChatGptExecutable(process.MainModule.FileName);
                            if (!accepted) continue;
                            result.Running = true;
                            if (name == "ChatGPT") result.Source = "OpenAI.Codex desktop";
                            else if (String.IsNullOrEmpty(result.Source)) result.Source = "Codex CLI/backend";
                            DateTime started = process.StartTime.ToUniversalTime();
                            if (!result.StartedUtc.HasValue || started < result.StartedUtc.Value) result.StartedUtc = started;
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // A codex CLI already has a known identity. An inaccessible,
                            // arbitrary ChatGPT.exe must not be treated as this app.
                            if (accepted)
                            {
                                result.Running = true;
                                if (String.IsNullOrEmpty(result.Source)) result.Source = "Codex CLI/backend";
                            }
                            else result.Complete = false;
                        }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            return result;
        }

        private void Discover(DateTime now)
        {
            bool rebuild;
            lock (discoveryGate)
            {
                rebuild = watcherNeedsRebuild;
                watcherNeedsRebuild = false;
            }
            if ((rebuild || !Directory.Exists(sessionRoot)) && watcher != null)
            {
                watcher.Dispose();
                watcher = null;
            }
            discoveryFailed = false;
            if (!disposed && Directory.Exists(sessionRoot) && watcher == null)
            {
                FileSystemWatcher candidate = null;
                try
                {
                    candidate = new FileSystemWatcher(sessionRoot, "rollout-*.jsonl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    candidate.Changed += OnSessionChanged;
                    candidate.Created += OnSessionChanged;
                    candidate.Renamed += delegate(object sender, RenamedEventArgs change) { QueuePath(change.FullPath); };
                    candidate.Error += delegate
                    {
                        lock (discoveryGate) { watcherNeedsRebuild = true; fullScanRequested = true; }
                        try { wake.Set(); } catch (ObjectDisposedException) { }
                    };
                    candidate.EnableRaisingEvents = true;
                    watcher = candidate;
                    // Files can already exist before the new watch starts, including
                    // when CODEX_HOME/sessions appears after the pet has launched.
                    lock (discoveryGate) fullScanRequested = true;
                }
                catch (IOException error) { if (candidate != null) candidate.Dispose(); discoveryFailed = true; RecordFailure("watcher", error, now); }
                catch (UnauthorizedAccessException error) { if (candidate != null) candidate.Dispose(); discoveryFailed = true; RecordFailure("watcher", error, now); }
            }
            List<FileInfo> files = new List<FileInfo>();
            bool fullScan;
            lock (discoveryGate)
            {
                fullScan = fullScanRequested || now >= nextFullDiscovery;
                fullScanRequested = false;
                foreach (string path in changedPaths) files.Add(new FileInfo(path));
                changedPaths.Clear();
            }
            if (fullScan && Directory.Exists(sessionRoot))
            {
                // A resumed chat keeps its original rollout directory. Search by file
                // modification time, never by the date embedded in its original filename.
                try { files.AddRange(new DirectoryInfo(sessionRoot).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)); }
                catch (IOException error) { discoveryFailed = true; RecordFailure("discovery", error, now); }
                catch (UnauthorizedAccessException error) { discoveryFailed = true; RecordFailure("discovery", error, now); }
            }
            if (fullScan) nextFullDiscovery = now.AddSeconds(discoveryFailed ? 2 : 60);
            // Existing active sessions remain tracked even when many other sessions appear.
            foreach (FileInfo file in files.OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (sessions.ContainsKey(file.FullName) || ignoredPaths.Contains(file.FullName)) continue;
                SessionTrack session = new SessionTrack { Path = file.FullName, Historical = firstPoll, Baseline = true };
                try
                {
                    using (FileStream stream = Open(file.FullName))
                    {
                        byte[] prefix = new byte[(int)Math.Min(stream.Length, 32768)];
                        int count = ReadBytes(stream, prefix);
                        int newline = Array.IndexOf(prefix, (byte)10, 0, count);
                        Dictionary<string, object> record = TelemetryParser.Metadata(Encoding.UTF8.GetString(prefix, 0, newline < 0 ? count : newline).TrimStart('\uFEFF'));
                        Dictionary<string, object> meta = TelemetryParser.Map(TelemetryParser.Get(record, "payload"));
                        if (TelemetryParser.String(record, "type") != "session_meta" || meta == null)
                        {
                            // The writer may have created the file before finishing its
                            // first line. Do not wait a full discovery minute to retry it.
                            QueuePath(file.FullName);
                            continue;
                        }
                        session.IsUser = TelemetryParser.IsUserSession(meta);
                        // Subagent histories may contain a copy of the parent's turns.
                        // They must never drive animation or inflate the active-task count.
                        if (!session.IsUser) { ignoredPaths.Add(file.FullName); continue; }
                        session.Position = Math.Max(0, stream.Length - TailBytes);
                        session.DiscardFirstFragment = session.Position > 0;
                        sessions.Add(file.FullName, session);
                        unreadablePaths.Remove(file.FullName);
                    }
                }
                catch (FileNotFoundException) { unreadablePaths.Remove(file.FullName); }
                catch (DirectoryNotFoundException) { unreadablePaths.Remove(file.FullName); }
                catch (IOException error) { QueuePath(file.FullName); unreadablePaths.Add(file.FullName); RecordFailure("session-discovery", error, now); }
                catch (UnauthorizedAccessException error) { QueuePath(file.FullName); unreadablePaths.Add(file.FullName); RecordFailure("session-discovery", error, now); }
                catch (ArgumentException) { }
            }
            // Bound memory without dropping sessions in long tool waits.
            foreach (SessionTrack old in sessions.Values.Where(s => s.CanPrune(now)).ToArray()) { sessions.Remove(old.Path); unreadablePaths.Remove(old.Path); }
            if (ignoredPaths.Count > 4096) ignoredPaths.Clear();
        }

        private void OnSessionChanged(object sender, FileSystemEventArgs change) { QueuePath(change.FullPath); }
        private void QueuePath(string path) { lock (discoveryGate) changedPaths.Add(path); }

        private static FileStream Open(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        private static int ReadBytes(FileStream stream, byte[] bytes)
        {
            int count = 0, got;
            while (count < bytes.Length && (got = stream.Read(bytes, count, bytes.Length - count)) > 0) count += got;
            return count;
        }

        private void ReadChanges(SessionTrack session, bool suppressAnimations, DateTime now)
        {
            using (FileStream stream = Open(session.Path))
            {
                if (session.ReplayRequested || stream.Length < session.Position)
                {
                    session.Position = Math.Max(0, stream.Length - TailBytes);
                    session.Partial = new byte[0];
                    session.DiscardFirstFragment = session.Position > 0;
                    session.Active = false;
                    session.ActiveTurn = session.LastTerminalTurn = null;
                    session.StartedUtc = session.LastEventUtc = DateTime.MinValue;
                    session.Historical = true;
                    session.Baseline = true;
                    session.ReplayRequested = false;
                }
                if (stream.Length == session.Position) return;
                stream.Position = session.Position;
                byte[] bytes = new byte[(int)Math.Min(TailBytes, stream.Length - session.Position)];
                int count = ReadBytes(stream, bytes);
                session.Position += count;
                byte[] joined = new byte[session.Partial.Length + count];
                Buffer.BlockCopy(session.Partial, 0, joined, 0, session.Partial.Length);
                Buffer.BlockCopy(bytes, 0, joined, session.Partial.Length, count);
                int start = 0;
                for (int i = 0; i < joined.Length; i++)
                {
                    if (joined[i] != 10) continue;
                    if (!session.DiscardFirstFragment)
                    {
                        string line = Encoding.UTF8.GetString(joined, start, i - start).TrimStart('\uFEFF');
                        ApplyLine(session, line, suppressAnimations || session.Historical, now);
                    }
                    session.DiscardFirstFragment = false;
                    start = i + 1;
                }
                int remainder = joined.Length - start;
                // Skip an oversized record rather than retaining unbounded message text.
                if (remainder > TailBytes)
                {
                    session.Partial = new byte[0];
                    session.DiscardFirstFragment = true;
                }
                else
                {
                    session.Partial = new byte[remainder];
                    Buffer.BlockCopy(joined, start, session.Partial, 0, remainder);
                }
                if (session.Position >= stream.Length) { session.Historical = false; session.Baseline = false; }
            }
        }

        private void ApplyLine(SessionTrack session, string line, bool historical, DateTime now)
        {
            Dictionary<string, object> record = TelemetryParser.Json(line);
            if (record == null) return;
            DateTime timestamp = TelemetryParser.Timestamp(TelemetryParser.Get(record, "timestamp"), now);
            string recordType = TelemetryParser.String(record, "type");
            Dictionary<string, object> payload = TelemetryParser.Map(TelemetryParser.Get(record, "payload"));
            if (payload == null) return;
            QuotaReading reading;
            object limits = TelemetryParser.Get(payload, "rate_limits") ?? TelemetryParser.Get(payload, "rateLimits");
            if (TelemetryParser.Get(payload, "rateLimitsByLimitId") != null || TelemetryParser.Get(payload, "rate_limits_by_limit_id") != null) limits = payload;
            if (limits == null && (recordType == "account/rateLimits/updated" || recordType == "rate_limits")) limits = payload;
            if (limits != null && TelemetryParser.TryQuota(limits, timestamp, out reading))
            {
                lock (quotaGate) if (quota == null || reading.UpdatedUtc >= quota.UpdatedUtc) quota = reading;
            }
            if (recordType != "event_msg") return;
            if (!lastObservedUtc.HasValue || timestamp > lastObservedUtc.Value) lastObservedUtc = timestamp;
            string terminal = session.Apply(payload, timestamp);
            if ((historical || session.Baseline) && session.Active && processStartedUtc.HasValue && session.LastEventUtc < processStartedUtc.Value.AddSeconds(-2)) session.Active = false;
            if (!historical && terminal != null && timestamp >= now.AddSeconds(-15) && timestamp >= transientAt)
            {
                transientAt = timestamp;
                transientState = terminal;
                transientUntil = now.AddSeconds(terminal == "interrupted" ? 8 : 6);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // The reader owns only its own read-only app-server child. Stop that
            // child before joining, rather than waiting for its network timeout.
            if (quotaReader != null) { try { quotaReader.Dispose(); } catch (Exception) { } }
            SignalQuota();
            wake.Set();
            bool stopped = worker == null || (worker != Thread.CurrentThread && worker.Join(3000));
            bool quotaStopped = quotaWorker == null || (quotaWorker != Thread.CurrentThread && quotaWorker.Join(1500));
            if (watcher != null) watcher.Dispose();
            if (stopped) wake.Dispose();
            if (quotaStopped) quotaWake.Dispose();
        }
    }

    internal class QuotaRefreshSchedule
    {
        internal DateTime NextAttemptUtc = DateTime.MinValue;
        internal DateTime? LastAttemptUtc;
        internal bool InFlight;
        private int failures;
        private bool requestedDuringFlight;
        private DateTime? observedFiveResetUtc, observedWeekResetUtc;

        internal void Request(DateTime now)
        {
            if (InFlight) requestedDuringFlight = true;
            if (NextAttemptUtc > now) NextAttemptUtc = now;
        }

        internal bool ObserveResets(QuotaReading reading, DateTime now)
        {
            if (reading == null) return false;
            bool changed = false;
            if (reading.FiveHourResetUtc.HasValue && reading.FiveHourResetUtc.Value <= now && observedFiveResetUtc != reading.FiveHourResetUtc)
            { observedFiveResetUtc = reading.FiveHourResetUtc; changed = true; }
            if (reading.WeekResetUtc.HasValue && reading.WeekResetUtc.Value <= now && observedWeekResetUtc != reading.WeekResetUtc)
            { observedWeekResetUtc = reading.WeekResetUtc; changed = true; }
            if (changed) Request(now);
            return changed;
        }

        internal bool TryStart(DateTime now)
        {
            if (InFlight || now < NextAttemptUtc || (LastAttemptUtc.HasValue && now < LastAttemptUtc.Value.AddSeconds(2))) return false;
            InFlight = true;
            LastAttemptUtc = now;
            return true;
        }

        internal void Finish(DateTime now, bool success)
        {
            InFlight = false;
            failures = success ? 0 : Math.Min(3, failures + 1);
            NextAttemptUtc = requestedDuringFlight ? now : now.AddSeconds(success ? 60 : failures == 1 ? 15 : failures == 2 ? 30 : 60);
            requestedDuringFlight = false;
        }
    }

    internal class ProcessReading
    {
        internal bool Running, Complete = true;
        internal DateTime? StartedUtc;
        internal string Source = "";
    }

    internal class SessionTrack
    {
        internal string Path, ActiveTurn, LastTerminalTurn;
        internal bool IsUser, Active, Historical, Baseline, DiscardFirstFragment, ReplayRequested;
        internal long Position;
        internal byte[] Partial = new byte[0];
        internal DateTime StartedUtc, LastEventUtc;
        internal bool CanPrune(DateTime now) { return !Baseline && !Active && LastEventUtc < now.AddDays(-7); }

        internal string Apply(Dictionary<string, object> payload, DateTime timestamp)
        {
            string kind = TelemetryParser.String(payload, "type"), turn = TelemetryParser.String(payload, "turn_id");
            if (timestamp > LastEventUtc) LastEventUtc = timestamp;
            if (kind == "task_started")
            {
                Active = true; ActiveTurn = turn; StartedUtc = timestamp;
            }
            else if (kind == "task_complete" || kind == "task_completed" || kind == "turn_aborted")
            {
                LastTerminalTurn = turn;
                // Never let a delayed terminal for another turn stop the current turn.
                if (!String.IsNullOrEmpty(ActiveTurn) && !String.IsNullOrEmpty(turn) && ActiveTurn != turn) return null;
                Active = false; ActiveTurn = null;
                return kind == "turn_aborted" ? "interrupted" : "completed";
            }
            else if (kind == "item_completed" && !Active && !String.IsNullOrEmpty(turn) && turn != LastTerminalTurn)
            {
                // Startup tails can begin after task_started during a long conversation.
                Active = true; ActiveTurn = turn; StartedUtc = timestamp.AddSeconds(-3);
            }
            return null;
        }
    }

    internal class QuotaReading
    {
        internal double? FiveHourRemaining, WeekRemaining;
        internal DateTime? FiveHourResetUtc, WeekResetUtc;
        internal DateTime UpdatedUtc;
    }

    internal static class TelemetryParser
    {
        internal static Dictionary<string, object> Json(string text)
        {
            try { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 128 }.DeserializeObject(text) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        internal static Dictionary<string, object> Metadata(string prefix)
        {
            // Identity fields precede base_instructions in released rollout metadata.
            // Parse that small JSON prefix without reading or retaining the instructions.
            bool inString = false, escaped = false;
            int depth = 0, stringStart = 0;
            for (int i = 0; i < prefix.Length; i++)
            {
                char ch = prefix[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch != '"') continue;
                    inString = false;
                    if (depth == 2 && prefix.Substring(stringStart + 1, i - stringStart - 1) == "base_instructions")
                    {
                        int comma = stringStart - 1;
                        while (comma >= 0 && Char.IsWhiteSpace(prefix[comma])) comma--;
                        if (comma >= 0 && prefix[comma] == ',') return Json(prefix.Substring(0, comma) + "}}");
                    }
                }
                else if (ch == '"') { inString = true; stringStart = i; }
                else if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') depth--;
            }
            return Json(prefix);
        }
        internal static object Get(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result : null; }
        internal static string String(Dictionary<string, object> value, string key) { return Get(value, key) as string; }
        internal static DateTime Timestamp(object value, DateTime fallback)
        {
            DateTime timestamp;
            return value is string && DateTime.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp) ? timestamp : fallback;
        }
        internal static bool IsUserSession(Dictionary<string, object> meta)
        {
            string threadSource = String(meta, "thread_source");
            Dictionary<string, object> sourceMap = Map(Get(meta, "source"));
            if (threadSource == "subagent" || Get(sourceMap, "subagent") != null) return false;
            if (threadSource == "user") return true;
            if (Get(meta, "parent_thread_id") != null || sourceMap != null) return false;
            // Compatibility with earlier main-thread rollouts lacking thread_source.
            string source = String(meta, "source");
            return source == "vscode" || source == "cli" || source == "exec" || source == "appServer" || source == "desktop";
        }
        private static double? Number(object value)
        {
            if (value == null || value is bool) return null;
            double number;
            return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) ? (double?)number : null;
        }
        private static DateTime? Reset(object value)
        {
            double? seconds = Number(value);
            if (!seconds.HasValue) return null;
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds.Value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        internal static bool TryQuota(object value, DateTime timestamp, out QuotaReading result)
        {
            result = null;
            Dictionary<string, object> map = Map(value);
            if (map == null) return false;
            Dictionary<string, object> buckets = Map(Get(map, "rateLimitsByLimitId") ?? Get(map, "rate_limits_by_limit_id"));
            if (buckets != null)
            {
                object codex = Get(buckets, "codex");
                if (codex != null) return TryQuota(codex, timestamp, out result);
                return false; // Another model's bucket is not the account's Codex quota.
            }
            object wrapped = Get(map, "rateLimits") ?? Get(map, "rate_limits");
            if (wrapped != null) return TryQuota(wrapped, timestamp, out result);
            string limitId = String(map, "limit_id") ?? String(map, "limitId");
            if (limitId != null && limitId != "codex") return false;
            QuotaReading candidate = new QuotaReading { UpdatedUtc = timestamp };
            foreach (string windowKey in new[] { "primary", "secondary" })
            {
                Dictionary<string, object> window = Map(Get(map, windowKey));
                if (window == null) continue;
                double? duration = Number(Get(window, "window_minutes") ?? Get(window, "windowDurationMins"));
                double? used = Number(Get(window, "used_percent") ?? Get(window, "usedPercent"));
                if (!duration.HasValue || !used.HasValue) continue;
                double remaining = Math.Max(0, Math.Min(100, 100 - used.Value));
                DateTime? reset = Reset(Get(window, "resets_at") ?? Get(window, "resetsAt"));
                if (duration.Value == 300) { candidate.FiveHourRemaining = remaining; candidate.FiveHourResetUtc = reset; }
                else if (duration.Value == 10080) { candidate.WeekRemaining = remaining; candidate.WeekResetUtc = reset; }
            }
            if (!candidate.FiveHourRemaining.HasValue && !candidate.WeekRemaining.HasValue) return false;
            result = candidate;
            return true;
        }
    }
}
