using Acropolis.Platform.Application;

namespace Acropolis.Platform.UnitTests;

public sealed class PlatformTests
{
    [Theory]
    [InlineData(true, "ok")]
    [InlineData(false, "not_ready")]
    public async Task ReadinessReportsTheObservedState(bool current, string status)
    {
        var service = new ReadinessService(new Reader(_ => Task.FromResult(current)), TimeSpan.FromSeconds(1));
        var result = await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(current, result.IsReady);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task DatabaseExceptionsAreSanitized()
    {
        var service = new ReadinessService(
            new Reader(_ => Task.FromException<bool>(new InvalidOperationException("private connection details"))),
            TimeSpan.FromSeconds(1));
        var result = await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.False(result.IsReady);
        Assert.Equal("not_ready", result.Status);
    }

    [Fact]
    public async Task SlowProbeIsBoundedAndCancelled()
    {
        var probeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ReadinessService(new Reader(async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }
            finally
            {
                probeCancelled.TrySetResult();
            }
        }), TimeSpan.FromMilliseconds(50));

        Assert.False((await service.CheckAsync(TestContext.Current.CancellationToken)).IsReady);
        await probeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RequestCancellationIsNotReportedAsDatabaseFailure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = new ReadinessService(new Reader(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancelled.Token));
    }

    [Fact]
    public async Task ReadinessCanRecoverWithoutRestartingTheService()
    {
        var ready = false;
        var service = new ReadinessService(new Reader(_ => Task.FromResult(ready)), TimeSpan.FromSeconds(1));
        Assert.False((await service.CheckAsync(TestContext.Current.CancellationToken)).IsReady);
        ready = true;
        Assert.True((await service.CheckAsync(TestContext.Current.CancellationToken)).IsReady);
    }

    [Fact]
    public async Task InvalidTimeoutIsRejected()
    {
        var service = new ReadinessService(new Reader(_ => Task.FromResult(true)), TimeSpan.Zero);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CheckAsync(TestContext.Current.CancellationToken));
    }

    private sealed class Reader(Func<CancellationToken, Task<bool>> read) : IPlatformStateReader
    {
        public Task<bool> IsCurrentAsync(CancellationToken cancellationToken) => read(cancellationToken);
    }
}
