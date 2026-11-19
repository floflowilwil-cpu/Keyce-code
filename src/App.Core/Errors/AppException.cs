namespace App.Core.Errors;

public enum ErrorCode
{
    Unknown,
    NotFound,
    AccessDenied,
    AlreadyExists,
    InvalidArgument,
    Conflict,
    Unavailable,
    Timeout,
    Cancelled
}

/// <summary>Erreur commune à tous les services. Les contrats lèvent cette exception.</summary>
public class AppException : Exception
{
    public ErrorCode Code { get; }

    public AppException(ErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}
