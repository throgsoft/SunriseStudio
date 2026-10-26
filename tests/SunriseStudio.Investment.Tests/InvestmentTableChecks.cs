using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Root-slot readers: a synthetic root with every table, and the counts measured on the installed build. */
internal static class InvestmentTableChecks
{
    private const uint First = 0x80A00000;
    // Fixture entry order; each tag is First plus its position in the package.
    private const int RootEntry = 0, ItemIndexEntry = 1, DisplayIndexEntry = 2, FlagSlotEntry = 3, ValueSlotEntry = 4, FlagMapEntry = 5,
        ValueMapEntry = 6, SharedEntry = 7, PoolEntry = 8, ProgressionEntry = 9, ObjectiveEntry = 10, FirstItemEntry = 11, ItemCount = 6,
        BucketEntry = FirstItemEntry + ItemCount, BankEntry = BucketEntry + 1, StringContainerEntry = BucketEntry + 2,
        StringDataEntry = BucketEntry + 3, ObjectiveTextEntry = BucketEntry + 4;
    // Sunrise reads the bucket table by its slot whatever its class, so the fixture gives it a class no reader names.
    private const uint AnyBucketClass = 0x80801717;
    // The Pursuits bucket id, which Sunrise names, and the fixture's invented first slot and slot count for it.
    private const byte PursuitsId = 40;
    private const int PursuitsFirst = 120, PursuitsSlots = 24;
    // The installed Pursuits bucket's first slot and slot count, as measured.
    private const int InstalledPursuitsFirst = 269, InstalledPursuitsSlots = 63;
    // String bank index, container, data and row classes of this build, and the variable hash a literal text part carries.
    private const uint BankIndexClass = 0x80805F98, StringContainerClass = 0x80809A88, StringDataClass = 0x80809A8A,
        StringHashClass = 0x80800070, StringPartClass = 0x80809A90, StringCombinationClass = 0x80809A8E, LiteralPart = 0x811C9DC5;
    // The objective display table's entry and row classes and its row size.
    private const uint ObjectiveDisplayClass = 0x808059EC, ObjectiveDisplayRowClass = 0x808059F0;
    private const int ObjectiveDisplayStride = 64;
    private const string FixtureObjectiveText = "Fixture targets defeated";
    // The reward site every reference kind in the fixture names, a second site, and the slot-79 row of item 0's socket lane.
    private const ushort FixtureSite = 3, SecondSite = 4, FixtureSlot79Row = 12;
    // A flag slot the account mapping table names (row 3), and item 0's maximum stack.
    private const ushort AccountFlagSlot = 4;
    private const uint ItemZeroStack = 3;

    /** A catalog with no root still loads; its tables report why they are empty. */
    public static void NoRoot(InvestmentCatalog catalog, uint itemTag, ReadOnlyMemory<byte> item)
    {
        var tables = catalog.Tables();
        Check(tables.Root is null && tables.Pools.Count == 0 && tables.Diagnostics.Contains("This installation has no investment root."),
            "A catalog without a root did not report it.");
        Check(catalog.ReadItem(itemTag, item) is { Pursuit: null, Wrapper: null, Diagnostics.Count: 0 }, "An item without blocks gained a pursuit.");
    }

    public static void Run(string parent)
    {
        string install = Path.Combine(parent, "tables-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        var items = Items();
        var strings = Strings(First + StringDataEntry, [FixtureObjectiveText]);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_tables_fixture_0100_0.pkg"), 0x100,
        [
            new(InvestmentRoot.RootClass, 8 << 9, Root()),
            new(0x80807BE5, 8 << 9, Index(Enumerable.Range(0, ItemCount).Select(i => ((uint)(0x1000 + i), First + FirstItemEntry + (uint)i)).ToArray())),
            new(0x80805CDA, 8 << 9, Index([])),
            new(InvestmentUnlocks.FlagSlotTableClass, 8 << 9, FlagSlots()),
            new(InvestmentUnlocks.ValueSlotTableClass, 8 << 9, ValueSlots()),
            new(InvestmentUnlocks.FlagMapTableClass, 8 << 9, Maps(0x80807D48, [0, 5, 2, 4], [], [1, 5], [])),
            new(InvestmentUnlocks.ValueMapTableClass, 8 << 9, Maps(0x80807C8D, [1], [0, 2], [], [])),
            new(InvestmentUnlocks.SharedTableClass, 8 << 9, Shared()),
            new(InvestmentRewards.TableClass, 8 << 9, Pools()),
            new(InvestmentSeasonPass.TableClass, 8 << 9, Progressions()),
            new(InvestmentPursuits.ObjectiveTableClass, 8 << 9, Objectives()),
            .. items.Select(item => new AuthoredPackageEntry(InvestmentCatalog.ItemClass, 8 << 9, item)),
            new(AnyBucketClass, 8 << 9, Buckets(3, (PursuitsId, PursuitsFirst, PursuitsSlots, 0, 0, 0), (10, 0, 50, 1, 1, 0), (12, 0, 6, 2, 0, 1))),
            new(BankIndexClass, 8 << 9, Banks(First + StringContainerEntry)),
            new(StringContainerClass, 8 << 9, strings.Container), new(StringDataClass, 8 << 9, strings.Data),
            new(ObjectiveDisplayClass, 8 << 9, ObjectiveDisplays())
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var sources = new InvestmentSources(Items: First + ItemIndexEntry, Displays: First + DisplayIndexEntry, StringBanks: First + BankEntry,
            ObjectiveDisplays: First + ObjectiveTextEntry);
        var catalog = InvestmentCatalog.Load(packages, sources: sources);
        Check(!catalog.TablesLoaded, "Load read the root-slot tables.");
        var tables = catalog.Tables();
        Check(catalog.TablesLoaded && ReferenceEquals(tables, catalog.Tables()), "Tables did not load once per catalog.");
        var root = tables.Root ?? throw new InvalidDataException("The root sweep missed the fixture root.");
        Check(root.Tag == First + RootEntry && root.SlotCount == 115, "The root sweep found another root.");
        Check(root.ReadSlot(packages, InvestmentRootSlots.Classes, InvestmentUnlocks.ClassTableClass).State == InvestmentSlotState.WrongClass
            && tables.Diagnostics.Contains("Unlock slots: Slot 12 has an unexpected class."), "A wrong slot class was accepted.");
        Check(tables.Slot84.State == InvestmentSlotState.Omitted, "Slot 84 was not omitted.");
        Expressions(tables);
        Bindings(tables);
        ProgressSources(tables);
        Pools(tables);
        Pass(tables);
        Pursuits(catalog, packages, tables);
        ObjectiveText(tables);
        ObjectiveTextRefusals(parent);
        Labels(catalog, tables);
        ItemCatalogChecks.Run(catalog);
        Edits(catalog, packages, tables);
        PassItems(catalog, tables);
        RewardEdits(catalog, tables);
        BucketTable(tables);
        BucketRefusals(parent);
        References(packages, tables);
        Rewards(catalog, packages, tables);
        PursuitPages(catalog, packages, tables);
        Progress(catalog, packages, tables);
        TruncatedItems(parent);
        Ambiguous(parent);
        Console.WriteLine($"investment_tables root=sweep slots={root.SlotCount} pools={tables.Pools.Count} "
            + $"progressions={tables.Progressions.Count} objectives={tables.Objectives.Count} diagnostics={tables.Diagnostics.Count}");
    }

    private static void Expressions(InvestmentTables tables)
    {
        var unlocks = tables.Unlocks;
        Check(unlocks.SharedCount == 50 && unlocks.Shared(5).IsEmpty && unlocks.Shared(9).Instructions.Count == 1,
            "Shared rows did not load.");
        Check(unlocks.GraphDiagnostics.Contains("1 shared expression references leave the table (first in row 4).")
            && unlocks.GraphDiagnostics.Contains("1 shared expression references name an empty row (first in row 6).")
            && unlocks.GraphDiagnostics.Any(row => row is "Shared expressions form a cycle: 2, 3." or "Shared expressions form a cycle: 3, 2."),
            "Shared graph problems were not all reported: " + string.Join(" | ", unlocks.GraphDiagnostics));
        InvestmentExpression Reference(uint row) => new(0, [new(InvestmentUnlocks.SharedOpcode, row, 0)]);
        Check(unlocks.Depth(Reference(1)) == 2 && unlocks.Depth(Reference(2)) == -1 && unlocks.Depth(Reference(4)) == -1
            && unlocks.Depth(Reference(5)) == -1 && unlocks.Depth(Reference(6)) == -1 && unlocks.Depth(Reference(99)) == -1,
            "Shared depths ignored a cycle or a broken reference.");
        Check(unlocks.Depth(Reference(8)) == 40 && unlocks.Problems(Reference(8))
            .SequenceEqual(["Sunrise refuses expressions nested deeper than 32 shared rows."]), "A 40-deep chain was refused or not noted.");
        Check(unlocks.Format(Reference(1)) == "shared #1" && unlocks.Format(unlocks.Shared(1)) == "shared #0 >= 1",
            "A balanced shared row did not print infix.");
        Check(unlocks.Format(unlocks.Shared(7)) == "op 19 (0), flag Account flag row 0, and"
            && unlocks.Problems(unlocks.Shared(7)).SequenceEqual(["Studio does not decode opcode 19."]), "An undecoded opcode printed infix.");
        Check(unlocks.Format(Reference(2)) == "shared #2 (missing, empty or cyclic)" && unlocks.Format(new InvestmentExpression(0, [])) == "None",
            "A cyclic reference printed as a value.");
        Check(unlocks.Format(new InvestmentExpression(0, [new(InvestmentUnlocks.SharedOpcode, 1, 0), new(2, 0, 0), new(2, 0, 0), new(4, 0, 0)]))
                == "shared #1, not, not, and", "A postfix listing did not name a shared row as infix does.");
    }

    private static void Bindings(InvestmentTables tables)
    {
        var unlocks = tables.Unlocks;
        Check(unlocks.FlagLabel(0) is { Text: "Account flag row 0", Proven: true } && unlocks.FlagLabel(1).Text == "Character flag row 0"
            && unlocks.ValueLabel(0).Text == "Character value row 0" && unlocks.ValueLabel(1).Text == "Account value row 0",
            "Agreeing slots lost their bank labels.");
        Check(tables.Diagnostics.Contains("2 unlock slots disagree with their mapping table; their labels show both rows.")
            && unlocks.FlagLabel(2) is { Proven: false, Text: "Account flag row 1" } label && label.Detail.EndsWith("names row 2", StringComparison.Ordinal),
            "A forced disagreement was resolved silently.");
        Check(unlocks.FlagLabel(3) is { Text: "Computed flag 0xABCD1234", Proven: false } && unlocks.ValueLabel(2) is { Text: "Item value (aggregation 2)", Proven: false }
            && unlocks.FlagLabel(40).Text == "Flag slot 40", "Unmapped kinds were given a bank.");
        LiveUnlockSource? Of(uint opcode, uint slot) => LiveUnlockSources.Of(unlocks, new(opcode, slot, 0));
        const uint flag = InvestmentUnlocks.FlagOpcode, value = InvestmentUnlocks.ValueOpcode;
        Check(Of(flag, 0) == new LiveUnlockSource(LiveUnlockKind.Flag, LiveUnlockBank.Account, 0)
            && Of(flag, 1) == new LiveUnlockSource(LiveUnlockKind.Flag, LiveUnlockBank.Character, 0)
            && Of(flag, 4) == new LiveUnlockSource(LiveUnlockKind.Flag, LiveUnlockBank.Account, 3)
            && Of(value, 0) == new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Character, 0)
            && Of(value, 1) == new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Account, 0), "Proven slots did not bind to their saved rows.");
        Check(Of(flag, 2) is null && Of(flag, 3) is null && Of(flag, 40) is null && Of(value, 2) is null && Of(InvestmentUnlocks.ConstantOpcode, 0) is null,
            "An unproven, computed or outside slot, or a constant, bound to a saved row.");
        // Slot 5's own row is character row 1, but the account table names it first, and Sunrise binds the first table naming a slot.
        Check(unlocks.FlagLabel(5).Proven && Of(flag, 5) is null, "A slot Sunrise binds to another bank bound to its label's row.");
    }

    /**
     * What an objective's progress reads once shared rows are inlined as Sunrise binds it: fixture objectives 0-7, then
     * expressions on objective 7's row through each kind of shared row. The fixture has no class table, so no class flag.
     */
    private static void ProgressSources(InvestmentTables tables)
    {
        const uint flag = InvestmentUnlocks.FlagOpcode, value = InvestmentUnlocks.ValueOpcode, shared = InvestmentUnlocks.SharedOpcode;
        InvestmentProgressSource Of(params (uint Opcode, uint Operand)[] instructions) => tables.ProgressSource(tables.Objectives[7] with
            { Source = new(0, [.. instructions.Select(row => new InvestmentUnlockInstruction(row.Opcode, row.Operand, 0))]) });
        var kinds = tables.Objectives.Select(objective => tables.ProgressSource(objective).Kind).ToArray();
        Check(kinds.SequenceEqual([InvestmentProgressKind.ItemValue, InvestmentProgressKind.Flag, InvestmentProgressKind.Unreadable,
                InvestmentProgressKind.Unreadable, InvestmentProgressKind.Value, InvestmentProgressKind.Value, InvestmentProgressKind.Several,
                InvestmentProgressKind.Other]), "Fixture objectives were classified wrongly: " + string.Join(", ", kinds));
        Check(tables.ProgressSource(tables.Objectives[1]).Instruction is { Opcode: flag, Operand: 6 }
            && tables.ProgressSource(tables.Objectives[5]).Instruction is { Opcode: value, Operand: 4 },
            "A one-source objective did not name the instruction it binds to, through a shared row included.");
        Check(Of((shared, 0)) is { Kind: InvestmentProgressKind.Flag, Instruction.Operand: 0 }
            && Of((shared, 49)).Kind == InvestmentProgressKind.ItemValue && Of((shared, 48)).Kind == InvestmentProgressKind.Value
            && Of((shared, 1)).Kind == InvestmentProgressKind.Several && Of((value, 1), (value, 3), (13, 0)).Kind == InvestmentProgressKind.Several,
            "Shared rows were not inlined before counting instructions.");
        // Value slots 2 and 7 are both of the per-item kind, but the character value map names slot 2, so Sunrise reads it from
        // the character bank and only slot 7 stays per-item progress.
        Check(tables.Unlocks.BoundValue(2) && !tables.Unlocks.BoundValue(7) && Of((value, 2)).Kind == InvestmentProgressKind.Value
            && Of((value, 7)).Kind == InvestmentProgressKind.ItemValue, "A per-item value slot a value map binds was read as item progress.");
        // Rows 17-47 nest 31 deep, which Sunrise binds; row 16 adds the 32nd level, which it refuses.
        Check(Of((shared, 17)).Kind == InvestmentProgressKind.Flag && Of((shared, 16)).Kind == InvestmentProgressKind.Unreadable,
            "The shared nesting limit differs from Sunrise's.");
        (string Name, (uint, uint)[] Program)[] unreadable =
        [
            ("a cycle", [(shared, 2)]), ("a row outside the table", [(shared, 99)]), ("an empty row", [(shared, 5)]),
            ("a row naming an empty row", [(shared, 6)]), ("an opcode Sunrise does not evaluate", [(shared, 7)]),
            ("a flag slot outside its table", [(flag, 40)]), ("a value slot outside its table", [(value, 8)]), ("no instruction", []),
            ("a shared row past the int range", [(shared, 0x80000000)])
        ];
        foreach (var (name, program) in unreadable)
            Check(Of(program).Kind == InvestmentProgressKind.Unreadable, $"An objective reading {name} was not unreadable.");
        Check(Of((InvestmentUnlocks.ConstantOpcode, 3)).Kind == InvestmentProgressKind.Other && Of((value, 7), (22, 0)).Kind == InvestmentProgressKind.Several,
            "A constant was a saved source, or an operator did not count as an instruction.");
    }

    /**
     * Set progress and Complete objectives gating on fixture item 3 (objectives 0-7: item value, a flag whose malformed
     * controls read optional, two unreadable, two values, several sources, and an optional constant), in the game's refusal order.
     */
    private static void Progress(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        var pursuit = catalog.ReadItem(First + FirstItemEntry + 3, packages.Read(First + FirstItemEntry + 3)).Pursuit!;
        Check(pursuit.Objectives.SequenceEqual(Enumerable.Range(0, 8).Select(index => (ushort)index)) && tables.Objectives[7].Optional,
            "Fixture item 3 no longer lists objectives 0-7 with an optional last one.");
        const LivePursuitObjectiveFlags known = LivePursuitObjectiveFlags.Known;
        LivePursuitObjective Row(int ordinal, LivePursuitObjectiveFlags flags, int progress = 0) =>
            new(tables.Objectives[ordinal].Hash, progress, 10, flags);
        LiveHeldPursuit Held(LivePursuitFlags flags, params LivePursuitObjective[] rows) =>
            new(0x4003, catalog.Items[3].Hash, 7, 1, 0, flags, LivePursuitReason.ObjectivesIncomplete, rows);
        var all = Held(LivePursuitFlags.ObjectivesKnown, [.. Enumerable.Range(0, LiveInvestmentProtocol.PursuitObjectiveCapacity).Select(ordinal => Row(ordinal, known))]);
        ProgressRefusal Set(int ordinal, LiveHeldPursuit? held, string link = "") => PursuitProgress.SetRefusal(tables, pursuit, ordinal, held, link);
        // Never hides the control: this server can never run it here. Anything else draws it, disabled with the text.
        bool Never(ProgressRefusal refusal, string text) => refusal == new ProgressRefusal(text, true);
        bool Waits(ProgressRefusal refusal, string text) => refusal == new ProgressRefusal(text, false);
        var noContract = all with { Flags = LivePursuitFlags.Bounty, Reason = LivePursuitReason.ContractUnavailable };
        Check(Waits(Set(1, all, LiveGate.AttachSentence), LiveGate.AttachSentence) && Never(Set(1, null), PursuitProgress.NotHeld)
            && Never(Set(1, noContract), PursuitProgress.NoContract)
            && Never(Set(1, all with { Flags = LivePursuitFlags.Bounty }), PursuitProgress.BountyNotSaved),
            "Set progress did not refuse the link for now, then a missing copy, a contract the game cannot read and a bounty for good.");
        Check(Waits(Set(7, all), PursuitProgress.Differs) && Waits(Set(1, all with { Objectives = [Row(0, known), Row(2, known)] }), PursuitProgress.Differs),
            "Set progress allowed an objective the game does not report at that position, or hid it for good.");
        Check(Never(Set(0, all), PursuitProgress.NotSaved) && Never(Set(2, all), PursuitProgress.NotSavedValue)
            && Never(Set(6, all), PursuitProgress.Several) && Waits(Set(4, all with { Objectives = [.. all.Objectives.Select((row, ordinal) =>
                ordinal == 4 ? row with { Flags = LivePursuitObjectiveFlags.None } : row)] }), PursuitProgress.Unreadable),
            "Set progress refusals are out of order, or an unread value hid Set progress for good.");
        Check(Waits(Set(1, all), "") && Waits(Set(4, all), "") && Waits(Set(5, all), ""), "A saved flag or value could not be set.");
        // The game reports a skipped objective as known with value 0 whatever its saved row holds.
        Check(Waits(Set(4, all with { Objectives = [.. all.Objectives.Select((row, ordinal) =>
                ordinal == 4 ? row with { Flags = known | LivePursuitObjectiveFlags.Skipped } : row)] }), PursuitProgress.Skipped),
            "Set progress allowed a skipped objective whose reported value is not its saved row.");
        Check(!PursuitProgress.CanSet(tables, pursuit, 1, null) && !PursuitProgress.CanSet(tables, pursuit, 0, all)
            && PursuitProgress.CanSet(tables, pursuit, 4, all with { Objectives = [.. all.Objectives.Select(row => row with { Flags = LivePursuitObjectiveFlags.None })] }),
            "Set progress was drawn where it can never run, or hidden while the game could not read a saved value.");

        ProgressRefusal Complete(LiveHeldPursuit? held, string link = "") => PursuitProgress.CompleteRefusal(tables, pursuit, held, link);
        var skipped = Row(0, LivePursuitObjectiveFlags.Skipped);
        var done = LivePursuitObjectiveFlags.Known | LivePursuitObjectiveFlags.Complete;
        var cut = Held(LivePursuitFlags.None, skipped, Row(1, done), Row(2, done), Row(3, done), Row(4, done), Row(5, done), Row(6, done));
        Check(Waits(Complete(cut), "The game's objective 8 differs from this item's. Install this change and restart the game first.")
            && Never(Complete(null), PursuitProgress.NotHeld) && Waits(Complete(all, "x."), "x.") && Never(Complete(noContract), PursuitProgress.NoContract),
            "Complete did not refuse a contract the game cut or the link for now, or a missing copy or unreadable contract for good.");
        // The game cuts the eighth objective, so a Studio pursuit of seven is the one Complete can run on. Objective 1 is optional
        // (its control byte is not 0), so it never gates completion.
        var seven = pursuit with { Objectives = [.. pursuit.Objectives.Take(LiveInvestmentProtocol.PursuitObjectiveCapacity)] };
        ProgressRefusal Seven(params LivePursuitObjective[] rows) => PursuitProgress.CompleteRefusal(tables, seven, Held(LivePursuitFlags.None, rows), "");
        // The game checks a required objective's item lane before whether it is complete, so a complete one still hides Complete.
        Check(Never(Seven(Row(0, LivePursuitObjectiveFlags.None), Row(1, known), Row(2, done), Row(3, done), Row(4, done), Row(5, done), Row(6, done)),
                PursuitProgress.NotSaved)
            && Never(Seven(Row(0, done), Row(1, known), Row(2, done), Row(3, done), Row(4, known), Row(5, done), Row(6, done)), PursuitProgress.NotSaved),
            "A required item-lane objective, incomplete or complete, did not hide Complete.");
        Check(Waits(Seven(skipped, Row(1, known), Row(2, LivePursuitObjectiveFlags.None), Row(3, done), Row(4, done), Row(5, done), Row(6, done)),
                "The game cannot read objective 3."), "An unknown objective did not block Complete by its number for now.");
        // With nothing written before them, an incomplete objective of several sources or of a source that is not saved hides Complete.
        Check(Never(Seven(skipped, Row(1, known), Row(2, done), Row(3, done), Row(4, done), Row(5, done), Row(6, known)),
                "Objective 7 combines several sources.")
            && Never(Seven(skipped, Row(1, known), Row(2, known), Row(3, done), Row(4, done), Row(5, done), Row(6, done)),
                "Objective 3 is not a saved value."),
            "An incomplete objective Studio cannot write did not hide Complete when nothing is written before it.");
        // Once objective 5 is written, objective 7 may complete by then, so the game decides it.
        Check(Waits(Seven(skipped, Row(1, known), Row(2, done), Row(3, done), Row(4, known), Row(5, done), Row(6, known)), ""),
            "An objective after a written one was refused instead of left to the game.");
        Check(Waits(Seven(skipped, Row(1, known), Row(2, done), Row(3, done), Row(4, known), Row(5, known), Row(6, done)), ""),
            "A skipped objective, or complete ones Studio cannot set, blocked Complete.");
        Check(Waits(Seven(skipped, Row(1, done), Row(2, done), Row(3, done), Row(4, done), Row(5, done), Row(6, done)), PursuitProgress.AllComplete),
            "Nothing incomplete did not say so, or hid Complete for good.");
        Check(Waits(Seven(skipped, Row(1, known), Row(2, done), Row(3, done), Row(4, done), Row(5, done), Row(5, done)),
                "The game's objective 7 differs from this item's. Install this change and restart the game first."),
            "A differing objective hash did not block Complete.");
        // Objective 8 is optional: Complete never needs it, even when the game cannot read it.
        Check(Waits(PursuitProgress.CompleteRefusal(tables, pursuit, Held(LivePursuitFlags.None, [skipped, Row(1, done), Row(2, done), Row(3, done),
                Row(4, known), Row(5, done), Row(6, done), Row(7, LivePursuitObjectiveFlags.None)]), ""), ""),
            "An optional objective blocked Complete.");
        Check(Never(PursuitProgress.CompleteRefusal(tables, seven, Held(LivePursuitFlags.Bounty, Row(1, known)), ""), PursuitProgress.BountyNotSaved)
            && !PursuitProgress.CanComplete(tables, seven, Held(LivePursuitFlags.Bounty, Row(1, known))),
            "A bounty did not hide Complete.");
        // The inspector draws exactly the controls the refusals leave: Set progress where a saved row is read, and disabled on
        // objective 8, which the game does not report; nothing without a held copy.
        var offers = PursuitInspector.Offers(tables, pursuit, all);
        Check(!offers.Complete && offers.Set.SequenceEqual([false, true, false, false, true, true, false, true])
            && PursuitInspector.Offers(tables, pursuit, null) is { Complete: false } none && !none.Set.Contains(true),
            "The inspector's progress controls do not follow the refusals.");
        Console.WriteLine("investment_progress_gate set=13 complete=13 offers=checked never=hidden transient=disabled");
    }

    private static void Pools(InvestmentTables tables)
    {
        var pools = tables.Pools;
        Check(pools.Count == 3 && pools[0].Entries.Count == 4 && pools[2].Error == "Pool has no definition hash.", "Pool rows were dropped.");
        var entries = pools[0].Entries;
        Check(entries[0].Error == "Weight is negative or not finite." && entries[0].Weight == -1 && entries[0].AnyCategory,
            "A negative weight row was not kept with its error.");
        Check(tables.Unlocks.Format(entries[0].Condition) == "Account flag row 0 and (Account value row 0 >= 5)", "A pool condition did not print infix.");
        Check(entries[1].Error == "Socket override 0 has no socket type." && entries[1].Modifiers.Single().Value == 2
            && entries[1].Unknown28 == 0x5EED, "A bad socket override or modifier was misread.");
        Check(entries[2].Error == "Nested pool 7 is outside the pool table.", "An out-of-range nested pool was accepted.");
        var graph = tables.RewardGraph;
        Check(graph.Cycles.Single().SequenceEqual([1]) && graph.Depths.SequenceEqual([1, -1, 0]) && graph.MaximumDepth == 1
            && tables.Diagnostics.Contains("Reward pools contain themselves: 1."), "The automatic-wrapper cycle was missed.");
        Check(graph.WrappersOf(1).SequenceEqual([1]) && graph.WrappersOf(2).SequenceEqual([2]) && graph.ParentsOf(2).SequenceEqual([(0, 3)]),
            "Pool references were not indexed.");
        Check(InvestmentRewardGraph.BaseTotal(pools[0], 0x22) == 3.5 + 1, "A selection total included another category.");
    }

    private static void Pass(InvestmentTables tables)
    {
        var pass = tables.PassCandidates.Single();
        Check(pass.Index == 1 && pass.Steps.Count == 3 && pass.Threshold(3) == 200 && pass.ScopeLabel == "Account"
            && tables.Progressions[0].ScopeLabel == "Character", "The pass candidate or its ladder was misread.");
        Check(pass.Rewards.Select(row => row.Index).SequenceEqual([0, 1, 2]) && pass.Rewards[0].Error == "Rank 300 is above 255."
            && pass.Rewards[1].Error == "Claim flag slot 5 is not an account flag." && pass.Rewards[2].Error.Length == 0,
            "Pass rows were reordered or validated wrongly.");
        Check(pass.Rewards[2].Conditions.Members.Count == 2
            && tables.Unlocks.Format(pass.Rewards[2].Conditions) == "(Account flag row 0) and (shared #1)", "Pass conditions were merged.");
    }

    private static void Pursuits(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        Check(tables.Objectives.Count == 8 && tables.Objectives[1].Error == "The optional or counts-down byte is above 1, so Sunrise refuses the whole objective table."
            && tables.Objectives[0].Error.Length == 0 && tables.Objectives[2].Source.IsEmpty && tables.Objectives[3].Source.IsEmpty,
            "Objective rows were misread.");
        var detail = catalog.ReadItem(First + FirstItemEntry + 3, packages.Read(First + FirstItemEntry + 3));
        var pursuit = detail.Pursuit ?? throw new InvalidDataException("The pursuit block was not read.");
        Check(pursuit.Objectives.Count == 8 && pursuit.Diagnostics.Contains("Sunrise reads at most 7 objectives on a pursuit.")
            && pursuit.Diagnostics.Contains("Unused reward preview slot 1 is not empty."), "Pursuit rule breaks were not noted.");
        Check(pursuit.Primary.Label == "Site 3; this installation does not ship root slot 84." && pursuit.Secondary.Label == "None"
            && pursuit.ContextValue.Label == "Account value row 0" && pursuit.CompletionValue.Label == "Character value row 0"
            && pursuit is { Suppress: true, RequireAll: false, RequireNonOptional: true, Automatic: false }, "Pursuit references were mislabelled.");
        Check(pursuit.Previews.Single() is { ItemIndex: 0, Quantity: 3, ConditionRow: 1, Condition.Instructions.Count: 3 }
            && pursuit.LifetimeSeconds == 3600, "Previews or lifetime were misread.");
        Check(pursuit.PresenceFlags.SequenceEqual([(ushort)5]) && tables.ProgressLane(tables.Objectives[0], pursuit) == 0
            && tables.ProgressLane(tables.Objectives[1], pursuit) == -1, "The progress lane hint is wrong.");
        Check(pursuit.Chain is { OwnerItem: 4, Mode: 0, ModeDecoded: false, Error: "" } chain
            && chain.Steps.Select(step => (step.Value, step.ItemIndex)).SequenceEqual([(100, (ushort)3), (200, (ushort)5)]),
            "The quest set was misread.");
        Check(Enumerable.Range(0, catalog.Items.Count).Where(item => tables.QuestOwner(item) == 4).SequenceEqual([3, 5])
            && tables.ObjectiveUsers(0).SequenceEqual([3, 5]), "Item links were not captured.");
        var second = catalog.ReadItem(First + FirstItemEntry + 5, packages.Read(First + FirstItemEntry + 5)).Pursuit!;
        Check(second.LifetimeSeconds is null && second.Lifetime?.Instructions.Count == 2 && second.Diagnostics.Count == 0,
            "A computed lifetime was read as a constant.");
        var wrapper = catalog.ReadItem(First + FirstItemEntry + 2, packages.Read(First + FirstItemEntry + 2)).Wrapper!;
        Check(wrapper is { Pool: 2, OpensOnAcquisition: false, Error: "More selections than Sunrise accepts (4)." } && wrapper.Selections.Count == 5,
            "Wrapper selections were capped or accepted.");
    }

    /** Display rows join objectives by index and hash; a row naming another hash gives no text and one diagnostic. */
    private static void ObjectiveText(InvestmentTables tables)
    {
        Check(tables.ObjectiveText(0) == FixtureObjectiveText && tables.ObjectiveText(7) == FixtureObjectiveText
            && tables.ObjectiveText(1) == "" && tables.ObjectiveText(2) == "" && tables.ObjectiveText(8) == "" && tables.ObjectiveText(-1) == "",
            "Objective descriptions were not joined by index and hash.");
        Check(tables.Diagnostics.Where(line => line.StartsWith("Objective text", StringComparison.Ordinal))
            .SequenceEqual(["Objective text: 1 objectives have no display row with their hash."]), "A mismatched display row was not one diagnostic.");
        Check(InvestmentTableViews.ObjectiveName(tables, tables.Objectives[0]) == (FixtureObjectiveText, "Objective 0\n0x00000041")
            && InvestmentTableViews.ObjectiveName(tables, tables.Objectives[2]) == ("Objective 0x00000043", "Objective 2\n0x00000043"),
            "An objective without a description was not named by its hash.");
    }

    /** A display table of another class, or none, is a section diagnostic and leaves every description empty. */
    private static void ObjectiveTextRefusals(string parent)
    {
        foreach (var (name, displayClass, expected) in new[] { ("objective-text-class", AnyBucketClass, "Investment reference has an unexpected class."),
            ("objective-text-missing", 0u, "The objective display table is not installed.") })
        {
            string install = Path.Combine(parent, name);
            Directory.CreateDirectory(Path.Combine(install, "packages"));
            var root = new Blob(8 + 115 * 16);
            for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
            root.U(8 + InvestmentRootSlots.Items * 16, First + 1); root.U(8 + InvestmentRootSlots.Objectives * 16, First + 3);
            AuthoredPackageEntry[] entries = [new(InvestmentRoot.RootClass, 8 << 9, root.ToArray()), new(0x80807BE5, 8 << 9, Index([])),
                new(0x80805CDA, 8 << 9, Index([])), new(InvestmentPursuits.ObjectiveTableClass, 8 << 9, Objectives())];
            if (displayClass != 0) entries = [.. entries, new(displayClass, 8 << 9, ObjectiveDisplays())];
            PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_objective_text_fixture_0100_0.pkg"), 0x100, entries);
            using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
            var tables = InvestmentCatalog.Load(packages, sources: new(Items: First + 1, Displays: First + 2, StringBanks: uint.MaxValue,
                ObjectiveDisplays: First + 4)).Tables();
            Check(tables.Objectives.Count == 8 && tables.Diagnostics.Contains("Objective text: " + expected)
                && Enumerable.Range(0, 8).All(index => tables.ObjectiveText(index) == ""), $"The {name} display table was read.");
        }
    }

    /** Labels that say what the install ships and what Sunrise does with it. */
    private static void Labels(InvestmentCatalog catalog, InvestmentTables tables)
    {
        Check(!InvestmentTableViews.Usable(InvestmentRoot.OmittedSlot) && !InvestmentTableViews.Usable(0)
            && InvestmentTableViews.Usable(tables.TableTag(InvestmentRootSlots.Pools)), "An omitted or unset table tag was offered to open.");
        Check(tables.TableTag(84) == InvestmentRoot.OmittedSlot && !InvestmentTableViews.Usable(tables.TableTag(84)),
            "The fixture's slot 84 is not omitted.");
        Check(InvestmentTableViews.PursuitReason(LivePursuitReason.ItemProgressNotSaved) == "This server does not save per-item progress",
            "Unsaved item progress is not worded as the server's fact.");
        const ushort absent = ushort.MaxValue;
        var entry = tables.Pools[0].Entries[0] with { Slot84Reference = 7, ItemIndex = 0, NestedPool = absent };
        Check(InvestmentRewardTree.SiteTip(tables, entry) == "References reward site 7 in root slot 84, which this install does not ship. "
            + "Sunrise skips the reference, so the row grants its item."
            && InvestmentRewardTree.SiteTip(tables, entry with { ItemIndex = absent, NestedPool = 2 }).EndsWith("the row draws its pool.", StringComparison.Ordinal)
            && InvestmentRewardTree.SiteTip(tables, entry with { ItemIndex = absent }).EndsWith("grants nothing.", StringComparison.Ordinal),
            "A reward site on an install without slot 84 was described as refused.");
        string Socket(ushort plug, ushort plugSet, ushort rollSet, uint selection)
        {
            var (text, tip) = InvestmentTableViews.SocketOverride(catalog, new(0, 1, plug, plugSet, rollSet, selection));
            return text + " | " + tip;
        }
        const uint fixedPlug = uint.MaxValue;
        Check(Socket(0, absent, absent, fixedPlug) == $"{catalog.Items[0].Name} (fixed) | Extracted: fixed roll"
            && Socket(absent, 12, absent, fixedPlug).StartsWith("from plug set 12 | References roll tables", StringComparison.Ordinal)
            && Socket(absent, absent, 3, 0).StartsWith("from roll set 3 | ", StringComparison.Ordinal)
            && Socket(absent, absent, absent, 0).StartsWith("initial plug kept | Extracted: ", StringComparison.Ordinal)
            && Socket(0, absent, absent, 0).StartsWith($"{catalog.Items[0].Name} (selection 0) | Sunrise refuses", StringComparison.Ordinal),
            "A socket override was labelled against Sunrise's order of checks.");
    }

    private static void Edits(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        byte[] pools = packages.Read(First + PoolEntry), pass = packages.Read(First + ProgressionEntry), objectives = packages.Read(First + ObjectiveEntry);
        var entry = tables.Pools[0].Entries[1];
        var reward = tables.PassCandidates[0].Rewards[2];
        var objective = tables.Objectives[0];
        foreach (var (field, bytes, value) in new[] { (entry.WeightField, pools, "4.5"), (entry.QuantityField, pools, "9"),
            (reward.QuantityField, pass, "8"), (reward.ItemField, pass, "3"), (objective.CompletionField, objectives, "12") })
        {
            byte[] edited = catalog.EditTableField(field, bytes, value);
            Check(OnlyRange(bytes, edited, field.Property.Offset, 4) && field.Property.Editable,
                $"{field.Kind} changed bytes outside its field.");
            Reject(() => catalog.EditTableField(field, edited, value), "This field changed. Reopen its editor.");
        }
        Check(BinaryPrimitives.ReadSingleLittleEndian(catalog.EditTableField(entry.WeightField, pools, "4.5").AsSpan(entry.WeightField.Property.Offset)) == 4.5f,
            "The weight edit wrote the wrong value.");
        Check(reward.ItemField.Property.Offset == reward.Offset + 4 && BinaryPrimitives.ReadUInt32LittleEndian(
            catalog.EditTableField(reward.ItemField, pass, "3").AsSpan(reward.Offset + 4)) == 3, "The pass item edit wrote the wrong field.");
        const string itemRange = "Item must be an index of this installation's item table (0 to 5).";
        foreach (string item in new[] { "6", "65535", "-1", "x" }) Reject(() => catalog.EditTableField(reward.ItemField, pass, item), itemRange);
        Reject(() => catalog.EditTableField(entry.WeightField, pools, "NaN"), "Weight must be a finite number of zero or more.");
        Reject(() => catalog.EditTableField(entry.WeightField, pools, "-1"), "Weight must be a finite number of zero or more.");
        Reject(() => catalog.EditTableField(entry.QuantityField, pools, "0"), $"Quantity must be 1 to {int.MaxValue}.");
        Reject(() => catalog.EditTableField(objective.CompletionField, objectives, "-1"), "Completion value must be zero or more.");
        Reject(() => catalog.EditTableField(entry.WeightField with { OwnerTag = First + ProgressionEntry }, pools, "4"),
            "This table is no longer the investment root's reward pool table.");
        Reject(() => catalog.EditTableField(entry.WeightField with { Element = 9 }, pools, "4"), "This field changed. Reopen its editor.");
    }

    /** Item replacement follows Sunrise's claim and rank-up checks: the rows it refuses, and the lines a flip or a stack shows. */
    private static void PassItems(InvestmentCatalog catalog, InvestmentTables tables)
    {
        var unlocks = tables.Unlocks;
        Check(unlocks.AccountFlag(AccountFlagSlot) && unlocks.AccountFlag(0) && !unlocks.AccountFlag(1) && !unlocks.AccountFlag(3)
            && !unlocks.AccountFlag(99), "Account flag binding does not follow the account mapping table.");
        Check(tables.AcquisitionFlag(4) == AccountFlagSlot && tables.AcquisitionFlag(1) == tables.AcquiredFlagSlot(1)
            && tables.AcquisitionFlag(0) == ushort.MaxValue && tables.AcquiredFlagSlot(4) == ushort.MaxValue,
            "The acquisition flag was not read on every item, or wrapper flags gained a perk.");
        Check(SeasonPassPage.Automatic(catalog, tables, 4) && !SeasonPassPage.Automatic(catalog, tables, 0)
            && !SeasonPassPage.Automatic(catalog, tables, 1) && !SeasonPassPage.Automatic(catalog, tables, 99), "Automatic perk items were misjudged.");
        var row = tables.PassCandidates[0].Rewards[2];
        var socketed = row with { ItemIndex = 0, Quantity = 1, Sockets = [new InvestmentSocketOverride(0, 1, 0xFFFF, 0xFFFF, 0xFFFF, 0)] };
        SeasonPassPage.ItemReplacement Replace(InvestmentSeasonPassRow edited, int item) => SeasonPassPage.Replacement(catalog, tables, edited, item);
        bool Has(SeasonPassPage.ItemReplacement replacement, string line) => replacement.Lines.Contains((line, true));
        Check(Replace(socketed, 1).Refusal == SeasonPassPage.SocketOwnerRefusal && Replace(socketed, 2).Refusal.Length == 0
            && Replace(socketed, 0).Refusal.Length == 0, "A socketed row was refused for a stored wrapper or allowed an opening one.");
        var automatic = Replace(socketed, 4);
        Check(automatic.Refusal == SeasonPassPage.PerkShapeRefusal && !Has(automatic, SeasonPassPage.BecomesAutomatic),
            "A socketed row became an automatic perk.");
        var twice = Replace(row with { ItemIndex = 0, Quantity = 2 }, 4);
        Check(twice.Refusal == SeasonPassPage.PerkShapeRefusal && Has(twice, SeasonPassPage.BecomesAutomatic),
            "A flip to an automatic perk with quantity 2 was not refused.");
        var flip = Replace(row with { ItemIndex = 0, Quantity = 1 }, 4);
        Check(flip.Refusal.Length == 0 && Has(flip, SeasonPassPage.BecomesAutomatic)
            && flip.Lines.Contains(("The row keeps its claim flag. An account that claimed it sees the new item as claimed.", false))
            && flip.Lines.Contains(("Its conditions stay: Account flag row 0", false)), "The flip lines did not show for an unsocketed row.");
        Check(Has(Replace(row with { ItemIndex = 4, Quantity = 1 }, 0), SeasonPassPage.StopsAutomatic)
            && Replace(row with { ItemIndex = 0, Quantity = 7 }, 1).Refusal == RewardRefusals.WrapperQuantity
            && Replace(row with { ItemIndex = 1, Quantity = 1 }, 1).Refusal.Length == 0, "Leaving the perk bucket or an opening wrapper's quantity was misjudged.");
        Check(Has(Replace(row with { ItemIndex = 1, Quantity = ItemZeroStack + 1 }, 0),
                $"Quantity {ItemZeroStack + 1} is above this item's maximum stack {ItemZeroStack}; the game may refuse the claim.")
            && !Replace(row with { ItemIndex = 1, Quantity = ItemZeroStack }, 0).Lines.Any(line => line.Text.StartsWith("Quantity", StringComparison.Ordinal)),
            "The stack line did not follow the item's maximum stack.");
    }

    /**
     * A pass row's quantity and a pool entry's quantity follow Sunrise's reward refusals: an opening wrapper and an automatic perk
     * row take quantity 1 only, and an item granted as itself must fit its placement (fixture item 0 moved into each case).
     */
    private static void RewardEdits(InvestmentCatalog catalog, InvestmentTables tables)
    {
        Check(SeasonPassPage.RowRefusal(catalog, tables, 1, 3, false) == RewardRefusals.WrapperQuantity
            && SeasonPassPage.RowRefusal(catalog, tables, 1, 1, false).Length == 0
            && SeasonPassPage.RowRefusal(catalog, tables, 4, 2, false) == SeasonPassPage.PerkShapeRefusal
            && SeasonPassPage.RowRefusal(catalog, tables, 4, 1, false).Length == 0
            && SeasonPassPage.RowRefusal(catalog, tables, 0, 5, false).Length == 0,
            "A pass row quantity Sunrise refuses was accepted, or one it accepts was refused.");
        var item = catalog.Items[0];
        (InvestmentItem Item, long Quantity, bool Sockets, string Refusal)[] placements =
        [
            (item with { Bucket = RewardRefusals.PerkBucket }, 1, false, RewardRefusals.PerkAcquisition),
            (catalog.Items[4], 1, false, ""), (catalog.Items[4], 2, false, RewardRefusals.PerkAcquisition),
            (item with { Bucket = 12 }, 1, false, RewardRefusals.OtherInventory),
            (item with { Bucket = 10, Instanced = true }, 1, false, RewardRefusals.InstanceQuantity),
            (item with { Bucket = PursuitsId, Instanced = true }, 2, false, RewardRefusals.InstanceQuantity),
            (item with { Bucket = PursuitsId, Instanced = true }, 1, true, ""),
            (item with { Bucket = 10 }, 9, true, RewardRefusals.StackSockets), (item with { Bucket = 10 }, 9, false, "")
        ];
        foreach (var (shown, quantity, sockets, refusal) in placements)
            Check(RewardRefusals.Placement(tables, shown, quantity, sockets) == refusal,
                $"Placing item 0 in bucket {shown.Bucket} (instanced {shown.Instanced}) x{quantity} was not refused as \"{refusal}\".");
        // The pool entry popups refuse through the same rules: pool 1's entry is fixture item 1, which opens when acquired.
        using var workspace = new StudioWorkspace();
        var popup = new InvestmentFieldPopup();
        popup.Begin(workspace, tables.Pools[1].Entries[0].QuantityField, "");
        popup.Value = "2";
        string wrapped = InvestmentRewardTree.QuantityRefusal(catalog, tables, popup);
        popup.Value = "1";
        string single = InvestmentRewardTree.QuantityRefusal(catalog, tables, popup);
        popup.Begin(workspace, tables.Pools[0].Entries[1].QuantityField, "");
        popup.Value = "0x9";
        Check(wrapped == RewardRefusals.WrapperQuantity && single.Length == 0 && InvestmentRewardTree.QuantityRefusal(catalog, tables, popup).Length == 0,
            "A pool entry quantity edit did not follow the wrapper rule.");
    }

    /** Two root entries never resolve to either; Load still succeeds. */
    private static void BucketTable(InvestmentTables tables)
    {
        var buckets = tables.Buckets;
        Check(buckets.Select(bucket => (bucket.Id, bucket.ArraySelector)).SequenceEqual([(PursuitsId, (byte)0), ((byte)10, (byte)1), ((byte)12, (byte)2)])
            && tables.BucketTableTag == First + BucketEntry && tables.BucketTableClass == AnyBucketClass,
            "The bucket table was not read by its slot whatever its class.");
        Check(tables.Bucket(PursuitsId) is { FirstSlot: PursuitsFirst, SlotCount: PursuitsSlots, LastSlot: PursuitsFirst + PursuitsSlots - 1, Fifo: 0 }
            && tables.Bucket(10) is { Fifo: 1, NoTransferOnEviction: 0 } && tables.Bucket(12) is { NoTransferOnEviction: 1, Raw.Length: 36 }
            && tables.Bucket(7) is null, "Bucket descriptors were misread.");
        Check(tables.BucketTableProblems.Count == 0 && buckets.All(bucket => bucket.Problem.Length == 0)
            && !tables.Diagnostics.Any(line => line.StartsWith("Buckets", StringComparison.Ordinal)), "A valid bucket table reported problems.");
        Check(tables.ItemsInBucket(PursuitsId).SequenceEqual([3, 5]) && tables.ItemsInBucket(37).SequenceEqual([4])
            && tables.ItemsInBucket(12).Count == 0, "Installed items were not indexed by bucket.");
    }

    /**
     * One table per refusal: Sunrise refuses the whole table for a repeated id, id 255, an unknown array selector or a FIFO or
     * no-transfer byte above 1, truncates a range, and fails a bad count.
     */
    private static void BucketRefusals(string parent)
    {
        const string refused = "Sunrise refuses the whole bucket table: ";
        var repeated = BucketTables(parent, "buckets-repeated", Buckets(3, (5, 0, 4, 0, 0, 0), (5, 4, 4, 0, 0, 0), (7, 8, 4, 0, 0, 0)));
        Check(repeated.BucketTableProblems.SequenceEqual([refused + "bucket 5 has more than one descriptor."])
            && repeated.Buckets.Count == 3 && repeated.Buckets.All(bucket => bucket.Problem.Length == 0), "A repeated bucket id was not a table problem.");
        var reserved = BucketTables(parent, "buckets-reserved", Buckets(2, (255, 0, 4, 0, 0, 0), (1, 4, 4, 0, 0, 0)));
        Check(reserved.BucketTableProblems.SequenceEqual([refused + "bucket 255 is reserved for no bucket."])
            && reserved.Buckets.All(bucket => bucket.Problem.Length == 0), "Bucket 255 was not a table problem.");
        var truncated = BucketTables(parent, "buckets-truncated", Buckets(2, (1, 0, ushort.MaxValue + 1, 0, 0, 0), (2, 0, 4, 3, 0, 0)));
        Check(truncated.BucketTableProblems.SequenceEqual([refused + "bucket 2's array selector 3 is not one Sunrise knows."])
            && truncated.Buckets[0].Problem.Contains("truncates", StringComparison.Ordinal) && truncated.Buckets[1].Problem.Length == 0,
            "A truncated range was not a row problem, or an unknown array selector not a table problem.");
        var controls = BucketTables(parent, "buckets-controls", Buckets(2, (1, 0, 4, 0, 2, 0), (2, 4, 4, 0, 1, 3)));
        Check(controls.BucketTableProblems.SequenceEqual([refused + "bucket 1's FIFO byte is 2, not 0 or 1.",
                refused + "bucket 2's no-transfer byte is 3, not 0 or 1."]) && controls.Buckets.All(bucket => bucket.Problem.Length == 0),
            "A FIFO or no-transfer byte above 1 was not a table problem.");
        foreach (int count in new[] { 0, InvestmentBuckets.Capacity + 1 })
        {
            var refusedTable = BucketTables(parent, $"buckets-count-{count}", Buckets(count));
            Check(refusedTable.Buckets.Count == 0 && refusedTable.Diagnostics.Contains($"Buckets: the bucket table declares {count} descriptors."),
                $"A bucket table declaring {count} descriptors was read.");
        }
    }

    /** A root naming only the item index and the bucket table, in an installation of its own. */
    private static InvestmentTables BucketTables(string parent, string name, byte[] buckets)
    {
        string install = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        var root = new Blob(8 + 115 * 16);
        for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
        root.U(8 + InvestmentRootSlots.Items * 16, First + 1); root.U(8 + InvestmentRootSlots.Buckets * 16, First + 3);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_buckets_fixture_0100_0.pkg"), 0x100,
        [
            new(InvestmentRoot.RootClass, 8 << 9, root.ToArray()), new(0x80807BE5, 8 << 9, Index([])), new(0x80805CDA, 8 << 9, Index([])),
            new(AnyBucketClass, 8 << 9, buckets)
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        return InvestmentCatalog.Load(packages, sources: new(Items: First + 1, Displays: First + 2, StringBanks: uint.MaxValue)).Tables();
    }

    /** A bucket table declaring count descriptors, followed by the given rows: id, first slot, slot count, selector, FIFO, no transfer. */
    private static byte[] Buckets(int count, params (byte Id, int First, int Slots, byte Selector, byte Fifo, byte NoTransfer)[] rows)
    {
        const int countOffset = 140, firstDescriptor = 144, size = 36;
        var blob = new Blob(firstDescriptor + rows.Length * size);
        blob.U(countOffset, (uint)count);
        for (int i = 0; i < rows.Length; i++)
        {
            int at = firstDescriptor + i * size;
            blob.B(at, rows[i].Id); blob.U(at + 4, (uint)rows[i].First); blob.U(at + 8, (uint)rows[i].Slots);
            blob.B(at + 24, rows[i].Selector); blob.B(at + 25, rows[i].Fifo); blob.B(at + 26, rows[i].NoTransfer);
        }
        return blob.ToArray();
    }

    private static void Ambiguous(string parent)
    {
        string install = Path.Combine(parent, "ambiguous-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_ambiguous_fixture_0100_0.pkg"), 0x100,
        [
            new(InvestmentRoot.RootClass, 8 << 9, Root()), new(InvestmentRoot.RootClass, 8 << 9, Root()),
            new(0x80807BE5, 8 << 9, Index([])), new(0x80805CDA, 8 << 9, Index([]))
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var catalog = InvestmentCatalog.Load(packages, sources: new(Items: First + 2, Displays: First + 3, StringBanks: uint.MaxValue));
        Check(catalog.Tables().Root is null && catalog.Tables().Diagnostics.Contains("The investment root is ambiguous."), "Two roots resolved to one.");
        var explicitRoot = InvestmentCatalog.Load(packages, sources: new(Items: First + 2, Displays: First + 3, StringBanks: uint.MaxValue, Root: First + 1));
        Check(explicitRoot.Tables().Root?.Tag == First + 1, "An explicit root tag was not used as-is.");
    }

    /** Reverse indexes: who names each site and slot-79 row, who reads each saved row (through shared rows too), and who gives an item. */
    private static void References(PackageCatalog packages, InvestmentTables tables)
    {
        var references = tables.References;
        Check(references.SiteIndices.SequenceEqual([FixtureSite, SecondSite]) && references.Sites(FixtureSite).SequenceEqual([
                new RewardReference(RewardReferenceKind.CompletionPrimary, 3, -1), new(RewardReferenceKind.CompletionPrimary, 5, -1),
                new(RewardReferenceKind.PoolEntry, 0, 3), new(RewardReferenceKind.PoolEntry, 1, 0), new(RewardReferenceKind.Acquire, 2, -1),
                new(RewardReferenceKind.SocketSite, 0, 0)])
            && references.Sites(SecondSite).SequenceEqual([new RewardReference(RewardReferenceKind.CompletionSecondary, 5, -1),
                new(RewardReferenceKind.Acquire, 1, -1)])
            && references.Sites(5).Count == 0, "Reward site references were not indexed: " + string.Join(", ", references.Sites(FixtureSite)));
        Check(references.ItemReferences(0).SequenceEqual([(new RewardReference(RewardReferenceKind.SocketSite, 0, 0), FixtureSite),
                (new RewardReference(RewardReferenceKind.SocketList, 0, 0), FixtureSlot79Row)])
            && references.ItemReferences(5).SequenceEqual([(new RewardReference(RewardReferenceKind.CompletionPrimary, 5, -1), FixtureSite),
                (new RewardReference(RewardReferenceKind.CompletionSecondary, 5, -1), SecondSite)])
            && references.ItemReferences(1).SequenceEqual([(new RewardReference(RewardReferenceKind.Acquire, 1, -1), SecondSite)])
            && references.ItemReferences(4).Count == 0, "An item's own site references were not listed in order.");
        Check(references.Slot79Indices.SequenceEqual([FixtureSlot79Row])
            && references.Slot79(FixtureSlot79Row).SequenceEqual([new RewardReference(RewardReferenceKind.SocketList, 0, 0)]),
            "The socket lane's slot-79 row was not indexed.");
        var unlocks = tables.Unlocks;
        UnlockBinding Flag(uint slot) => unlocks.Binding(new(InvestmentUnlocks.FlagOpcode, slot, 0)) ?? throw new InvalidDataException("A flag did not bind.");
        UnlockBinding Value(uint slot) => unlocks.Binding(new(InvestmentUnlocks.ValueOpcode, slot, 0)) ?? throw new InvalidDataException("A value did not bind.");
        // Slots 2 and 6 name account flag row 1, so every use of either is a use of that row; slot 3 and value slot 7 have no bank row.
        Check(Flag(2) == new UnlockBinding(false, 1, 1, -1) && Flag(6) == Flag(2) && Flag(5) != Flag(2)
            && Value(7) == new UnlockBinding(true, 0, 0, 7) && Flag(3) == new UnlockBinding(false, 0, 0, 3)
            && unlocks.Binding(new(InvestmentUnlocks.ConstantOpcode, 1, 0)) is null, "Unlock rows were bound by the wrong key.");
        Check(references.Users(Flag(2)).SequenceEqual([new UnlockUse(UnlockUseKind.ObjectiveSource, 1, -1, 6, -1),
                new(UnlockUseKind.ObjectiveTarget, 1, -1, 2, -1), new(UnlockUseKind.WrapperAcquired, 1, -1, 2, -1)]),
            "Two slots on one bank row did not share their uses: " + string.Join(", ", references.Users(Flag(2))));
        // Pool entry 1's weight rule, pass row 2's second condition and item 3's preview reach flag slot 0 through shared rows 1 and 0.
        Check(references.Users(Flag(0)).SequenceEqual([new UnlockUse(UnlockUseKind.PoolCondition, 0, 0, 0, -1),
                new(UnlockUseKind.PoolWeightRule, 0, 1, 0, 0), new(UnlockUseKind.PassCondition, 1, 2, 0, -1),
                new(UnlockUseKind.PassCondition, 1, 2, 0, 0), new(UnlockUseKind.PreviewCondition, 3, 0, 0, 0)]),
            "Shared rows were not expanded into their uses: " + string.Join(", ", references.Users(Flag(0))));
        Check(references.Users(Flag(4)).SequenceEqual([new UnlockUse(UnlockUseKind.PassClaim, 1, 0, 4, -1), new(UnlockUseKind.PassClaim, 1, 2, 4, -1)])
            && references.Users(Value(0)).SequenceEqual([new UnlockUse(UnlockUseKind.CompletionValue, 3, -1, 0, -1), new(UnlockUseKind.ChainValue, 4, -1, 0, -1)])
            && references.Users(Value(1)).SequenceEqual([new UnlockUse(UnlockUseKind.PoolCondition, 0, 0, 1, -1), new(UnlockUseKind.ContextValue, 3, -1, 1, -1)])
            && references.Users(Value(7)).SequenceEqual([new UnlockUse(UnlockUseKind.ObjectiveSource, 0, -1, 7, -1)]),
            "Claims, pursuit values or an item value lost their uses.");
        Check(references.PoolsGiving(0).SequenceEqual([(0, 0), (0, 1)]) && references.PoolsGiving(1).SequenceEqual([(1, 0)])
            && references.PassRowsOf(0).SequenceEqual([(1, 0), (1, 1)]) && references.PassRowsOf(1).SequenceEqual([(1, 2)])
            && references.PreviewedBy(0).SequenceEqual([3]) && references.PreviewedBy(1).Count == 0, "The reverse reward links are wrong.");
        Check(tables.PassRowsOfPool(1).SequenceEqual([(1, 2)]) && tables.PassRowsOfPool(2).Count == 0
            && tables.RewardGraph.Closure(0).SequenceEqual([0, 2]) && tables.RewardGraph.Closure(1).SequenceEqual([1])
            && tables.RewardGraph.Closure(-1).Count == 0, "Pass rows by pool or a pool closure are wrong.");
        Check(tables.QuestOwner(3) == 4 && tables.QuestOwner(0) == -1 && tables.PursuitShape(3) == InvestmentPursuitShape.Quest
            && tables.PursuitShape(4) == InvestmentPursuitShape.Quest && tables.PursuitShape(0) == InvestmentPursuitShape.None,
            "Pursuit shapes or quest owners were misread.");
        byte[] step = packages.Read(First + FirstItemEntry + 5);
        Check(InvestmentPursuits.Classify(step) == (InvestmentPursuitShape.Quest, PursuitsId)
            && InvestmentPursuits.Classify(packages.Read(First + FirstItemEntry + 4)) == (InvestmentPursuitShape.Quest, (byte)37)
            && InvestmentPursuits.Classify(packages.Read(First + FirstItemEntry)) == (InvestmentPursuitShape.None, (byte)0),
            "An item was classified onto the wrong pursuit page.");
        // The same pursuit naming no quest owner is a single-step pursuit.
        int block = 0x30 + (int)BinaryPrimitives.ReadInt64LittleEndian(step.AsSpan(0x30));
        BinaryPrimitives.WriteUInt16LittleEndian(step.AsSpan(block + 0x1C), ushort.MaxValue);
        Check(InvestmentPursuits.Classify(step) == (InvestmentPursuitShape.Single, PursuitsId), "A pursuit outside a chain was not single-step.");
        var slots = RewardSitesPage.RootSlots(tables, _ => true);
        var omitted = Enumerable.Range(0, tables.Root!.SlotCount).Where(slot => tables.Root.SlotTag(slot) == InvestmentRoot.OmittedSlot).ToArray();
        Check(slots.Select(row => row.Slot).Order().SequenceEqual(omitted)
            && slots[0] == new RewardSitesPage.RootSlot(InvestmentRootSlots.Slot84, "Reward sites", "Not in this install", "8")
            && slots[1] == new RewardSitesPage.RootSlot(79, "Slot 79", "Not in this install", "1")
            && slots.Skip(1).All(row => row.Table == $"Slot {row.Slot}" && row.State == "Not in this install")
            && slots.Skip(2).All(row => row.References == LiveGate.NoValue), "The root slots did not list every omitted slot, naming only 84.");
    }

    /**
     * The Rewards page model of the engram (item 1: its draw table, two sites and what gives it) and of a plain item, the
     * draw table's graph with its cycle, the graph's size cap, and pool search by an entry item's index and hash.
     */
    private static void Rewards(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        InvestmentItemDetail Detail(int item) => catalog.ReadItem(First + FirstItemEntry + (uint)item, packages.Read(First + FirstItemEntry + (uint)item));
        var none = new InvestmentVendorSales(new Dictionary<int, int[]>(), 0, []);
        var engram = Detail(1);
        var model = ItemRewardsPage.Build(catalog, tables, engram, none);
        Check(model.DrawTable.Select(line => (line.Text, line.Link)).SequenceEqual([("Opens when acquired \u00B7 Draws 1 from category 0x00000000", RewardLinkKind.None),
                ("Pool 1 \u00B7 1 entry", RewardLinkKind.Pool)]) && model.AcquiredFlag?.Value == tables.Unlocks.FlagLabel(2).Text,
            "The engram's draw table lines are wrong: " + string.Join(" | ", model.DrawTable.Select(line => line.Text)));
        Check(model.Sites.Select(line => (line.Text, line.Detail, line.Link, line.Target)).SequenceEqual([
                ($"Site {SecondSite}", "Acquire", RewardLinkKind.Site, SecondSite), ($"Site {FixtureSite}", "Pool 1, entry 0", RewardLinkKind.Site, FixtureSite)]),
            "The engram's reward sites are wrong: " + string.Join(" | ", model.Sites.Select(line => $"{line.Text} {line.Detail}")));
        Check(model.GivenBy.Select(line => (line.Text, line.Link, line.Target, line.Element)).SequenceEqual([
                ("Pool 1, entry 0", RewardLinkKind.Pool, 1, 0), ("Season pass rank 3 \u00B7 progression 1", RewardLinkKind.PassRow, 1, 2)])
            && model.Previews.Single() is { Text: "No preview", Style: RewardLineStyle.Muted },
            "The engram's givers are wrong: " + string.Join(" | ", model.GivenBy.Select(line => line.Text)));
        var plain = ItemRewardsPage.Build(catalog, tables, Detail(4), none);
        Check(plain.DrawTable.Single().Text == "No draw table" && plain.AcquiredFlag is null && plain.Sites.Single().Text == "None referenced"
            && plain.Previews.Single().Text == "No preview" && plain.GivenBy.Single().Text == "Nothing in this install gives this item.",
            "A plain item's Rewards page is not one empty line per section.");
        Check(ItemRewardsPage.Build(catalog, tables, Detail(4), null).GivenBy.Single().Text == "Reading vendors."
            && ItemRewardsPage.Build(catalog, tables, Detail(0), new(new Dictionary<int, int[]>(), 1, ["Vendor: broken."])).GivenBy
                .Select(line => line.Text).SequenceEqual(["Pool 0, entry 0", "Pool 0, entry 1", "Season pass rank 300 \u00B7 progression 1",
                    "Season pass rank 2 \u00B7 progression 1", "1 vendor not read.", $"{catalog.Items[3].Name} (preview)"]),
            "Vendor loading, vendor problems or preview givers are not listed.");
        // Item 1 opens pool 1, which grants item 1 again: the item and its pool form a cycle.
        string Row(string name, string suffix) => name + suffix;
        var graph = RewardClosureGraph.Build(catalog, tables, 1, engram.Wrapper!, Row);
        Check(graph is { TooLarge: false, Pools: 1 } && graph.Nodes.Select(node => node.Key).SequenceEqual(["item/1", "pool/1"])
            && graph.Edges.Select(edge => (edge.Source, edge.Target)).SequenceEqual([("item/1", "pool/1"), ("pool/1", "item/1")])
            && graph.Nodes.All(node => node.Tint == StudioTheme.Failure && node.Subtitle.EndsWith("cycle", StringComparison.Ordinal))
            && graph.Nodes[1].Rows!.SequenceEqual([$"{catalog.Items[1].Name} x1 \u00B7 2"]) && graph.Nodes.All(node => !node.Rows!.Any() || node.Rows!.Count == 1),
            "The engram's closure graph is wrong: " + string.Join(" | ", graph.Nodes.Select(node => $"{node.Key} {node.Subtitle}")));
        // Item 2's pool has no entries; pool 0 nests pool 2 and grants item 0 twice.
        var nested = RewardClosureGraph.Build(catalog, tables, 2, Detail(2).Wrapper! with { Pool = 0 }, Row);
        Check(nested.Nodes.Select(node => node.Key).SequenceEqual(["item/2", "pool/0", "pool/2"])
            && nested.Edges.Select(edge => (edge.Source, edge.Target)).SequenceEqual([("item/2", "pool/0"), ("pool/0", "pool/2")])
            && nested.Nodes[1].Rows!.SequenceEqual([$"{catalog.Items[0].Name} x2 \u00B7 -1", $"{catalog.Items[0].Name} x1 \u00B7 3.5",
                "Pool 7 (unreadable) \u00B7 1", "Pool 2 \u00B7 1"]) && nested.Nodes.All(node => node.Tint != StudioTheme.Failure || node.Key == "pool/2"),
            "A nested closure graph is wrong: " + string.Join(" | ", nested.Nodes.SelectMany(node => node.Rows ?? [])));
        Check(RewardClosureGraph.Build(catalog, tables, 2, Detail(2).Wrapper! with { Pool = 0 }, Row, limit: 1) is { TooLarge: true, Pools: 2, Nodes.Count: 0 },
            "A closure above the cap was graphed.");
        // Pool 1 gives item 1; its search text names the item's index and hash, and no other pool's does.
        var item = catalog.Items[1];
        string[] Lines(int pool) => RewardPoolsPage.SearchText(catalog, tables, pool, RewardPoolsPage.Label(catalog, tables, pool)).Split('\n');
        Check(Lines(1).Contains(item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)) && Lines(1).Contains($"{item.Hash:X8}")
            && Enumerable.Range(0, tables.Pools.Count).Where(pool => Lines(pool).Contains($"{item.Hash:X8}")).SequenceEqual([1]),
            "Pool search does not find an entry by its item's index and hash.");
    }

    /**
     * Which page opens each pursuit shape, the quest chain's nodes with held and unreadable steps, and the inspector's
     * sources, completion plan and saved values for the fixture's quest steps (items 3 and 5 of item 4's quest set).
     */
    private static void PursuitPages(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        StudioDocument Document(int item, byte[] bytes) => new(new PackageEntry(First + FirstItemEntry + (uint)item, InvestmentCatalog.ItemClass, 8, 0,
            bytes.Length, "fixture", 0x100, item), $"Item {item}", bytes, new AssetInspection("Item", "fixture", [], [], []), null, null, "");
        byte[] member = packages.Read(First + FirstItemEntry + 5);
        // The same pursuit naming no quest owner is a single-step pursuit; moved to bucket 13, the Investment page keeps its objectives.
        byte[] single = (byte[])member.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(single.AsSpan(0x30 + (int)BinaryPrimitives.ReadInt64LittleEndian(single.AsSpan(0x30)) + 0x1C), ushort.MaxValue);
        byte[] other = (byte[])single.Clone(); other[184] = 13;
        var quest = new QuestPage(); var pursuit = new PursuitPage();
        var documents = new[] { ("member", Document(5, member)), ("owner", Document(4, packages.Read(First + FirstItemEntry + 4))),
            ("bounty", Document(5, single)), ("other", Document(5, other)), ("plain", Document(0, packages.Read(First + FirstItemEntry))) };
        string Opens(StudioDocument document) => (quest.CanOpen(document) ? "quest" : "") + (pursuit.CanOpen(document) ? "pursuit" : "")
            + (InvestmentPage.ShowsObjectives(document) ? "investment" : "");
        Check(documents.Select(row => Opens(row.Item2)).SequenceEqual(["quest", "quest", "pursuit", "investment", ""]),
            "The pursuit pages open the wrong items: " + string.Join(", ", documents.Select(row => $"{row.Item1}={Opens(row.Item2)}")));
        var chain = catalog.ReadQuestSet(4);
        Check(chain.Steps.Select(step => step.ItemIndex).SequenceEqual(new ushort[] { 3, 5 }) && chain.Error.Length == 0, "The owner's quest set was not read.");
        try { catalog.ReadQuestSet(ItemCount); throw new InvalidDataException("A quest owner outside the item index was read."); }
        catch (ArgumentOutOfRangeException) { }
        QuestStepDetail Step(int item) => new(item, catalog.ReadItem(First + FirstItemEntry + (uint)item, packages.Read(First + FirstItemEntry + (uint)item)), "");
        string Row(string text, string suffix) => text + suffix;
        var context = new LiveInvestmentContext(1, 2, 3);
        LiveHeldPursuit Held(int item, LivePursuitFlags flags, LivePursuitReason reason, uint itemFlags, params LivePursuitObjective[] objectives) =>
            new(0x4000 + (ulong)item, catalog.Items[item].Hash, 7, 1, itemFlags, flags, reason, objectives);
        var known = Held(3, LivePursuitFlags.ObjectivesKnown, LivePursuitReason.ObjectivesIncomplete, 0,
            new LivePursuitObjective(tables.Objectives[0].Hash, 4, 10, LivePursuitObjectiveFlags.Known));
        var model = QuestChainView.Build(catalog, tables, chain, [Step(3), Step(5)], 5, Row);
        string first = OneLine(tables, 0), target = tables.Objectives[0].CompletionValue.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        // One node per step and one unlabelled link per step after the first, in authored order.
        Check(model.Nodes.Count == chain.Steps.Count && model.Edges.Count == chain.Steps.Count - 1
            && model.Edges.Single() == new CanvasEdge("step/0", "step/1", false, Tip: "Next in authored order.")
            && model.Nodes.All(node => node.Rows!.Count <= QuestChainView.ObjectiveRows),
            "The quest model is not one node per step and one link per step after the first.");
        // Item 3 has eight objectives: three lines with their targets and a count of the rest; its one preview is a chip of 3.
        Check(model.Nodes.All(node => node.Tint is null && node.Note.Length == 0) && model.Nodes[1].Subtitle.EndsWith("This step", StringComparison.Ordinal)
            && model.Nodes[0].Rows!.SequenceEqual([$"{first} \u00B7 {target}", $"{OneLine(tables, 1)} \u00B7 1", $"{OneLine(tables, 2)} \u00B7 1", "+5 objectives"])
            && model.Nodes[0].Chips!.Single() == new CanvasChip(0, "3", tables.WrapperPool(0) >= 0) && model.Rewards["step/0"].SequenceEqual([0])
            && model.Tips["step/0#0"].StartsWith($"{catalog.Items[0].Name}\nx3 \u00B7 Conditional", StringComparison.Ordinal),
            "The step's tint, rows or reward chips are wrong: " + string.Join(" | ", model.Nodes[0].Rows!));
        var held = QuestChainView.Build(catalog, tables, chain, [Step(3), Step(5)], 5, Row, new HashSet<int> { 3 });
        Check(held.Nodes[0] is { Tint: var tint, Subtitle: var subtitle } && tint == QuestChainView.HeldTint && subtitle.EndsWith("Held", StringComparison.Ordinal)
            && held.Nodes[1].Tint is null, "A held step was not tinted and marked.");
        var unsaved = Held(5, LivePursuitFlags.Bounty, LivePursuitReason.ItemProgressNotSaved, 0x2,
            new LivePursuitObjective(tables.Objectives[0].Hash, 0, 10, LivePursuitObjectiveFlags.None));
        var broken = QuestChainView.Build(catalog, tables, chain, [new(3, null, "Broken."), Step(5)], 5, Row);
        Check(broken.Nodes[0].Tint == StudioTheme.Failure && broken.Nodes[0].Rows!.SequenceEqual(["Not read."]) && broken.Nodes[0].Chips is null
            && broken.Nodes[1].Rows!.SequenceEqual([$"{first} \u00B7 {target}"]),
            "An unreadable step was not marked, or a read step lost its target.");
        Check(QuestChainView.Build(catalog, tables, chain, null, 5, Row).Nodes.All(node => node.Rows!.SequenceEqual(["Reading."]) && node.Tint is null),
            "Steps still being read did not say so.");
        Check(QuestChainView.Count(0) == "" && QuestChainView.Count(1) == "" && QuestChainView.Count(500) == "500" && QuestChainView.Count(1500) == "1.5k"
            && QuestChainView.Count(25000) == "25k" && QuestChainView.Count(2_500_000) == "2.5M", "Reward chip counts are not compact.");
        // Objective 0 reads item value slot 7, which item 3 keeps as its instance lane 0; objective 1 reads flag slot 6, which shares a bank row with slot 2.
        var detail = Step(3).Detail!;
        var sources = PursuitInspector.Sources(catalog, tables, detail.Pursuit!);
        var unlocks = tables.Unlocks;
        var read = sources[1].Rows.Single();
        // The item lane keeps no saved row, so objective 0 lists none.
        Check(sources.Select(row => row.Ordinal).SequenceEqual(Enumerable.Range(0, 8)) && sources[0].Rows.Count == 0
            && read is { ViaShared: -1, Slot: 6, Uses: 3 }
            && read.Label == (unlocks.FlagLabel(6) is { Proven: true } proven ? proven.Text : $"Slot 6 \u00B7 {unlocks.FlagLabel(6).Text}") && sources[2].Rows.Count == 0,
            "Objective sources were not listed: " + string.Join(" | ", sources.Select(row => $"{row.Ordinal}:{row.Rows.Count}")));
        Check(PursuitInspector.UseLines(catalog, tables, read.Binding!.Value, 6).Select(line => (line.Text, line.Link)).SequenceEqual([
                ($"{catalog.Items[3].Name}: {OneLine(tables, 1)}", RewardLinkKind.Item), ($"{catalog.Items[3].Name}: {OneLine(tables, 1)} (target) (slot 2)", RewardLinkKind.Item),
                ($"Acquisition flag of {catalog.Items[1].Name} (slot 2)", RewardLinkKind.Item)]),
            "A saved row's other uses were not listed with their slots: "
            + string.Join(" | ", PursuitInspector.UseLines(catalog, tables, read.Binding!.Value, 6).Select(line => line.Text)));
        // Pass row 2 reads flag slot 0 directly and through shared row 0: one row, one line.
        var flag0 = unlocks.Binding(new(InvestmentUnlocks.FlagOpcode, 0, 0))!.Value;
        Check(PursuitInspector.UseLines(catalog, tables, flag0, 0).Select(line => line.Text).SequenceEqual(["Pool 0, entry 0 condition", "Pool 0, entry 1 weight rule",
                $"Season pass rank 3 \u00B7 row 2 condition", $"{catalog.Items[3].Name} preview 0"]),
            "Uses of one row reached through shared rows were not merged: "
            + string.Join(" | ", PursuitInspector.UseLines(catalog, tables, flag0, 0).Select(line => line.Text)));
        Check(unlocks.Reads(new InvestmentExpression(0, [new(InvestmentUnlocks.SharedOpcode, 1, 0)])).Select(row => (row.Instruction.Opcode, row.Instruction.Operand, row.ViaShared))
                .SequenceEqual([(InvestmentUnlocks.FlagOpcode, 0u, 0)])
            && unlocks.Reads(new InvestmentExpression(0, [new(InvestmentUnlocks.SharedOpcode, 2, 0)])).Count == 0,
            "Shared rows were not inlined into an expression's reads, or a cycle was followed.");
        var saved = PursuitInspector.Saved(unsaved);
        Check(saved.Select(row => row.Name).SequenceEqual(["Instance", "Serial", "Quantity", "Item flags"])
            && saved[3].Value == "0x00000002 \u00B7 Tracked" && !PursuitInspector.Saved(known)[3].Value.Contains("Tracked", StringComparison.Ordinal),
            "The saved values do not mark the tracked flag.");
        Check(PursuitInspector.Installed(catalog, tables, detail.Item, detail.Pursuit).Select(row => (row.Name, row.Value)).SequenceEqual([
                ("Item", $"3 \u00B7 0x{detail.Item.Hash:X8}"), ("Quest owner", $"{catalog.Items[4].Name} \u00B7 item 4"),
                ("Chain value", unlocks.ValueLabel(0).Text), ("Primary", $"Site {FixtureSite}"), ("Secondary", "None"),
                ("Context value", unlocks.ValueLabel(1).Text), ("Completion value", unlocks.ValueLabel(0).Text), ("Presence flags", unlocks.FlagLabel(5).Text),
                ("Item values", unlocks.ValueLabel(7).Text)]),
            "The installed data rows are wrong: " + string.Join(" | ", PursuitInspector.Installed(catalog, tables, detail.Item, detail.Pursuit).Select(row => row.Value)));
        Check(RewardSitesPage.CompletionPage(tables, catalog, new(RewardReferenceKind.CompletionPrimary, 3, -1)) == QuestPage.PageId
            && RewardSitesPage.CompletionPage(tables, catalog, new(RewardReferenceKind.Acquire, 3, -1)) is null,
            "A completion reference does not open where its pursuit's completion is shown.");
        quest.Dispose(); pursuit.Dispose();
        Board(catalog, tables, context, known, unsaved);
    }

    /**
     * The held pursuit board: the game's bounty flag wins over the installed lifetime, which places the rows it did not
     * report; unknown progress is never drawn as none done, and progress this server never saves is not marked unknown; the
     * newest change comes first.
     */
    private static void Board(InvestmentCatalog catalog, InvestmentTables tables, LiveInvestmentContext context, LiveHeldPursuit known,
        LiveHeldPursuit unsaved)
    {
        // Item 3's lifetime is one constant row of 3600 seconds, item 5's a computed two-row expression, item 0 has no objective block.
        Check(tables.Expires(3) && !tables.Expires(5) && !tables.Expires(0) && !tables.Expires(-1) && !tables.Expires(ItemCount),
            "The installed lifetime does not mark the pursuit Sunrise reports as a bounty.");
        LiveHeld Row(int item, int serial) => new(LiveInventoryScope.CharacterItem, 0x4000 + (ulong)item, catalog.Items[item].Hash, 1, serial,
            InvestmentPursuits.PursuitBucket, false);
        LiveHeld[] rows = [Row(5, 7), Row(3, 9), Row(0, 8)];
        var installed = LiveInvestmentSection.PursuitCards(catalog, tables, rows, null);
        Check(installed.Quests.Select(card => card.Item!.Index).SequenceEqual([0, 5]) && installed.Bounties.Single().Item!.Index == 3
            && installed.Quests.Concat(installed.Bounties).All(card => card is { Status: null, Progress: IconGrid.NoProgress, Unknown: false, Tracked: false }),
            "Without pursuit progress the board did not place rows by their installed lifetime, or drew progress.");
        var complete = new LiveHeldPursuit(0x4000, catalog.Items[0].Hash, 8, 1, 0,
            LivePursuitFlags.Bounty | LivePursuitFlags.ObjectivesKnown | LivePursuitFlags.ObjectivesComplete, LivePursuitReason.ManualActionNotEstablished,
            [new LivePursuitObjective(tables.Objectives[0].Hash, 10, 10, LivePursuitObjectiveFlags.Known | LivePursuitObjectiveFlags.Complete)]);
        var reported = LiveInvestmentSection.PursuitCards(catalog, tables, rows, new(context, 1, [known, unsaved, complete]));
        var (quest, bounties) = (reported.Quests.Single(), reported.Bounties);
        Check(quest is { Bounty: false, Progress: 0, Unknown: false, Complete: false } && quest.Item!.Index == 3
            && bounties.Select(card => card.Item!.Index).SequenceEqual([0, 5]),
            "The game's bounty flag did not decide the tabs, or the newest change did not come first.");
        Check(bounties[0] is { Progress: 1, Complete: true, Unknown: false, Tracked: false }
            && bounties[0].Tip.Contains("1 of 1 objectives", StringComparison.Ordinal),
            "A complete bounty was not marked complete.");
        Check(bounties[1] is { Progress: IconGrid.NoProgress, Unknown: false, Complete: false, Tracked: true }
            && !bounties[1].Tip.Contains(" objectives", StringComparison.Ordinal) && !bounties[1].Tip.Contains("Progress unknown", StringComparison.Ordinal)
            && bounties[1].Tip.Contains("Tracked", StringComparison.Ordinal)
            && bounties[1].Tip.EndsWith(StudioUi.MayBeInaccurate, StringComparison.Ordinal) && !quest.Tip.Contains(StudioUi.MayBeInaccurate, StringComparison.Ordinal),
            "Unsaved bounty progress was drawn or marked unknown: " + bounties[1].Tip.Replace('\n', '|'));
        var other = known with { Reason = LivePursuitReason.ObjectiveSourceUnavailable, Flags = LivePursuitFlags.None };
        Check(LiveInvestmentSection.PursuitCards(catalog, tables, [Row(3, 1)], new(context, 1, [other])).Quests.Single().Tip
                .Contains("Progress unknown: objective source unavailable", StringComparison.Ordinal),
            "Unknown progress for another reason does not name it.");
    }

    private static string OneLine(InvestmentTables tables, int objective) =>
        PursuitInspector.OneLine(InvestmentTableViews.ObjectiveName(tables, tables.Objectives[objective]).Text);

    /** Every prefix of two rich items loads: link reading never throws, and only a cut through the fixed fields is reported. */
    private static void TruncatedItems(string parent)
    {
        string install = Path.Combine(parent, "truncated-items");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[][] full = Items();
        // Lengths step by four bytes, so cuts land inside every block and field.
        const int step = 4;
        var cuts = new[] { full[0], full[3] }.SelectMany(item => Enumerable.Range(1, item.Length / step).Select(length => item[..(length * step)]))
            .ToArray();
        var root = new Blob(8 + 115 * 16);
        for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
        root.U(8 + InvestmentRootSlots.Items * 16, First + 1);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_truncated_fixture_0100_0.pkg"), 0x100,
        [
            new(InvestmentRoot.RootClass, 8 << 9, root.ToArray()),
            new(0x80807BE5, 8 << 9, Index(cuts.Select((_, i) => ((uint)(0x2000 + i), First + 3 + (uint)i)).ToArray())),
            new(0x80805CDA, 8 << 9, Index([])),
            .. cuts.Select(item => new AuthoredPackageEntry(InvestmentCatalog.ItemClass, 8 << 9, item))
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var catalog = InvestmentCatalog.Load(packages, sources: new(Items: First + 1, Displays: First + 2, StringBanks: uint.MaxValue));
        var shapes = cuts.Select(item => InvestmentPursuits.Classify(item)).ToArray();
        Check(catalog.Items.Count == cuts.Length && catalog.Diagnostics.All(line => line.EndsWith(": Item fixed fields are truncated.", StringComparison.Ordinal))
            && catalog.Tables().References.SiteIndices.SequenceEqual([FixtureSite]), "A truncated item failed to load: " + string.Join(" | ", catalog.Diagnostics.Take(3)));
        Check(shapes[^1] == (InvestmentPursuitShape.Quest, PursuitsId) && shapes[0] == (InvestmentPursuitShape.None, byte.MaxValue),
            "A truncated item was classified from bytes it does not have.");
    }

    /** Measured on the installed build; a different build changes these on purpose. */
    public static void Owned(string install, string? keys)
    {
        using var packages = PackageCatalog.Open(install, keys, new() { Enabled = false });
        var catalog = InvestmentCatalog.Load(packages);
        var timer = Stopwatch.StartNew();
        var tables = catalog.Tables();
        timer.Stop();
        Check(tables.Root is { SlotCount: 119 } && tables.Slot84.State == InvestmentSlotState.Omitted, "The installed root changed.");
        Check(tables.Pools.Count == 979 && tables.Pools.Sum(pool => pool.Entries.Count) == 10549, "Installed reward pool counts changed.");
        Check(tables.Progressions.Count == 88 && tables.PassCandidates.Select(row => row.Index).SequenceEqual([32, 34, 36, 40])
            && tables.Progressions[40].Rewards.Count == 196, "Installed progression counts changed.");
        Check(tables.Objectives.Count == 8483 && tables.Objectives.All(row => row.Error.Length == 0), "Installed objective counts changed.");
        string progress = OwnedProgressSources(tables);
        int described = Enumerable.Range(0, tables.Objectives.Count).Count(index => tables.ObjectiveText(index).Length != 0);
        // No diagnostic means every display row repeats its objective's hash.
        Check(!tables.Diagnostics.Any(line => line.StartsWith("Objective text", StringComparison.Ordinal)) && described == 5886,
            "Installed objective descriptions changed.");
        Check(!tables.Diagnostics.Any(line => line.Contains("disagree with their mapping table", StringComparison.Ordinal))
            && tables.Unlocks.GraphDiagnostics.Count == 0 && tables.RewardGraph.Cycles.Count == 0,
            "Installed unlock bindings disagree or the shared graph is broken.");
        string buckets = OwnedBuckets(catalog, tables);
        string passItems = OwnedPassItems(catalog, packages, tables);
        string unlockSources = OwnedUnlockSources(tables.Unlocks);
        Check(tables.Pools.SelectMany(pool => pool.Entries).All(entry => entry.Error.Length == 0)
            && tables.Progressions[40].Rewards.All(row => row.Error.Length == 0), "Installed reward rows report errors.");
        int deepest = tables.Objectives.Max(row => tables.Unlocks.Depth(row.Source));
        Check(deepest > InvestmentUnlocks.ServerNestingLimit, "Installed objective nesting no longer exceeds Sunrise's limit.");
        int pursuits = 0, eight = 0, chains = 0, previews = 0, wrappers = 0, lifetimes = 0, longest = 0;
        var vendorTimer = Stopwatch.StartNew();
        var vendors = catalog.VendorSales();
        vendorTimer.Stop();
        var rewards = new OwnedRewardPages();
        var pursuitPages = new OwnedPursuitPages();
        foreach (var item in catalog.Items)
        {
            var detail = catalog.ReadItem(item.Tag, packages.Read(item.Tag));
            rewards.Add(catalog, tables, detail, vendors);
            pursuitPages.Add(catalog, tables, item, packages.Read(item.Tag), detail);
            if (detail.Wrapper is not null) wrappers++;
            if (detail.Pursuit is not { } pursuit) continue;
            pursuits++; previews += pursuit.Previews.Count != 0 ? 1 : 0; chains += pursuit.Chain is { Error: "" } ? 1 : 0;
            eight += pursuit.Objectives.Count == 8 ? 1 : 0; lifetimes += pursuit.LifetimeSeconds is not null ? 1 : 0;
            longest = Math.Max(longest, pursuit.Chain?.Steps.Count ?? 0);
        }
        Check(pursuits == 3167 && eight == 4 && wrappers == 444, "Installed pursuit or wrapper counts changed.");
        string references = OwnedReferences(catalog, tables, longest);
        string pursuitSummary = pursuitPages.Summary(catalog, packages, tables);
        string campaign = OwnedCampaign(catalog, packages, tables);
        string pages = rewards.Summary(catalog, packages, tables, vendors, vendorTimer.ElapsedMilliseconds);
        var sockets = tables.Pools.SelectMany(pool => pool.Entries).SelectMany(entry => entry.Sockets)
            .Concat(tables.Progressions.SelectMany(row => row.Rewards).SelectMany(row => row.Sockets))
            .GroupBy(socket => InvestmentTableViews.SocketOverride(catalog, socket).Tip).ToDictionary(group => group.Key, group => group.Count());
        var sites = tables.Pools.SelectMany(pool => pool.Entries).Where(entry => entry.Slot84Reference != ushort.MaxValue)
            .Select(entry => InvestmentRewardTree.SiteTip(tables, entry)).ToArray();
        int fixedRolls = sockets.GetValueOrDefault("Extracted: fixed roll"), siteItems = sites.Count(tip => tip.EndsWith("grants its item.", StringComparison.Ordinal));
        // Measured: every plug-set override is also marked fixed, so the set check has to come before the fixed one.
        Check(fixedRolls == 791 && sockets.Single(pair => pair.Key.StartsWith("References", StringComparison.Ordinal)).Value == 1560
            && sites.Length == 221 && siteItems == 202, "Installed socket override or reward site shapes changed: "
            + string.Join(", ", sockets.Select(pair => $"{pair.Value} {pair.Key}")));
        Console.WriteLine($"investment_tables owned=pass load_ms={timer.ElapsedMilliseconds} pools={tables.Pools.Count} "
            + $"entries={tables.Pools.Sum(pool => pool.Entries.Count)} pass={string.Join('/', tables.PassCandidates.Select(row => row.Index))} "
            + $"objectives={tables.Objectives.Count} objective_text={described}/{tables.Objectives.Count} shared={tables.Unlocks.SharedCount} objective_depth={deepest} "
            + $"pool_depth={tables.RewardGraph.MaximumDepth} pursuits={pursuits} chains={chains} previews={previews} lifetimes={lifetimes} "
            + $"wrappers={wrappers} fixed_rolls={fixedRolls} sites={sites.Length} site_items={siteItems} {buckets} {passItems} {unlockSources} {references} {pages} "
            + $"{pursuitSummary} {campaign} {progress} "
            + $"diagnostics={tables.Diagnostics.Count}");
        foreach (string diagnostic in tables.Diagnostics) Console.WriteLine("investment_tables diagnostic=" + diagnostic);
    }

    /**
     * The installation's campaign: the one chain of thirty steps that each own a copy of it, found by that shape. Its steps
     * show no rewards and complete automatically, so every node says so and carries no chips, and its links keep authored order.
     */
    private static string OwnedCampaign(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        int owner = Campaign(catalog, tables);
        var chain = catalog.ReadQuestSet(owner);
        var details = chain.Steps.Select(step => QuestSteps.Read(catalog, packages, step.ItemIndex, default)).ToArray();
        Check(chain.Steps.All(step => tables.QuestOwner(step.ItemIndex) == step.ItemIndex)
            && details.All(step => step.Detail?.Pursuit is { Automatic: true, Previews.Count: 0 }),
            "The campaign's steps no longer own their chain copies, or now show rewards.");
        var model = QuestChainView.Build(catalog, tables, chain, details, chain.Steps[1].ItemIndex, (text, suffix) => text + suffix);
        Check(model.Nodes.All(node => node.Note == QuestChainView.AutomaticNote && node.Chips is null)
            && model.Edges.All(edge => edge is { Label: "", Tip: "Next in authored order.", Editable: false }),
            "The campaign does not read as automatic steps without rewards, linked in authored order.");
        return $"campaign={owner}:{chain.Steps.Count}";
    }

    /** The owner of the installation's campaign copy that starts at its own step: the one thirty-step chain whose every step owns a copy. */
    internal static int Campaign(InvestmentCatalog catalog, InvestmentTables tables) => QuestOwners(catalog, tables)
        .Single(owner => catalog.ReadQuestSet(owner) is { Steps.Count: 30 } chain && chain.Steps[0].ItemIndex == owner
            && chain.Steps.All(step => tables.QuestOwner(step.ItemIndex) == step.ItemIndex));

    /** Every item some item names as its quest-set owner, once each and in index order. */
    internal static int[] QuestOwners(InvestmentCatalog catalog, InvestmentTables tables) =>
        [.. Enumerable.Range(0, catalog.Items.Count).Select(tables.QuestOwner).Where(owner => owner >= 0).Distinct().Order()];

    /**
     * Every installed item's Rewards page model and every draw table's graph: the largest closure is exactly the graph's cap,
     * vendors read without problems, and the sale rows' item indices stay in the range the vendor reader keeps.
     */
    private sealed class OwnedRewardPages
    {
        private int sold, givers, sited, largest, largestNodes, refused;

        public void Add(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItemDetail detail, InvestmentVendorSales vendors)
        {
            var model = ItemRewardsPage.Build(catalog, tables, detail, vendors);
            if (model.GivenBy.Any(line => line.Link == RewardLinkKind.Vendor)) sold++;
            if (model.GivenBy[0].Style != RewardLineStyle.Muted) givers++;
            if (model.Sites[0].Style != RewardLineStyle.Muted) sited++;
            if (detail.Wrapper is not { } wrapper) return;
            var graph = RewardClosureGraph.Build(catalog, tables, detail.Item.Index, wrapper, (name, suffix) => name + suffix);
            if (graph.TooLarge) refused++;
            if (graph.Pools > largest) (largest, largestNodes) = (graph.Pools, graph.Nodes.Count);
        }

        public string Summary(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables, InvestmentVendorSales vendors, long milliseconds)
        {
            // The producer: a sale names its item by the u16 at +70 (InvestmentDetails.ReadVendor); count what the install holds.
            var items = catalog.Vendors.SelectMany(vendor => catalog.ReadVendor(vendor.Tag, packages.Read(vendor.Tag)).Sales).Select(sale => sale.ItemIndex).ToArray();
            int unset = items.Count(item => item == ushort.MaxValue), outside = items.Count(item => item != ushort.MaxValue && item >= catalog.Items.Count);
            Check(vendors.Problems.Count == 0 && vendors.Rows == items.Length && outside == 0, $"Installed vendor sales changed: {vendors.Problems.Count} problems, "
                + $"{outside} sale items outside the item table.");
            Check(largest == RewardClosureGraph.PoolLimit && refused == 0, $"The largest installed draw table closure is {largest} pools, not the graph cap.");
            // A pool's search text names each entry item's index and hash.
            var (pool, entry) = Enumerable.Range(0, tables.Pools.Count).SelectMany(row => tables.Pools[row].Entries.Where(item => item.ItemIndex < catalog.Items.Count)
                .Select(item => (row, (int)item.ItemIndex))).First();
            var lines = RewardPoolsPage.SearchText(catalog, tables, pool, RewardPoolsPage.Label(catalog, tables, pool)).Split('\n');
            Check(lines.Contains(entry.ToString(System.Globalization.CultureInfo.InvariantCulture)) && lines.Contains($"{catalog.Items[entry].Hash:X8}"),
                "Installed pool search does not name an entry's item index and hash.");
            return $"vendor_sales={vendors.Rows} ms={milliseconds} sale_unset={unset} sold_items={sold} given_items={givers} sited_items={sited} "
                + $"graph_pools={largest} graph_nodes={largestNodes}";
        }
    }

    /**
     * Every installed item's pursuit page as CanOpen decides it from the document bytes, checked against the shape the
     * tables index; every pursuit's inspector content, and every quest chain's nodes.
     */
    private sealed class OwnedPursuitPages
    {
        private readonly QuestPage quest = new();
        private readonly PursuitPage pursuit = new();
        private int quests, pursuits, investment, sourceRows, uses, bounties;

        public void Add(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItem item, byte[] bytes, InvestmentItemDetail detail)
        {
            var document = new StudioDocument(new PackageEntry(item.Tag, InvestmentCatalog.ItemClass, 8, 0, bytes.Length, "owned", 0, 0), item.Name, bytes,
                new AssetInspection("Item", item.Name, [], [], []), null, null, "");
            bool opensQuest = quest.CanOpen(document), opensPursuit = pursuit.CanOpen(document), keeps = InvestmentPage.ShowsObjectives(document);
            Check(InvestmentPursuits.Classify(bytes).Shape == tables.PursuitShape(item.Index) && (opensQuest ? 1 : 0) + (opensPursuit ? 1 : 0) + (keeps ? 1 : 0)
                == (tables.PursuitShape(item.Index) == InvestmentPursuitShape.None ? 0 : 1), $"Item {item.Index} opens on the wrong pursuit pages.");
            quests += opensQuest ? 1 : 0; pursuits += opensPursuit ? 1 : 0; investment += keeps ? 1 : 0;
            // The board's installed bounty rule agrees with the pursuit reader's constant lifetime on every held-pursuit item.
            if (item.Bucket == InvestmentPursuits.PursuitBucket)
            {
                bool expires = detail.Pursuit?.LifetimeSeconds > 0;
                Check(tables.Expires(item.Index) == expires, $"Item {item.Index}'s installed lifetime and its bounty placement disagree.");
                bounties += expires ? 1 : 0;
            }
            if (detail.Pursuit is not { } read) return;
            foreach (var objective in PursuitInspector.Sources(catalog, tables, read))
            {
                sourceRows += objective.Rows.Count;
                uses += objective.Rows.Sum(row => row.Uses);
            }
            Check(PursuitInspector.Installed(catalog, tables, item, read).Count >= 8, $"Item {item.Index} has no installed data.");
        }

        /**
         * Measured: 1,320 quest items, and 2,018 single-step pursuits of which 980 sit outside the Pursuits bucket; 987
         * Pursuits-bucket items have a positive lifetime, every one a single-step pursuit.
         */
        public string Summary(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
        {
            Check(quests == 1320 && pursuits == 2018 - 980 && investment == 980, $"Installed pursuit pages changed: quest={quests} pursuit={pursuits} investment={investment}.");
            Check(bounties == 987, $"Installed Pursuits-bucket lifetimes changed: {bounties} expire.");
            int owners = 0, nodes = 0, longest = -1;
            foreach (int owner in QuestOwners(catalog, tables))
            {
                var chain = catalog.ReadQuestSet(owner);
                var model = QuestChainView.Build(catalog, tables, chain, null, -1, (text, suffix) => text + suffix);
                Check(model.Nodes.Count == chain.Steps.Count && model.Edges.Count == Math.Max(0, chain.Steps.Count - 1), $"Quest {owner} drew the wrong nodes.");
                owners++; nodes += model.Nodes.Count;
                if (longest < 0 || chain.Steps.Count > catalog.ReadQuestSet(longest).Steps.Count) longest = owner;
            }
            // The longest chain with every step read: no node draws more than its objective summary.
            var steps = catalog.ReadQuestSet(longest);
            var details = steps.Steps.Select(step => new QuestStepDetail(step.ItemIndex,
                catalog.ReadItem(catalog.Items[step.ItemIndex].Tag, packages.Read(catalog.Items[step.ItemIndex].Tag)), "")).ToArray();
            var full = QuestChainView.Build(catalog, tables, steps, details, -1, (text, suffix) => text + suffix);
            Check(full.Nodes.All(node => node.Rows!.Count is > 0 and <= QuestChainView.ObjectiveRows && node.Tint is null), "The longest quest's nodes are wrong.");
            quest.Dispose(); pursuit.Dispose();
            return $"quest_pages={quests} pursuit_pages={pursuits} investment_objectives={investment} board_bounties={bounties} quest_owners={owners} quest_nodes={nodes} "
                + $"longest_quest={longest}:{steps.Steps.Count} source_rows={sourceRows} source_uses={uses}";
        }
    }

    /**
     * The reference indexes and pursuit shapes as measured with an independent byte probe: every site and lane index is a
     * u16 whose only absent value is 0xFFFF (no zero appears), and the largest wrapper closure, chain and owner count bound
     * the pages that show them.
     */
    private static string OwnedReferences(InvestmentCatalog catalog, InvestmentTables tables, int longestChain)
    {
        var references = tables.References;
        var sites = references.SiteIndices.SelectMany(references.Sites).GroupBy(reference => reference.Kind).ToDictionary(group => group.Key, group => group.Count());
        int lists = references.Slot79Indices.Sum(row => references.Slot79(row).Count);
        Check(sites.GetValueOrDefault(RewardReferenceKind.CompletionPrimary) == 2451 && sites.GetValueOrDefault(RewardReferenceKind.CompletionSecondary) == 507
            && sites.GetValueOrDefault(RewardReferenceKind.PoolEntry) == 221 && sites.GetValueOrDefault(RewardReferenceKind.Acquire) == 1849
            && sites.GetValueOrDefault(RewardReferenceKind.SocketSite) == 1651 && lists == 12002 && references.Slot79Indices.Count == 185
            && references.SiteIndices[0] != 0 && references.Slot79Indices[0] != 0,
            "Installed reward site references changed: " + string.Join(", ", sites.Select(pair => $"{pair.Key}={pair.Value}")) + $", slot79={lists}");
        int closure = Enumerable.Range(0, catalog.Items.Count).Where(item => tables.WrapperPool(item) >= 0)
            .Max(item => tables.RewardGraph.Closure(tables.WrapperPool(item)).Count);
        int owners = QuestOwners(catalog, tables).Length;
        var shapes = Enumerable.Range(0, catalog.Items.Count).GroupBy(tables.PursuitShape).ToDictionary(group => group.Key, group => group.ToArray());
        int single = shapes.GetValueOrDefault(InvestmentPursuitShape.Single)?.Length ?? 0;
        int outside = shapes.GetValueOrDefault(InvestmentPursuitShape.Single)?.Count(item => catalog.Items[item].Bucket != InvestmentPursuits.PursuitBucket) ?? 0;
        Check(closure == 26 && longestChain == 30 && owners == 807 && single == 2018 && outside == 980
            && shapes.GetValueOrDefault(InvestmentPursuitShape.Quest)?.Length == 1320, "Installed closures, chains or pursuit shapes changed.");
        int bindings = 0, uses = 0;
        foreach (var binding in tables.Objectives.SelectMany(row => row.Source.Instructions).Select(tables.Unlocks.Binding).OfType<UnlockBinding>().Distinct())
        { bindings++; uses += references.Users(binding).Count; }
        return $"reward_sites={references.SiteIndices.Count} slot79_rows={references.Slot79Indices.Count} site_references={sites.Values.Sum()} "
            + $"closure={closure} longest_chain={longestChain} owners={owners} single={single}/{outside} objective_rows={bindings} objective_row_uses={uses}";
    }

    /**
     * Keeping each installed pass row's own item passes every replacement check but the missing perk flag Sunrise refuses, and the
     * perk rows Sunrise grants at rank-up are counted from installed bytes. Item edits reach the game only through a patchable package.
     */
    private static string OwnedPassItems(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        uint tag = tables.TableTag(InvestmentRootSlots.Progressions);
        Check(packages.TryGetEntry(tag, out var entry) && !ModInstaller.IsStartupPackage(entry.PackageName),
            "The progression table is in a startup package, so Build and install would refuse a pass item edit.");
        var refused = tables.PassCandidates.SelectMany(ladder => ladder.Rewards)
            .Select(row => (Row: row, Refusal: SeasonPassPage.Replacement(catalog, tables, row, (int)row.ItemIndex).Refusal))
            .Where(row => row.Refusal.Length != 0).ToArray();
        // Sunrise grants a perk only through an account acquisition flag, so it refuses the claim of a perk row without one.
        var other = refused.Where(row => row.Refusal != RewardRefusals.PerkAcquisition).ToArray();
        Check(other.Length == 0, "Installed pass rows are refused with their own items for more than a missing perk flag: "
            + string.Join(", ", other.Select(row => $"{row.Row.Progression}/{row.Row.Index} {row.Refusal}")));
        Check(Enumerable.Range(0, catalog.Items.Count).All(item => tables.AcquiredFlagSlot(item) == ushort.MaxValue
                || tables.AcquiredFlagSlot(item) == tables.AcquisitionFlag(item)), "The two reads of an item's acquisition flag disagree.");
        var pass = tables.Progressions[40];
        int automatic = pass.Rewards.Count(row => SeasonPassPage.Automatic(catalog, tables, (int)row.ItemIndex));
        // Measured: 35 rows of pass 40 name a perk-bucket item without a pool; 3 of them have no account acquisition flag.
        Check(automatic == 32, $"Installed automatic perk rows of progression 40 changed: {automatic}.");
        byte[] bytes = packages.Read(tag);
        var field = pass.Rewards[0].ItemField;
        string last = (catalog.Items.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(catalog.EditTableField(field, bytes, last).AsSpan(field.Property.Offset)) == catalog.Items.Count - 1,
            "The last installed item index was not accepted as a pass item.");
        Reject(() => catalog.EditTableField(field, bytes, catalog.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            $"Item must be an index of this installation's item table (0 to {last}).");
        return $"pass_package={entry.PackageName} pass_automatic={automatic} pass_perk_refused={refused.Length}";
    }

    /**
     * Every installed objective's progress source against Sunrise's objective build on this install (its log reads rows=8483
     * unreadable=31 item_progress=2334). Of the 1,066 programs that expand to several instructions, 19 also hold one Sunrise
     * cannot bind, so they are among its 31 unreadable ones.
     */
    private static string OwnedProgressSources(InvestmentTables tables)
    {
        var sources = tables.Objectives.Select(tables.ProgressSource).ToArray();
        var counts = Enum.GetValues<InvestmentProgressKind>().ToDictionary(kind => kind, kind => sources.Count(source => source.Kind == kind));
        Check(counts.Values.Sum() == 8483 && counts[InvestmentProgressKind.ItemValue] == 2334 && counts[InvestmentProgressKind.Several] == 1047
            && counts[InvestmentProgressKind.Unreadable] == 31,
            "Installed objective sources differ from Sunrise's binding: " + string.Join(", ", counts.Select(pair => $"{pair.Key}={pair.Value}")));
        string targets = string.Join('/', tables.Objectives.Where((_, index) => sources[index].Kind == InvestmentProgressKind.Flag)
            .GroupBy(objective => objective.CompletionValue).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}"));
        return "progress_sources=" + string.Join(',', counts.Select(pair => $"{pair.Key}:{pair.Value}")) + $" flag_targets={targets}";
    }

    /**
     * Studio sends a saved row exactly when its proven label names the row Sunrise binds the slot to; on the installed tables
     * every proven slot qualifies, and the three class flags are read from Hello instead.
     */
    private static string OwnedUnlockSources(InvestmentUnlocks unlocks)
    {
        int Count(uint opcode, int slots, out int proven, out int classes)
        {
            int bound = 0; proven = 0; classes = 0;
            for (uint slot = 0; slot < slots; slot++)
            {
                var instruction = new InvestmentUnlockInstruction(opcode, slot, 0);
                if (unlocks.ClassOf(instruction) >= 0) { classes++; Check(LiveUnlockSources.Of(unlocks, instruction) is null, $"Class flag {slot} was sent."); continue; }
                if (unlocks.Label(instruction).Proven) proven++;
                if (LiveUnlockSources.Of(unlocks, instruction) is not null) bound++;
            }
            return bound;
        }
        int flags = Count(InvestmentUnlocks.FlagOpcode, unlocks.FlagSlots.Count, out int provenFlags, out int classFlags);
        int values = Count(InvestmentUnlocks.ValueOpcode, unlocks.ValueSlots.Count, out int provenValues, out _);
        // Measured: 15,438 proven flag slots and 6,405 proven value slots, each bound by Sunrise to the row its label names.
        Check(flags == provenFlags && values == provenValues && classFlags == 3 && flags == 15438 && values == 6405,
            $"Installed saved-row bindings changed: flags {flags}/{provenFlags}, values {values}/{provenValues}, class flags {classFlags}.");
        return $"unlock_sources=flags:{flags}/{unlocks.FlagSlots.Count},values:{values}/{unlocks.ValueSlots.Count},class:{classFlags}";
    }

    /** The installed bucket table as measured; the byte counts are printed, not asserted, since nothing upstream reads them. */
    private static string OwnedBuckets(InvestmentCatalog catalog, InvestmentTables tables)
    {
        var buckets = tables.Buckets;
        var scopes = buckets.GroupBy(bucket => bucket.ArraySelector).OrderBy(group => group.Key).Select(group => group.Count()).ToArray();
        Check(buckets.Count == 49 && scopes.SequenceEqual([32, 11, 6]) && buckets.Select(bucket => bucket.Id).Distinct().Count() == 49
            && tables.BucketTableProblems.Count == 0 && buckets.All(bucket => bucket.Problem.Length == 0)
            && tables.Bucket(PursuitsId) is { FirstSlot: InstalledPursuitsFirst, SlotCount: InstalledPursuitsSlots },
            "The installed bucket table changed.");
        int[] missing = catalog.Items.Select(item => item.Bucket).Distinct().Where(bucket => tables.Bucket(bucket) is null)
            .Select(bucket => (int)bucket).Order().ToArray();
        string Bytes(Func<InvestmentBucket, byte> field) => string.Join('/', buckets.GroupBy(field).OrderBy(group => group.Key)
            .Select(group => $"{group.Key}x{group.Count()}"));
        return $"buckets={buckets.Count} bucket_tag={tables.BucketTableTag:X8} bucket_class=0x{tables.BucketTableClass:X8} "
            + $"fifo={Bytes(bucket => bucket.Fifo)} no_transfer={Bytes(bucket => bucket.NoTransferOnEviction)} "
            + $"item_buckets_without_descriptor={missing.Length} ({string.Join(',', missing)})";
    }

    private static byte[] Root()
    {
        var root = new Blob(8 + 115 * 16);
        for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
        void Slot(int slot, int entry) => root.U(8 + slot * 16, First + (uint)entry);
        Slot(InvestmentRootSlots.Classes, ItemIndexEntry); Slot(InvestmentRootSlots.Items, ItemIndexEntry);
        Slot(InvestmentRootSlots.FlagSlots, FlagSlotEntry); Slot(InvestmentRootSlots.ValueSlots, ValueSlotEntry);
        Slot(InvestmentRootSlots.FlagMaps, FlagMapEntry); Slot(InvestmentRootSlots.ValueMaps, ValueMapEntry);
        Slot(InvestmentRootSlots.SharedExpressions, SharedEntry); Slot(InvestmentRootSlots.Pools, PoolEntry);
        Slot(InvestmentRootSlots.Progressions, ProgressionEntry); Slot(InvestmentRootSlots.Objectives, ObjectiveEntry);
        Slot(InvestmentRootSlots.Buckets, BucketEntry);
        return root.ToArray();
    }

    // Flag slot 2 claims bank row 1, which feeds slot 5; the map names slot 2 at row 2. Slot 6 claims the same row as slot 2.
    private static byte[] FlagSlots() => Slots(0x80807D4F, [(0, 1, 0), (0, 3, 0), (0, 1, 1), (0xABCD1234, 6, 0xFFFF), (0, 1, 3), (0, 3, 1), (0, 1, 1)],
        (blob, at, row) => { blob.S(at + 4, (ushort)row.Kind); blob.S(at + 6, row.Row); });
    // Value slot kind byte, aggregation byte, then the bank row; slot 7 is the per-item value objective 0 reads.
    private static byte[] ValueSlots() => Slots(0x80807C96, [(0, 2, 0), (0, 1, 0), (0, 4 | 2 << 8, 0xFFFF), (0, 0, 0xFFFF), (0, 0, 0xFFFF),
        (0, 0, 0xFFFF), (0, 0, 0xFFFF), (0, 4, 0xFFFF)], (blob, at, row) => { blob.S(at + 4, (ushort)row.Kind); blob.S(at + 6, row.Row); });

    private static byte[] Slots(uint rowClass, (uint Hash, int Kind, ushort Row)[] rows, Action<Blob, int, (uint Hash, int Kind, ushort Row)> write)
    {
        var blob = new Blob(24);
        int data = blob.Array(8, rowClass, rows.Length, 8);
        for (int i = 0; i < rows.Length; i++) { blob.U(data + i * 8, rows[i].Hash); write(blob, data + i * 8, rows[i]); }
        return blob.ToArray();
    }

    private static byte[] Maps(uint rowClass, params short[][] maps)
    {
        var blob = new Blob(72);
        for (int map = 0; map < maps.Length; map++)
        {
            if (maps[map].Length == 0) continue;
            int data = blob.Array(8 + map * 16, rowClass, maps[map].Length, 8);
            for (int row = 0; row < maps[map].Length; row++) blob.S(data + row * 8 + 4, (ushort)maps[map][row]);
        }
        return blob.ToArray();
    }

    /**
     * Rows: 0 flag; 1 shared 0 >= 1; 2/3 a cycle; 4 out of range; 5 empty; 6 names 5; 7 opcode 19; 8-47 a 40-deep chain; 48 a
     * computed value; 49 the per-item value.
     */
    private static byte[] Shared()
    {
        const int rows = 50;
        var blob = new Blob(24);
        int data = blob.Array(8, 0x80807C4F, rows, 24);
        (uint, uint)[] Body(int row) => row switch
        {
            0 => [(1, 0)], 1 => [(12, 0), (11, 1), (14, 0xFFFFFFFF)], 2 => [(12, 3)], 3 => [(12, 2)], 4 => [(12, 99)], 5 => [],
            6 => [(12, 5)], 7 => [(19, 0), (1, 0), (4, 0)], 47 => [(1, 0)], 48 => [(10, 4)], 49 => [(10, 7)], _ => [(12, (uint)row + 1)]
        };
        for (int row = 0; row < rows; row++)
        {
            blob.U(data + row * 24, 0xE000 + (uint)row);
            var body = Body(row);
            if (body.Length != 0) blob.Expression(data + row * 24 + 8, body);
        }
        return blob.ToArray();
    }

    private static byte[] Pools()
    {
        var blob = new Blob(24);
        int pools = blob.Array(8, 0x8080748C, 3, 24);
        blob.U(pools, 0x11); blob.U(pools + 24, 0x12);
        int entries = blob.Array(pools + 8, 0x8080748E, 4, 80);
        void Entry(int at, ushort item, uint quantity, ushort nested, uint category, float weight)
        {
            blob.S(at, item); blob.U(at + 4, quantity); blob.S(at + 8, nested); blob.S(at + 10, 0xFFFF);
            blob.U(at + 20, category); blob.F(at + 24, weight);
        }
        Entry(entries, 0, 2, 0xFFFF, InvestmentRewards.AnyCategory, -1);
        blob.Expression(entries + 32, (1, 0), (10, 1), (11, 5), (14, 0xFFFFFFFF), (4, 0));
        Entry(entries + 80, 0, 1, 0xFFFF, 0x22, 3.5f); blob.U(entries + 80 + 28, 0x5EED);
        int modifier = blob.Array(entries + 80 + 48, 0x80807490, 1, 24);
        blob.Expression(modifier, (12, 1)); blob.F(modifier + 20, 2);
        int socket = blob.Array(entries + 80 + 64, 0x80803062, 1, 12);
        blob.S(socket, 0xFFFF); blob.S(socket + 2, 0xFFFF);
        Entry(entries + 160, 0xFFFF, 1, 7, 0x33, 1);
        Entry(entries + 240, 0xFFFF, 1, 2, 0x22, 1); blob.S(entries + 240 + 10, FixtureSite);
        int wrapped = blob.Array(pools + 24 + 8, 0x8080748E, 1, 80);
        Entry(wrapped, 1, 1, 0xFFFF, InvestmentRewards.AnyCategory, 2); blob.S(wrapped + 10, FixtureSite);
        return blob.ToArray();
    }

    private static byte[] Progressions()
    {
        var blob = new Blob(24);
        int rows = blob.Array(8, 0x80807CDD, 2, 88);
        blob.U(rows, 0x31); blob.B(rows + 4, 1);
        int pass = rows + 88;
        blob.U(pass, 0x32);
        int steps = blob.Array(pass + 24, 0x80807CED, 3, 8);
        blob.U(steps + 8, 100); blob.U(steps + 16, 100);
        int rewards = blob.Array(pass + 72, 0x80802FF7, 3, 56);
        void Reward(int row, uint rank, uint item, uint quantity, uint claim)
        { int at = rewards + row * 56; blob.U(at, rank); blob.U(at + 4, item); blob.U(at + 8, quantity); blob.U(at + 20, claim); }
        Reward(0, 300, 0, 1, 4); Reward(1, 2, 0, 5, 5); Reward(2, 3, 1, 7, 4);
        int conditions = blob.Array(rewards + 2 * 56 + 24, 0x80807D2F, 2, 16);
        blob.Expression(conditions, (1, 0)); blob.Expression(conditions + 16, (12, 1));
        return blob.ToArray();
    }

    /** Display rows for the fixture's objectives: 1 has no bank, 2 repeats another hash, every other row has the fixture text. */
    private static byte[] ObjectiveDisplays()
    {
        const int hash = 0, bank = 24, text = 28;
        var blob = new Blob(24);
        int rows = blob.Array(8, ObjectiveDisplayRowClass, 8, ObjectiveDisplayStride);
        for (int row = 0; row < 8; row++)
        {
            int at = rows + row * ObjectiveDisplayStride;
            blob.U(at + hash, row == 2 ? 0x99u : 0x41 + (uint)row);
            // The bank is a u16 followed by two zero bytes, as on the installed build.
            blob.S(at + bank, row == 1 ? ushort.MaxValue : (ushort)0);
            blob.U(at + text, row == 1 ? LiteralPart : 1);
        }
        return blob.ToArray();
    }

    /** A string bank index naming one container. */
    private static byte[] Banks(uint container)
    {
        var blob = new Blob(24);
        int rows = blob.Array(8, BankIndexClass, 1, 8);
        blob.U(rows + 4, container);
        return blob.ToArray();
    }

    /** One string bank: the container lists hashes 1 to n, and the data holds one literal part per text. */
    private static (byte[] Container, byte[] Data) Strings(uint dataTag, string[] texts)
    {
        const int englishData = 24, partsField = 8, combinationsField = 72, partStride = 32, combinationStride = 16;
        const int partText = 8, partVariable = 0x10, partLength = 0x14, partCount = 8;
        var container = new Blob(englishData + sizeof(uint));
        container.U(englishData, dataTag);
        int hashes = container.Array(8, StringHashClass, texts.Length, sizeof(uint));
        for (int i = 0; i < texts.Length; i++) container.U(hashes + i * sizeof(uint), (uint)(i + 1));
        var data = new Blob(combinationsField + 16);
        int parts = data.Array(partsField, StringPartClass, texts.Length, partStride);
        int combinations = data.Array(combinationsField, StringCombinationClass, texts.Length, combinationStride);
        for (int i = 0; i < texts.Length; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(texts[i]);
            int part = parts + i * partStride, combination = combinations + i * combinationStride;
            data.Bytes(data.Block(part + partText, 0, bytes.Length), bytes);
            data.U(part + partVariable, LiteralPart); data.S(part + partLength, (ushort)bytes.Length);
            data.Pointer(combination, part); data.Q(combination + partCount, 1);
        }
        return (container.ToArray(), data.ToArray());
    }

    /** Objective 0 reads value slot 7; 1 has a non-boolean control byte and reads flags; 2 and 3 have empty sources in both encodings. */
    private static byte[] Objectives()
    {
        var blob = new Blob(24);
        int rows = blob.Array(8, 0x8080775F, 8, 0xA0);
        for (int row = 0; row < 8; row++) { blob.U(rows + row * 0xA0, 0x41 + (uint)row); blob.U(rows + row * 0xA0 + 0x30, 1); }
        blob.Expression(rows + 8, (10, 7)); blob.U(rows + 0x30, 10);
        blob.B(rows + 0xA0 + 0x28, 2);
        // Objective 1 reads flag slot 6 and adjusts its target by flag slot 2, which bind the same account row.
        blob.Expression(rows + 0xA0 + 8, (1, 6)); blob.Expression(rows + 0xA0 + 0x38, (1, 2));
        blob.Array(rows + 3 * 0xA0 + 8, 0x80807D31, 0, 8);
        // Objective 4 reads a computed value, 5 another through shared row 48, 6 two flags, and 7 is an optional constant.
        blob.Expression(rows + 4 * 0xA0 + 8, (10, 3));
        blob.Expression(rows + 5 * 0xA0 + 8, (12, 48));
        blob.Expression(rows + 6 * 0xA0 + 8, (1, 1), (1, 5), (4, 0));
        blob.Expression(rows + 7 * 0xA0 + 8, (11, 1)); blob.B(rows + 7 * 0xA0 + 0x28, 1);
        return blob.ToArray();
    }

    /**
     * Items 1 and 2 wrap pools 1 and 2, item 1 with an acquisition flag and opening when acquired; item 0 stacks to
     * ItemZeroStack; item 4 is a perk whose acquisition flag is an account flag; 3 and 5 are pursuits in item 4's quest set; items 1
     * and 2 name reward sites SecondSite and FixtureSite on acquisition and item 0 has one socket lane naming FixtureSite and a
     * slot-79 row.
     */
    private static byte[][] Items()
    {
        var items = new byte[ItemCount][];
        for (int i = 0; i < ItemCount; i++)
        {
            var item = new Blob(512);
            item.S(0xD8, i switch { 1 => SecondSite, 2 => FixtureSite, _ => 0xFFFF });
            item.S(0xDA, i switch { 1 => 2, 4 => AccountFlagSlot, _ => 0xFFFF });
            if (i == 0) item.U(180, ItemZeroStack);
            if (i == 0)
            {
                int sockets = item.Block(104, 0x808077C1, 32);
                int lanes = item.Array(sockets, 0x808077C4, 2, 80);
                item.S(lanes + 4, FixtureSlot79Row); item.S(lanes + 6, FixtureSite);
                item.S(lanes + 80 + 4, 0xFFFF); item.S(lanes + 80 + 6, 0xFFFF);
            }
            if (i is 1 or 2)
            {
                int wrapper = item.Block(0x58, 0x808077CC, 32);
                item.S(wrapper, (ushort)i); item.U(wrapper + 24, i == 1 ? 1u : 0u);
                int selections = item.Array(wrapper + 8, 0x808077CF, i == 2 ? 5 : 1, 12);
                item.U(selections + 4, 1);
            }
            if (i is 3 or 5)
            {
                item.B(184, 40);
                int block = item.Block(0x30, 0x808077EB, 0x20);
                int objectives = item.Array(block, 0x808087B1, i == 3 ? 8 : 1, 2);
                for (int k = 0; k < (i == 3 ? 8 : 1); k++) item.S(objectives + k * 2, (ushort)k);
                item.S(block + 0x10, FixtureSite); item.S(block + 0x12, i == 3 ? (ushort)0xFFFF : SecondSite);
                item.S(block + 0x14, (ushort)(i == 3 ? 1 : 0xFFFF)); item.S(block + 0x1A, (ushort)(i == 3 ? 0 : 0xFFFF));
                if (i == 3) { item.B(block + 0x16, 1); item.B(block + 0x18, 1); }
                item.S(block + 0x1C, 4);
                if (i == 3) item.Expression(192, (11, 3600)); else item.Expression(192, (11, 5), (22, 0));
            }
            if (i == 3)
            {
                int preview = item.Block(0x98, 0x80807875, 4 + 6 * 12);
                item.U(preview, 1); item.U(preview + 4, 0); item.U(preview + 8, 3); item.U(preview + 12, 1);
                for (int slot = 1; slot < 6; slot++) { item.U(preview + 4 + slot * 12, 0xFFFF); item.U(preview + 12 + slot * 12, slot == 1 ? 0u : 0xFFFF); }
                int values = item.Block(0x90, 0x808077AB, 0x30);
                int presence = item.Array(values, 0x80807D4B, 1, 2); item.S(presence, 5);
                int instances = item.Array(values + 0x20, 0x808077AE, 1, 8); item.S(instances, 7);
            }
            if (i == 4)
            {
                item.B(184, 37);
                int set = item.Block(0x60, 0x808077C8, 0x20);
                item.S(set + 0x10, 0); item.B(set + 0x1C, 0);
                int members = item.Array(set, 0x808077CA, 2, 8);
                item.U(members, 100); item.S(members + 4, 3); item.U(members + 8, 200); item.S(members + 12, 5);
            }
            items[i] = item.ToArray();
        }
        return items;
    }

    private static byte[] Index((uint Hash, uint Tag)[] rows)
    {
        var blob = new Blob(24);
        int data = blob.Array(8, 0x80807BE8, rows.Length, 24);
        for (int i = 0; i < rows.Length; i++) { blob.U(data + i * 24, rows[i].Hash); blob.U(data + i * 24 + 16, rows[i].Tag); }
        return blob.ToArray();
    }

    /** A growing tag blob; typed arrays and class-tagged blocks are appended and pointed to from their fields. */
    private sealed class Blob(int size)
    {
        private byte[] bytes = new byte[Math.Max(size, 64)];
        private int length = size;

        /** Appends a typed array with its 0x80809FBD marker and points the descriptor at `field` to it; returns its data. */
        public int Array(int field, uint type, int count, int stride)
        {
            int header = Grow(4 + 16 + count * stride) + 4;
            U(header - 4, 0x80809FBD); Q(header, count); U(header + 8, type);
            Q(field, count); Q(field + 8, header - field - 8);
            return header + 16;
        }
        /** Appends a block after its four-byte class and points the relative field at it. */
        public int Block(int field, uint type, int size)
        {
            int at = Grow(4 + size) + 4;
            U(at - 4, type); Q(field, at - field);
            return at;
        }
        public void Expression(int field, params (uint Opcode, uint Operand)[] instructions)
        {
            int data = Array(field, InvestmentUnlocks.InstructionClass, instructions.Length, 8);
            for (int i = 0; i < instructions.Length; i++) { U(data + i * 8, instructions[i].Opcode); U(data + i * 8 + 4, instructions[i].Operand); }
        }
        public void B(int at, byte value) => bytes[at] = value;
        public void S(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        public void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        public void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        public void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        public void Bytes(int at, byte[] value) => value.CopyTo(bytes, at);
        /** Points the relative field at `target`. */
        public void Pointer(int field, int target) => Q(field, target - field);
        public byte[] ToArray() => bytes[..length];
        private int Grow(int count)
        {
            int start = (length + 7) & ~7;
            if (start + count > bytes.Length) System.Array.Resize(ref bytes, Math.Max(bytes.Length * 2, start + count));
            length = start + count;
            return start;
        }
    }

    private static bool OnlyRange(byte[] before, byte[] after, int offset, int size) => before.Length == after.Length
        && before.Take(offset).SequenceEqual(after.Take(offset)) && before.Skip(offset + size).SequenceEqual(after.Skip(offset + size))
        && !before.Skip(offset).Take(size).SequenceEqual(after.Skip(offset).Take(size));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message == message) { return; }
        throw new Exception("Expected refusal: " + message);
    }
}
