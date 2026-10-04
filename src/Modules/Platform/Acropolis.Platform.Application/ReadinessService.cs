namespace Acropolis.Platform.Application;

public interface IPlatformStateReader
{
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

public sealed record ReadinessResult(bool IsReady)
{
    public string Status => IsReady ? "ok" : "not_ready";
}

public sealed class ReadinessService(IPlatformStateReader stateReader, TimeSpan timeout)
{
    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var ready = await stateReader.IsCurrentAsync(deadline.Token)
                .WaitAsync(timeout, cancellationToken);
            return new ReadinessResult(ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await deadline.CancelAsync();
            return new ReadinessResult(false);
        }
    }
}
