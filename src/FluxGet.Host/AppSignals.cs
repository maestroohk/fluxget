namespace FluxGet.Host;

public sealed class AppSignals
{
    public event Action<string?>? ActivateRequested;

    public void Activate(string? argument) => ActivateRequested?.Invoke(argument);
}

public sealed record ActivateBody(string? Argument);
