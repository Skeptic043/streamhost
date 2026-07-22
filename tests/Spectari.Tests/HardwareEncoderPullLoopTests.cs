using System.Collections.ObjectModel;
using System.Diagnostics;
using Spectari.Encode;
using Xunit;

namespace Spectari.Tests;

public sealed class HardwareEncoderPullLoopTests
{
    private const int FieldFramesPerSecond = 60;
    private const int FieldInFlightDepth = 2;

    [Fact]
    public void FieldDepthRunsTenThousandStepsWithoutExhaustionOrDuplicates()
    {
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 1,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: FieldInFlightDepth);

        harness.Run(10_000);

        Assert.Equal(10_000, harness.Encoder.SubmittedVersions.Count);
        Assert.Equal(
            harness.Encoder.SubmittedVersions.Count,
            harness.Encoder.SubmittedVersions.Distinct().Count());
        Assert.Equal(0, harness.Converter.ExhaustionCount);
        Assert.True(harness.Converter.MaximumOutstanding <= FieldInFlightDepth + 1);
        Assert.Equal(1, harness.EpochCount);
    }

    [Fact]
    public void SlowCreditsStillMakeProgressWithoutDuplicateSubmission()
    {
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 3,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: 5);

        harness.Run(10_000);

        Assert.InRange(harness.Encoder.SubmittedVersions.Count, 3_333, 3_334);
        Assert.Equal(
            harness.Encoder.SubmittedVersions.Count,
            harness.Encoder.SubmittedVersions.Distinct().Count());
        Assert.Equal(0, harness.Converter.ExhaustionCount);
        Assert.True(harness.Output.AccessUnitCount > 3_000);
    }

    [Fact]
    public void AvailableCreditAndSurfaceAlwaysSubmitTheReadyFrame()
    {
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 1,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: 1);

        harness.Run(1_000);

        Assert.Equal(harness.Converter.SuccessCount, harness.Encoder.SubmittedVersions.Count);
        Assert.Equal(1_000, harness.Encoder.SubmittedVersions.Count);
    }

    [Fact]
    public void CreditFamineReturnsAStallInsteadOfHanging()
    {
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 1,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: 1);

        harness.Step();
        harness.Encoder.CreditFamine = true;

        HardwarePullStepResult result = default;
        for (int i = 0; i < 20 && result.StallReason is null; i++)
            result = harness.Step();

        Assert.Contains("no NeedInput", result.StallReason);
    }

    [Fact]
    public void ReorderedOutputDoesNotChangeInputOwnershipOrDuplicateFrames()
    {
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 1,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: 4,
            reorderOutput: true);

        harness.Run(10_000);

        Assert.Equal(
            harness.Encoder.SubmittedVersions.Count,
            harness.Encoder.SubmittedVersions.Distinct().Count());
        Assert.Equal(0, harness.Converter.ExhaustionCount);
        Assert.True(harness.Output.AccessUnitCount > 9_900);
        Assert.False(harness.Output.Versions.SequenceEqual(
            harness.Output.Versions.Order()));
    }

    [Fact]
    public void CancellationStopsTheHeadlessLoopPromptly()
    {
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 1,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: 1,
            advanceCaptureOnWait: true);
        using var cancellation = new CancellationTokenSource();
        HardwarePullRunResult result = default;
        var thread = new Thread(() =>
        {
            result = harness.Loop.Run(
                cancellation.Token,
                harness.NextTimestamp);
        });

        thread.Start();
        Assert.True(SpinWait.SpinUntil(
            () => harness.Encoder.SubmittedFrameCount >= 100,
            TimeSpan.FromSeconds(1)));
        cancellation.Cancel();

        Assert.True(thread.Join(TimeSpan.FromSeconds(1)));
        Assert.Equal("stopped", result.Reason);
    }

    [Fact]
    public void FasterCaptureConvergesToSessionSubmissionRate()
    {
        const int captureFramesPerSecond = 120;
        const int simulationSeconds = 120;
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 1,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: FieldInFlightDepth);

        int captureFrames = captureFramesPerSecond * simulationSeconds;
        for (int frame = 1; frame <= captureFrames; frame++)
        {
            harness.StepAt(
                frame * Stopwatch.Frequency / captureFramesPerSecond);
        }

        double submittedRate = harness.Encoder.SubmittedVersions.Count /
            (double)simulationSeconds;
        Assert.InRange(submittedRate, 59.9, 60.1);
        Assert.Equal(
            harness.Encoder.SubmittedVersions.Count,
            harness.Encoder.SubmittedVersions.Distinct().Count());
        Assert.Equal(0, harness.Loop.DuplicateFrames);
    }

    [Fact]
    public void JitteredFramesStraddlingDeadlinesDoNotDepressSubmissionRate()
    {
        const int simulationSeconds = 120;
        var harness = new PullLoopHarness(
            poolCapacity: FieldInFlightDepth + 1,
            creditEverySteps: 1,
            holdFrames: FieldInFlightDepth,
            outputLagFrames: FieldInFlightDepth);

        long timestampTicks = 0;
        long simulationTicks = simulationSeconds * Stopwatch.Frequency;
        int frame = 0;
        while (timestampTicks < simulationTicks)
        {
            harness.StepAt(timestampTicks);
            int intervalMilliseconds = frame++ % 2 == 0 ? 7 : 9;
            timestampTicks += intervalMilliseconds * Stopwatch.Frequency / 1000;
        }

        double submittedRate = harness.Encoder.SubmittedVersions.Count /
            (double)simulationSeconds;
        Assert.InRange(submittedRate, 59.9, 60.1);
    }

    [Fact]
    public void CaptureStallPreservesTimestampGapAndCannotBurstOnResume()
    {
        var harness = new PullLoopHarness(
            poolCapacity: 16,
            creditEverySteps: 1,
            holdFrames: 0,
            outputLagFrames: 0);
        long frameInterval = Stopwatch.Frequency / FieldFramesPerSecond;

        harness.StepAt(0);
        harness.StepAt(frameInterval);
        int beforeStall = harness.Encoder.SubmittedVersions.Count;
        harness.Encoder.GrantCredits(10);

        long resumeTicks = Stopwatch.Frequency * 10;
        for (int frame = 0; frame < 10; frame++)
            harness.StepAt(resumeTicks);

        Assert.Equal(
            beforeStall + 1,
            harness.Encoder.SubmittedVersions.Count);
        harness.StepAt(resumeTicks + frameInterval - 1);
        Assert.Equal(
            beforeStall + 1,
            harness.Encoder.SubmittedVersions.Count);
        harness.StepAt(resumeTicks + frameInterval);
        Assert.Equal(
            beforeStall + 2,
            harness.Encoder.SubmittedVersions.Count);

        IReadOnlyList<long> timestamps =
            harness.Encoder.SubmittedPresentationTimes100ns;
        Assert.True(
            timestamps[2] - timestamps[1] > TimeSpan.FromSeconds(9).Ticks);
        Assert.Equal(
            harness.Encoder.SubmittedVersions.Count,
            harness.Encoder.SubmittedVersions.Distinct().Count());
    }

    [Fact]
    public void StaticCaptureSubmitsPeriodicKeepalivesWithRealTimestamps()
    {
        var harness = new PullLoopHarness(
            poolCapacity: 16,
            creditEverySteps: 1,
            holdFrames: 0,
            outputLagFrames: 0);
        long keepaliveTicks = Stopwatch.Frequency *
            HardwareEncoderPullLoop.KeepaliveIntervalMilliseconds / 1000;

        harness.StepAt(0);
        for (int keepalive = 1; keepalive <= 8; keepalive++)
        {
            harness.StepWithoutCaptureAt(keepalive * keepaliveTicks - 1);
            Assert.Equal(keepalive, harness.Encoder.SubmittedVersions.Count);

            harness.StepWithoutCaptureAt(keepalive * keepaliveTicks);
            Assert.Equal(keepalive + 1, harness.Encoder.SubmittedVersions.Count);
        }

        Assert.Equal(8, harness.Loop.DuplicateFrames);
        Assert.Single(harness.Encoder.SubmittedVersions.Distinct());
        Assert.Equal(
            Enumerable.Range(0, 9)
                .Select(index => checked(
                    index * keepaliveTicks * TimeSpan.TicksPerSecond /
                    Stopwatch.Frequency)),
            harness.Encoder.SubmittedPresentationTimes100ns);
        Assert.True(harness.Encoder.SubmittedPresentationTimes100ns
            .Zip(harness.Encoder.SubmittedPresentationTimes100ns.Skip(1))
            .All(pair => pair.First < pair.Second));
    }

    [Fact]
    public void KeepaliveCannotExceedTheSessionRateCap()
    {
        const int sessionFramesPerSecond = 2;
        var harness = new PullLoopHarness(
            poolCapacity: 16,
            creditEverySteps: 1,
            holdFrames: 0,
            outputLagFrames: 0,
            framesPerSecond: sessionFramesPerSecond);
        long stepTicks = Stopwatch.Frequency / 100;

        harness.StepAt(0);
        for (int step = 1; step <= 500; step++)
            harness.StepWithoutCaptureAt(step * stepTicks);

        Assert.Equal(11, harness.Encoder.SubmittedVersions.Count);
        long minimumInterval100ns = TimeSpan.TicksPerSecond /
            sessionFramesPerSecond;
        Assert.All(
            harness.Encoder.SubmittedPresentationTimes100ns
                .Zip(harness.Encoder.SubmittedPresentationTimes100ns.Skip(1)),
            pair => Assert.True(
                pair.Second - pair.First >= minimumInterval100ns));
    }

    [Fact]
    public void StaticCaptureCreditFamineStillIdentifiesEncoderFailure()
    {
        var harness = new PullLoopHarness(
            poolCapacity: 16,
            creditEverySteps: 1,
            holdFrames: 0,
            outputLagFrames: 0);
        long keepaliveTicks = Stopwatch.Frequency *
            HardwareEncoderPullLoop.KeepaliveIntervalMilliseconds / 1000;
        long famineTicks = Stopwatch.Frequency *
            EncoderCreditFaminePolicy.DefaultFrameIntervals /
            FieldFramesPerSecond;

        harness.StepAt(0);
        harness.Encoder.CreditFamine = true;
        Assert.Null(harness.StepWithoutCaptureAt(keepaliveTicks).StallReason);

        HardwarePullStepResult result = harness.StepWithoutCaptureAt(
            keepaliveTicks + famineTicks);

        Assert.Contains("no NeedInput", result.StallReason);
    }

    private sealed class PullLoopHarness
    {
        private long _step;
        private long _captureVersion;

        internal PullLoopHarness(
            int poolCapacity,
            int creditEverySteps,
            int holdFrames,
            int outputLagFrames,
            bool reorderOutput = false,
            bool advanceCaptureOnWait = false,
            int framesPerSecond = FieldFramesPerSecond)
        {
            Capture = new FakeCapture(advanceCaptureOnWait);
            Converter = new FakeConverter(poolCapacity);
            Encoder = new FakeEncoder(
                creditEverySteps,
                holdFrames,
                outputLagFrames,
                reorderOutput);
            Output = new FakeOutput();
            Loop = new HardwareEncoderPullLoop(
                Capture,
                Converter,
                Encoder,
                Output,
                framesPerSecond,
                Stopwatch.Frequency,
                _ => EpochCount++);
        }

        internal FakeCapture Capture { get; }
        internal FakeConverter Converter { get; }
        internal FakeEncoder Encoder { get; }
        internal FakeOutput Output { get; }
        internal HardwareEncoderPullLoop Loop { get; }
        internal int EpochCount { get; private set; }

        internal void Run(int steps)
        {
            for (int i = 0; i < steps; i++)
                Step();
        }

        internal HardwarePullStepResult Step()
        {
            long step = ++_step;
            return StepAt(
                step * Stopwatch.Frequency / FieldFramesPerSecond);
        }

        internal HardwarePullStepResult StepAt(long timestampTicks)
        {
            Capture.Publish(++_captureVersion);
            return Loop.Step(timestampTicks);
        }

        internal HardwarePullStepResult StepWithoutCaptureAt(long timestampTicks) =>
            Loop.Step(timestampTicks);

        internal long NextTimestamp()
        {
            long step = Interlocked.Increment(ref _step);
            Capture.Publish(Interlocked.Increment(ref _captureVersion));
            return step * Stopwatch.Frequency / FieldFramesPerSecond;
        }
    }

    private sealed class FakeCapture(bool advanceOnWait) : IHardwareEncodeCapture
    {
        private long _frameVersion;

        public long FrameVersion => Interlocked.Read(ref _frameVersion);
        public long FramesArrived => FrameVersion;

        internal void Publish(long version) =>
            Interlocked.Exchange(ref _frameVersion, version);

        public bool WaitForFreshFrame(long sinceVersion, int timeoutMs)
        {
            if (advanceOnWait)
                Interlocked.Increment(ref _frameVersion);
            return FrameVersion > sinceVersion;
        }
    }

    private sealed class FakeFrame(long captureVersion, Action onReturn) : IHardwareEncodeFrame
    {
        private int _returned;

        public long CaptureVersion { get; } = captureVersion;

        public void Return(FrameLeaseReturnReason reason)
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                onReturn();
        }
    }

    private sealed class FakeConverter : IHardwareEncodeConverter
    {
        private readonly int _capacity;
        private int _outstanding;
        private int _maximumOutstanding;

        internal FakeConverter(int capacity)
        {
            _capacity = capacity;
        }

        public FrameLeaseAccounting PoolAccounting => new(
            _capacity,
            _capacity - Volatile.Read(ref _outstanding),
            Volatile.Read(ref _outstanding),
            SuccessCount,
            new ReadOnlyDictionary<FrameLeaseReturnReason, long>(
                Enum.GetValues<FrameLeaseReturnReason>()
                    .ToDictionary(reason => reason, _ => 0L)));

        internal int SuccessCount { get; private set; }
        internal int ExhaustionCount { get; private set; }
        internal int MaximumOutstanding => Volatile.Read(ref _maximumOutstanding);

        public bool TryConvert(
            long captureVersion,
            out IHardwareEncodeFrame? frame,
            out string failureReason)
        {
            int outstanding = Volatile.Read(ref _outstanding);
            if (outstanding >= _capacity)
            {
                ExhaustionCount++;
                frame = null;
                failureReason = "pool exhausted";
                return false;
            }

            int current = Interlocked.Increment(ref _outstanding);
            UpdateMaximum(current);
            SuccessCount++;
            frame = new FakeFrame(
                captureVersion,
                () => Interlocked.Decrement(ref _outstanding));
            failureReason = "none";
            return true;
        }

        private void UpdateMaximum(int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maximumOutstanding);
                if (current >= value) return;
            }
            while (Interlocked.CompareExchange(
                ref _maximumOutstanding,
                value,
                current) != current);
        }
    }

    private sealed class FakeEncoder : IHardwarePullEncoder
    {
        private readonly int _creditEverySteps;
        private readonly int _holdFrames;
        private readonly int _outputLagFrames;
        private readonly bool _reorderOutput;
        private readonly List<HeldFrame> _held = [];
        private readonly List<PendingOutput> _outputs = [];
        private readonly List<long> _submittedVersions = [];
        private readonly List<long> _submittedPresentationTimes100ns = [];
        private long _lastPolledTicks = long.MinValue;
        private long _step = -1;
        private int _inputCredits;
        private long _lastNeedInput;
        private long _lastHaveOutput;
        private long _submittedCount;

        internal FakeEncoder(
            int creditEverySteps,
            int holdFrames,
            int outputLagFrames,
            bool reorderOutput)
        {
            _creditEverySteps = creditEverySteps;
            _holdFrames = holdFrames;
            _outputLagFrames = outputLagFrames;
            _reorderOutput = reorderOutput;
        }

        public long SubmittedFrameCount => Interlocked.Read(ref _submittedCount);
        internal IReadOnlyList<long> SubmittedVersions => _submittedVersions;
        internal IReadOnlyList<long> SubmittedPresentationTimes100ns =>
            _submittedPresentationTimes100ns;
        internal bool CreditFamine { get; set; }

        internal void GrantCredits(int credits) => _inputCredits += credits;

        public HardwarePullEncoderProgress GetProgressSnapshot() => new(
            _held.Count,
            _inputCredits,
            _lastNeedInput,
            _lastHaveOutput);

        public IReadOnlyList<EncodedAccessUnit> Poll(long nowTicks)
        {
            if (nowTicks != _lastPolledTicks)
            {
                _lastPolledTicks = nowTicks;
                _step++;
                ReleaseHeldFrames();
                if (!CreditFamine && _step % _creditEverySteps == 0)
                {
                    _inputCredits++;
                    _lastNeedInput = nowTicks;
                }
            }

            List<PendingOutput> ready = _outputs
                .Where(output => output.DueStep <= _step)
                .ToList();
            _outputs.RemoveAll(output => output.DueStep <= _step);
            if (_reorderOutput && ready.Count > 1)
                ready.Reverse();
            if (ready.Count > 0) _lastHaveOutput = nowTicks;
            return ready
                .Select(output => new EncodedAccessUnit(
                    BitConverter.GetBytes(output.CaptureVersion),
                    IsKeyFrame: false))
                .ToArray();
        }

        public bool TrySubmit(
            IHardwareEncodeFrame frame,
            long presentationTime100ns,
            long duration100ns)
        {
            if (_inputCredits == 0) return false;
            _inputCredits--;
            _submittedVersions.Add(frame.CaptureVersion);
            _submittedPresentationTimes100ns.Add(presentationTime100ns);
            Interlocked.Increment(ref _submittedCount);
            _held.Add(new HeldFrame(frame, _step + _holdFrames));
            _outputs.Add(new PendingOutput(
                frame.CaptureVersion,
                _step + _outputLagFrames +
                    (_reorderOutput && frame.CaptureVersion % 2 != 0 ? 1 : 0)));
            return true;
        }

        private void ReleaseHeldFrames()
        {
            foreach (HeldFrame held in _held.Where(frame => frame.DueStep <= _step).ToArray())
            {
                held.Frame.Return(FrameLeaseReturnReason.InputReleased);
                _held.Remove(held);
            }
        }

        private readonly record struct HeldFrame(
            IHardwareEncodeFrame Frame,
            long DueStep);

        private readonly record struct PendingOutput(
            long CaptureVersion,
            long DueStep);
    }

    private sealed class FakeOutput : IEncodedAccessUnitSink
    {
        public int AccessUnitCount { get; private set; }
        internal List<long> Versions { get; } = [];

        public void Write(IReadOnlyList<EncodedAccessUnit> accessUnits)
        {
            AccessUnitCount += accessUnits.Count;
            Versions.AddRange(accessUnits.Select(unit =>
                BitConverter.ToInt64(unit.Data.Span)));
        }
    }
}
