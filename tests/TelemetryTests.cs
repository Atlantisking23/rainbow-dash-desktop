using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using RainbowDashDesktop;

public static class TelemetryTests
{
    private static int tests;
    private static void Assert(bool condition, string message)
    {
        tests++;
        if (!condition) throw new Exception(message);
    }
    private static Dictionary<string, object> Fixture(string text) { return TelemetryParser.Json(text); }
    private static Dictionary<string, object> Event(string kind, string turn)
    {
        return new Dictionary<string, object> { { "type", kind }, { "turn_id", turn } };
    }
    private static string Record(DateTime timestamp, string kind, string turn)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
            { "timestamp", timestamp.ToString("o") }, { "type", "event_msg" }, { "payload", Event(kind, turn) }
        }) + "\n";
    }
    private static string MetadataLine()
    {
        return "{\"type\":\"session_meta\",\"payload\":{\"source\":\"vscode\",\"thread_source\":\"user\"}}\n";
    }
    private sealed class FakeQuotaReader : ILiveQuotaReader
    {
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
        internal readonly ManualResetEvent Release = new ManualResetEvent(false);
        internal QuotaReading Result;
        internal bool Disposed, Fail;
        public bool TryRead(out QuotaReading reading, out string status)
        {
            Entered.Set();
            Release.WaitOne();
            reading = Disposed || Fail ? null : Result;
            status = Disposed ? "stopped" : Fail ? "service-error" : "live";
            return reading != null;
        }
        public void Dispose() { Disposed = true; Release.Set(); }
    }
    private static void WaitUntil(Func<bool> condition, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 2000) Thread.Sleep(10);
        Assert(condition(), message);
    }
    private static void QuotaWorkerTests(DateTime now)
    {
        QuotaRefreshSchedule schedule = new QuotaRefreshSchedule();
        Assert(schedule.TryStart(now), "quota worker reads immediately at launch without waiting for task activity");
        Assert(!schedule.TryStart(now.AddSeconds(10)), "only one quota request is in flight");
        schedule.Finish(now, true);
        Assert(!schedule.TryStart(now.AddSeconds(59)) && schedule.TryStart(now.AddSeconds(60)), "successful quota reads repeat every minute while idle");
        schedule.Finish(now.AddSeconds(60), false);
        Assert(!schedule.TryStart(now.AddSeconds(74)) && schedule.TryStart(now.AddSeconds(75)), "first failed quota request backs off fifteen seconds");
        schedule.Finish(now.AddSeconds(75), false);
        Assert(!schedule.TryStart(now.AddSeconds(104)) && schedule.TryStart(now.AddSeconds(105)), "second quota failure backs off thirty seconds");
        schedule.Finish(now.AddSeconds(105), false);
        Assert(!schedule.TryStart(now.AddSeconds(164)) && schedule.TryStart(now.AddSeconds(165)), "subsequent quota failures cap backoff at one minute");
        schedule.Finish(now.AddSeconds(165), true);
        schedule.Request(now.AddSeconds(166));
        Assert(!schedule.TryStart(now.AddSeconds(166)) && schedule.TryStart(now.AddSeconds(167)), "manual refresh bypasses the periodic delay with a two-second minimum interval");
        schedule.Request(now.AddSeconds(168));
        schedule.Finish(now.AddSeconds(169), true);
        Assert(schedule.TryStart(now.AddSeconds(169)), "a refresh requested during a live read is not lost");
        schedule.Finish(now.AddSeconds(169), true);
        QuotaReading expired = new QuotaReading { FiveHourResetUtc = now.AddSeconds(170), WeekResetUtc = now.AddDays(1) };
        Assert(!schedule.ObserveResets(expired, now.AddSeconds(169)), "future resets do not cause premature quota polling");
        Assert(schedule.ObserveResets(expired, now.AddSeconds(170)) && !schedule.ObserveResets(expired, now.AddSeconds(171)), "each expired reset requests one immediate read without a retry storm");
        Assert(schedule.TryStart(now.AddSeconds(171)), "reset expiry bypasses the ordinary minute interval");
        schedule.Finish(now.AddSeconds(171), false);
        Assert(!schedule.TryStart(now.AddSeconds(172)), "failure after reset respects backoff instead of spinning on the expired cache");

        string fixtureParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "reconnect-fix"));
        string fixtureRoot = Path.Combine(fixtureParent, "quota-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            long reset = (long)(now.AddHours(2) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            string quotaLine = "{\"timestamp\":\"" + now.ToString("o") + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"primary\":{\"used_percent\":41,\"window_minutes\":300,\"resets_at\":" + reset + "},\"secondary\":{\"used_percent\":59,\"window_minutes\":10080,\"resets_at\":" + reset + "}}}}\n";
            File.WriteAllText(Path.Combine(fixtureRoot, "rollout-quota.jsonl"), MetadataLine() + Record(now.AddSeconds(-10), "task_started", "quota-turn") + quotaLine);
            FakeQuotaReader reader = new FakeQuotaReader { Result = new QuotaReading { FiveHourRemaining = 87, WeekRemaining = 64, FiveHourResetUtc = now.AddHours(3), WeekResetUtc = now.AddDays(4), UpdatedUtc = now.AddSeconds(1) } };
            using (CodexMonitor monitor = new CodexMonitor(fixtureRoot, delegate { return new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-1) }; }, false, reader, true))
            {
                Assert(reader.Entered.WaitOne(1000), "independent quota worker starts the injected reader");
                Stopwatch responsive = Stopwatch.StartNew();
                monitor.PollForTest(now);
                PetSnapshot pending = monitor.Read();
                Assert(responsive.ElapsedMilliseconds < 500 && pending.State == "working", "blocked quota network read cannot block metadata processing or UI snapshots");
                Assert(pending.FiveHourRemaining == 59 && pending.WeekRemaining == 41 && pending.QuotaSource == "local-log" && pending.QuotaStatus == "refreshing", "valid local quota remains visible while live quota refreshes");
                reader.Release.Set();
                WaitUntil(delegate { return monitor.Read().QuotaStatus == "ok"; }, "live quota completion becomes visible without another task event");
                PetSnapshot refreshed = monitor.Read();
                Assert(refreshed.FiveHourRemaining == 87 && refreshed.WeekRemaining == 64 && refreshed.QuotaSource == "account/rateLimits/read" && refreshed.QuotaLastAttemptUtc.HasValue, "server quota supersedes older logs and exposes its own source and attempt time");
            }
            FakeQuotaReader failing = new FakeQuotaReader { Fail = true };
            failing.Release.Set();
            using (CodexMonitor monitor = new CodexMonitor(fixtureRoot, delegate { return new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-1) }; }, false, failing, true))
            {
                monitor.PollForTest(now);
                WaitUntil(delegate { return monitor.Read().QuotaLastFailure == "service-error"; }, "quota API failures are exposed separately from task connection");
                PetSnapshot fallback = monitor.Read();
                Assert(fallback.ConnectionStatus == "connected" && fallback.QuotaStatus == "retrying" && fallback.FiveHourRemaining == 59 && fallback.WeekRemaining == 41, "quota failure preserves valid cached values without claiming the quota service is connected");
            }
            string expiredLine = quotaLine.Replace(reset.ToString(), ((long)(now.AddSeconds(-10) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString());
            File.WriteAllText(Path.Combine(fixtureRoot, "rollout-quota.jsonl"), MetadataLine() + expiredLine);
            FakeQuotaReader stopped = new FakeQuotaReader();
            CodexMonitor disposing = new CodexMonitor(fixtureRoot, delegate { return new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-1) }; }, false, stopped, true);
            Assert(stopped.Entered.WaitOne(1000), "disposal fixture has a live quota request in flight");
            disposing.PollForTest(now);
            Assert(!disposing.Read().FiveHourRemaining.HasValue && !disposing.Read().WeekRemaining.HasValue, "expired quota stays unknown until a fresh sample arrives instead of becoming zero or one hundred");
            Stopwatch disposal = Stopwatch.StartNew();
            disposing.Dispose();
            Assert(stopped.Disposed && disposal.ElapsedMilliseconds < 1000, "monitor disposal cancels its own blocked quota reader promptly");
        }
        finally
        {
            string absoluteFixture = Path.GetFullPath(fixtureRoot);
            if (!absoluteFixture.StartsWith(fixtureParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("quota fixture cleanup escaped its workspace");
            Directory.Delete(absoluteFixture, true);
        }
    }
    private static void ReconnectionTests(DateTime now)
    {
        string packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        Assert(CodexMonitor.IsOfficialChatGptExecutable(Path.Combine(packageRoot, "OpenAI.Codex_26.1007.9446.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")), "official packaged ChatGPT frontend is recognized before its backend starts");
        Assert(!CodexMonitor.IsOfficialChatGptExecutable(Path.Combine(Path.GetTempPath(), "ChatGPT.exe")), "unrelated ChatGPT process name is not a connection");
        Assert(!CodexMonitor.IsOfficialChatGptExecutable(Path.Combine(packageRoot, "OpenAI.Codex_1_x64__otherpublisher", "app", "ChatGPT.exe")), "desktop package publisher identity is checked");
        Assert(!CodexMonitor.IsOfficialChatGptExecutable(Path.Combine(packageRoot, "OpenAI.ChatGPT_1_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")), "another product's ChatGPT executable is excluded");
        Assert(!CodexMonitor.IsOfficialChatGptExecutable(Path.Combine(Path.GetTempPath(), "WindowsApps", "OpenAI.Codex_1_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")), "package-shaped directory outside installed WindowsApps is excluded");

        string fixtureParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "reconnect-fix"));
        string fixtureRoot = Path.Combine(fixtureParent, "telemetry-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            string rollout = Path.Combine(fixtureRoot, "rollout-active.jsonl");
            File.WriteAllText(rollout, MetadataLine() + Record(now.AddSeconds(-50), "task_started", "long-turn"));
            ProcessReading observed = new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-5), Source = "OpenAI.Codex desktop" };
            using (CodexMonitor monitor = new CodexMonitor(fixtureRoot, delegate { return observed; }, false))
            {
                monitor.PollForTest(now);
                PetSnapshot first = monitor.Read();
                Assert(first.CodexRunning && first.ActiveTasks == 1 && first.State == "working", "startup reconstructs a task under the verified frontend process");
                Assert(first.ConnectionStatus == "connected" && first.SessionCount == 1 && first.ProcessSource == "OpenAI.Codex desktop", "diagnostics describe the actual local connection");
                Assert(first.LastObservedUtc == now.AddSeconds(-50), "last observed event timestamp is exposed without message text");
                first.ConnectionStatus = "modified";
                Assert(monitor.Read().ConnectionStatus == "connected", "snapshot diagnostics are copied safely");

                observed = new ProcessReading { Running = false, Complete = true };
                monitor.PollForTest(now.AddSeconds(3));
                monitor.PollForTest(now.AddSeconds(14));
                Assert(monitor.Read().CodexRunning && monitor.Read().ActiveTasks == 1, "short backend absence does not discard an active task");
                observed = new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-5), Source = "OpenAI.Codex desktop" };
                monitor.PollForTest(now.AddSeconds(17));
                Assert(monitor.Read().ActiveTasks == 1, "backend recovery preserves the existing task without a new task_started event");

                observed = new ProcessReading { Running = false, Complete = true };
                monitor.PollForTest(now.AddSeconds(20));
                observed = new ProcessReading { Running = false, Complete = false };
                monitor.PollForTest(now.AddSeconds(23));
                monitor.PollForTest(now.AddSeconds(50));
                Assert(monitor.Read().CodexRunning && monitor.Read().ActiveTasks == 1 && monitor.Read().ConnectionStatus == "retrying", "access-denied or incomplete process checks cannot confirm an exit");

                observed = new ProcessReading { Running = false, Complete = true };
                monitor.PollForTest(now.AddSeconds(53));
                Assert(monitor.Read().CodexRunning && monitor.Read().ActiveTasks == 1, "unknown process checks do not count toward confirmed-absence debounce");
                monitor.PollForTest(now.AddSeconds(69));
                Assert(!monitor.Read().CodexRunning && monitor.Read().ActiveTasks == 0 && monitor.Read().State == "idle", "confirmed application exit clears observation without declaring completion");
                observed = new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-5), Source = "OpenAI.Codex desktop" };
                monitor.PollForTest(now.AddSeconds(72));
                Assert(monitor.Read().ActiveTasks == 1 && monitor.Read().State == "working", "reconnection replays the baseline and recovers a task without new log bytes");
                monitor.PollForTest(now.AddHours(4));
                Assert(monitor.Read().ActiveTasks == 1 && monitor.Read().State == "working", "hours of tool silence are never an idle timeout");

                using (FileStream exclusive = new FileStream(rollout, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    monitor.PollForTest(now.AddHours(4).AddSeconds(3));
                    Assert(monitor.Read().ConnectionStatus == "retrying" && monitor.Read().ActiveTasks == 1, "temporary log sharing failure preserves an active task and advertises retrying");
                    Assert(monitor.Read().LastReadFailure == "session-read: IOException" && monitor.Read().LastReadFailureUtc.HasValue, "read diagnostics contain only stage and exception type");
                }
                monitor.PollForTest(now.AddHours(4).AddSeconds(4));
                Assert(monitor.Read().ConnectionStatus == "connected" && monitor.Read().ActiveTasks == 1, "log connection recovers automatically after a sharing failure");

                FieldInfo watcherField = typeof(CodexMonitor).GetField("watcher", BindingFlags.NonPublic | BindingFlags.Instance);
                FileSystemWatcher oldWatcher = (FileSystemWatcher)watcherField.GetValue(monitor);
                typeof(FileSystemWatcher).GetMethod("OnError", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(oldWatcher,
                    new object[] { new ErrorEventArgs(new InternalBufferOverflowException()) });
                monitor.PollForTest(now.AddHours(4).AddSeconds(7));
                Assert(!Object.ReferenceEquals(oldWatcher, watcherField.GetValue(monitor)), "watcher overflow replaces the failed watcher instead of only rescanning once");

                observed = new ProcessReading { Running = false, Complete = true };
                monitor.RefreshQuota();
                monitor.PollForTest(now.AddHours(4).AddSeconds(8));
                observed = new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-5), Source = "OpenAI.Codex desktop" };
                FileSystemWatcher beforeRefresh = (FileSystemWatcher)watcherField.GetValue(monitor);
                monitor.RefreshQuota();
                monitor.PollForTest(now.AddHours(4).AddSeconds(8).AddMilliseconds(100));
                Assert(monitor.Read().CodexRunning && monitor.Read().ConnectionStatus == "connected" && !Object.ReferenceEquals(beforeRefresh, watcherField.GetValue(monitor)), "manual quota refresh immediately rechecks processes and rebuilds discovery");

                File.Delete(rollout);
                monitor.PollForTest(now.AddHours(4).AddSeconds(9));
                Assert(monitor.Read().SessionCount == 0 && monitor.Read().ActiveTasks == 0 && monitor.Read().ConnectionStatus != "retrying", "deleted or archived sessions cannot leave a permanent busy or retrying state");
                File.WriteAllText(rollout, MetadataLine() + Record(now.AddHours(4).AddSeconds(9), "task_started", "recreated-turn"));
                monitor.RefreshQuota();
                monitor.PollForTest(now.AddHours(4).AddSeconds(10));
                Assert(monitor.Read().SessionCount == 1 && monitor.Read().ActiveTasks == 1, "a recreated session is rediscovered and becomes active");

                observed = new ProcessReading { Running = false, Complete = true };
                monitor.PollForTest(now.AddHours(5));
                monitor.PollForTest(now.AddHours(5).AddSeconds(16));
                observed = new ProcessReading { Running = true, StartedUtc = now.AddHours(5).AddSeconds(19), Source = "OpenAI.Codex desktop" };
                monitor.PollForTest(now.AddHours(5).AddSeconds(20));
                Assert(monitor.Read().CodexRunning && monitor.Read().ActiveTasks == 0, "a fresh process does not resurrect an abandoned pre-launch turn");
            }

            string pendingRoot = Path.Combine(fixtureRoot, "pending");
            Directory.CreateDirectory(pendingRoot);
            string pending = Path.Combine(pendingRoot, "rollout-created.jsonl");
            File.WriteAllText(pending, "{\"type\":\"session_meta\",\"payload\":");
            using (CodexMonitor monitor = new CodexMonitor(pendingRoot, delegate { return new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-1) }; }, false))
            {
                monitor.PollForTest(now);
                Assert(monitor.Read().SessionCount == 0, "an unfinished metadata line is not misclassified");
                File.WriteAllText(pending, MetadataLine() + Record(now.AddSeconds(1), "task_started", "new-turn"));
                monitor.PollForTest(now.AddSeconds(3));
                Assert(monitor.Read().SessionCount == 1 && monitor.Read().ActiveTasks == 1, "unfinished newly-created metadata retries promptly without a one-minute scan delay");
            }
            string offlineRoot = Path.Combine(fixtureRoot, "offline");
            string movedRoot = Path.Combine(fixtureRoot, "offline-moved");
            Directory.CreateDirectory(offlineRoot);
            File.WriteAllText(Path.Combine(offlineRoot, "rollout-offline.jsonl"), MetadataLine() + Record(now.AddSeconds(-10), "task_started", "offline-turn"));
            using (CodexMonitor monitor = new CodexMonitor(offlineRoot, delegate { return new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-1) }; }, false))
            {
                monitor.PollForTest(now);
                Directory.Move(offlineRoot, movedRoot);
                monitor.PollForTest(now.AddSeconds(3));
                Assert(monitor.Read().ConnectionStatus == "retrying" && monitor.Read().ActiveTasks == 1, "temporary loss of the whole session directory preserves the observed task");
                Directory.Move(movedRoot, offlineRoot);
                monitor.PollForTest(now.AddSeconds(6));
                Assert(monitor.Read().ConnectionStatus == "connected" && monitor.Read().ActiveTasks == 1, "restored session directory replays its baseline and reconnects automatically");
            }
            string delayedRoot = Path.Combine(fixtureRoot, "created-after-pet");
            using (CodexMonitor monitor = new CodexMonitor(delayedRoot, delegate { return new ProcessReading { Running = true, StartedUtc = now.AddMinutes(-1) }; }, false))
            {
                monitor.PollForTest(now);
                Directory.CreateDirectory(delayedRoot);
                File.WriteAllText(Path.Combine(delayedRoot, "rollout-already-written.jsonl"), MetadataLine() + Record(now.AddSeconds(1), "task_started", "delayed-root-turn"));
                monitor.PollForTest(now.AddSeconds(3));
                Assert(monitor.Read().SessionCount == 1 && monitor.Read().ActiveTasks == 1, "a session directory created after launch scans files written before its watcher starts");
            }
        }
        finally
        {
            string absoluteFixture = Path.GetFullPath(fixtureRoot);
            if (!absoluteFixture.StartsWith(fixtureParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("fixture cleanup escaped its workspace");
            Directory.Delete(absoluteFixture, true);
        }
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "smoke")
        {
            using (CodexMonitor monitor = new CodexMonitor())
            {
                Thread.Sleep(4000);
                Console.WriteLine(new JavaScriptSerializer().Serialize(monitor.Read()));
                monitor.RefreshQuota();
                Thread.Sleep(750);
                Console.WriteLine(new JavaScriptSerializer().Serialize(monitor.Read()));
            }
            return 0;
        }
        try
        {
            DateTime now = DateTime.UtcNow;
            Assert(!new SessionTrack { Baseline = true }.CanPrune(now), "newly discovered sessions survive until initial tail read");
            Assert(!new SessionTrack { Active = true, LastEventUtc = now.AddDays(-20) }.CanPrune(now), "active task is not evicted by old file metadata");
            QuotaReading quota;
            Assert(TelemetryParser.TryQuota(Fixture("{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":3,\"window_minutes\":300,\"resets_at\":1792761371},\"secondary\":{\"used_percent\":7,\"window_minutes\":10080,\"resets_at\":1793325441}}"), now, out quota), "actual snake_case quota schema");
            Assert(quota.FiveHourRemaining == 97 && quota.WeekRemaining == 93, "remaining means 100 minus used");
            Assert(quota.FiveHourResetUtc.HasValue && quota.FiveHourResetUtc.Value.Kind == DateTimeKind.Utc, "Unix seconds convert to UTC");
            Assert(TelemetryParser.TryQuota(Fixture("{\"rateLimitsByLimitId\":{\"other\":{\"primary\":{\"usedPercent\":99,\"windowDurationMins\":300}},\"codex\":{\"primary\":{\"usedPercent\":30,\"windowDurationMins\":10080},\"secondary\":{\"usedPercent\":12,\"windowDurationMins\":300}}},\"rateLimits\":{\"primary\":{\"usedPercent\":55,\"windowDurationMins\":300}}}"), now, out quota), "official multi-bucket schema");
            Assert(quota.FiveHourRemaining == 88 && quota.WeekRemaining == 70, "duration selects windows and multi-bucket wins");
            Assert(!TelemetryParser.TryQuota(Fixture("{\"primary\":{\"used_percent\":null,\"window_minutes\":300}}"), now, out quota), "missing usage stays unknown");
            Assert(!TelemetryParser.TryQuota(Fixture("{\"limit_id\":\"other\",\"primary\":{\"used_percent\":1,\"window_minutes\":300}}"), now, out quota), "foreign buckets excluded");
            Assert(!TelemetryParser.TryQuota(Fixture("{\"primary\":{\"used_percent\":1,\"window_minutes\":60}}"), now, out quota), "one-hour quota never masquerades as five-hour quota");
            Assert(TelemetryParser.TryQuota(Fixture("{\"primary\":{\"used_percent\":110,\"window_minutes\":300},\"secondary\":{\"used_percent\":-10,\"window_minutes\":10080}}"), now, out quota) && quota.FiveHourRemaining == 0 && quota.WeekRemaining == 100, "remaining is clamped");
            Assert(TelemetryParser.IsUserSession(Fixture("{\"source\":\"vscode\",\"thread_source\":\"user\"}")), "current desktop roots are vscode/user");
            Assert(!TelemetryParser.IsUserSession(Fixture("{\"source\":{\"subagent\":{\"thread_spawn\":{}}},\"thread_source\":\"subagent\"}")), "subagents excluded");
            Assert(TelemetryParser.IsUserSession(Fixture("{\"source\":\"vscode\",\"thread_source\":\"user\",\"parent_thread_id\":\"parent\"}")), "explicit user forks remain visible");
            Assert(TelemetryParser.Metadata("{\"timestamp\":\"now\",\"type\":\"session_meta\",\"payload\":{\"source\":\"vscode\",\"thread_source\":\"user\",\"base_instructions\":\"large truncated") != null, "metadata prefix is read without full instructions");
            SessionTrack first = new SessionTrack { IsUser = true };
            first.Apply(Event("task_started", "first"), now);
            List<SessionTrack> active = new List<SessionTrack> { first };
            Assert(CodexMonitor.Compose(active, true, null, DateTime.MinValue, now.AddSeconds(1)).State == "received", "dedicated receive animation");
            Assert(CodexMonitor.Compose(active, true, null, DateTime.MinValue, now.AddHours(3)).State == "working", "long tool waits never silently become idle");
            first.Apply(Event("task_complete", "older-turn"), now.AddHours(3));
            Assert(first.Active, "delayed terminal cannot end another active turn");
            first.Apply(Event("turn_aborted", "first"), now.AddHours(3));
            Assert(!first.Active, "interrupt clears the corresponding active turn");
            Assert(CodexMonitor.Compose(new List<SessionTrack>(), true, "interrupted", now.AddSeconds(8), now.AddSeconds(7)).State == "interrupted", "interrupted lasts eight seconds");
            Assert(CodexMonitor.Compose(new List<SessionTrack>(), true, "completed", now.AddSeconds(6), now.AddSeconds(7)).State == "idle", "completion expires into idle");
            SessionTrack second = new SessionTrack { IsUser = true };
            second.Apply(Event("task_started", "second"), now.AddSeconds(-10));
            Assert(CodexMonitor.Compose(new List<SessionTrack> { second }, true, "completed", now.AddSeconds(6), now).State == "completed", "another completed thread gets a brief action");
            Assert(CodexMonitor.Compose(new List<SessionTrack> { second }, true, "completed", now.AddSeconds(6), now.AddSeconds(4)).State == "working", "brief action resumes other active tasks");
            SessionTrack tail = new SessionTrack { IsUser = true };
            tail.Apply(Event("item_completed", "long-turn"), now);
            Assert(tail.Active, "bounded startup tails infer an in-progress turn");
            tail.Apply(Event("task_complete", "long-turn"), now.AddSeconds(1));
            tail.Apply(Event("item_completed", "long-turn"), now.AddSeconds(2));
            Assert(!tail.Active, "late item does not resurrect a completed turn");
            Assert(TelemetryParser.Json("{broken") == null, "partial or corrupt record fails closed");
            ReconnectionTests(now);
            QuotaWorkerTests(now);
            Console.WriteLine("PASS " + tests + " telemetry assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
