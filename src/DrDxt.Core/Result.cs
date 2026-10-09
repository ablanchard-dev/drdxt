namespace DrDxt.Core;

// Pas d'exception en cascade : tout échec attendu passe par Result.
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
