using System;
using RainbowDashDesktop;

internal static class RestPolicyTests
{
    private static int count;
    private static void Check(bool condition, string message)
    { count++; if (!condition) throw new Exception(message); }
    private static RestController Sleeping(long start)
    {
        RestController rest = new RestController(start);
        rest.ObserveTask("idle", 0, start);
        rest.Advance(start + RestController.DefaultSleepDelayMs);
        rest.Advance(start + RestController.DefaultSleepDelayMs + RestController.FallDurationMs);
        return rest;
    }

    public static int Main()
    {
        try
        {
            RestController rest = new RestController();
            Check(rest.Stage == RestStage.Awake && rest.CanRunIdleExtras && rest.SleepDelayMs == 300000,
                "Startup is awake; default timeout is five minutes.");
            rest.ObserveTask("idle", 0, 0);
            for (long now = 1000; now < 300000; now += 1000)
            {
                // Each observation can carry different quota/status metadata in
                // the caller. It must not be mistaken for task or user activity.
                rest.ObserveTask("idle", 0, now);
                rest.Advance(now); // stationary cursor/rendering/gaze sampling
            }
            rest.Advance(299999);
            Check(rest.Stage == RestStage.Awake && rest.QuietSince == 0 && rest.LastInteractionAt == 0,
                "Repeated idle polls, refreshes and a stationary cursor do not postpone sleep.");
            rest.Advance(300000);
            Check(rest.Stage == RestStage.Falling && rest.PhaseStartedAt == 300000 && rest.PhaseDuration == 1200,
                "Exact five-minute boundary begins the full 1.2-second settling bridge.");
            Check(!rest.CanRunIdleExtras && rest.IsSleepingLike && rest.PhaseFrame(300000, 6) == 0,
                "Idle extras cannot override the first settling cel.");
            Check(rest.PhaseFrame(301199, 6) == 5 && rest.Stage == RestStage.Falling,
                "The last bridge cel is displayed before sleep.");
            rest.Advance(301200);
            Check(rest.Stage == RestStage.Sleeping && rest.PhaseDuration == 0 && rest.PhaseStartedAt == 301200,
                "Sleep begins exactly after the settling bridge.");
            rest.ObserveTask("idle", 0, 900000);
            rest.Advance(900001);
            Check(rest.Stage == RestStage.Sleeping && rest.WakeCompletedVersion == 0,
                "Long idle observations and automatic animation do not spontaneously wake the pet.");

            foreach (string state in new string[] { "received", "working", "waiting", "completed", "interrupted", "error", "unknown" })
            {
                RestController active = new RestController();
                active.ObserveTask(state, 0, 0);
                active.ObserveTask(state, 0, 600000);
                Check(active.Stage == RestStage.Awake && !active.CanRunIdleExtras,
                    "Non-idle state prevents automatic sleep: " + state);
                active.ObserveTask("idle", 0, 600001);
                active.Advance(900000);
                Check(active.Stage == RestStage.Awake, "Full quiet interval begins only when the backend returns idle: " + state);
                active.Advance(900001);
                Check(active.Stage == RestStage.Falling, "New quiet interval reaches exact threshold: " + state);
            }
            RestController hiddenActive = new RestController();
            hiddenActive.ObserveTask("idle", 1, 0); hiddenActive.Advance(1000000);
            Check(hiddenActive.Stage == RestStage.Awake && !hiddenActive.IsQuiet,
                "Active-task count blocks sleep even if the state label temporarily says idle.");
            hiddenActive.ObserveTask("idle", 0, 1000001); hiddenActive.Advance(1300000);
            Check(hiddenActive.Stage == RestStage.Awake, "Finishing a long task starts a fresh five-minute quiet interval.");

            foreach (RestAttentionKind kind in new RestAttentionKind[] { RestAttentionKind.Pointer, RestAttentionKind.Click, RestAttentionKind.Menu })
            {
                RestController attention = new RestController();
                attention.NotifyInteraction(kind, 299999);
                attention.Advance(599998);
                Check(attention.Stage == RestStage.Awake, "Real attention postpones sleep: " + kind);
                attention.Advance(599999);
                Check(attention.Stage == RestStage.Falling, "Attention starts a full new quiet interval: " + kind);
                attention.NotifyInteraction(kind, 600000);
                Check(attention.Stage == RestStage.Waking && attention.WakeFromFalling && attention.PhaseDuration == 120,
                    "Attention reverses a just-started settling bridge: " + kind);
            }
            RestController boundary = new RestController();
            boundary.ObserveTask("received", 1, 300000);
            Check(boundary.Stage == RestStage.Awake && boundary.WakeCompletedVersion == 0,
                "A task observed exactly at the threshold wins without a spurious sleep/wake bridge.");

            RestController wake = Sleeping(0);
            wake.ObserveTask("received", 1, 400000);
            Check(wake.Stage == RestStage.Waking && wake.PendingTaskState == "received" && wake.PhaseStartedAt == 400000,
                "A task wakes the pet and defers its visual target.");
            wake.ObserveTask("working", 1, 400200);
            wake.ObserveTask("waiting", 1, 400400);
            wake.NotifyInteraction(RestAttentionKind.Pointer, 400500);
            Check(wake.PendingTaskState == "waiting" && wake.RequestedTaskState == "waiting" && wake.PhaseStartedAt == 400000,
                "Latest task and pointer attention during wake cannot restart the bridge.");
            wake.Advance(400999);
            Check(wake.Stage == RestStage.Waking && wake.WakeCompletedVersion == 0, "Wake remains active for the whole second.");
            wake.ObserveTask("completed", 0, 401000);
            Check(wake.Stage == RestStage.Awake && wake.LastWakeTarget == "completed" && wake.WakeCompletedVersion == 1,
                "A task arriving on the wake boundary is included in its single completion commit.");
            wake.Advance(401001); wake.ObserveTask("completed", 0, 401002);
            Check(wake.WakeCompletedVersion == 1, "Repeated animation and task polls never emit duplicate wake completion.");

            RestController shortTask = Sleeping(0);
            shortTask.ObserveTask("received", 1, 400000);
            shortTask.ObserveTask("working", 1, 400010);
            shortTask.ObserveTask("completed", 0, 400020);
            shortTask.ObserveTask("idle", 0, 400030);
            Check(shortTask.RequestedTaskState == "idle" && shortTask.PendingTaskState == "completed",
                "A very short task retains its completed reaction during wake without falsifying real idle state.");
            shortTask.Advance(401000);
            Check(shortTask.LastWakeTarget == "completed" && shortTask.PendingTaskState == "idle" && shortTask.CanRunIdleExtras,
                "The completion target is committed once, then pending state follows the real backend.");
            shortTask.Advance(700999);
            Check(shortTask.Stage == RestStage.Awake, "Waking provides a fresh quiet interval before the next sleep.");
            shortTask.Advance(701000);
            Check(shortTask.Stage == RestStage.Falling, "Fresh interval eventually permits automatic sleep again.");

            RestController superseded = Sleeping(0);
            superseded.ObserveTask("received", 1, 400000);
            superseded.ObserveTask("completed", 0, 400020);
            superseded.ObserveTask("idle", 0, 400030);
            superseded.ObserveTask("received", 1, 400040);
            superseded.ObserveTask("working", 1, 400050);
            superseded.Advance(401000);
            Check(superseded.LastWakeTarget == "working" && superseded.RequestedTaskState == "working",
                "A newer task supersedes a preserved older completion during the same wake.");
            RestController cancelled = Sleeping(0);
            cancelled.ObserveTask("received", 1, 400000);
            cancelled.ObserveTask("interrupted", 0, 400010);
            cancelled.ObserveTask("idle", 0, 400020);
            cancelled.Advance(401000);
            Check(cancelled.LastWakeTarget == "interrupted", "A short interrupted task preserves its distinct reaction.");

            RestController falling = new RestController(); falling.Advance(300000);
            falling.ObserveTask("received", 1, 300600);
            Check(falling.Stage == RestStage.Waking && falling.PhaseStartedAt == 300600 &&
                falling.WakeFromFalling && falling.WakeFromFallProgress == .5 && falling.PhaseDuration == 300,
                "A task halfway through settling reverses from that pose in 300 milliseconds.");
            falling.ObserveTask("working", 1, 300700); falling.Advance(300899);
            Check(falling.Stage == RestStage.Waking && falling.WakeFromFallProgress == .5,
                "A newer task does not change the reversal origin or restart its short bridge.");
            falling.Advance(300900);
            Check(falling.Stage == RestStage.Awake && falling.LastWakeTarget == "working" && !falling.WakeFromFalling,
                "Preempted settling commits its latest task once and clears reversal metadata.");
            RestController immediateReturn = new RestController(); immediateReturn.Advance(300000);
            immediateReturn.NotifyInteraction(RestAttentionKind.Click, 300000);
            Check(immediateReturn.WakeFromFalling && immediateReturn.WakeFromFallProgress == 0 && immediateReturn.PhaseDuration == 120,
                "Attention on the first settling cel uses the bounded minimal return, not an asleep wake cel.");
            RestController nearlyAsleep = new RestController(); nearlyAsleep.Advance(300000);
            nearlyAsleep.ObserveTask("received", 1, 301199);
            Check(nearlyAsleep.WakeFromFalling && nearlyAsleep.WakeFromFallProgress > .99 && nearlyAsleep.PhaseDuration == 600,
                "A nearly finished settling pose reverses naturally within 600 milliseconds.");
            RestController completedFall = new RestController(); completedFall.Advance(300000);
            completedFall.ObserveTask("received", 1, 301200);
            Check(!completedFall.WakeFromFalling && completedFall.PhaseDuration == 1000,
                "A task at the completed settling boundary uses normal asleep-to-awake artwork.");
            RestController dragReturn = new RestController(); dragReturn.Advance(300000);
            dragReturn.NotifyInteraction(RestAttentionKind.Drag, 300600);
            Check(dragReturn.WakeFromFalling && dragReturn.WakeFromFallProgress == .5 && dragReturn.PhaseDuration == 200,
                "Dragging a half-settled pet reverses its current pose within 200 milliseconds.");

            RestController drag = Sleeping(0);
            drag.NotifyInteraction(RestAttentionKind.Drag, 400000);
            Check(drag.Stage == RestStage.Waking && drag.PhaseDuration == 200,
                "Captured drag requests a fast visual wake; policy exposes no movement/capture gate.");
            drag.NotifyInteraction(RestAttentionKind.Drag, 400050);
            drag.NotifyInteraction(RestAttentionKind.Drag, 400199);
            drag.Advance(400200);
            Check(drag.Stage == RestStage.Awake && drag.WakeCompletedVersion == 1,
                "Repeated drag samples cannot hold the pet in wake indefinitely.");
            RestController faster = Sleeping(0);
            faster.ObserveTask("received", 1, 400000);
            faster.NotifyInteraction(RestAttentionKind.Drag, 400100);
            Check(faster.PhaseStartedAt == 400000 && faster.PhaseDuration == 300,
                "Drag shortens an existing wake without restarting its timeline.");
            faster.ObserveTask("working", 1, 400150); faster.Advance(400300);
            Check(faster.Stage == RestStage.Awake && faster.LastWakeTarget == "working",
                "The latest task survives a faster captured-drag wake.");

            RestController config = new RestController();
            config.Configure(180000, 0); config.Advance(179999);
            Check(config.Stage == RestStage.Awake, "Three-minute option waits for its exact boundary.");
            config.Advance(180000);
            Check(config.Stage == RestStage.Falling, "Three-minute option enters settling at its exact boundary.");
            config.Configure(0, 180100);
            Check(config.Stage == RestStage.Waking, "Never-sleep setting releases an already settling pet.");
            config.Advance(181100); config.Advance(10000000);
            Check(config.Stage == RestStage.Awake && config.SleepDelayMs == 0, "Never-sleep remains awake indefinitely.");
            config.Configure(600000, 10000001); config.Advance(10600000);
            Check(config.Stage == RestStage.Awake, "Enabling ten-minute sleep grants a full new quiet interval.");
            config.Configure(600000, 10600001);
            Check(config.Stage == RestStage.Falling, "Repeating an unchanged setting does not restart the interval.");
            RestController disableSleep = Sleeping(0);
            disableSleep.Configure(-1, 400000); disableSleep.Advance(401000);
            Check(disableSleep.SleepDelayMs == 0 && disableSleep.Stage == RestStage.Awake,
                "Negative legacy configuration safely means never and wakes a sleeping pet.");

            RestController clock = new RestController(1000);
            clock.NotifyInteraction(RestAttentionKind.Click, 2000);
            clock.Advance(-100); clock.ObserveTask(" IDLE ", -3, 500);
            Check(clock.QuietSince == 2000 && clock.RequestedTaskState == "idle" && clock.IsQuiet,
                "Backwards clocks cannot rewind attention; task normalization is deterministic.");
            clock.Advance(301999);
            Check(clock.Stage == RestStage.Awake, "Backwards input does not shorten the quiet interval.");
            clock.Advance(302000);
            Check(clock.Stage == RestStage.Falling, "Clamped clock still reaches its original exact deadline.");
            Check(clock.PhaseFrame(301000, 6) == 0 && clock.PhaseAgeMs == 0,
                "Backwards render time cannot rewind or skip a bridge.");
            Check(clock.PhaseFrame(302200, 0) == -1 && clock.PhaseFrame(302200, 1) == 0,
                "Empty and single-cel artwork bounds are safe.");
            RestController late = new RestController(); late.Advance(900000);
            Check(late.Stage == RestStage.Falling && late.PhaseStartedAt == 900000,
                "A stalled renderer still plays a complete settling bridge on resume.");
            RestController huge = new RestController(Int64.MaxValue - 100);
            huge.Advance(Int64.MaxValue);
            Check(huge.Stage == RestStage.Awake && huge.PhaseAgeMs == 100,
                "Monotonic elapsed arithmetic is safe near the long-clock boundary.");

            RestController manual = new RestController();
            manual.Configure(0, 0); manual.RequestSleep(100);
            Check(manual.ManualSleepRequested && manual.Stage == RestStage.Falling && manual.PhaseStartedAt == 100,
                "Manual sleep immediately settles even when automatic sleep is disabled.");
            Check(manual.SleepDelayMs == 0 && manual.RequestedTaskState == "idle",
                "Manual sleep changes neither the automatic preference nor the backend task state.");
            manual.RequestSleep(400); manual.ObserveTask("idle", 0, 700); manual.RequestSleep(1000);
            Check(manual.Stage == RestStage.Falling && manual.PhaseStartedAt == 100 && manual.PhaseDuration == 1200,
                "Repeated sleep commands and idle polls never restart manual settling.");
            manual.Advance(1300); manual.RequestSleep(2000); manual.Configure(0, 3000);
            Check(manual.Stage == RestStage.Sleeping && manual.ManualSleepRequested && manual.PhaseStartedAt == 1300,
                "A manual nap persists; repeating never-auto-sleep does not wake or restart it.");
            manual.Advance(1000000); manual.ObserveTask("idle", 0, 1000001);
            Check(manual.Stage == RestStage.Sleeping && manual.WakeCompletedVersion == 0 && !manual.CanRunIdleExtras,
                "A manual nap has no preview timeout and cannot start idle extras.");

            RestController busyNap = new RestController();
            busyNap.ObserveTask("working", 2, 0); busyNap.RequestSleep(100);
            Check(busyNap.Stage == RestStage.Falling && busyNap.ManualSleepRequested &&
                busyNap.RequestedTaskState == "working" && !busyNap.IsQuiet,
                "The user may deliberately nap during existing work without falsifying it as idle.");
            busyNap.ObserveTask("working", 2, 100); busyNap.ObserveTask("working", 2, 700);
            busyNap.ObserveTask("working", 2, 1300); busyNap.ObserveTask(" WORKING ", 2, 400000);
            Check(busyNap.Stage == RestStage.Sleeping && busyNap.ManualSleepRequested,
                "Same normalized busy state and count keep the manual nap through ordinary polls.");
            busyNap.ObserveTask("working", 1, 400100);
            Check(busyNap.Stage == RestStage.Sleeping && busyNap.ManualSleepRequested,
                "A decreasing task count with the same state is not new work.");
            busyNap.ObserveTask("idle", 0, 400200); busyNap.Advance(700200);
            Check(busyNap.Stage == RestStage.Sleeping && busyNap.ManualSleepRequested && busyNap.IsQuiet,
                "Returning from busy to idle may continue the deliberately requested nap.");
            busyNap.ObserveTask("working", 1, 700300);
            Check(busyNap.Stage == RestStage.Waking && !busyNap.ManualSleepRequested && busyNap.PendingTaskState == "working",
                "New work after idle cancels a manual nap and wakes for its real target.");

            foreach (string next in new string[] { "received", "waiting", "completed", "interrupted", "error" })
            {
                RestController changed = new RestController(); changed.ObserveTask("working", 1, 0);
                changed.RequestSleep(100); changed.Advance(1300);
                changed.ObserveTask(next, next == "completed" || next == "interrupted" ? 0 : 1, 1400);
                Check(changed.Stage == RestStage.Waking && !changed.ManualSleepRequested && changed.PendingTaskState == next,
                    "A new non-idle state interrupts the manual nap: " + next);
            }
            RestController added = new RestController(); added.ObserveTask("working", 1, 0);
            added.RequestSleep(100); added.Advance(1300); added.ObserveTask("working", 2, 1400);
            Check(added.Stage == RestStage.Waking && !added.ManualSleepRequested && added.PendingTaskState == "working",
                "Added concurrent work wakes even when the busy state label does not change.");
            RestController idleAdded = new RestController(); idleAdded.RequestSleep(100); idleAdded.Advance(1300);
            idleAdded.ObserveTask("idle", 1, 1400);
            Check(idleAdded.Stage == RestStage.Waking && !idleAdded.ManualSleepRequested && idleAdded.PendingTaskState == "working",
                "An increased task count wins even before the backend state label catches up.");
            RestController halfway = new RestController(); halfway.ObserveTask("working", 1, 0); halfway.RequestSleep(100);
            halfway.ObserveTask("waiting", 1, 700);
            Check(halfway.Stage == RestStage.Waking && !halfway.ManualSleepRequested && halfway.WakeFromFalling &&
                halfway.WakeFromFallProgress == .5 && halfway.PhaseDuration == 300,
                "New task state halfway through manual settling reverses from its current pose.");

            foreach (RestAttentionKind kind in new RestAttentionKind[] { RestAttentionKind.Pointer, RestAttentionKind.Click,
                RestAttentionKind.Menu, RestAttentionKind.Drag })
            {
                RestController touched = new RestController(); touched.Configure(0, 0); touched.RequestSleep(100); touched.Advance(1300);
                touched.NotifyInteraction(kind, 1400);
                Check(touched.Stage == RestStage.Waking && !touched.ManualSleepRequested &&
                    touched.PhaseDuration == (kind == RestAttentionKind.Drag ? 200 : 1000),
                    "Real attention cancels a manual nap, with the usual wake duration: " + kind);
                touched.Advance(2400); touched.Advance(1000000);
                Check(touched.Stage == RestStage.Awake && !touched.ManualSleepRequested,
                    "Attention does not leave a hidden manual-sleep request that reappears: " + kind);
            }

            RestController queued = Sleeping(0); queued.NotifyInteraction(RestAttentionKind.Click, 400000);
            queued.RequestSleep(400100); queued.RequestSleep(400800);
            Check(queued.Stage == RestStage.Waking && queued.ManualSleepRequested && queued.PhaseStartedAt == 400000 && queued.PhaseDuration == 1000,
                "Manual sleep during waking queues one nap without jumping pose or restarting wake.");
            queued.Advance(400999);
            Check(queued.Stage == RestStage.Waking && queued.WakeCompletedVersion == 0,
                "A queued nap lets the existing wake bridge reach its final cel.");
            queued.Advance(401000);
            Check(queued.Stage == RestStage.Falling && queued.PhaseStartedAt == 401000 && queued.WakeCompletedVersion == 1,
                "After one wake completion the queued manual nap starts a fresh full settling bridge.");
            queued.RequestSleep(401600); queued.Advance(402200);
            Check(queued.Stage == RestStage.Sleeping && queued.ManualSleepRequested && queued.WakeCompletedVersion == 1,
                "Repeated queued requests cannot prolong settling or create extra wake completions.");

            RestController busyQueued = Sleeping(0); busyQueued.ObserveTask("working", 1, 400000);
            busyQueued.RequestSleep(400100); busyQueued.ObserveTask("working", 1, 400500); busyQueued.Advance(401000);
            Check(busyQueued.Stage == RestStage.Falling && busyQueued.ManualSleepRequested && busyQueued.LastWakeTarget == "working",
                "A queued manual nap also persists through polls of work already present when requested.");
            RestController cancelledQueue = Sleeping(0); cancelledQueue.ObserveTask("working", 1, 400000);
            cancelledQueue.RequestSleep(400100); cancelledQueue.ObserveTask("waiting", 1, 400500); cancelledQueue.Advance(401000);
            Check(cancelledQueue.Stage == RestStage.Awake && !cancelledQueue.ManualSleepRequested && cancelledQueue.LastWakeTarget == "waiting",
                "New task state during a queued nap cancels it without restarting the current wake.");
            RestController touchedQueue = Sleeping(0); touchedQueue.NotifyInteraction(RestAttentionKind.Click, 400000);
            touchedQueue.RequestSleep(400100); touchedQueue.NotifyInteraction(RestAttentionKind.Menu, 400500); touchedQueue.Advance(401000);
            Check(touchedQueue.Stage == RestStage.Awake && !touchedQueue.ManualSleepRequested && touchedQueue.WakeCompletedVersion == 1,
                "An explicit interaction cancels queued manual sleep as well as an actual nap.");
            RestController queuedTerminal = Sleeping(0); queuedTerminal.ObserveTask("working", 1, 400000);
            queuedTerminal.RequestSleep(400100); queuedTerminal.ObserveTask("completed", 0, 400500);
            queuedTerminal.ObserveTask("idle", 0, 400600); queuedTerminal.Advance(401000);
            Check(queuedTerminal.Stage == RestStage.Awake && !queuedTerminal.ManualSleepRequested && queuedTerminal.LastWakeTarget == "completed",
                "Cancelling a queued nap for a short terminal task retains the existing wake's terminal latch.");
            RestController requestClock = new RestController(1000); requestClock.RequestSleep(2000); requestClock.RequestSleep(500);
            Check(requestClock.Stage == RestStage.Falling && requestClock.PhaseStartedAt == 2000 && requestClock.PhaseAgeMs == 0,
                "Backwards request time cannot rewind or restart manual settling.");

            Console.WriteLine("PASS " + count + " rest policy checks"); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
