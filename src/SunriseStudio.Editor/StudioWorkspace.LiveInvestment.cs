namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /** The investment link to a running game, shared by every page and the browser. */
    public LiveInvestmentSession LiveInvestment { get; } = new();

    /** Where a changed tag stands against the connected game, or against a restart when none is connected. */
    public ProjectInstallState InstallStateOfLive(uint tag) => InstallStateOf(tag, LiveInvestment.GameStartedAt);
}
