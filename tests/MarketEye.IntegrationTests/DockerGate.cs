namespace MarketEye.IntegrationTests;

/// <summary>
/// Integration tests start real SQL Server containers. On Apple Silicon those run emulated at
/// roughly 2 GB each, so they are opt-in rather than part of the default loop.
///
/// Enable with:  MARKETEYE_INTEGRATION=1 dotnet test
/// </summary>
public static class DockerGate
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("MARKETEYE_INTEGRATION") == "1";

    public const string SkipReason =
        "Container-backed test. Set MARKETEYE_INTEGRATION=1 to run.";
}
