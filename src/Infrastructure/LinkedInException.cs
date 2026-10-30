namespace Linkedin.Console.Infrastructure;

/// <summary>Exit codes, doubling as the machine-readable <c>code:</c> field in the error envelope.</summary>
public enum ErrorCode
{
    Ok = 0,
    Error = 1,
    Network = 2,
    AuthRequired = 3,
    NotFound = 4,
    RateLimited = 5,
    InvalidInput = 6
}

public static class ErrorCodes
{
    public static string Name(ErrorCode code) => code switch
    {
        ErrorCode.Ok => "ok",
        ErrorCode.Network => "network",
        ErrorCode.AuthRequired => "auth_required",
        ErrorCode.NotFound => "not_found",
        ErrorCode.RateLimited => "rate_limited",
        ErrorCode.InvalidInput => "invalid_input",
        _ => "error"
    };
}

public class LinkedInException : Exception
{
    public ErrorCode Code { get; }

    /// <summary>Extra context, such as the upstream error text.</summary>
    public string? Detail { get; }

    /// <summary>A literal command the user can run to fix this. Agents surface it verbatim.</summary>
    public string? Remediation { get; }

    public LinkedInException(ErrorCode code, string message, string? detail = null, string? remediation = null)
        : base(message)
    {
        Code = code;
        Detail = detail;
        Remediation = remediation;
    }

    public static LinkedInException Invalid(string message, string? remediation = null) =>
        new(ErrorCode.InvalidInput, message, remediation: remediation);

    public static LinkedInException Auth(string message, string? detail = null, string? remediation = null) =>
        new(ErrorCode.AuthRequired, message, detail, remediation);
}
