using Npgsql;

namespace Acropolis.Api;

internal sealed class ReadinessDiagnostics(ILogger<ReadinessDiagnostics> logger)
{
    private static readonly EventId FailureEvent = new(1001, "DatabaseReadinessFailure");

    public void HistoryMismatch() => Record("history_mismatch", "None", "");

    public void Failure(Exception error)
    {
        Exception? database = null;
        Exception? timeout = null;
        string sqlState = "";
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException) database = current;
            if (current is TimeoutException or OperationCanceledException) timeout = current;
            if (current is PostgresException postgres && postgres.SqlState.Length == 5
                && postgres.SqlState.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9'))
                sqlState = postgres.SqlState;
        }
        var relevant = timeout ?? database ?? error;
        var type = relevant switch
        {
            TimeoutException => "TimeoutException",
            OperationCanceledException => "OperationCanceledException",
            PostgresException => "PostgresException",
            NpgsqlException => "NpgsqlException",
            InvalidOperationException => "InvalidOperationException",
            _ => "Exception"
        };
        Record(timeout is not null ? "timeout" : database is not null ? "database_error" : "probe_error", type, sqlState);
    }

    private void Record(string reason, string exceptionType, string sqlState) =>
        logger.LogWarning(FailureEvent, "Database readiness failed: Reason={Reason} ExceptionType={ExceptionType} SqlState={SqlState}",
            reason, exceptionType, sqlState);
}
