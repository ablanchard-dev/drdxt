namespace DrDxt.Server.Common;

// Self-contained Result type (mirrors DrDxt.Core.Result by shape) so the server project stays
// FULLY decoupled from the client: the client never references the server, and the server never
// drags client assemblies into its build. Every expected failure flows through Result — no
// exception cascades across the signing boundary.
public readonly record struct Error(string Code, string Message);

public class Result
{
    public bool IsOk { get; init; }
    public Error? Error { get; init; }

    public static Result Ok() => new() { IsOk = true };
    public static Result Fail(string code, string msg) => new() { IsOk = false, Error = new(code, msg) };
}

public sealed class Result<T> : Result
{
    public T? Value { get; init; }

    public static Result<T> Ok(T value) => new() { IsOk = true, Value = value };
    public static new Result<T> Fail(string code, string msg) => new() { IsOk = false, Error = new(code, msg) };
}
