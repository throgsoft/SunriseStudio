using SunriseStudio.Editor;
using SunriseStudio.Live;

/**
 * Attaches a form's Live tab as its Attach button does, to this process standing in for the game: the investment session
 * gets a scripted link, the exit probe is scripted, and the world view is refused, so no check maps a game's world.
 */
internal static class ScriptedGame
{
    /** What LiveConnection.Attach says for this process, whose executable is not the listed one. */
    public const string WorldRefusal = "The selected game process changed. Refresh the process list.";
    /** The game process instance every scripted hello names, so receipts stay readable across hellos. */
    public const ulong Started = 0x01DB1234ABCD5678;
    /** A signed-in client owns the account, so changes can show. */
    public const LiveInvestmentPublication Owned = LiveInvestmentPublication.PeerPresent | LiveInvestmentPublication.PeerOwnsAccount;
    /** Every catalog the game reads. */
    public static readonly LiveInvestmentReadiness Ready =
        Enum.GetValues<LiveInvestmentReadiness>().Aggregate(LiveInvestmentReadiness.None, (all, flag) => all | flag);

    /** Every command the protocol defines, but those named. */
    public static ulong Capabilities(params LiveInvestmentOpcode[] without) => Enum.GetValues<LiveInvestmentOpcode>()
        .Where(opcode => opcode != LiveInvestmentOpcode.None && !without.Contains(opcode))
        .Aggregate(0UL, (bits, opcode) => bits | LiveInvestmentProtocol.Capability(opcode));

    /**
     * A game's hello for the context's character, selected and of this class; a null class selects no character. By default
     * the game supports every command, has read every catalog and a signed-in client owns the account.
     */
    public static LiveInvestmentHello Hello(LiveInvestmentContext context, byte? characterClass = 2, ulong? capabilities = null,
        LiveInvestmentReadiness? readiness = null, LiveInvestmentPublication publication = Owned, ulong started = Started) =>
        new(context, readiness ?? Ready, capabilities ?? Capabilities(), started,
            characterClass is { } shown ? [new LiveInvestmentCharacter(context.CharacterSoid, shown, true)] : [], publication, 1);

    public static LiveTab Tab(StudioForm form) => form.LiveTab;
    public static LivePage World(StudioForm form) => form.LivePage;
    public static LiveInvestmentSection Investment(StudioForm form) => form.LiveTab.Sections.OfType<LiveInvestmentSection>().Single();

    /** Forgets which game views were drawn, so Asked tells whether a later draw asked the game for one. */
    public static void ForgetAsks(LiveInvestmentSession live) => live.Inventory.WantedUntil = live.SeasonPass.WantedUntil = live.Pursuits.WantedUntil = 0;

    /** Whether anything drawn since ForgetAsks asked the game for its inventory, season pass or pursuit progress. */
    public static bool Asked(LiveInvestmentSession live) =>
        live.Inventory.WantedUntil != 0 || live.SeasonPass.WantedUntil != 0 || live.Pursuits.WantedUntil != 0;

    /** exited is the game's exit as the Live tab reads it; worldAsked runs each time World tries to connect. */
    public static void Attach(StudioForm form, ILiveInvestmentLink link, Func<bool> exited, Action? worldAsked = null)
    {
        form.Workspace.LiveInvestment.OpenLink = _ => link;
        World(form).AttachWorld = _ => { worldAsked?.Invoke(); throw new InvalidOperationException(WorldRefusal); };
        var tab = Tab(form);
        tab.WatchExit = _ => exited;
        tab.Attach(new LiveProcess(Environment.ProcessId, ""));
    }
}
