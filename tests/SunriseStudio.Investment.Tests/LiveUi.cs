using System.Diagnostics.CodeAnalysis;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** One step of a UI check: a state to wait for, held for some frames so text and icons settle before the next step runs. */
internal readonly record struct UiWait(string Label, Func<bool> Ready, int Frames = LiveUi.Settle);

/**
 * One real Studio window on the installation for one UI check. The script yields what to wait for, and the code after each
 * yield runs once that state has held for its frames, so a check reads top to bottom.
 */
internal sealed class LiveUi
{
    /** Frames a state holds before the next step, so text and icons settle. */
    public const int Settle = 5;
    /** Frames a popup's dimmed background takes to fade in before a capture. */
    public const int PopupSettle = 30;
    /** Frames an icon count holds before an icon capture, so the streamed icons have arrived. */
    public const int IconSteady = 10;
    public const ulong Account = 0x4000000000000002, Character = 0x4000000000000003, Content = 0x1F2E3D4C5B6A7988;
    public static readonly LiveInvestmentContext Context = new(Account, Character, Content);
    private readonly string? captures;
    private Size? wide;
    private int baseHeight;

    private LiveUi(StudioForm form, string? captures) { Form = form; this.captures = captures; }

    public StudioForm Form { get; }
    public StudioWorkspace Work => Form.Workspace;
    public LiveInvestmentSession Live => Work.LiveInvestment;
    public LiveTab Tab => Form.LiveTab;
    public LiveInvestmentSection Section => Tab.Sections.OfType<LiveInvestmentSection>().Single();
    public string Shown => Form.ShownPage;
    public StudioLiveLink Link => Form.LiveLink();

    /** Runs the script in a window of its own on its own thread; the check fails with the state it last waited for. */
    public static void Run(string name, string install, string temporary, string? captures, string? keys,
        Func<LiveUi, InvestmentCatalog, InvestmentTables, IEnumerable<UiWait>> script)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Frames(name, install, temporary, captures, keys, script); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException($"The {name} UI check failed.", failure);
    }

    private static void Frames(string name, string install, string temporary, string? captures, string? keys,
        Func<LiveUi, InvestmentCatalog, InvestmentTables, IEnumerable<UiWait>> script)
    {
        var project = StudioProject.Create(Path.Combine(temporary, "ui-" + name), name);
        string settings = Path.Combine(temporary, $"ui-{name}-settings.json");
        new StudioSettings { DiskCacheEnabled = false }.Save(settings);
        using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Warp: true, Smoke: true, Frames: 20000,
            WaitForNames: true, SettingsPath: settings, KeySource: keys));
        var ui = new LiveUi(form, captures);
        IEnumerator<UiWait>? steps = null;
        UiWait? waiting = null;
        InvestmentItem? start = null;
        int held = 0;
        bool done = false;
        void Advance()
        {
            held = 0;
            if (steps!.MoveNext()) { waiting = steps.Current; return; }
            done = true; form.Close();
        }
        form.FrameRendered += () =>
        {
            var work = form.Workspace;
            if (work.Error.Length != 0) throw new InvalidDataException(work.Error);
            if (done || !Catalog(form, out var catalog)) return;
            // Every script starts on an item page, the stack's, as a user arrives from the browser.
            if (start is null) { ui.Open(start = Picks.Stack(catalog)); return; }
            if (steps is null && !ui.ItemShown(start)) return;
            if (steps is null) { steps = script(ui, catalog, catalog.Tables()).GetEnumerator(); Advance(); return; }
            if (!waiting!.Value.Ready()) { held = 0; return; }
            if (++held >= waiting.Value.Frames) Advance();
        };
        Application.Run(form);
        string step = waiting?.Label ?? "the installation";
        if (form.Failure is { } error) throw new InvalidDataException(done ? error : $"{error} It was waiting for {step}.");
        if (!done) throw new InvalidDataException($"The {name} UI check stopped waiting for {step}.");
    }

    /**
     * The installed investment catalog with its tables, once the editor is idle; false until then. The checks open no item at
     * startup, so this reads the catalog for them.
     */
    public static bool Catalog(StudioForm form, [NotNullWhen(true)] out InvestmentCatalog? catalog)
    {
        var work = form.Workspace;
        catalog = null;
        if (!form.EditorReady || work.IsBusy) return false;
        if (work.Investment is not { } read) { work.Run(work.LoadInvestment); return false; }
        if (!read.TablesLoaded) { work.Run(work.LoadInvestmentTables); return false; }
        catalog = read;
        return true;
    }

    /** A wait that holds once the cache's icon count has reached least and stopped growing. */
    public static UiWait Icons(string label, Func<bool> ready, InvestmentIconCache cache, int least = 1)
    {
        int last = -1, steady = 0;
        return new(label, () =>
        {
            if (cache.Uploaded != last) { last = cache.Uploaded; steady = 0; }
            return ready() && last >= least && ++steady >= IconSteady;
        });
    }

    public void Capture(string name)
    {
        if (captures is null) return;
        Directory.CreateDirectory(captures);
        Form.Renderer!.CapturePng(Path.Combine(captures, name));
    }

    /** Live on an Investment subtab with a row chosen, as history and Open in Live show it. */
    public void ShowLive(string view, string? row) { Form.ShowAssetPage("Live"); Tab.Show("investment", view, row); }
    public bool LiveShown(string view) => Shown == "live:investment:" + view;

    /** An item on one of its pages, as the browser and the pages' links open it. */
    public void Open(InvestmentItem item, string page = InvestmentPage.PageId) { Work.Open(item.Tag); Form.ShowAssetPage(page); }

    /** The page draws this item's detail. */
    public bool ItemShown(InvestmentItem item, string page = InvestmentPage.PageId) => Shown == "asset:" + page && Work.InvestmentDetailCurrent
        && Work.SelectedInvestmentItem?.Item.Tag == item.Tag;

    /** The window at its narrowest, the smallest room the pages get, or back at its size. */
    public void Narrowest(bool on)
    {
        wide ??= Form.Size;
        Form.Size = on ? wide.Value with { Width = Form.MinimumSize.Width } : wide.Value;
    }

    /** The window taller by this many pixels at UI scale 1, so a page's lower rows show without scrolling; 0 restores it. */
    public void Taller(int room)
    {
        if (baseHeight == 0) baseHeight = Form.ClientSize.Height;
        Form.ClientSize = new(Form.ClientSize.Width, baseHeight + (int)Math.Ceiling(room * Form.UiScale));
    }

    /** Attaches the scripted game as the Live tab's Attach button does; the world view is refused. */
    public void Attach(ILiveInvestmentLink link, Func<bool>? exited = null, Action? worldAsked = null) =>
        ScriptedGame.Attach(Form, link, exited ?? (() => false), worldAsked);

    public static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

/** The installed items the UI checks show, chosen by rule so the captures follow the installation. */
internal static class Picks
{
    /** A stackable currency, which the character holds as a profile row. */
    public static InvestmentItem Stack(InvestmentCatalog catalog) =>
        catalog.Items.First(item => item.Category == InvestmentCategory.Currencies && item.MaximumStack > 1 && !item.Instanced);

    /** Two of the first rows of the Weapons list, so their held marks show without scrolling. */
    public static InvestmentItem[] Weapons(StudioWorkspace work, InvestmentCatalog catalog) =>
        [.. work.CategoryRows[InvestmentItemKinds.Weapons].Take(6).Select(row => catalog.FindItem(row.Entry.TagHash)).OfType<InvestmentItem>().Take(2)];
}

/**
 * The scripted game's account, built from the installation by each check: Hello, the inventory that holds the stack, the
 * weapons (the first equipped) and the held pursuits in the installed buckets, the pass at a fixed rank, saved rows, and
 * receipts that publish at once unless a kind is refused or the next grant stops part way.
 */
internal sealed class ScriptedAccount
{
    public const ulong WeaponInstance = 0x4000000000000400;
    // The scripted character has reached this rank and claimed the rows below ClaimedBelow.
    public const int Rank = 5, ClaimedBelow = 3;
    private readonly InvestmentCatalog catalog;
    private readonly InvestmentTables tables;
    // Operations whose published receipts carry a reason, and those the game refused with their label, by id.
    private readonly Dictionary<ulong, string> reasons = [], refused = [];

    public ScriptedAccount(InvestmentCatalog catalog, InvestmentTables tables)
    {
        this.catalog = catalog;
        this.tables = tables;
        Game.Answer = Answer;
    }

    public FakeInvestmentLink Game { get; } = new();
    public InvestmentItem? Stack { get; init; }
    public InvestmentItem[] Weapons { get; init; } = [];
    /** Pursuit items held as character items, by instance. */
    public (ulong Instance, InvestmentItem Item)[] HeldItems { get; set; } = [];
    /** Character stacks, each of an item in a bucket with a quantity; their serials count up from StackSerial. */
    public (InvestmentItem Item, byte Bucket, int Quantity)[] Stacks { get; init; } = [];
    public const int StackSerial = 10;
    /** The pursuit progress the game reports. */
    public IReadOnlyList<LiveHeldPursuit> Pursuits { get; set; } = [];
    /** False for a Sunrise build that reports the inventory but not pursuit progress. */
    public bool PursuitProgress { get; set; } = true;
    public InvestmentProgression? Ladder { get; set; }
    /** True while the pass the game reports is one row short, so no installed progression matches it. */
    public bool OtherPass { get; set; }
    /** The chain row the open quest step names and its value; every other saved row holds UnlockValue. */
    public (LiveUnlockSource Source, int Value)? Chain { get; set; }
    /** The label the game refuses an operation kind with, as "objective"; others publish. */
    public Dictionary<string, string> Refusals { get; } = [];
    /** The reason the next grant publishes with, as when it stopped part way; empty publishes it whole. */
    public string NextGrantReason { get; set; } = "";

    /** The scripted game's saved rows: every other flag set, values from the row index, and the chain value the open step's. */
    public int UnlockValue(LiveUnlockSource source) =>
        Chain is { } named && named.Source == source ? named.Value : source.Kind == LiveUnlockKind.Flag ? source.Index % 2 : source.Index * 3;

    private object? Answer(string name)
    {
        string kind = name.Split(' ')[0];
        ulong id = name.Contains(' ') ? Convert.ToUInt64(name.Split(' ')[1], 16) : 0;
        var context = LiveUi.Context;
        LiveInvestmentReceipt Receipt(LiveInvestmentOpcode opcode) => refused.TryGetValue(id, out string? label)
            ? new(id, opcode, LiveReceiptState.Refused, label, LiveInvestmentWait.None, 3, 1, 100, 200, context)
            : new(id, opcode, LiveReceiptState.Published, reasons.GetValueOrDefault(id, ""), LiveInvestmentWait.None, 3, 1, 100, 200, context);
        LiveCharacterItem Item(ulong instance, InvestmentItem item, bool equipped) => new(instance, item.Hash, 1, 1, 1600, 0, item.Bucket,
            equipped ? (byte)0 : LiveInvestmentProtocol.Unequipped, LiveSocketPolicy.NativeDefaults, [], true);
        if (Refusals.TryGetValue(kind, out string? refusal)) refused[id] = refusal;
        else if (kind == "grant" && NextGrantReason.Length != 0) { reasons[id] = NextGrantReason; NextGrantReason = ""; }
        var sent = Game.Sent.LastOrDefault(row => row.Operation.Id == id);
        return kind switch
        {
            "hello" => ScriptedGame.Hello(context, capabilities: PursuitProgress ? null : ScriptedGame.Capabilities(LiveInvestmentOpcode.PursuitSnapshot)),
            // The game reports every installed bucket; the equipped weapon's bucket pairs with the slot its copy is equipped in.
            "inventory" => new LiveInventorySnapshot(context, 1,
                [.. tables.Buckets.Select(bucket => new LiveBucket(bucket.Id, bucket.ArraySelector, (ushort)bucket.FirstSlot, (ushort)bucket.SlotCount,
                    Weapons.Length != 0 && bucket.Id == Weapons[0].Bucket ? (sbyte)0 : LiveInvestmentProtocol.NoEquipmentSlot))],
                // The stack is a plain profile row, which has no instance (studio_investment_wire.h SetQuantityRequest).
                Stack is null ? [] : [new LiveProfileRow(0, Stack.Hash, 25, 4, Stack.Bucket, true)],
                [.. Weapons.Select((item, index) => Item(WeaponInstance + (ulong)index, item, index == 0)),
                    .. HeldItems.Select(row => Item(row.Instance, row.Item, false))],
                [.. Stacks.Select((row, index) => new LiveCharacterStack(row.Item.Hash, row.Quantity, StackSerial + index, row.Bucket))]),
            "pass" => new LiveSeasonPassSnapshot(context, 1, (int)Ladder!.Threshold(Rank), Rank,
                [.. Ladder.Rewards.Take(Ladder.Rewards.Count - (OtherPass ? 1 : 0)).Select(PassRow)]),
            "pursuits" => new LivePursuitSnapshot(context, 1, Pursuits),
            "unlocks" => new LiveUnlockSnapshot(context, 1, [.. Game.Unlocks[^1].Select(source => new LiveUnlockValue(source, true, UnlockValue(source)))]),
            "grant" or "quantity" or "experience" or "claim" or "reset" or "objective" or "complete" or "read" or "ack" =>
                Receipt(sent is null ? LiveInvestmentOpcode.None : Opcode(sent.Kind)),
            _ => null
        };
    }

    private static LiveInvestmentOpcode Opcode(string kind) => kind switch
    {
        "grant" => LiveInvestmentOpcode.GrantItem, "quantity" => LiveInvestmentOpcode.SetQuantity,
        "experience" => LiveInvestmentOpcode.SetSeasonalExperience, "claim" => LiveInvestmentOpcode.ClaimSeasonPassReward,
        "reset" => LiveInvestmentOpcode.ResetSeasonPassClaim, "objective" => LiveInvestmentOpcode.SetPursuitObjective,
        _ => LiveInvestmentOpcode.CompletePursuitObjectives
    };

    private LiveSeasonPassRow PassRow(InvestmentSeasonPassRow row)
    {
        uint hash = row.Error.Length == 0 && row.ItemIndex < catalog.Items.Count ? catalog.Items[(int)row.ItemIndex].Hash : 0;
        var flags = hash == 0 ? LiveSeasonPassRowFlags.None : LiveSeasonPassRowFlags.Available | LiveSeasonPassRowFlags.ConditionKnown
            | LiveSeasonPassRowFlags.ConditionMet | (row.ClaimSlot == 0 ? LiveSeasonPassRowFlags.Automatic : 0)
            | (row.Rank <= Rank ? LiveSeasonPassRowFlags.RankMet : 0)
            | (row.Rank < ClaimedBelow && row.ClaimSlot != 0 ? LiveSeasonPassRowFlags.Claimed : 0);
        return new((ushort)row.Index, hash, hash == 0 ? 0 : row.Quantity, (byte)Math.Min(row.Rank, byte.MaxValue), flags);
    }
}
