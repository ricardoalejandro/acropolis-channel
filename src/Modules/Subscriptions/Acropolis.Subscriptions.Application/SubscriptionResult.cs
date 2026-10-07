namespace Acropolis.Subscriptions.Application;

public sealed record SubscriptionResult<T>(T? Value, string? Error = null, int Status = 200)
{
    public bool Succeeded => Error is null;
    public static SubscriptionResult<T> Ok(T value) => new(value);
    public static SubscriptionResult<T> Fail(string code, int status = 400) => new(default, code, status);
}
