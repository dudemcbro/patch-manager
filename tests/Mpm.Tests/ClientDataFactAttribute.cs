namespace Mpm.Tests;

/// <summary>
/// Runs only when MPM_CLIENT_DATA points at a 3.3.5a client's Data directory (read-only use).
/// </summary>
public sealed class ClientDataFactAttribute : FactAttribute
{
    public static string? DataDir => Environment.GetEnvironmentVariable("MPM_CLIENT_DATA") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public ClientDataFactAttribute()
    {
        if (DataDir is null)
        {
            Skip = "Set MPM_CLIENT_DATA to a 3.3.5a client Data directory to run";
        }
    }
}
