using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acropolis.Api.Tests;

public sealed class ConsumptionRetentionWorkerTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task TestingDefaultsToNoScopesOrDatabaseEvenWhenRecordingIsEnabled()
    {
        var retention = new RetentionSpy(); using var services = Services(retention); var clock = new RetentionClock();
        using var worker = Worker(services, retention, clock, "Testing", recording: true);
        await worker.StartAsync(Token);
        Assert.True(worker.ExecuteTask?.IsCompletedSuccessfully); Assert.Equal(0, retention.Calls); Assert.Equal(0, clock.CreatedTimers);
        await worker.StopAsync(Token);
    }
    [Fact]
    public async Task ProductionRetainsAutomaticCleanupWhenRecordingIsDisabled()
    {
        var retention = new RetentionSpy(); using var services = Services(retention); var clock = new RetentionClock();
        using var worker = Worker(services, retention, clock, "Production", recording: false);
        await worker.StartAsync(Token); await clock.MinuteScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.Equal(1, retention.Calls); Assert.False(worker.ExecuteTask!.IsCompleted);
        await worker.StopAsync(Token); Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }
    [Fact]
    public async Task TestingOptInExercisesTenBatchesAndOneMinuteCadence()
    {
        var retention = new RetentionSpy { Result = new(1000, 0, 0, 0) }; using var services = Services(retention); var clock = new RetentionClock();
        using var worker = Worker(services, retention, clock, "Testing", testingEnabled: true);
        await worker.StartAsync(Token); await clock.MinuteScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3), Token); Assert.Equal(10, retention.Calls);
        clock.Advance(TimeSpan.FromSeconds(59)); Assert.Equal(10, retention.Calls);
        clock.Advance(TimeSpan.FromSeconds(1)); await retention.TwentyCalls.Task.WaitAsync(TimeSpan.FromSeconds(3), Token); Assert.Equal(20, retention.Calls);
        await worker.StopAsync(Token);
    }
    [Fact]
    public async Task ThirtySecondBudgetCancelsAnInFlightBatchAndLogsOnlySanitizedCategory()
    {
        var retention = new RetentionSpy { Block = true }; using var services = Services(retention); var clock = new RetentionClock(); var logs = new CaptureLogger();
        using var worker = Worker(services, retention, clock, "Production", logger: logs);
        await worker.StartAsync(Token); await retention.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        clock.Advance(TimeSpan.FromSeconds(30)); await clock.MinuteScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.Equal(1, retention.Calls); Assert.True(retention.ObservedToken.IsCancellationRequested); Assert.Contains(logs.Messages, value => value.Contains("bounded budget", StringComparison.Ordinal));
        await worker.StopAsync(Token);
    }
    [Fact]
    public async Task StopCancelsAnInFlightBatchWithoutSchedulingAnotherCycle()
    {
        var retention = new RetentionSpy { Block = true }; using var services = Services(retention); var clock = new RetentionClock();
        using var worker = Worker(services, retention, clock, "Production");
        await worker.StartAsync(Token); await retention.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token); await worker.StopAsync(Token);
        Assert.True(retention.ObservedToken.IsCancellationRequested); Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully); Assert.False(clock.MinuteScheduled.Task.IsCompleted); Assert.Equal(1, retention.Calls);
    }
    [Fact]
    public async Task FailureDoesNotExposeExceptionMessageAndNextCycleRemainsAvailable()
    {
        var retention = new RetentionSpy { Fail = true }; using var services = Services(retention); var clock = new RetentionClock(); var logs = new CaptureLogger();
        using var worker = Worker(services, retention, clock, "Production", logger: logs);
        await worker.StartAsync(Token); await clock.MinuteScheduled.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        var warning = Assert.Single(logs.Messages); Assert.Contains("InvalidOperationException", warning); Assert.DoesNotContain("private-account-or-connection", warning);
        retention.Fail = false; clock.Advance(TimeSpan.FromMinutes(1)); await retention.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(3), Token); await worker.StopAsync(Token);
        Assert.Equal(2, retention.Calls);
    }
    private static ServiceProvider Services(RetentionSpy retention) => new ServiceCollection().AddSingleton<IConsumptionRetentionService>(retention).BuildServiceProvider();
    private static ConsumptionRetentionWorker Worker(ServiceProvider services, RetentionSpy retention, RetentionClock clock, string environment, bool recording = false, bool testingEnabled = false, CaptureLogger? logger = null) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), clock, logger ?? new CaptureLogger(), new EnvironmentStub(environment), Options.Create(new ConsumptionRecordingOptions { RecordingEnabled = recording, EnableRetentionInTesting = testingEnabled }));
    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Consumption QA";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class RetentionSpy : IConsumptionRetentionService
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool Block { get; set; }
        public bool Fail { get; set; }
        public ConsumptionPruneResult Result { get; set; } = new(0, 0, 0, 0);
        public CancellationToken ObservedToken { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwentyCalls { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConsumptionPruneResult> PruneAsync(CancellationToken token)
        {
            var count = Interlocked.Increment(ref calls); ObservedToken = token; Entered.TrySetResult(); if (count >= 2) SecondCall.TrySetResult(); if (count >= 20) TwentyCalls.TrySetResult();
            if (Fail) throw new InvalidOperationException("private-account-or-connection");
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result;
        }
    }
    private sealed class CaptureLogger : ILogger<ConsumptionRetentionWorker>
    {
        private readonly List<string> messages = [];
        public string[] Messages { get { lock (messages) return messages.ToArray(); } }
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { lock (messages) messages.Add(formatter(state, exception)); }
    }
    private sealed class RetentionClock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<ClockTimer> timers = []; private TimeSpan now;
        public int CreatedTimers { get { lock (gate) return timers.Count; } }
        public TaskCompletionSource MinuteScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() { lock (gate) return DateTimeOffset.UnixEpoch + now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state); lock (gate) timers.Add(timer); timer.Change(dueTime, period); if (dueTime == TimeSpan.FromMinutes(1)) MinuteScheduled.TrySetResult(); return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            List<ClockTimer> ready;
            lock (gate) { now += elapsed; ready = timers.Where(value => !value.Disposed && value.Due is { } due && due <= now).ToList(); foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : now + timer.Period; }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class ClockTimer(RetentionClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback; public object? State { get; } = state; public TimeSpan? Due { get; set; }
            public TimeSpan Period { get; set; }
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner.gate) { if (Disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime; Period = period; return true; } }
            public void Dispose() { lock (owner.gate) { Disposed = true; Due = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
