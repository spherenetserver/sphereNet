namespace SphereNet.Panel;

/// <summary>Base exception for failures between the panel host and game server.</summary>
public class PanelBackendException : Exception
{
    public PanelBackendException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class PanelBackendUnavailableException : PanelBackendException
{
    public PanelBackendUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class PanelBackendTimeoutException : PanelBackendException
{
    public PanelBackendTimeoutException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class PanelBackendOperationException : PanelBackendException
{
    public string? Code { get; }

    public PanelBackendOperationException(string message, string? code = null)
        : base(message)
    {
        Code = code;
    }
}
