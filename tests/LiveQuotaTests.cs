using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RainbowDashDesktop;

public static class LiveQuotaTests
{
    private static int assertions;
    private static void Assert(bool value, string description)
    {
        assertions++;
        if (!value) throw new Exception(description);
    }
    private const string Initialize = "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}";
    private const string Quota = "{\"id\":2,\"result\":{\"rateLimitsByLimitId\":{\"codex\":{\"limitId\":\"codex\",\"primary\":{\"windowDurationMins\":300,\"usedPercent\":28,\"resetsAt\":1999999999},\"secondary\":{\"windowDurationMins\":10080,\"usedPercent\":53,\"resetsAt\":1999999999}}}}}";

    private sealed class FixtureTransport : ILiveQuotaTransport
    {
        internal readonly Queue<string> Responses = new Queue<string>();
        internal readonly List<string> Writes = new List<string>();
        internal readonly ManualResetEvent EnteredRead = new ManualResetEvent(false);
        internal readonly ManualResetEvent Stop = new ManualResetEvent(false);
        internal bool Disposed, Block, Closed;
        internal int MaxTimeout;
        internal FixtureTransport(params string[] replies) { foreach (string reply in replies) Responses.Enqueue(reply); }
        public bool Ended { get { return Closed || Disposed; } }
        public void SendLine(string line) { Writes.Add(line); }
        public bool TryReadLine(int timeoutMilliseconds, out string line)
        {
            MaxTimeout = Math.Max(MaxTimeout, timeoutMilliseconds);
            if (Responses.Count > 0) { line = Responses.Dequeue(); return true; }
            line = null;
            EnteredRead.Set();
            if (Block) Stop.WaitOne(timeoutMilliseconds);
            return false;
        }
        public void Dispose() { Disposed = true; Stop.Set(); }
    }

    private static bool Read(FixtureTransport fixture, out QuotaReading quota, out string status)
    {
        using (LiveQuotaReader reader = new LiveQuotaReader(delegate { return fixture; }, 200)) return reader.TryRead(out quota, out status);
    }
    private static void Protocol()
    {
        QuotaReading quota;
        string status;
        FixtureTransport fixture = new FixtureTransport("{\"method\":\"serverNotification\",\"params\":{}}", "{\"id\":\"1\",\"result\":{}}", "{\"id\":99,\"result\":{}}", Initialize, Quota);
        DateTime before = DateTime.UtcNow;
        Assert(Read(fixture, out quota, out status) && status == "live", "notifications and wrong JSON-RPC ids cannot complete the initialize handshake");
        Assert(quota.FiveHourRemaining == 72 && quota.WeekRemaining == 47, "real parser handles the Codex bucket and converts used percentage to remaining percentage");
        Assert(quota.UpdatedUtc >= before && quota.UpdatedUtc <= DateTime.UtcNow, "live sample uses successful fetch time rather than stale log time");
        Assert(fixture.Disposed && fixture.Writes.Count == 3, "one read closes its transport and sends only the three permitted protocol messages");
        Dictionary<string, object> first = TelemetryParser.Json(fixture.Writes[0]);
        Dictionary<string, object> second = TelemetryParser.Json(fixture.Writes[1]);
        Dictionary<string, object> third = TelemetryParser.Json(fixture.Writes[2]);
        Assert((int)first["id"] == 1 && (string)first["method"] == "initialize" && TelemetryParser.Map(first["params"]) != null, "first request initializes the official app server");
        Assert(!second.ContainsKey("id") && (string)second["method"] == "initialized", "initialized is a notification, not a request");
        Assert((int)third["id"] == 2 && (string)third["method"] == "account/rateLimits/read" && !third.ContainsKey("params"), "quota read has exactly the documented method and no account mutation parameters");
        Assert(TelemetryParser.String(TelemetryParser.Map(TelemetryParser.Get(TelemetryParser.Map(first["params"]), "clientInfo")), "name") == "rainbow_dash_desktop", "handshake identifies the desktop pet client");

        fixture = new FixtureTransport("{\"id\":1,\"error\":{\"code\":-32000,\"message\":\"private fixture authentication details\"}}");
        Assert(!Read(fixture, out quota, out status) && quota == null && status == "service-error", "server errors expose a fixed status without copying account/error contents");
        Assert(fixture.Writes.Count == 1 && fixture.Disposed, "failed initialization never requests account quota");
        fixture = new FixtureTransport(Initialize, "{\"id\":2,\"error\":{\"code\":-32000,\"message\":\"private quota error\"}}");
        Assert(!Read(fixture, out quota, out status) && status == "service-error" && fixture.Disposed, "quota error leaves no invented quota sample and still closes transport");
        fixture = new FixtureTransport(Initialize, "{\"id\":2,\"result\":{\"rateLimitsByLimitId\":{\"other-model\":{\"primary\":{\"usedPercent\":5,\"windowDurationMins\":300}}}}}");
        Assert(!Read(fixture, out quota, out status) && quota == null && status == "invalid-quota", "another model bucket cannot become the Codex quota");
        fixture = new FixtureTransport(Initialize, "{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":0,\"windowDurationMins\":60}}}}");
        Assert(!Read(fixture, out quota, out status) && status == "invalid-quota", "an unrelated window duration cannot be labeled five hours");
        fixture = new FixtureTransport("{bad-json");
        Assert(!Read(fixture, out quota, out status) && status == "protocol-error" && fixture.Disposed, "malformed server output is rejected without retaining the body");
        fixture = new FixtureTransport("{\"id\":1,\"result\":null}");
        Assert(!Read(fixture, out quota, out status) && status == "protocol-error" && fixture.Writes.Count == 1, "a null initialize response does not unlock quota requests");
        fixture = new FixtureTransport(Initialize, "{\"id\":2}");
        Assert(!Read(fixture, out quota, out status) && status == "protocol-error", "missing quota result is a protocol failure");
        fixture = new FixtureTransport(Initialize) { Closed = true };
        Assert(!Read(fixture, out quota, out status) && status == "transport" && fixture.Disposed, "an exited child does not masquerade as a live empty quota");
        using (LiveQuotaReader reader = new LiveQuotaReader(delegate { return null; }, 100))
            Assert(!reader.TryRead(out quota, out status) && status == "unavailable", "missing verified official backend is reported without PATH fallback");
    }

    private static void TimeoutAndCancellation()
    {
        QuotaReading quota;
        string status;
        FixtureTransport fixture = new FixtureTransport() { Block = true };
        Stopwatch elapsed = Stopwatch.StartNew();
        using (LiveQuotaReader reader = new LiveQuotaReader(delegate { return fixture; }, 160))
            Assert(!reader.TryRead(out quota, out status) && status == "timeout", "a silent backend has a bounded total read deadline");
        Assert(elapsed.ElapsedMilliseconds < 1500 && fixture.MaxTimeout <= 160 && fixture.Disposed, "timeout closes the backend and reserves cleanup time");

        fixture = new FixtureTransport() { Block = true };
        LiveQuotaReader cancellable = new LiveQuotaReader(delegate { return fixture; }, 15000);
        string workerStatus = null;
        Thread thread = new Thread(new ThreadStart(delegate { QuotaReading ignored; cancellable.TryRead(out ignored, out workerStatus); }));
        thread.IsBackground = true;
        thread.Start();
        Assert(fixture.EnteredRead.WaitOne(1000), "cancellation fixture reaches an in-progress server read");
        Assert(!cancellable.TryRead(out quota, out status) && status == "busy", "concurrent refresh cannot spawn a second child inside one reader");
        elapsed.Restart();
        cancellable.Dispose();
        Assert(thread.Join(1000) && fixture.Disposed && workerStatus == "stopped" && elapsed.ElapsedMilliseconds < 1000, "disposing the reader interrupts pending IO immediately rather than waiting fifteen seconds");
        Assert(!cancellable.TryRead(out quota, out status) && status == "stopped", "disposed reader cannot launch another backend");
    }

    private static void PathAndOwnership()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        Assert(CodexQuotaTransport.IsOfficialExecutable(Path.Combine(root, "9614f1d9b40d9c4f", "codex.exe")), "the current official versioned backend directory is accepted");
        Assert(!CodexQuotaTransport.IsOfficialExecutable(Path.Combine(Path.GetTempPath(), "codex.exe")), "an arbitrary executable named codex is rejected");
        Assert(!CodexQuotaTransport.IsOfficialExecutable(Path.Combine(root, "9614f1d9b40d9c4f", "nested", "codex.exe")), "nested executables are outside the exact installation layout");
        Assert(!CodexQuotaTransport.IsOfficialExecutable(Path.Combine(root, "..", "untrusted", "codex.exe")), "directory traversal cannot escape the official bin root");
        Assert(!CodexQuotaTransport.IsOfficialExecutable(Path.Combine(root, "another-folder", "codex.exe")), "an arbitrary directory under bin is not an installed version");
        const int fixturePid = 1987654321;
        Assert(!LiveQuotaReader.IsOwnedProcess(fixturePid), "an unrelated process is not initially owned");
        try
        {
            LiveQuotaReader.RegisterOwnedProcess(fixturePid);
            Assert(LiveQuotaReader.IsOwnedProcess(fixturePid), "quota backend ownership is visible to the process monitor");
        }
        finally { LiveQuotaReader.RemoveOwnedProcess(fixturePid); }
        Assert(!LiveQuotaReader.IsOwnedProcess(fixturePid), "completed quota child is removed from ownership tracking");
    }

    public static int Main()
    {
        Protocol();
        TimeoutAndCancellation();
        PathAndOwnership();
        Console.WriteLine("Live quota protocol: " + assertions + " checks passed.");
        return 0;
    }
}
