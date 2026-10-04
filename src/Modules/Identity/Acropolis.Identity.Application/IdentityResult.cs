namespace Acropolis.Identity.Application;

public sealed record IdentityResult<T>(T? Value, string? Error = null, int Status = 200, Dictionary<string, string[]>? FieldErrors = null)
{
    public bool Succeeded => Error is null;
    public static IdentityResult<T> Fail(string error, int status = 400, Dictionary<string, string[]>? fields = null) => new(default, error, status, fields);
}
