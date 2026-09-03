namespace MarketEye.BacktestTests;

/// <summary>
/// A local copy of `MarketEye.IntegrationTests.DockerGate`, deliberately duplicated rather than
/// referenced across test projects — the two test projects should not depend on each other.
///
/// Container-backed tests start real SQL Server instances, which run emulated at ~2 GB each on
/// Apple Silicon, so they are opt-in rather than part of the default loop.
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
