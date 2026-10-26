using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using SunriseStudio.Live;
using P = SunriseStudio.Live.LiveInvestmentProtocol;

/** The investment link mirrors the native SSIV layout; this process plays the game on its own mapping. */
internal static class InvestmentChecks
{
    private const ulong Account = 0x4000000000000002, Character = 0x4000000000000003, OtherCharacter = 0x4000000000000004;
    private const ulong Content = 0x1F2E3D4C5B6A7988, Started = 0x01DB1234ABCD5678;
    private const uint ItemHash = 0x80A10001, PlugHash = 0x80A10002, StackHash = 0x80A10003;
    private static readonly LiveInvestmentContext Bound = new(Account, Character, Content);
    // A command no Studio build knows, as a newer Sunrise may advertise.
    private const LiveInvestmentOpcode Unknown = (LiveInvestmentOpcode)63;
    // Saved rows of each kind and bank the game reads: account flag 11, profile flag 2, character flag 7, then values.
    private static readonly LiveUnlockSource[] Sources =
    [
        new(LiveUnlockKind.Flag, LiveUnlockBank.Account, 11), new(LiveUnlockKind.Flag, LiveUnlockBank.Profile, 2),
        new(LiveUnlockKind.Flag, LiveUnlockBank.Character, 7), new(LiveUnlockKind.Value, LiveUnlockBank.Account, 5866),
        new(LiveUnlockKind.Value, LiveUnlockBank.Character, 0xFFFF)
    ];
    private static readonly ulong Served = P.Capability(LiveInvestmentOpcode.Hello) | P.Capability(LiveInvestmentOpcode.InventorySnapshot)
        | P.Capability(LiveInvestmentOpcode.GrantItem) | P.Capability(LiveInvestmentOpcode.SetQuantity)
        | P.Capability(LiveInvestmentOpcode.ReadReceipt) | P.Capability(LiveInvestmentOpcode.AcknowledgeReceipt)
        | P.Capability(LiveInvestmentOpcode.WithdrawOperation);

    public static async Task Run()
    {
        Offsets();
        Readers();
        Rejections();
        Unlocks();
        PursuitEdits();
        await Connection();
        Console.WriteLine("live investment_offsets=checked investment_readers=checked investment_rejects=checked investment_unlocks=checked "
            + "investment_pursuit_edits=checked investment_connection=checked");
    }

    /** Expected numbers are the native header's; a change on either side must change both. */
    private static void Offsets()
    {
        // XP is two int32s, the expected and the replacement; a pass row is a u16 index, two reserved bytes and the item hash.
        byte[] experience = P.SetSeasonalExperience(-7, 1_234_567), passRow = P.SeasonPassRow(0xC8, 0x80A10001);
        Check(experience.Length == P.SetSeasonalExperienceBytes && BitConverter.ToInt32(experience, P.ExperienceExpectedOffset) == -7
            && BitConverter.ToInt32(experience, P.ExperienceReplacementOffset) == 1_234_567, "The XP request layout differs.");
        Check(passRow.Length == P.SeasonPassRowBytes && BitConverter.ToUInt16(passRow, P.PassRowIndexOffset) == 0xC8
            && passRow[P.PassReservedOffset] == 0 && passRow[P.PassReservedOffset + 1] == 0 && BitConverter.ToUInt32(passRow, P.PassHashOffset) == 0x80A10001,
            "The season pass row request layout differs.");
        Check(P.Magic == 0x56495353 && P.Version == 2, "Investment magic or version differs.");
        Check(P.MappingName(42) == @"Local\SunriseStudio.Investment.42", "Investment mapping name differs.");
        Check(P.MappingBytes == 55264 && P.MappingBytes == P.ResponsePayloadOffset + P.ResponsePayloadBytes
            && P.ResponsePayloadOffset == P.ControlBytes + P.RequestBlockBytes + P.ResponseHeaderBytes, "Investment mapping size differs.");
        Check(P.RequestPayloadOffset + P.RequestPayloadBytes == P.ResponseBlockOffset
            && P.ReasonOffset + P.ReasonBytes == P.ResponsePayloadOffset, "Investment blocks do not tile the mapping.");
        int[] sizes = [P.GrantItemBytes, P.SetQuantityBytes, P.SetSeasonalExperienceBytes, P.SeasonPassRowBytes, P.UnlockValuesRequestBytes,
            P.SetObjectiveBytes, P.CompleteObjectivesBytes];
        int[] answers = [P.HelloBytes, P.InventoryBytes, P.SeasonPassBytes, P.PursuitBytes, P.ReceiptBytes, P.UnlockValuesBytes];
        Check(sizes.Max() == P.RequestPayloadBytes && answers.Max() == P.ResponsePayloadBytes, "Investment payload capacity differs.");
        Check(P.HelloCharactersOffset + P.CharacterCapacity * P.CharacterBytes == P.HelloRevisionOffset
            && P.HelloRevisionOffset + sizeof(ulong) == P.HelloBytes, "Hello strides differ.");
        Check(P.BucketsOffset + P.BucketCapacity * P.BucketBytes == P.ProfileOffset
            && P.ProfileOffset + P.ProfileRowCapacity * P.ProfileRowBytes == P.ItemsOffset
            && P.ItemsOffset + P.CharacterItemCapacity * P.ItemBytes == P.StacksOffset
            && P.StacksOffset + P.CharacterStackCapacity * P.StackBytes == P.InventoryBytes
            && P.ItemPlugsOffset + P.PlugCapacity * sizeof(uint) == P.ItemTailOffset, "Inventory strides differ.");
        Check(P.PassRowsOffset + P.SeasonPassRowCapacity * P.PassRowBytes == P.SeasonPassBytes, "Season pass strides differ.");
        Check(P.HeldOffset + P.HeldPursuitCapacity * P.HeldBytes == P.PursuitBytes
            && P.HeldObjectivesOffset + P.PursuitObjectiveCapacity * P.ObjectiveBytes == P.HeldBytes, "Pursuit strides differ.");
        Check((P.PursuitRevisionOffset, P.HeldOffset, P.HeldFlagsOffset, P.HeldObjectiveCountOffset, P.HeldReservedOffset, P.HeldReasonOffset,
                P.HeldTailOffset, P.HeldObjectivesOffset) == (8, 16, 24, 26, 27, 28, 30, 32), "Held pursuit offsets differ from the native header.");
        Check((P.SetQuantityBytes, P.SetReservedOffset, P.SetInstanceOffset, P.SetHashOffset, P.SetExpectedSerialOffset,
                P.SetExpectedQuantityOffset, P.SetQuantityOffset) == (32, 1, 8, 16, 20, 24, 28)
            && (P.SeasonPassRowBytes, P.PassRowIndexOffset, P.PassReservedOffset, P.PassHashOffset) == (8, 0, 2, 4),
            "Set quantity or season pass row requests differ from the native header.");
        // Nine sources fill the request payload, and the answer is a 16-byte header and nine 8-byte values.
        Check(P.UnlockReadCapacity == 9 && P.UnlockValuesRequestBytes == 40 && P.UnlockValuesBytes == 88
            && P.UnlockSourcesOffset + P.UnlockReadCapacity * P.UnlockSourceBytes == P.UnlockValuesRequestBytes
            && P.UnlockValuesOffset + P.UnlockReadCapacity * P.UnlockValueBytes == P.UnlockValuesBytes
            && (P.UnlockRequestReservedOffset, P.UnlockSourcesOffset, P.UnlockSourceBytes, P.UnlockBankOffset, P.UnlockIndexOffset) == (2, 4, 4, 1, 2)
            && (P.UnlockReservedOffset, P.UnlockRevisionOffset, P.UnlockValuesOffset, P.UnlockValueBytes, P.UnlockStateReservedOffset, P.UnlockValueOffset)
                == (2, 8, 16, 8, 1, 4), "Unlock value strides differ from the native header.");
        Check(P.RequestBytes(LiveInvestmentOpcode.UnlockValues) == 40 && P.ResponseBytes(LiveInvestmentOpcode.UnlockValues) == 88
            && !P.IsMutation(LiveInvestmentOpcode.UnlockValues) && !P.IsReceiptRequest(LiveInvestmentOpcode.UnlockValues),
            "The unlock read is sized or classed differently from the native header.");
        // Each offset beside the native header's value, so a difference names the field.
        (string Name, int Actual, int Expected)[] offsets =
        [
            (nameof(P.HeartbeatOffset), P.HeartbeatOffset, 16), (nameof(P.GameVersionOffset), P.GameVersionOffset, 24),
            (nameof(P.GamePidOffset), P.GamePidOffset, 28), (nameof(P.CapabilitiesOffset), P.CapabilitiesOffset, 32),
            (nameof(P.GameStartedOffset), P.GameStartedOffset, 40), (nameof(P.RequestOffset), P.RequestOffset, 48),
            (nameof(P.ResponseOffset), P.ResponseOffset, 52), (nameof(P.ControlReservedOffset), P.ControlReservedOffset, 56),
            (nameof(P.OpcodeOffset), P.OpcodeOffset, 64), (nameof(P.RequestPayloadBytesOffset), P.RequestPayloadBytesOffset, 68),
            (nameof(P.OperationOffset), P.OperationOffset, 72), (nameof(P.ExpectedOffset), P.ExpectedOffset, 80),
            (nameof(P.RequestPayloadOffset), P.RequestPayloadOffset, 104), (nameof(P.StatusOffset), P.StatusOffset, 144),
            (nameof(P.ResponseReservedOffset), P.ResponseReservedOffset, 148), (nameof(P.ResponseOperationOffset), P.ResponseOperationOffset, 152),
            (nameof(P.ResponseOpcodeOffset), P.ResponseOpcodeOffset, 160), (nameof(P.ResponsePayloadBytesOffset), P.ResponsePayloadBytesOffset, 164),
            (nameof(P.ContextOffset), P.ContextOffset, 168), (nameof(P.ReasonOffset), P.ReasonOffset, 192),
            (nameof(P.ResponsePayloadOffset), P.ResponsePayloadOffset, 256),
            (nameof(P.HelloBytes), P.HelloBytes, 88), (nameof(P.InventoryBytes), P.InventoryBytes, 55008),
            (nameof(P.SeasonPassBytes), P.SeasonPassBytes, 4112), (nameof(P.PursuitBytes), P.PursuitBytes, 47968),
            (nameof(P.ReceiptBytes), P.ReceiptBytes, 48),
            (nameof(P.ProfileRowCapacity), P.ProfileRowCapacity, 701), (nameof(P.CharacterItemCapacity), P.CharacterItemCapacity, 350),
            (nameof(P.CharacterStackCapacity), P.CharacterStackCapacity, 333), (nameof(P.BucketCapacity), P.BucketCapacity, 255),
            (nameof(P.SeasonPassRowCapacity), P.SeasonPassRowCapacity, 256), (nameof(P.HeldPursuitCapacity), P.HeldPursuitCapacity, 333),
            (nameof(P.CharacterCapacity), P.CharacterCapacity, 3), (nameof(P.PursuitObjectiveCapacity), P.PursuitObjectiveCapacity, 7),
            (nameof(P.ProfileOffset), P.ProfileOffset, 2056), (nameof(P.ItemsOffset), P.ItemsOffset, 18880),
            (nameof(P.StacksOffset), P.StacksOffset, 49680), (nameof(P.ItemBytes), P.ItemBytes, 88), (nameof(P.HeldBytes), P.HeldBytes, 144),
            (nameof(P.ReceiptStateOffset), P.ReceiptStateOffset, 12), (nameof(P.ReceiptReservedOffset), P.ReceiptReservedOffset, 16),
            (nameof(P.ReceiptWaitOffset), P.ReceiptWaitOffset, 20), (nameof(P.ReceiptVersionOffset), P.ReceiptVersionOffset, 24),
            (nameof(P.ReceiptFinishedOffset), P.ReceiptFinishedOffset, 40),
            (nameof(P.SetSeasonalExperienceBytes), P.SetSeasonalExperienceBytes, 8),
            (nameof(P.ExperienceExpectedOffset), P.ExperienceExpectedOffset, 0), (nameof(P.ExperienceReplacementOffset), P.ExperienceReplacementOffset, 4)
        ];
        var differing = offsets.Where(row => row.Actual != row.Expected).Select(row => $"{row.Name} {row.Actual} (header {row.Expected})").ToArray();
        Check(differing.Length == 0, "Investment offsets differ from the native header: " + string.Join(", ", differing));
        Check((int)LivePursuitReason.ItemProgressNotSaved == 10 && (int)LivePursuitReason.ExternalValue == 11
            && (int)LiveInvestmentOpcode.WithdrawOperation == 12 && (int)LiveInvestmentOpcode.UnlockValues == 13
            && (int)LiveInvestmentStatus.Refused == 3 && (int)LiveReceiptState.Published == 4 && (int)LiveReceiptState.CommittedUnpublished == 5
            && (int)LiveInvestmentWait.Catalog == 3 && (int)LivePursuitFlags.BindingMatches == 2 && (int)LivePursuitFlags.EffectsKnown == 64
            && (byte)LiveUnlockKind.Value == 2 && (byte)LiveUnlockBank.Character == 3
            && (int)LiveInvestmentOpcode.SetPursuitObjective == 14 && (int)LiveInvestmentOpcode.CompletePursuitObjectives == 15,
            "Investment enum values differ from the native header.");
    }

    /** Readers decode only the counted prefix and keep every field where the game put it. */
    private static void Readers()
    {
        var hello = P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, HelloPayload()));
        Check(hello is { Readiness: LiveInvestmentReadiness.Items | LiveInvestmentReadiness.Buckets, Revision: 7, GameStarted: Started }
            && hello.Characters.Count == 2 && hello.Characters[0] is { Soid: Character, CharacterClass: 2, Selected: true }
            && hello.Publication.HasFlag(LiveInvestmentPublication.PeerOwnsAccount) && hello.Supports(LiveInvestmentOpcode.GrantItem)
            && !hello.Supports(LiveInvestmentOpcode.SetSeasonalExperience), "The hello layout differs.");
        var newer = P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, HelloPayload(Served | P.Capability(Unknown))) with
            { Capabilities = Served | P.Capability(Unknown) });
        Check(newer.Capabilities == Served && !newer.Supports(Unknown) && newer.Supports(LiveInvestmentOpcode.GrantItem),
            "A hello advertising a command Studio does not know was refused or kept the bit.");
        ulong reads = Served | P.Capability(LiveInvestmentOpcode.UnlockValues);
        Check(P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, HelloPayload(reads)) with { Capabilities = reads }).Supports(LiveInvestmentOpcode.UnlockValues),
            "A hello advertising unlock reads dropped them.");

        var inventory = P.ReadInventory(Reply(LiveInvestmentOpcode.InventorySnapshot, Bound, InventoryPayload()));
        Check(inventory is { Revision: 11, Buckets.Count: 2, Profile.Count: 1, Items.Count: 2, Stacks.Count: 1 }
            && inventory.Buckets[1] is { BucketId: 3, ArraySelector: 0, FirstSlot: 9, SlotCount: 10, EquipmentSlot: 18 }
            && inventory.Profile[0] is { Instance: 0x5000000000000001, DefinitionHash: StackHash, Quantity: 25, MutationSerial: 4, Seen: true }
            && inventory.Items[0] is { Equipped: true, EquipmentSlot: 0, Level: 1600, SocketPolicy: LiveSocketPolicy.Authored }
            && inventory.Items[0].Plugs.SequenceEqual(new uint?[] { PlugHash, null })
            && inventory.Items[1] is { Equipped: false, BucketId: P.NoBucket, Plugs.Count: 0 }
            && inventory.Stacks[0] is { DefinitionHash: StackHash, Quantity: 3, BucketId: 5 }, "The inventory layout differs.");

        var pass = new byte[P.SeasonPassBytes];
        I32(pass, P.PassExperienceOffset, 125000); U16(pass, P.PassRankOffset, 12); U16(pass, P.PassRowCountOffset, 3);
        PassRow(pass, 0, ItemHash, LiveSeasonPassRowFlags.Available | LiveSeasonPassRowFlags.Claimed);
        PassRow(pass, 2, StackHash, LiveSeasonPassRowFlags.Available | LiveSeasonPassRowFlags.Automatic);
        var season = P.ReadSeasonPass(Reply(LiveInvestmentOpcode.SeasonPassSnapshot, Bound, pass));
        Check(season is { Experience: 125000, Rank: 12, Rows.Count: 3 } && season.Rows[1] is { ItemHash: 0, Flags: LiveSeasonPassRowFlags.None }
            && season.Rows[2] is { Index: 2, Quantity: 1, RequiredRank: 4, Flags: LiveSeasonPassRowFlags.Available | LiveSeasonPassRowFlags.Automatic },
            "The season pass layout differs.");

        var pursuits = PursuitPayload();
        var held = P.ReadPursuits(Reply(LiveInvestmentOpcode.PursuitSnapshot, Bound, pursuits)).Held.Single();
        Check(held is { Instance: 0x4000000000000100, Reason: LivePursuitReason.ItemProgressNotSaved, Objectives.Count: 2 }
            && held.Flags == (LivePursuitFlags.Bounty | LivePursuitFlags.ConditionsKnown)
            && held.Objectives[1] is { Value: 2, Target: 5, Flags: LivePursuitObjectiveFlags.Known }, "The pursuit layout differs.");
    }

    /** Every count, reserved field and unused slot is checked; nothing out of range reaches the editor. */
    private static void Rejections()
    {
        Reject(() => P.ReadInventory(Reply(LiveInvestmentOpcode.InventorySnapshot, Bound,
            Changed(InventoryPayload(), P.ProfileOffset + P.ProfileRowBytes + 3, 1))), "A value in an unused profile slot was accepted.");
        Reject(() => P.ReadInventory(Reply(LiveInvestmentOpcode.InventorySnapshot, Bound,
            Changed(InventoryPayload(), P.ItemsOffset + P.ItemPlugsOffset + sizeof(uint), 1))), "A value in an empty plug lane was accepted.");
        Reject(() => P.ReadInventory(Reply(LiveInvestmentOpcode.InventorySnapshot, Bound,
            Changed(InventoryPayload(), P.ItemCountOffset, P.CharacterItemCapacity + 1))), "An item count past capacity was accepted.");
        Reject(() => P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound with { CharacterSoid = OtherCharacter }, HelloPayload())),
            "A hello whose selected character differs from its context was accepted.");
        Reject(() => P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, HelloPayload(Served | P.Capability(Unknown)))),
            "A hello whose commands differ from the control word was accepted.");
        Reject(() => P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, HelloPayload()) with { Capabilities = Served | P.Capability(Unknown) }),
            "A control word advertising more than the hello was accepted.");
        Reject(() => P.ReadPursuits(Reply(LiveInvestmentOpcode.PursuitSnapshot, Bound,
            Changed(PursuitPayload(), P.HeldOffset + P.HeldReasonOffset, (int)LivePursuitReason.ExternalValue + 1))),
            "An unknown pursuit reason was accepted.");
        Reject(() => P.ReadPursuits(Reply(LiveInvestmentOpcode.PursuitSnapshot, Bound,
            Changed16(PursuitPayload(), P.HeldOffset + P.HeldFlagsOffset, (int)LivePursuitFlags.EffectsKnown << 1))),
            "An unknown pursuit flag was accepted.");
        Reject(() => P.ReadReceipt(Reply(LiveInvestmentOpcode.ReadReceipt, Bound,
            Changed(Receipt(1, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Queued), P.ReceiptReservedOffset, 1), operationId: 1)),
            "A receipt with a nonzero reserved field was accepted.");
        Reject(() => P.ReadReceipt(Reply(LiveInvestmentOpcode.ReadReceipt, Bound,
            Receipt(1, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Refused, finished: 200), operationId: 1)),
            "A refused receipt without its label was accepted.");

        byte[] control = new byte[P.ControlBytes], response = new byte[P.ResponseHeaderBytes + P.HelloBytes];
        U32(control, P.MagicOffset, P.Magic); U32(control, P.VersionOffset, P.Version); U32(control, P.BytesOffset, P.MappingBytes);
        U32(control, P.GameVersionOffset, P.Version); U32(control, P.GamePidOffset, 7); U64(control, P.GameStartedOffset, Started);
        U32(control, P.ResponseOffset, 5);
        U32(response, P.StatusOffset - P.ResponseBlockOffset, (uint)LiveInvestmentStatus.Ok);
        U32(response, P.ResponseOpcodeOffset - P.ResponseBlockOffset, (uint)LiveInvestmentOpcode.Hello);
        U32(response, P.ResponsePayloadBytesOffset - P.ResponseBlockOffset, P.HelloBytes);
        LiveInvestmentReply? Read(uint request = 5, ulong started = Started) =>
            P.Read(control, response, 7, started, request, LiveInvestmentOpcode.Hello, 0);
        Check(Read() is { Status: LiveInvestmentStatus.Ok, Reason: "" } && Read(request: 4) is null, "A reply to the current id was not read.");
        Reject(() => Read(started: Started + 1), "A reply from another game instance was accepted.");
        U32(response, P.StatusOffset - P.ResponseBlockOffset, (uint)LiveInvestmentStatus.Refused + 1);
        Reject(() => Read(), "A status past Refused was accepted.");
        U32(response, P.StatusOffset - P.ResponseBlockOffset, (uint)LiveInvestmentStatus.Ok);
        response[P.ReasonOffset - P.ResponseBlockOffset + 1] = (byte)'x';
        Reject(() => Read(), "A reason with bytes after its terminator was accepted.");
        response.AsSpan(P.ReasonOffset - P.ResponseBlockOffset, P.ReasonBytes).Fill((byte)'x');
        Reject(() => Read(), "An unterminated reason was accepted.");
        response.AsSpan(P.ReasonOffset - P.ResponseBlockOffset, P.ReasonBytes).Clear();
        U32(response, P.ResponseReservedOffset - P.ResponseBlockOffset, 1);
        Reject(() => Read(), "A reply with a nonzero reserved field was accepted.");
        U32(response, P.ResponseReservedOffset - P.ResponseBlockOffset, 0);
        U32(response, P.ResponsePayloadBytesOffset - P.ResponseBlockOffset, P.HelloBytes - 1);
        Reject(() => Read(), "A reply with a partial payload was accepted.");

        Check(P.Describe("equipped") == "Equipped; unequip before editing." && P.Describe("some_new_label") == "some_new_label",
            "Refusal labels are not described or passed through.");
        Check(P.Describe("editor_disconnected") == "Studio disconnected before the game ran it."
            && P.Describe("pursuits_unsupported") != "pursuits_unsupported" && P.Describe("rewards_unsupported") != "rewards_unsupported",
            "Unreadable catalog and disconnect refusals are not described.");
        // Sunrise refuses lowering XP without the artifact's sale rows, bounds a grant by what the account can hold, and names an
        // occupied bucket without a descriptor as item_definition.
        Check(P.Describe("artifact_unsupported").Contains("artifact", StringComparison.Ordinal)
            && P.Describe("quantity").Contains("can hold", StringComparison.Ordinal)
            && P.Describe("item_definition").Contains("bucket", StringComparison.Ordinal)
            && P.Describe("item_unavailable") != P.Describe("item_definition"), "The XP, grant bound or bucket refusals are not described.");
        RejectArgument(() => P.GrantItem(ItemHash, 0), "A zero grant quantity was encoded.");
        RejectArgument(() => P.SetQuantity(LiveInventoryScope.CharacterItem, 0, ItemHash, 1, 1, 0),
            "An instanced item without its instance was encoded.");
        // Claim and reset name a row the same way; the reserved half-word stays zero.
        var row = P.SeasonPassRow(255, ItemHash);
        Check(row.Length == P.SeasonPassRowBytes && BinaryPrimitives.ReadUInt16LittleEndian(row) == 255
            && row.AsSpan(P.PassReservedOffset, sizeof(ushort)).IndexOfAnyExcept((byte)0) < 0
            && BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(P.PassHashOffset)) == ItemHash, "The season pass row request differs.");
        RejectArgument(() => P.SeasonPassRow(P.SeasonPassRowCapacity, ItemHash), "A row past the pass capacity was encoded.");
        RejectArgument(() => LiveInvestmentOperation.Create(Bound with { ContentHash = 0 }, Started),
            "An operation without the game's catalog was created.");
    }

    /** The unlockValues request and answer: sources and values at the native offsets, every count, state and spare byte checked. */
    private static void Unlocks()
    {
        var request = P.UnlockValues(Sources);
        Check(request.Length == P.UnlockValuesRequestBytes && BinaryPrimitives.ReadUInt16LittleEndian(request) == Sources.Length
            && request.AsSpan(P.UnlockRequestReservedOffset, 2).IndexOfAnyExcept((byte)0) < 0
            && request[P.UnlockSourcesOffset] == (byte)LiveUnlockKind.Flag && request[P.UnlockSourcesOffset + 1] == (byte)LiveUnlockBank.Account
            && BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(P.UnlockSourcesOffset + 2)) == 11
            && request[P.UnlockSourcesOffset + 4 * 4] == (byte)LiveUnlockKind.Value && request[P.UnlockSourcesOffset + 4 * 4 + 1] == (byte)LiveUnlockBank.Character
            && BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(P.UnlockSourcesOffset + 4 * 4 + 2)) == 0xFFFF
            && request.AsSpan(P.UnlockSourcesOffset + Sources.Length * P.UnlockSourceBytes).IndexOfAnyExcept((byte)0) < 0,
            "The unlock request layout differs.");
        var block = new byte[P.RequestBlockBytes];
        P.EncodeRequest(block, LiveInvestmentOpcode.UnlockValues, 0, default, request);
        Check(Field(block, P.OpcodeOffset) == 13 && Field(block, P.RequestPayloadBytesOffset) == 40 && Field64(block, P.OperationOffset) == 0
            && block.AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset).SequenceEqual(request), "The unlock request block differs.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.UnlockValues, 0, Bound, request), "An unlock read carried a context.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.UnlockValues, 1, default, request), "An unlock read carried an operation id.");
        RejectArgument(() => P.UnlockValues([]), "An unlock read of no source was encoded.");
        RejectArgument(() => P.UnlockValues([.. Enumerable.Repeat(Sources[0], P.UnlockReadCapacity + 1)]), "An unlock read past capacity was encoded.");
        RejectArgument(() => P.UnlockValues([new(LiveUnlockKind.Value, LiveUnlockBank.Profile, 1)]), "A profile value was encoded.");
        RejectArgument(() => P.UnlockValues([default]), "An all-zero source was encoded.");
        RejectArgument(() => P.UnlockValues([new((LiveUnlockKind)3, LiveUnlockBank.Account, 1)]), "An unknown kind was encoded.");

        var values = P.ReadUnlockValues(Reply(LiveInvestmentOpcode.UnlockValues, Bound, UnlockPayload()), Sources);
        Check(values is { Revision: 21, Values.Count: 5 } && values.Context == Bound && values.Values.Select(value => value.Source).SequenceEqual(Sources)
            && values.Values[0] is { Known: true, Value: 1 } && values.Values[1] is { Known: true, Value: 0 } && values.Values[3] is { Known: true, Value: -40 }
            && values.Values[4] is { Known: false, Value: 0 }, "The unlock values layout differs.");
        void Refuse(byte[] payload, string message, LiveUnlockSource[]? asked = null) =>
            Reject(() => P.ReadUnlockValues(Reply(LiveInvestmentOpcode.UnlockValues, Bound, payload), asked ?? Sources), message);
        Refuse(UnlockPayload(), "Values for another number of sources were accepted.", Sources[..4]);
        Refuse(Changed16(UnlockPayload(), P.UnlockCountOffset, P.UnlockReadCapacity + 1), "An unlock count past capacity was accepted.",
            [.. Enumerable.Repeat(Sources[0], P.UnlockReadCapacity + 1)]);
        Refuse(Changed16(UnlockPayload(), P.UnlockReservedOffset, 1), "Nonzero unlock reserved bytes were accepted.");
        Refuse(Changed(UnlockPayload(), P.UnlockReservedOffset + 2, 1), "A nonzero second unlock reserved field was accepted.");
        Refuse(Changed8(UnlockPayload(), P.UnlockValuesOffset + P.UnlockStateReservedOffset, 1), "A nonzero value reserved byte was accepted.");
        Refuse(Changed8(UnlockPayload(), P.UnlockValuesOffset + 2, 1), "A nonzero second value reserved field was accepted.");
        Refuse(Changed8(UnlockPayload(), P.UnlockValuesOffset + P.UnlockValueBytes, 2), "An unknown unlock read state was accepted.");
        Refuse(Changed(UnlockPayload(), P.UnlockValuesOffset + P.UnlockValueOffset, 2), "A flag reading 2 was accepted.");
        Refuse(Changed(UnlockPayload(), P.UnlockValuesOffset + Sources.Length * P.UnlockValueBytes + P.UnlockValueOffset, 1),
            "A value past the count was accepted.");
    }

    /**
     * setPursuitObjective and completePursuitObjectives: SetPursuitObjectiveRequest and CompletePursuitObjectivesRequest at the
     * native offsets, the whole int32 range for a value row, the receipt both answer with, and the capability bits.
     */
    private static void PursuitEdits()
    {
        Check(P.SetObjectiveBytes == 32 && P.CompleteObjectivesBytes == 16
            && (P.PursuitInstanceOffset, P.PursuitHashOffset, P.PursuitSerialOffset) == (0, 8, 12)
            && (P.ObjectiveRequestHashOffset, P.ObjectiveOrdinalOffset, P.ObjectiveRequestReservedOffset) == (16, 20, 21)
            && (P.ObjectiveExpectedOffset, P.ObjectiveReplacementOffset) == (24, 28)
            && P.ObjectiveReplacementOffset + sizeof(int) == P.SetObjectiveBytes
            && P.PursuitSerialOffset + sizeof(int) == P.CompleteObjectivesBytes, "Pursuit edit layouts differ from the native header.");
        foreach (var opcode in new[] { LiveInvestmentOpcode.SetPursuitObjective, LiveInvestmentOpcode.CompletePursuitObjectives })
            Check(P.IsMutation(opcode) && !P.IsReceiptRequest(opcode) && P.ResponseBytes(opcode) == P.ReceiptBytes,
                $"{opcode} is classed or answered differently from the native header.");
        Check(P.RequestBytes(LiveInvestmentOpcode.SetPursuitObjective) == 32 && P.RequestBytes(LiveInvestmentOpcode.CompletePursuitObjectives) == 16,
            "Pursuit edit payload sizes differ.");

        const ulong instance = 0x4000000000000100;
        const uint objective = 0x80A1C0DE;
        // Saved value rows span the whole int32 range, negative values and values near the top included.
        foreach (int value in new[] { int.MinValue, -1, 0, 1, int.MaxValue - 7, int.MaxValue })
        {
            var set = P.SetPursuitObjective(instance, ItemHash, -7, objective, 6, value, ~value);
            Check(set.Length == 32 && BinaryPrimitives.ReadUInt64LittleEndian(set) == instance
                && BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(8)) == ItemHash && BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(12)) == -7
                && BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(16)) == objective && set[20] == 6
                && set.AsSpan(21, 3).IndexOfAnyExcept((byte)0) < 0 && BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(24)) == value
                && BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(28)) == ~value, $"The set objective request for {value} differs.");
        }
        var complete = P.CompletePursuitObjectives(instance, ItemHash, int.MinValue);
        Check(complete.Length == 16 && BinaryPrimitives.ReadUInt64LittleEndian(complete) == instance
            && BinaryPrimitives.ReadUInt32LittleEndian(complete.AsSpan(8)) == ItemHash
            && BinaryPrimitives.ReadInt32LittleEndian(complete.AsSpan(12)) == int.MinValue, "The complete objectives request differs.");
        // A zero objective hash is sent as read: the game matches it against the held copy.
        Check(P.SetPursuitObjective(instance, ItemHash, 0, 0, 0, 0, 1).AsSpan(16, 4).IndexOfAnyExcept((byte)0) < 0,
            "A zero objective hash was not sent as read.");
        RejectArgument(() => P.SetPursuitObjective(0, ItemHash, 0, objective, 0, 0, 1), "A set objective without an instance was encoded.");
        RejectArgument(() => P.SetPursuitObjective(instance, 0, 0, objective, 0, 0, 1), "A set objective without an item hash was encoded.");
        RejectArgument(() => P.SetPursuitObjective(instance, ItemHash, 0, objective, P.PursuitObjectiveCapacity, 0, 1),
            "An ordinal past the objective capacity was encoded.");
        RejectArgument(() => P.CompletePursuitObjectives(0, ItemHash, 0), "A completion without an instance was encoded.");
        RejectArgument(() => P.CompletePursuitObjectives(instance, 0, 0), "A completion without an item hash was encoded.");

        var block = new byte[P.RequestBlockBytes];
        P.EncodeRequest(block, LiveInvestmentOpcode.CompletePursuitObjectives, 5, Bound, complete);
        Check(Field(block, P.OpcodeOffset) == 15 && Field(block, P.RequestPayloadBytesOffset) == 16 && Field64(block, P.OperationOffset) == 5
            && Field64(block, P.ExpectedOffset + 8) == Character && block.AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset).StartsWith(complete)
            && block.AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset + 16).IndexOfAnyExcept((byte)0) < 0,
            "The complete objectives request block differs.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.CompletePursuitObjectives, 0, Bound, complete),
            "A completion without an operation id was encoded.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.SetPursuitObjective, 5, Bound with { ContentHash = 0 },
            P.SetPursuitObjective(instance, ItemHash, 0, objective, 0, 0, 1)), "A set objective without a complete context was encoded.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.CompletePursuitObjectives + 1, 5, Bound, []), "Opcode 16 was encoded.");

        foreach (var opcode in new[] { LiveInvestmentOpcode.SetPursuitObjective, LiveInvestmentOpcode.CompletePursuitObjectives })
            Check(P.ReadReceipt(Reply(LiveInvestmentOpcode.ReadReceipt, Bound, Receipt(9, opcode, LiveReceiptState.Published, changed: 3,
                finished: 150), operationId: 9)) is { State: LiveReceiptState.Published, ChangedRows: 3 } receipt && receipt.Opcode == opcode,
                $"A receipt for {opcode} was not read.");
        ulong edits = Served | P.Capability(LiveInvestmentOpcode.UnlockValues) | P.Capability(LiveInvestmentOpcode.SetPursuitObjective)
            | P.Capability(LiveInvestmentOpcode.CompletePursuitObjectives);
        var hello = P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, HelloPayload(edits)) with { Capabilities = edits });
        Check(hello.Capabilities == edits && hello.Supports(LiveInvestmentOpcode.UnlockValues) && hello.Supports(LiveInvestmentOpcode.SetPursuitObjective)
            && hello.Supports(LiveInvestmentOpcode.CompletePursuitObjectives), "A hello advertising pursuit edits dropped them.");
        Check(P.Describe("value_changed") == P.Describe("inventory_row") && P.Describe("item_progress_not_saved") == "This server does not save per-item progress."
            && P.Describe("expiry_not_saved") == "This server does not save when a bounty expires.", "Pursuit edit refusals are not described.");
    }

    /** One persistent mapping: hello, snapshots, a receipt's whole life, stale answers, timeouts and a version change. */
    private static async Task Connection()
    {
        int pid = Environment.ProcessId;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = limit.Token;
        using var connection = new LiveInvestmentConnection(pid);
        using var mapping = await OpenAsync(pid, token);
        using var view = mapping.CreateViewAccessor();
        Check(view.ReadUInt32(P.MagicOffset) == P.Magic && view.ReadUInt32(P.VersionOffset) == P.Version
            && view.ReadUInt32(P.BytesOffset) == P.MappingBytes && view.ReadInt32(P.EditorPidOffset) == pid
            && view.ReadInt64(P.HeartbeatOffset) != 0, "The investment mapping header was not written.");
        Check(connection.State == LiveInvestmentLinkState.Opening, "The link connected before the game wrote its version.");
        view.Write(P.GamePidOffset, (uint)pid); view.Write(P.CapabilitiesOffset, Served); view.Write(P.GameStartedOffset, Started);
        view.Write(P.GameVersionOffset, P.Version);
        await UntilAsync(() => connection.State == LiveInvestmentLinkState.Connected, token);
        var game = new FakeGame(view);

        var helloTask = connection.HelloAsync(token);
        uint request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.Hello
            && game.Block().AsSpan(P.RequestPayloadBytesOffset - P.RequestBlockOffset).IndexOfAnyExcept((byte)0) < 0,
            "The hello request carries a payload, an operation or a context.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, HelloPayload(), answerId: request - 1);
        await Task.Delay(100, token);
        Check(!helloTask.IsCompleted, "An answer to an older request id was taken.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, HelloPayload());
        var hello = await helloTask.WaitAsync(token);
        // The connection sends only operations made from the game's last hello, so one from another game start never goes out.
        Check(hello.Context == Bound && connection.GrantItemAsync(LiveInvestmentOperation.Create(hello.Context, hello.GameStarted + 1),
            ItemHash, 1, token).IsFaulted, "The hello was not adopted.");
        // A written mutation stays unresolved, and is refused before it is sent again, until the game refuses it, forgets it or
        // reports it never arrived. A resolved one goes out again, which the game refuses as reused.
        void Unresolved(LiveInvestmentOperation sent, string message) => Check(connection.GrantItemAsync(sent, ItemHash, 1, token)
            is { IsFaulted: true, Exception.InnerException: InvalidOperationException }, message);
        async Task Resolved(LiveInvestmentOperation sent, string message)
        {
            var again = connection.GrantItemAsync(sent, ItemHash, 1, token);
            Check(!again.IsCompleted, message);
            game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Refused, Bound,
                Receipt(sent.Id, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Published, finished: 150), "operation_reused");
            await Rejected<InvalidOperationException>(again, message);
        }

        var inventoryTask = connection.InventoryAsync(token);
        request = await game.NextAsync(token);
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Changed(InventoryPayload(), P.StacksOffset + P.StackBytes, 1));
        await Rejected<InvalidDataException>(inventoryTask, "A snapshot with nonzero padding was accepted.");
        inventoryTask = connection.InventoryAsync(token);
        request = await game.NextAsync(token);
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, InventoryPayload());
        Check((await inventoryTask.WaitAsync(token)).Items.Count == 2, "A partial inventory was not read.");

        // A read of saved rows carries its sources and nothing else, and its answer is matched to them.
        var unlocks = connection.UnlockValuesAsync(Sources, token);
        request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.UnlockValues && game.Operation == 0 && game.Expected == default
            && game.Block().AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset).SequenceEqual(P.UnlockValues(Sources)), "The unlock read was not sent as encoded.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, UnlockPayload());
        Check((await unlocks.WaitAsync(token)).Values[3].Value == -40, "The unlock values were not read.");
        unlocks = connection.UnlockValuesAsync(Sources, token);
        request = await game.NextAsync(token);
        game.Answer(request, LiveInvestmentStatus.Refused, default, [], "account_state");
        await Rejected<InvalidOperationException>(unlocks, "A refused unlock read was accepted.");

        var operation = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var grant = connection.GrantItemAsync(operation, ItemHash, 3, token);
        request = await game.NextAsync(token);
        var block = game.Block();
        Check(game.Opcode == LiveInvestmentOpcode.GrantItem && game.Operation == operation.Id && game.Expected == Bound
            && Field(block, P.RequestPayloadBytesOffset) == P.GrantItemBytes && Field(block, P.RequestPayloadOffset) == ItemHash
            && Field(block, P.RequestPayloadOffset + 4) == 3
            && block.AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset + P.GrantItemBytes).IndexOfAnyExcept((byte)0) < 0,
            "The grant request layout differs.");
        Unresolved(operation, "A written mutation was not unresolved.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Receipt(operation.Id, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Queued));
        Check(await grant.WaitAsync(token) is { State: LiveReceiptState.Queued, Terminal: false }, "The queued receipt was not read.");
        await Rejected<InvalidOperationException>(connection.GrantItemAsync(operation, ItemHash, 3, token),
            "An unresolved operation was sent again.");

        var read = connection.ReadReceiptAsync(operation.Id, token);
        request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.ReadReceipt && game.Operation == operation.Id && game.Expected == default
            && Field(game.Block(), P.RequestPayloadBytesOffset) == 0, "The receipt request layout differs.");
        var published = Receipt(operation.Id, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Published, version: 9, changed: 2, finished: 150);
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, published);
        Check(await read.WaitAsync(token) is { State: LiveReceiptState.Published, Terminal: true, Family4Version: 9, ChangedRows: 2 },
            "The published receipt was not read.");
        Unresolved(operation, "Reading a published receipt resolved its operation.");
        var acknowledge = connection.AcknowledgeAsync(operation.Id, token);
        request = await game.NextAsync(token);
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, published);
        await acknowledge.WaitAsync(token);
        await Resolved(operation, "An acknowledged operation stayed unresolved.");

        // A set objective goes out as encoded, with its id and context, and its receipt names opcode 14.
        var edit = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var setObjective = connection.SetPursuitObjectiveAsync(edit, 0x4000000000000100, ItemHash, 4, 0x80A1C0DE, 1, -1, 40, token);
        request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.SetPursuitObjective && game.Operation == edit.Id && game.Expected == Bound
            && Field(game.Block(), P.RequestPayloadBytesOffset) == P.SetObjectiveBytes
            && game.Block().AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset)
                .StartsWith(P.SetPursuitObjective(0x4000000000000100, ItemHash, 4, 0x80A1C0DE, 1, -1, 40)),
            "The set objective request was not sent as encoded.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Receipt(edit.Id, LiveInvestmentOpcode.SetPursuitObjective, LiveReceiptState.Queued));
        Check(await setObjective.WaitAsync(token) is { State: LiveReceiptState.Queued, Opcode: LiveInvestmentOpcode.SetPursuitObjective },
            "The set objective receipt was not read.");
        var forget = connection.ReadReceiptAsync(edit.Id, token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, default, Receipt(edit.Id, LiveInvestmentOpcode.None, LiveReceiptState.Unknown));
        await forget.WaitAsync(token);
        var completion = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var completeTask = connection.CompletePursuitObjectivesAsync(completion, 0x4000000000000100, ItemHash, 4, token);
        request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.CompletePursuitObjectives && Field(game.Block(), P.RequestPayloadBytesOffset) == 16,
            "The complete objectives request was not sent.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Receipt(completion.Id, LiveInvestmentOpcode.CompletePursuitObjectives,
            LiveReceiptState.Refused, finished: 150), "objectives_complete");
        Check(await completeTask.WaitAsync(token) is { State: LiveReceiptState.Refused, Reason: "objectives_complete" },
            "A refused completion receipt was not read.");
        var dropped = connection.AcknowledgeAsync(completion.Id, token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, default, Receipt(completion.Id, LiveInvestmentOpcode.None, LiveReceiptState.Unknown));
        await dropped.WaitAsync(token);

        // XP, a claim and a reset go out as encoded, each with its id and context, and each receipt is read and acknowledged.
        (LiveInvestmentOpcode Opcode, Func<LiveInvestmentOperation, Task<LiveInvestmentReceipt>> Send, byte[] Payload)[] passRequests =
        [
            (LiveInvestmentOpcode.SetSeasonalExperience, sent => connection.SetSeasonalExperienceAsync(sent, 10, 20, token),
                P.SetSeasonalExperience(10, 20)),
            (LiveInvestmentOpcode.ClaimSeasonPassReward, sent => connection.ClaimSeasonPassRewardAsync(sent, 3, ItemHash, token), P.SeasonPassRow(3, ItemHash)),
            (LiveInvestmentOpcode.ResetSeasonPassClaim, sent => connection.ResetSeasonPassClaimAsync(sent, 4, ItemHash, token), P.SeasonPassRow(4, ItemHash))
        ];
        var passOperations = new List<LiveInvestmentOperation>();
        foreach (var (opcode, send, payload) in passRequests)
        {
            var sent = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
            passOperations.Add(sent);
            var submitted = send(sent);
            request = await game.NextAsync(token);
            block = game.Block();
            Check(game.Opcode == opcode && game.Operation == sent.Id && game.Expected == Bound
                && Field(block, P.RequestPayloadBytesOffset) == payload.Length
                && block.AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset, payload.Length).SequenceEqual(payload)
                && block.AsSpan(P.RequestPayloadOffset - P.RequestBlockOffset + payload.Length).IndexOfAnyExcept((byte)0) < 0,
                $"The {opcode} request was not sent as encoded.");
            game.Answer(request, LiveInvestmentStatus.Ok, Bound, Receipt(sent.Id, opcode, LiveReceiptState.Published, finished: 150));
            Check(await submitted.WaitAsync(token) is { State: LiveReceiptState.Published } receipt && receipt.Opcode == opcode,
                $"The {opcode} receipt was not read.");
            var acknowledged = connection.AcknowledgeAsync(sent.Id, token);
            game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, Bound, Receipt(sent.Id, opcode, LiveReceiptState.Published, finished: 150));
            await acknowledged.WaitAsync(token);
        }
        foreach (var done in passOperations) await Resolved(done, "An acknowledged XP, claim or reset stayed unresolved.");

        // SetQuantity fields at +0/+8/+16/+20/+24/+28 of the request payload; the character is the expected context's.
        var reused = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var quantity = connection.SetQuantityAsync(reused, LiveInventoryScope.CharacterItem, 0x4000000000000100, ItemHash, 4, 1, 0, token);
        request = await game.NextAsync(token);
        block = game.Block();
        int at = P.RequestPayloadOffset;
        Check(game.Expected == Bound && block[at - P.RequestBlockOffset] == (byte)LiveInventoryScope.CharacterItem
            && Field64(block, at + 8) == 0x4000000000000100 && Field(block, at + 16) == ItemHash && Field(block, at + 20) == 4
            && Field(block, at + 24) == 1 && Field(block, at + 28) == 0 && block.AsSpan(at + 1 - P.RequestBlockOffset, 7).IndexOfAnyExcept((byte)0) < 0
            && block.AsSpan(at + P.SetQuantityBytes - P.RequestBlockOffset).IndexOfAnyExcept((byte)0) < 0,
            "The set quantity request layout differs.");
        game.Answer(request, LiveInvestmentStatus.Refused, Bound,
            Receipt(reused.Id, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Published, finished: 150), "operation_reused");
        await Rejected<InvalidOperationException>(quantity, "A submission the game did not retain was accepted.");
        await Resolved(reused, "A submission the game did not retain stayed unresolved.");

        var lost = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var silent = connection.SetQuantityAsync(lost, LiveInventoryScope.CharacterStack, 0, StackHash, 2, 3, 5, token);
        uint unanswered = await game.NextAsync(token);
        game.Skip(unanswered);
        try { await silent.WaitAsync(token); throw new InvalidDataException("A mutation without an answer completed."); }
        catch (LiveInvestmentOutcomeUnknownException error) { Check(error.Operation == lost, "The unknown outcome names another operation."); }
        Unresolved(lost, "A timed-out mutation was not kept unresolved.");
        var snapshot = connection.InventoryAsync(token);
        game.Skip(await game.NextAsync(token));
        try { await snapshot.WaitAsync(token); throw new InvalidDataException("A snapshot without an answer completed."); }
        catch (TimeoutException error) when (error is not LiveInvestmentOutcomeUnknownException) { }

        var check = connection.ReadReceiptAsync(lost.Id, token);
        request = await game.NextAsync(token);
        game.Answer(unanswered, LiveInvestmentStatus.Ok, Bound, Receipt(lost.Id, LiveInvestmentOpcode.SetQuantity, LiveReceiptState.Queued),
            answerId: unanswered);
        await Task.Delay(100, token);
        Check(!check.IsCompleted, "A late answer to a timed-out request was taken.");
        game.Answer(request, LiveInvestmentStatus.Ok, default, Receipt(lost.Id, LiveInvestmentOpcode.None, LiveReceiptState.Unknown));
        Check(await check.WaitAsync(token) is { State: LiveReceiptState.Unknown },
            "A receipt the game never received did not resolve the operation.");
        await Resolved(lost, "A receipt the game never received did not resolve the operation.");

        view.Write(P.GameVersionOffset, P.Version + 1);
        await UntilAsync(() => connection.State == LiveInvestmentLinkState.VersionDiffers, token);
        Check(connection.Diagnostic.Contains("differs"), "A version mismatch has no note.");
        await Rejected<InvalidOperationException>(connection.HelloAsync(token), "A request was sent across a version mismatch.");
        connection.Dispose();
        Check(view.ReadInt64(P.HeartbeatOffset) == 0, "Dispose did not tell the game to drop the link.");
    }

    /** Studio creates the mapping on its own task; the game only opens it. */
    private static async Task<MemoryMappedFile> OpenAsync(int pid, CancellationToken token)
    {
        while (true)
        {
            try { return MemoryMappedFile.OpenExisting(P.MappingName(pid)); }
            catch (FileNotFoundException) { await Task.Delay(5, token); }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(5, token);
    }

    /** Answers like the server: response fields first, then the response id. */
    private sealed class FakeGame(MemoryMappedViewAccessor view)
    {
        private uint served;
        public LiveInvestmentOpcode Opcode => (LiveInvestmentOpcode)view.ReadUInt32(P.OpcodeOffset);
        public ulong Operation => view.ReadUInt64(P.OperationOffset);
        public LiveInvestmentContext Expected =>
            new(view.ReadUInt64(P.ExpectedOffset), view.ReadUInt64(P.ExpectedOffset + 8), view.ReadUInt64(P.ExpectedOffset + 16));

        public async Task<uint> NextAsync(CancellationToken token)
        {
            while (true)
            {
                uint request = view.ReadUInt32(P.RequestOffset);
                if (request != 0 && request != served) { Thread.MemoryBarrier(); return request; }
                await Task.Delay(1, token);
            }
        }

        public byte[] Block()
        {
            var block = new byte[P.RequestBlockBytes];
            view.ReadArray(P.RequestBlockOffset, block, 0, block.Length);
            return block;
        }

        public void Skip(uint request) => served = request;

        public void Answer(uint request, LiveInvestmentStatus status, LiveInvestmentContext context, byte[] payload, string reason = "",
            uint? answerId = null)
        {
            view.Write(P.StatusOffset, (uint)status);
            view.Write(P.ResponseOperationOffset, view.ReadUInt64(P.OperationOffset));
            view.Write(P.ResponseOpcodeOffset, view.ReadUInt32(P.OpcodeOffset));
            view.Write(P.ResponsePayloadBytesOffset, (uint)payload.Length);
            view.Write(P.ContextOffset, context.AccountSoid);
            view.Write(P.ContextOffset + 8, context.CharacterSoid);
            view.Write(P.ContextOffset + 16, context.ContentHash);
            var text = new byte[P.ReasonBytes];
            Encoding.ASCII.GetBytes(reason, text);
            view.WriteArray(P.ReasonOffset, text, 0, text.Length);
            view.WriteArray(P.ResponsePayloadOffset, payload, 0, payload.Length);
            Thread.MemoryBarrier();
            view.Write(P.ResponseOffset, answerId ?? request);
            if (answerId is null) served = request;
        }
    }

    private static byte[] HelloPayload() => HelloPayload(Served);

    private static byte[] HelloPayload(ulong capabilities)
    {
        var bytes = new byte[P.HelloBytes];
        U32(bytes, P.HelloVersionOffset, P.Version);
        U32(bytes, P.HelloReadinessOffset, (uint)(LiveInvestmentReadiness.Items | LiveInvestmentReadiness.Buckets));
        U64(bytes, P.HelloCapabilitiesOffset, capabilities); U64(bytes, P.HelloGameStartedOffset, Started);
        U32(bytes, P.HelloCharacterCountOffset, 2);
        U32(bytes, P.HelloPublicationOffset, (uint)(LiveInvestmentPublication.PeerPresent | LiveInvestmentPublication.PeerOwnsAccount));
        U64(bytes, P.HelloCharactersOffset, Character);
        bytes[P.HelloCharactersOffset + P.CharacterClassOffset] = 2; bytes[P.HelloCharactersOffset + P.CharacterSelectedOffset] = 1;
        U64(bytes, P.HelloCharactersOffset + P.CharacterBytes, OtherCharacter);
        U64(bytes, P.HelloRevisionOffset, 7);
        return bytes;
    }

    private static byte[] InventoryPayload()
    {
        var bytes = new byte[P.InventoryBytes];
        U16(bytes, P.BucketCountOffset, 2); U16(bytes, P.ProfileCountOffset, 1); U16(bytes, P.ItemCountOffset, 2);
        U16(bytes, P.StackCountOffset, 1); U64(bytes, P.InventoryRevisionOffset, 11);
        for (int index = 0; index < 2; index++)
        {
            int bucket = P.BucketsOffset + index * P.BucketBytes;
            bytes[bucket + P.BucketIdOffset] = (byte)(index == 0 ? 2 : 3);
            bytes[bucket + P.BucketArrayOffset] = (byte)(index == 0 ? 1 : 0);
            U16(bytes, bucket + P.BucketFirstSlotOffset, (ushort)(index * 9)); U16(bytes, bucket + P.BucketSlotCountOffset, 10);
            bytes[bucket + P.BucketEquipmentSlotOffset] = (byte)(index == 0 ? P.NoEquipmentSlot : 18);
        }
        U64(bytes, P.ProfileOffset + P.ProfileInstanceOffset, 0x5000000000000001);
        U32(bytes, P.ProfileOffset + P.ProfileHashOffset, StackHash); I32(bytes, P.ProfileOffset + P.ProfileQuantityOffset, 25);
        I32(bytes, P.ProfileOffset + P.ProfileSerialOffset, 4); bytes[P.ProfileOffset + P.ProfileBucketOffset] = 2;
        bytes[P.ProfileOffset + P.ProfileFlagsOffset] = P.ItemSeen;
        for (int index = 0; index < 2; index++)
        {
            int item = P.ItemsOffset + index * P.ItemBytes;
            U64(bytes, item + P.ItemInstanceOffset, 0x4000000000000200UL + (ulong)index);
            U32(bytes, item + P.ItemHashOffset, ItemHash); I32(bytes, item + P.ItemQuantityOffset, 1);
            I32(bytes, item + P.ItemLevelOffset, 1600);
            bytes[item + P.ItemBucketOffset] = index == 0 ? (byte)3 : P.NoBucket;
            bytes[item + P.ItemEquipmentSlotOffset] = index == 0 ? (byte)0 : P.Unequipped;
        }
        bytes[P.ItemsOffset + P.ItemSocketPolicyOffset] = (byte)LiveSocketPolicy.Authored;
        bytes[P.ItemsOffset + P.ItemPlugCountOffset] = 2; U16(bytes, P.ItemsOffset + P.ItemPlugPresentOffset, 1);
        U32(bytes, P.ItemsOffset + P.ItemPlugsOffset, PlugHash);
        U32(bytes, P.StacksOffset + P.StackHashOffset, StackHash); I32(bytes, P.StacksOffset + P.StackQuantityOffset, 3);
        bytes[P.StacksOffset + P.StackBucketOffset] = 5;
        return bytes;
    }

    private static byte[] PursuitPayload()
    {
        var bytes = new byte[P.PursuitBytes];
        U16(bytes, P.HeldCountOffset, 1); U64(bytes, P.PursuitRevisionOffset, 11);
        int held = P.HeldOffset;
        U64(bytes, held + P.HeldInstanceOffset, 0x4000000000000100); U32(bytes, held + P.HeldHashOffset, ItemHash);
        U16(bytes, held + P.HeldFlagsOffset, (ushort)(LivePursuitFlags.Bounty | LivePursuitFlags.ConditionsKnown));
        bytes[held + P.HeldObjectiveCountOffset] = 2;
        U16(bytes, held + P.HeldReasonOffset, (ushort)LivePursuitReason.ItemProgressNotSaved);
        int objective = held + P.HeldObjectivesOffset + P.ObjectiveBytes;
        U32(bytes, objective + P.ObjectiveHashOffset, 0x1234); I32(bytes, objective + P.ObjectiveValueOffset, 2);
        I32(bytes, objective + P.ObjectiveTargetOffset, 5); bytes[objective + P.ObjectiveFlagsOffset] = (byte)LivePursuitObjectiveFlags.Known;
        return bytes;
    }

    /** Values for Sources: account flag set, profile flag clear, character flag set, value -40, and an invalid row. */
    private static byte[] UnlockPayload()
    {
        var bytes = new byte[P.UnlockValuesBytes];
        U16(bytes, P.UnlockCountOffset, (ushort)Sources.Length); U64(bytes, P.UnlockRevisionOffset, 21);
        int[] values = [1, 0, 1, -40];
        for (int index = 0; index < values.Length; index++)
        {
            int at = P.UnlockValuesOffset + index * P.UnlockValueBytes;
            bytes[at + P.UnlockStateOffset] = 1;
            I32(bytes, at + P.UnlockValueOffset, values[index]);
        }
        return bytes;
    }

    private static void PassRow(byte[] bytes, int index, uint hash, LiveSeasonPassRowFlags flags)
    {
        int row = P.PassRowsOffset + index * P.PassRowBytes;
        U32(bytes, row + P.RowHashOffset, hash); U32(bytes, row + P.RowQuantityOffset, 1);
        bytes[row + P.RowRankOffset] = (byte)(index * 2); U16(bytes, row + P.RowFlagsOffset, (ushort)flags);
    }

    private static byte[] Receipt(ulong operationId, LiveInvestmentOpcode opcode, LiveReceiptState state, int version = 0,
        uint changed = 0, ulong finished = 0)
    {
        var bytes = new byte[P.ReceiptBytes];
        U64(bytes, P.ReceiptOperationOffset, operationId);
        if (state == LiveReceiptState.Unknown) return bytes;
        U32(bytes, P.ReceiptOpcodeOffset, (uint)opcode); U32(bytes, P.ReceiptStateOffset, (uint)state);
        I32(bytes, P.ReceiptVersionOffset, version); U32(bytes, P.ReceiptChangedRowsOffset, changed);
        U64(bytes, P.ReceiptSubmittedOffset, 100); U64(bytes, P.ReceiptFinishedOffset, finished);
        return bytes;
    }

    private static LiveInvestmentReply Reply(LiveInvestmentOpcode opcode, LiveInvestmentContext context, byte[] payload,
        ulong operationId = 0) => new(LiveInvestmentStatus.Ok, "", opcode, operationId, context, Started, Served, payload);

    private static byte[] Changed(byte[] bytes, int offset, int value) { U32(bytes, offset, (uint)value); return bytes; }
    private static byte[] Changed16(byte[] bytes, int offset, int value) { U16(bytes, offset, (ushort)value); return bytes; }
    private static byte[] Changed8(byte[] bytes, int offset, byte value) { bytes[offset] = value; return bytes; }
    private static uint Field(byte[] block, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset - P.RequestBlockOffset));
    private static ulong Field64(byte[] block, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset - P.RequestBlockOffset));
    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void I32(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U64(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }

    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidDataException(message); }

    private static void RejectArgument(Action action, string message)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidDataException(message); }

    private static async Task Rejected<T>(Task task, string message) where T : Exception
    { try { await task; } catch (T) { return; } throw new InvalidDataException(message); }
}
