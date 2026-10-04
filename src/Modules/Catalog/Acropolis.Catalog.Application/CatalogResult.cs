namespace Acropolis.Catalog.Application;

public sealed record CatalogResult<T>(T? Value = default, string? Error = null, int Status = 200, Dictionary<string, string[]>? FieldErrors = null)
{
    public bool Succeeded => Error is null;
    public static CatalogResult<T> Fail(string error, int status, Dictionary<string, string[]>? fields = null) => new(default, error, status, fields);
}
