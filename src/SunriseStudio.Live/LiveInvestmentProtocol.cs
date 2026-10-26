using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Live;

/** Each opcode's value is also its capability bit: the game sets bit N for every opcode N it serves. */
public enum LiveInvestmentOpcode : uint
{
    None, Hello, InventorySnapshot, SeasonPassSnapshot, PursuitSnapshot, GrantItem, SetQuantity, SetSeasonalExperience,
    ClaimSeasonPassReward, ResetSeasonPassClaim, ReadReceipt, AcknowledgeReceipt, WithdrawOperation, UnlockValues,
    SetPursuitObjective, CompletePursuitObjectives
}

/** The transport outcome of one request. A mutation's own outcome is in its receipt. */
public enum LiveInvestmentStatus : uint { None, Ok, Invalid, Refused }
/** Unknown means the game holds no receipt with that id in this process. */
public enum LiveReceiptState : uint { Unknown, Queued, Deferred, Refused, Published, CommittedUnpublished }
/** Why a non-terminal operation is waiting. */
public enum LiveInvestmentWait : uint { None, PublicationPeer, AccountResync, Catalog }
public enum LiveInventoryScope : byte { Profile, CharacterItem, CharacterStack }
public enum LiveSocketPolicy : byte { NativeDefaults, Authored }
/** Where a held pursuit's completion stands. The game reports one reason, and the values follow its priority order. */
public enum LivePursuitReason : ushort
{
    ContractUnavailable, OperationBodyUnavailable, ValueDestinationUnavailable, SweepSuppressed, ObjectivesIncomplete,
    ValueAlreadyNonzero, ObjectiveSourceUnavailable, ValueSourceUnavailable, AutomaticConditionsMet,
    ManualActionNotEstablished, ItemProgressNotSaved, ExternalValue
}

/** The game catalogs that finished loading. */
[Flags]
public enum LiveInvestmentReadiness : uint
{
    None = 0, Items = 1, ItemDetails = 2, Buckets = 4, Progressions = 8, SeasonPass = 16, Rewards = 32, Objectives = 64,
    Pursuits = 128
}
/** Whether a signed-in game client can show a change to this account. */
[Flags]
public enum LiveInvestmentPublication : uint { None = 0, PeerPresent = 1, PeerOwnsAccount = 2, PeerResyncArmed = 4 }
[Flags]
public enum LiveSeasonPassRowFlags : ushort
{
    None = 0, Available = 1, Claimed = 2, Acquired = 4, Automatic = 8, RankMet = 16, ConditionKnown = 32, ConditionMet = 64
}
[Flags]
public enum LivePursuitFlags : ushort
{
    None = 0, Bounty = 1, BindingMatches = 2, ConditionsKnown = 4, ObjectivesKnown = 8, ObjectivesComplete = 16, Eligible = 32,
    EffectsKnown = 64
}
[Flags]
public enum LivePursuitObjectiveFlags : byte { None = 0, Known = 1, Complete = 2, Skipped = 4 }
/** The kind of saved row a source reads. Zero is never valid, so unused source slots stay zero. */
public enum LiveUnlockKind : byte { Flag = 1, Value }
/** The saved bank a source reads. A value source names the account or character bank, never the profile bank. */
public enum LiveUnlockBank : byte { Account = 1, Profile, Character }

/** The account, selected character and package content one request or answer is bound to. */
public readonly record struct LiveInvestmentContext(ulong AccountSoid, ulong CharacterSoid, ulong ContentHash)
{
    /** True when every part is set, as a mutation's expected context must be. */
    public bool Complete => AccountSoid != 0 && CharacterSoid != 0 && ContentHash != 0;
}

/** One answer copied out of the mapping. Payload holds exactly the bytes the game declared. */
public sealed record LiveInvestmentReply(LiveInvestmentStatus Status, string Reason, LiveInvestmentOpcode Opcode,
    ulong OperationId, LiveInvestmentContext Context, ulong GameStarted, ulong Capabilities, byte[] Payload);

public sealed record LiveInvestmentCharacter(ulong Soid, byte CharacterClass, bool Selected);

/** GameStarted identifies the game process instance. Receipts do not outlive it. */
public sealed record LiveInvestmentHello(LiveInvestmentContext Context, LiveInvestmentReadiness Readiness,
    ulong Capabilities, ulong GameStarted, IReadOnlyList<LiveInvestmentCharacter> Characters,
    LiveInvestmentPublication Publication, ulong Revision)
{
    public bool Supports(LiveInvestmentOpcode opcode) => (Capabilities & LiveInvestmentProtocol.Capability(opcode)) != 0;
}

/** One State bucket descriptor. EquipmentSlot is -1 for a bucket that equips nothing. */
public sealed record LiveBucket(byte BucketId, byte ArraySelector, ushort FirstSlot, ushort SlotCount, sbyte EquipmentSlot);
/** BucketId is NoBucket when the definition names no installed bucket. */
public sealed record LiveProfileRow(ulong Instance, uint DefinitionHash, int Quantity, int MutationSerial, byte BucketId,
    bool Seen);
/** Plugs holds one entry per plug lane the game reports. A null entry is a lane the game reports empty. */
public sealed record LiveCharacterItem(ulong Instance, uint DefinitionHash, int Quantity, int MutationSerial, int Level,
    uint ItemFlags, byte BucketId, byte EquipmentSlot, LiveSocketPolicy SocketPolicy, IReadOnlyList<uint?> Plugs, bool Seen)
{
    public bool Equipped => EquipmentSlot != LiveInvestmentProtocol.Unequipped;
}
public sealed record LiveCharacterStack(uint DefinitionHash, int Quantity, int MutationSerial, byte BucketId);
/** The selected character and the profile, read under one game lock. */
public sealed record LiveInventorySnapshot(LiveInvestmentContext Context, ulong Revision, IReadOnlyList<LiveBucket> Buckets,
    IReadOnlyList<LiveProfileRow> Profile, IReadOnlyList<LiveCharacterItem> Items, IReadOnlyList<LiveCharacterStack> Stacks);

/** Index is the game's claim index. An unavailable placeholder row has item hash zero. */
public sealed record LiveSeasonPassRow(ushort Index, uint ItemHash, uint Quantity, byte RequiredRank, LiveSeasonPassRowFlags Flags);
public sealed record LiveSeasonPassSnapshot(LiveInvestmentContext Context, ulong Revision, int Experience, ushort Rank,
    IReadOnlyList<LiveSeasonPassRow> Rows);

public sealed record LivePursuitObjective(uint ObjectiveHash, int Value, int Target, LivePursuitObjectiveFlags Flags);
public sealed record LiveHeldPursuit(ulong Instance, uint ItemHash, int MutationSerial, int Quantity, uint ItemFlags,
    LivePursuitFlags Flags, LivePursuitReason Reason, IReadOnlyList<LivePursuitObjective> Objectives);
public sealed record LivePursuitSnapshot(LiveInvestmentContext Context, ulong Revision, IReadOnlyList<LiveHeldPursuit> Held);

/** One saved flag or value row. Index is the row inside its bank, bound from the installed slot tables. */
public readonly record struct LiveUnlockSource(LiveUnlockKind Kind, LiveUnlockBank Bank, ushort Index);
/** Known is false when the bank cannot hold this row, and Value is then 0. A known flag reads 0 or 1. */
public sealed record LiveUnlockValue(LiveUnlockSource Source, bool Known, int Value);
/** Saved rows read for the selected character under one game lock, in the order they were requested. */
public sealed record LiveUnlockSnapshot(LiveInvestmentContext Context, ulong Revision, IReadOnlyList<LiveUnlockValue> Values);

/**
 * The game's record of one mutation. Reason is the label sent with this answer: why the mutation or the request was
 * refused, or why a published grant stopped part way.
 */
public sealed record LiveInvestmentReceipt(ulong OperationId, LiveInvestmentOpcode Opcode, LiveReceiptState State,
    string Reason, LiveInvestmentWait Wait, int Family4Version, uint ChangedRows, ulong SubmittedTick, ulong FinishedTick,
    LiveInvestmentContext Context)
{
    public bool Terminal => State is LiveReceiptState.Refused or LiveReceiptState.Published or LiveReceiptState.CommittedUnpublished;
}

/** A mutation's identity. Studio never reuses an id, so an acknowledged receipt cannot be mistaken for a new one. */
public sealed record LiveInvestmentOperation(ulong Id, LiveInvestmentContext Expected, ulong GameStarted)
{
    /** Draws a random nonzero id bound to the target and the game instance it was read from. */
    public static LiveInvestmentOperation Create(LiveInvestmentContext expected, ulong gameStarted)
    {
        if (!expected.Complete || gameStarted == 0)
            throw new ArgumentException("An operation needs an account, a selected character and the game's catalog.", nameof(expected));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong id;
        do { RandomNumberGenerator.Fill(bytes); id = BinaryPrimitives.ReadUInt64LittleEndian(bytes); } while (id == 0);
        return new(id, expected, gameStarted);
    }
}

/**
 * Mirrors the investment mapping in Sunrise's middleware/studio/studio_investment_wire.h. Offsets are absolute in the
 * mapping, except payload field offsets, which count from the start of their payload.
 */
public static class LiveInvestmentProtocol
{
    public const uint Magic = 0x56495353, Version = 2;
    public const int ControlBytes = 64, RequestBlockBytes = 80, ResponseHeaderBytes = 112, ReasonBytes = 64;
    public const int RequestPayloadBytes = 40, ResponsePayloadBytes = 55008;
    public const int RequestBlockOffset = 64, ResponseBlockOffset = 144, ResponsePayloadOffset = 256;
    public const int MappingBytes = ResponsePayloadOffset + ResponsePayloadBytes;
    // Mirror investment_wire::kConnectIntervalMs and kHeartbeatTimeoutMs (middleware/studio/studio_investment_wire.h).
    public const int ConnectIntervalMilliseconds = 1000, HeartbeatTimeoutMilliseconds = 3000;

    public const int MagicOffset = 0, VersionOffset = 4, BytesOffset = 8, EditorPidOffset = 12, HeartbeatOffset = 16;
    public const int GameVersionOffset = 24, GamePidOffset = 28, CapabilitiesOffset = 32, GameStartedOffset = 40;
    public const int RequestOffset = 48, ResponseOffset = 52, ControlReservedOffset = 56;
    public const int OpcodeOffset = 64, RequestPayloadBytesOffset = 68, OperationOffset = 72, ExpectedOffset = 80;
    public const int RequestPayloadOffset = 104;
    public const int StatusOffset = 144, ResponseReservedOffset = 148, ResponseOperationOffset = 152, ResponseOpcodeOffset = 160;
    public const int ResponsePayloadBytesOffset = 164, ContextOffset = 168, ReasonOffset = 192;
    public const int ContextBytes = 24;

    public const int GrantItemBytes = 8, SetQuantityBytes = 32, SetSeasonalExperienceBytes = 8, SeasonPassRowBytes = 8;
    public const int GrantHashOffset = 0, GrantQuantityOffset = 4;
    public const int SetScopeOffset = 0, SetReservedOffset = 1, SetInstanceOffset = 8, SetHashOffset = 16;
    public const int SetExpectedSerialOffset = 20, SetExpectedQuantityOffset = 24, SetQuantityOffset = 28;
    public const int ExperienceExpectedOffset = 0, ExperienceReplacementOffset = 4;
    public const int PassRowIndexOffset = 0, PassReservedOffset = 2, PassHashOffset = 4;

    public const int HelloBytes = 88, InventoryBytes = 55008, SeasonPassBytes = 4112, PursuitBytes = 47968, ReceiptBytes = 48;
    public const int CharacterCapacity = 3, BucketCapacity = 255, ProfileRowCapacity = 701, CharacterStackCapacity = 333;
    /** The native character array's 350 rows: 333 unequipped rows, which include held pursuits, and 17 equipment slots. */
    public const int UnequippedItemCapacity = 333, EquipmentSlotCount = 17;
    public const int CharacterItemCapacity = UnequippedItemCapacity + EquipmentSlotCount;
    public const int PlugCapacity = 12, SeasonPassRowCapacity = 256, HeldPursuitCapacity = UnequippedItemCapacity;
    public const int PursuitObjectiveCapacity = 7;
    public const byte NoBucket = 0xFF, Unequipped = 0xFF, ItemSeen = 1;
    public const sbyte NoEquipmentSlot = -1;
    public const byte LastArraySelector = 2;

    public const int HelloVersionOffset = 0, HelloReadinessOffset = 4, HelloCapabilitiesOffset = 8, HelloGameStartedOffset = 16;
    public const int HelloCharacterCountOffset = 24, HelloPublicationOffset = 28, HelloCharactersOffset = 32;
    public const int HelloRevisionOffset = 80, CharacterBytes = 16;
    public const int CharacterSoidOffset = 0, CharacterClassOffset = 8, CharacterSelectedOffset = 9, CharacterReservedOffset = 10;

    public const int BucketCountOffset = 0, ProfileCountOffset = 2, ItemCountOffset = 4, StackCountOffset = 6;
    public const int InventoryRevisionOffset = 8, BucketsOffset = 16, ProfileOffset = 2056, ItemsOffset = 18880, StacksOffset = 49680;
    public const int BucketBytes = 8, ProfileRowBytes = 24, ItemBytes = 88, StackBytes = 16;
    public const int BucketIdOffset = 0, BucketArrayOffset = 1, BucketFirstSlotOffset = 2, BucketSlotCountOffset = 4;
    public const int BucketEquipmentSlotOffset = 6, BucketReservedOffset = 7;
    public const int ProfileInstanceOffset = 0, ProfileHashOffset = 8, ProfileQuantityOffset = 12, ProfileSerialOffset = 16;
    public const int ProfileBucketOffset = 20, ProfileFlagsOffset = 21, ProfileReservedOffset = 22;
    public const int ItemInstanceOffset = 0, ItemHashOffset = 8, ItemQuantityOffset = 12, ItemSerialOffset = 16;
    public const int ItemLevelOffset = 20, ItemFlagsOffset = 24, ItemBucketOffset = 28, ItemEquipmentSlotOffset = 29;
    public const int ItemSocketPolicyOffset = 30, ItemPlugCountOffset = 31, ItemPlugPresentOffset = 32, ItemRecordFlagsOffset = 34;
    public const int ItemReservedOffset = 35, ItemPlugsOffset = 36, ItemTailOffset = 84;
    public const int StackHashOffset = 0, StackQuantityOffset = 4, StackSerialOffset = 8, StackBucketOffset = 12;
    public const int StackReservedOffset = 13;

    public const int PassExperienceOffset = 0, PassRankOffset = 4, PassRowCountOffset = 6, PassRevisionOffset = 8;
    public const int PassRowsOffset = 16, PassRowBytes = 16;
    public const int RowHashOffset = 0, RowQuantityOffset = 4, RowRankOffset = 8, RowReservedOffset = 9, RowFlagsOffset = 10;
    public const int RowTailOffset = 12;

    public const int HeldCountOffset = 0, PursuitReservedOffset = 2, PursuitRevisionOffset = 8;
    public const int HeldOffset = 16, HeldBytes = 144, ObjectiveBytes = 16;
    public const int HeldInstanceOffset = 0, HeldHashOffset = 8, HeldSerialOffset = 12, HeldQuantityOffset = 16;
    public const int HeldItemFlagsOffset = 20, HeldFlagsOffset = 24, HeldObjectiveCountOffset = 26;
    public const int HeldReservedOffset = 27, HeldReasonOffset = 28, HeldTailOffset = 30, HeldObjectivesOffset = 32;
    public const int ObjectiveHashOffset = 0, ObjectiveValueOffset = 4, ObjectiveTargetOffset = 8, ObjectiveFlagsOffset = 12;
    public const int ObjectiveReservedOffset = 13;

    public const int ReceiptOperationOffset = 0, ReceiptOpcodeOffset = 8, ReceiptStateOffset = 12, ReceiptReservedOffset = 16;
    public const int ReceiptWaitOffset = 20, ReceiptVersionOffset = 24, ReceiptChangedRowsOffset = 28;
    public const int ReceiptSubmittedOffset = 32, ReceiptFinishedOffset = 40;

    /** Sources one unlockValues request reads: a 4-byte header and nine 4-byte sources fill the request payload. */
    public const int UnlockReadCapacity = 9, UnlockValuesRequestBytes = 40, UnlockValuesBytes = 88;
    public const int UnlockCountOffset = 0, UnlockRequestReservedOffset = 2, UnlockSourcesOffset = 4, UnlockSourceBytes = 4;
    public const int UnlockKindOffset = 0, UnlockBankOffset = 1, UnlockIndexOffset = 2;
    public const int UnlockReservedOffset = 2, UnlockRevisionOffset = 8, UnlockValuesOffset = 16, UnlockValueBytes = 8;
    public const int UnlockStateOffset = 0, UnlockStateReservedOffset = 1, UnlockValueOffset = 4;

    /** SetPursuitObjectiveRequest and CompletePursuitObjectivesRequest share their first three fields. */
    public const int SetObjectiveBytes = 32, CompleteObjectivesBytes = 16;
    public const int PursuitInstanceOffset = 0, PursuitHashOffset = 8, PursuitSerialOffset = 12;
    public const int ObjectiveRequestHashOffset = 16, ObjectiveOrdinalOffset = 20, ObjectiveRequestReservedOffset = 21;
    public const int ObjectiveExpectedOffset = 24, ObjectiveReplacementOffset = 28;

    private const uint ReadinessBits = 0xFF, PublicationBits = 0x7;
    private const ushort SeasonPassRowBits = 0x7F, PursuitBits = 0x7F;
    private const byte ObjectiveBits = 0x7;
    /** Unlock read states: invalid when the bank cannot hold the row, known when the value is the saved one. */
    private const byte UnlockInvalid = 0, UnlockKnown = 1;
    /** Opcodes this Studio build knows. ReadHello drops any other bit a newer Sunrise advertises, so the link still opens. */
    private const ulong KnownCapabilities = ((1UL << ((int)LiveInvestmentOpcode.CompletePursuitObjectives + 1)) - 1) & ~1UL;

    public static string MappingName(int processId) => $@"Local\SunriseStudio.Investment.{processId}";
    public static ulong Capability(LiveInvestmentOpcode opcode) => 1UL << (int)opcode;
    /** Mutations carry an operation id and a complete expected context. */
    public static bool IsMutation(LiveInvestmentOpcode opcode) =>
        opcode is >= LiveInvestmentOpcode.GrantItem and <= LiveInvestmentOpcode.ResetSeasonPassClaim
            or LiveInvestmentOpcode.SetPursuitObjective or LiveInvestmentOpcode.CompletePursuitObjectives;
    /** Receipt requests carry the id of an earlier mutation and no context. */
    public static bool IsReceiptRequest(LiveInvestmentOpcode opcode) =>
        opcode is >= LiveInvestmentOpcode.ReadReceipt and <= LiveInvestmentOpcode.WithdrawOperation;

    public static int RequestBytes(LiveInvestmentOpcode opcode) => opcode switch
    {
        LiveInvestmentOpcode.GrantItem => GrantItemBytes,
        LiveInvestmentOpcode.SetQuantity => SetQuantityBytes,
        LiveInvestmentOpcode.SetSeasonalExperience => SetSeasonalExperienceBytes,
        LiveInvestmentOpcode.ClaimSeasonPassReward or LiveInvestmentOpcode.ResetSeasonPassClaim => SeasonPassRowBytes,
        LiveInvestmentOpcode.UnlockValues => UnlockValuesRequestBytes,
        LiveInvestmentOpcode.SetPursuitObjective => SetObjectiveBytes,
        LiveInvestmentOpcode.CompletePursuitObjectives => CompleteObjectivesBytes,
        _ => 0
    };

    public static int ResponseBytes(LiveInvestmentOpcode opcode) => opcode switch
    {
        LiveInvestmentOpcode.Hello => HelloBytes,
        LiveInvestmentOpcode.InventorySnapshot => InventoryBytes,
        LiveInvestmentOpcode.SeasonPassSnapshot => SeasonPassBytes,
        LiveInvestmentOpcode.PursuitSnapshot => PursuitBytes,
        LiveInvestmentOpcode.UnlockValues => UnlockValuesBytes,
        _ when IsMutation(opcode) || IsReceiptRequest(opcode) => ReceiptBytes,
        _ => 0
    };

    /** Encodes a whole request block with every unused byte zero. The request id goes in the control block separately. */
    public static void EncodeRequest(Span<byte> block, LiveInvestmentOpcode opcode, ulong operationId,
        LiveInvestmentContext expected, ReadOnlySpan<byte> payload)
    {
        if (block.Length != RequestBlockBytes) throw new ArgumentException("The request block has the wrong size.", nameof(block));
        if (opcode is LiveInvestmentOpcode.None or > LiveInvestmentOpcode.CompletePursuitObjectives)
            throw new ArgumentOutOfRangeException(nameof(opcode));
        if (payload.Length != RequestBytes(opcode)) throw new ArgumentException("The payload does not fit this command.", nameof(payload));
        bool identified = IsMutation(opcode) || IsReceiptRequest(opcode);
        if ((operationId != 0) != identified)
            throw new ArgumentException("Only mutations and receipt requests carry an operation id.", nameof(operationId));
        if (IsMutation(opcode) ? !expected.Complete : expected != default)
            throw new ArgumentException("Only mutations carry an expected context, and it names every part.", nameof(expected));
        block.Clear();
        PutU32(block, OpcodeOffset - RequestBlockOffset, (uint)opcode);
        PutU32(block, RequestPayloadBytesOffset - RequestBlockOffset, (uint)payload.Length);
        PutU64(block, OperationOffset - RequestBlockOffset, operationId);
        PutContext(block[(ExpectedOffset - RequestBlockOffset)..], expected);
        payload.CopyTo(block[(RequestPayloadOffset - RequestBlockOffset)..]);
    }

    public static byte[] GrantItem(uint itemHash, uint quantity)
    {
        if (itemHash == 0) throw new ArgumentOutOfRangeException(nameof(itemHash));
        if (quantity is 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(quantity));
        var bytes = new byte[GrantItemBytes];
        PutU32(bytes, GrantHashOffset, itemHash);
        PutU32(bytes, GrantQuantityOffset, quantity);
        return bytes;
    }

    /**
     * Quantity 0 removes the row. Instance is zero for character stacks and profile stacks without their own item.
     * A character row belongs to the expected context's character.
     */
    public static byte[] SetQuantity(LiveInventoryScope scope, ulong instance, uint hash, int expectedSerial, int expectedQuantity,
        int quantity)
    {
        if (scope > LiveInventoryScope.CharacterStack) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scope == LiveInventoryScope.CharacterItem ? instance == 0 : scope == LiveInventoryScope.CharacterStack && instance != 0)
            throw new ArgumentOutOfRangeException(nameof(instance));
        if (hash == 0) throw new ArgumentOutOfRangeException(nameof(hash));
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var bytes = new byte[SetQuantityBytes];
        bytes[SetScopeOffset] = (byte)scope;
        PutU64(bytes, SetInstanceOffset, instance);
        PutU32(bytes, SetHashOffset, hash);
        PutI32(bytes, SetExpectedSerialOffset, expectedSerial);
        PutI32(bytes, SetExpectedQuantityOffset, expectedQuantity);
        PutI32(bytes, SetQuantityOffset, quantity);
        return bytes;
    }

    /** The game compares its current experience with expected before it writes the replacement. */
    public static byte[] SetSeasonalExperience(int expected, int replacement)
    {
        if (replacement < 0) throw new ArgumentOutOfRangeException(nameof(replacement));
        var bytes = new byte[SetSeasonalExperienceBytes];
        PutI32(bytes, ExperienceExpectedOffset, expected);
        PutI32(bytes, ExperienceReplacementOffset, replacement);
        return bytes;
    }

    /** Names one pass row by its claim index for a claim or claim reset. The game checks that the row's item still matches. */
    public static byte[] SeasonPassRow(ushort rowIndex, uint itemHash)
    {
        if (rowIndex >= SeasonPassRowCapacity) throw new ArgumentOutOfRangeException(nameof(rowIndex));
        if (itemHash == 0) throw new ArgumentOutOfRangeException(nameof(itemHash));
        var bytes = new byte[SeasonPassRowBytes];
        PutU16(bytes, PassRowIndexOffset, rowIndex);
        PutU32(bytes, PassHashOffset, itemHash);
        return bytes;
    }

    /** Encodes up to UnlockReadCapacity sources for the selected character. A value source cannot name the profile bank. */
    public static byte[] UnlockValues(IReadOnlyList<LiveUnlockSource> sources)
    {
        if (sources.Count is 0 or > UnlockReadCapacity) throw new ArgumentOutOfRangeException(nameof(sources));
        var bytes = new byte[UnlockValuesRequestBytes];
        PutU16(bytes, UnlockCountOffset, (ushort)sources.Count);
        for (int index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            if (source.Kind is not (LiveUnlockKind.Flag or LiveUnlockKind.Value)
                || source.Bank is not (LiveUnlockBank.Account or LiveUnlockBank.Profile or LiveUnlockBank.Character)
                || source is { Kind: LiveUnlockKind.Value, Bank: LiveUnlockBank.Profile })
                throw new ArgumentException("A source names a kind or bank the game does not read.", nameof(sources));
            int at = UnlockSourcesOffset + index * UnlockSourceBytes;
            bytes[at + UnlockKindOffset] = (byte)source.Kind;
            bytes[at + UnlockBankOffset] = (byte)source.Bank;
            PutU16(bytes, at + UnlockIndexOffset, source.Index);
        }
        return bytes;
    }

    /**
     * The game compares the objective's saved row with expected before it writes value. A flag value is 0 or 1.
     * The ordinal is the objective's position in the pursuit's installed contract.
     */
    public static byte[] SetPursuitObjective(ulong instance, uint itemHash, int expectedSerial, uint objectiveHash, byte ordinal,
        int expected, int value)
    {
        if (ordinal >= PursuitObjectiveCapacity) throw new ArgumentOutOfRangeException(nameof(ordinal));
        var bytes = new byte[SetObjectiveBytes];
        PutHeldPursuit(bytes, instance, itemHash, expectedSerial);
        PutU32(bytes, ObjectiveRequestHashOffset, objectiveHash);
        bytes[ObjectiveOrdinalOffset] = ordinal;
        PutI32(bytes, ObjectiveExpectedOffset, expected);
        PutI32(bytes, ObjectiveReplacementOffset, value);
        return bytes;
    }

    /** Asks the game to set every incomplete objective of one held pursuit to its target. The pursuit is not turned in. */
    public static byte[] CompletePursuitObjectives(ulong instance, uint itemHash, int expectedSerial)
    {
        var bytes = new byte[CompleteObjectivesBytes];
        PutHeldPursuit(bytes, instance, itemHash, expectedSerial);
        return bytes;
    }

    private static void PutHeldPursuit(Span<byte> bytes, ulong instance, uint itemHash, int expectedSerial)
    {
        if (instance == 0) throw new ArgumentOutOfRangeException(nameof(instance));
        if (itemHash == 0) throw new ArgumentOutOfRangeException(nameof(itemHash));
        PutU64(bytes, PursuitInstanceOffset, instance);
        PutU32(bytes, PursuitHashOffset, itemHash);
        PutI32(bytes, PursuitSerialOffset, expectedSerial);
    }

    /**
     * Reads the answer to one request.
     * @param control The 64-byte control block.
     * @param response The response block from its start, holding at least the declared payload.
     * @param gameStarted The instance the connection already saw, or 0 before its first answer.
     * @return Null until the response id matches the request.
     */
    public static LiveInvestmentReply? Read(ReadOnlySpan<byte> control, ReadOnlySpan<byte> response, uint gamePid,
        ulong gameStarted, uint request, LiveInvestmentOpcode opcode, ulong operationId)
    {
        if (gamePid == 0 || request == 0 || control.Length != ControlBytes || U32(control, MagicOffset) != Magic
            || U32(control, VersionOffset) != Version || U32(control, BytesOffset) != MappingBytes
            || U32(control, GameVersionOffset) != Version)
            throw new InvalidDataException("The investment link has an incompatible layout.");
        if (U32(control, ResponseOffset) != request) return null;
        if (U32(control, GamePidOffset) != gamePid) throw new InvalidDataException("The investment reply belongs to another game process.");
        ulong started = U64(control, GameStartedOffset);
        if (started == 0 || (gameStarted != 0 && started != gameStarted))
            throw new InvalidDataException("The investment reply belongs to another game instance.");
        Zero(control[ControlReservedOffset..], "investment link control block");
        if (response.Length < ResponseHeaderBytes) throw new InvalidDataException("The investment reply is incomplete.");
        uint status = U32(response, StatusOffset - ResponseBlockOffset);
        if (status is (uint)LiveInvestmentStatus.None or > (uint)LiveInvestmentStatus.Refused)
            throw new InvalidDataException($"The game sent unknown investment status {status}.");
        Zero(response.Slice(ResponseReservedOffset - ResponseBlockOffset, sizeof(uint)), "investment reply");
        if (U32(response, ResponseOpcodeOffset - ResponseBlockOffset) != (uint)opcode
            || U64(response, ResponseOperationOffset - ResponseBlockOffset) != operationId)
            throw new InvalidDataException("The investment reply answers another request.");
        uint payloadBytes = U32(response, ResponsePayloadBytesOffset - ResponseBlockOffset);
        bool full = payloadBytes == ResponseBytes(opcode);
        var context = ReadContext(response[(ContextOffset - ResponseBlockOffset)..]);
        string reason = ReadReason(response.Slice(ReasonOffset - ResponseBlockOffset, ReasonBytes));
        if ((payloadBytes != 0 && !full) || response.Length < ResponseHeaderBytes + payloadBytes)
            throw new InvalidDataException($"The investment reply declares {payloadBytes} payload bytes.");
        switch ((LiveInvestmentStatus)status)
        {
            case LiveInvestmentStatus.Ok when !full:
                throw new InvalidDataException("The investment reply has no payload.");
            case LiveInvestmentStatus.Invalid when payloadBytes != 0 || context != default:
                throw new InvalidDataException("A rejected investment request carries a payload or context.");
            case LiveInvestmentStatus.Refused when reason.Length == 0:
                throw new InvalidDataException("A refused investment request has no reason.");
        }
        return new((LiveInvestmentStatus)status, reason, opcode, operationId, context, started,
            U64(control, CapabilitiesOffset), response.Slice(ResponseHeaderBytes, (int)payloadBytes).ToArray());
    }

    public static LiveInvestmentHello ReadHello(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.Hello, HelloBytes);
        if (U32(bytes, HelloVersionOffset) != Version) throw new InvalidDataException("The investment hello names another protocol version.");
        uint readiness = Bits(U32(bytes, HelloReadinessOffset), ReadinessBits, "catalog readiness");
        ulong capabilities = U64(bytes, HelloCapabilitiesOffset);
        if (capabilities != reply.Capabilities || (capabilities & Capability(LiveInvestmentOpcode.Hello)) == 0)
            throw new InvalidDataException("The investment hello advertises commands the link does not.");
        if (U64(bytes, HelloGameStartedOffset) != reply.GameStarted)
            throw new InvalidDataException("The investment hello names another game instance.");
        uint count = U32(bytes, HelloCharacterCountOffset);
        if (count > CharacterCapacity) throw new InvalidDataException("The investment hello has too many characters.");
        uint publication = Bits(U32(bytes, HelloPublicationOffset), PublicationBits, "publication state");
        var characters = new LiveInvestmentCharacter[count];
        for (int index = 0; index < count; index++)
        {
            var row = bytes.Slice(HelloCharactersOffset + index * CharacterBytes, CharacterBytes);
            byte selected = row[CharacterSelectedOffset];
            if (U64(row, CharacterSoidOffset) == 0 || selected > 1)
                throw new InvalidDataException("An investment hello character is invalid.");
            Zero(row[CharacterReservedOffset..], "investment hello character");
            characters[index] = new(U64(row, CharacterSoidOffset), row[CharacterClassOffset], selected != 0);
        }
        Zero(bytes[(HelloCharactersOffset + (int)count * CharacterBytes)..HelloRevisionOffset], "investment hello");
        var chosen = characters.Where(character => character.Selected).ToArray();
        if (chosen.Length > 1 || (chosen.Length == 1 ? chosen[0].Soid : 0) != reply.Context.CharacterSoid)
            throw new InvalidDataException("The investment hello's selected character differs from its context.");
        return new(reply.Context, (LiveInvestmentReadiness)readiness, capabilities & KnownCapabilities, reply.GameStarted, characters,
            (LiveInvestmentPublication)publication, U64(bytes, HelloRevisionOffset));
    }

    public static LiveInventorySnapshot ReadInventory(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.InventorySnapshot, InventoryBytes);
        int buckets = Count(U16(bytes, BucketCountOffset), BucketCapacity, "inventory bucket");
        int profile = Count(U16(bytes, ProfileCountOffset), ProfileRowCapacity, "profile row");
        int items = Count(U16(bytes, ItemCountOffset), CharacterItemCapacity, "character item");
        int stacks = Count(U16(bytes, StackCountOffset), CharacterStackCapacity, "character stack");
        return new(reply.Context, U64(bytes, InventoryRevisionOffset),
            Rows(bytes, BucketsOffset, BucketBytes, buckets, BucketCapacity, "inventory bucket", ReadBucket),
            Rows(bytes, ProfileOffset, ProfileRowBytes, profile, ProfileRowCapacity, "profile row", ReadProfileRow),
            Rows(bytes, ItemsOffset, ItemBytes, items, CharacterItemCapacity, "character item", ReadItem),
            Rows(bytes, StacksOffset, StackBytes, stacks, CharacterStackCapacity, "character stack", ReadStack));
    }

    public static LiveSeasonPassSnapshot ReadSeasonPass(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.SeasonPassSnapshot, SeasonPassBytes);
        int count = Count(U16(bytes, PassRowCountOffset), SeasonPassRowCapacity, "season pass row");
        var rows = Rows(bytes, PassRowsOffset, PassRowBytes, count, SeasonPassRowCapacity, "season pass row",
            (row, index) =>
            {
                Zero(row.Slice(RowReservedOffset, 1), "season pass row");
                Zero(row[RowTailOffset..], "season pass row");
                uint hash = U32(row, RowHashOffset);
                var flags = (LiveSeasonPassRowFlags)Bits(U16(row, RowFlagsOffset), SeasonPassRowBits, "season pass row state");
                if ((hash != 0) != flags.HasFlag(LiveSeasonPassRowFlags.Available))
                    throw new InvalidDataException("A season pass row's item disagrees with its availability.");
                return new LiveSeasonPassRow((ushort)index, hash, U32(row, RowQuantityOffset), row[RowRankOffset], flags);
            });
        return new(reply.Context, U64(bytes, PassRevisionOffset), I32(bytes, PassExperienceOffset), U16(bytes, PassRankOffset), rows);
    }

    public static LivePursuitSnapshot ReadPursuits(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.PursuitSnapshot, PursuitBytes);
        int count = Count(U16(bytes, HeldCountOffset), HeldPursuitCapacity, "held pursuit");
        Zero(bytes[PursuitReservedOffset..PursuitRevisionOffset], "pursuit snapshot");
        return new(reply.Context, U64(bytes, PursuitRevisionOffset),
            Rows(bytes, HeldOffset, HeldBytes, count, HeldPursuitCapacity, "held pursuit", ReadHeldPursuit));
    }

    /** Reads the answer to one unlockValues request: one value per requested source, in request order. */
    public static LiveUnlockSnapshot ReadUnlockValues(LiveInvestmentReply reply, IReadOnlyList<LiveUnlockSource> sources)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.UnlockValues, UnlockValuesBytes);
        int count = Count(U16(bytes, UnlockCountOffset), UnlockReadCapacity, "unlock value");
        if (count != sources.Count) throw new InvalidDataException("The unlock values answer another number of sources.");
        Zero(bytes[UnlockReservedOffset..UnlockRevisionOffset], "unlock values");
        var values = Rows(bytes, UnlockValuesOffset, UnlockValueBytes, count, UnlockReadCapacity, "unlock value", (row, index) =>
        {
            Zero(row[UnlockStateReservedOffset..UnlockValueOffset], "unlock value");
            byte state = row[UnlockStateOffset];
            int value = I32(row, UnlockValueOffset);
            var source = sources[index];
            if (state > UnlockKnown || (state == UnlockKnown && source.Kind == LiveUnlockKind.Flag && value is not (0 or 1)))
                throw new InvalidDataException("An unlock value has an unknown state.");
            return new LiveUnlockValue(source, state == UnlockKnown, state == UnlockInvalid ? 0 : value);
        });
        return new(reply.Context, U64(bytes, UnlockRevisionOffset), values);
    }

    /** Reads the receipt that answers a mutation or a receipt request, including a refused one. */
    public static LiveInvestmentReceipt ReadReceipt(LiveInvestmentReply reply)
    {
        if ((!IsMutation(reply.Opcode) && !IsReceiptRequest(reply.Opcode))
            || reply.Status is not (LiveInvestmentStatus.Ok or LiveInvestmentStatus.Refused) || reply.Payload.Length != ReceiptBytes)
            throw new InvalidDataException("The investment reply holds no receipt.");
        ReadOnlySpan<byte> bytes = reply.Payload;
        Zero(bytes.Slice(ReceiptReservedOffset, sizeof(uint)), "investment receipt");
        uint opcode = U32(bytes, ReceiptOpcodeOffset), state = U32(bytes, ReceiptStateOffset), wait = U32(bytes, ReceiptWaitOffset);
        if (U64(bytes, ReceiptOperationOffset) != reply.OperationId)
            throw new InvalidDataException("The investment receipt names another operation.");
        if (state > (uint)LiveReceiptState.CommittedUnpublished || wait > (uint)LiveInvestmentWait.Catalog)
            throw new InvalidDataException("The investment receipt has an unknown state.");
        var receipt = new LiveInvestmentReceipt(reply.OperationId, (LiveInvestmentOpcode)opcode, (LiveReceiptState)state,
            reply.Reason, (LiveInvestmentWait)wait, I32(bytes, ReceiptVersionOffset), U32(bytes, ReceiptChangedRowsOffset),
            U64(bytes, ReceiptSubmittedOffset), U64(bytes, ReceiptFinishedOffset), reply.Context);
        if (receipt.State == LiveReceiptState.Unknown)
        {
            Zero(bytes[ReceiptOpcodeOffset..], "unknown investment receipt");
            if (reply.Context != default) throw new InvalidDataException("An unknown investment receipt names a context.");
            return receipt;
        }
        if (!IsMutation(receipt.Opcode) || !receipt.Context.Complete || receipt.SubmittedTick == 0)
            throw new InvalidDataException("The investment receipt does not name its mutation.");
        if (receipt.Terminal != (receipt.FinishedTick != 0) || (receipt.Terminal && receipt.FinishedTick < receipt.SubmittedTick))
            throw new InvalidDataException("The investment receipt's finish time disagrees with its state.");
        // A queued operation may already name what it will wait for; a deferred one always does.
        if (receipt.State != LiveReceiptState.Queued && (receipt.State == LiveReceiptState.Deferred) != (receipt.Wait != LiveInvestmentWait.None))
            throw new InvalidDataException("The investment receipt's wait disagrees with its state.");
        if (receipt.State == LiveReceiptState.Refused && receipt.Reason.Length == 0)
            throw new InvalidDataException("A refused investment receipt has no reason.");
        return receipt;
    }

    /** Each catalog the game reads, in the order Studio names them. */
    private static readonly (LiveInvestmentReadiness Flag, string Name)[] Catalogs =
    [
        (LiveInvestmentReadiness.Items, "Items"), (LiveInvestmentReadiness.ItemDetails, "Item details"),
        (LiveInvestmentReadiness.Buckets, "Buckets"), (LiveInvestmentReadiness.Progressions, "Progressions"),
        (LiveInvestmentReadiness.SeasonPass, "Season pass"), (LiveInvestmentReadiness.Rewards, "Rewards"),
        (LiveInvestmentReadiness.Objectives, "Objectives"), (LiveInvestmentReadiness.Pursuits, "Pursuits")
    ];

    /** Names of the catalogs the game has not finished reading, limited to needs when it is given. */
    public static IEnumerable<string> Unread(LiveInvestmentReadiness readiness, LiveInvestmentReadiness? needs = null) =>
        Catalogs.Where(part => (needs is not { } only || only.HasFlag(part.Flag)) && !readiness.HasFlag(part.Flag)).Select(part => part.Name);

    /** User-facing text for a reason label the game sent. A label Studio does not know is returned unchanged. */
    public static string Describe(string reason) => reason switch
    {
        "" => "The game did not give a reason.",
        "account_changed" or "character_changed" => "The character changed since you started this.",
        "catalog_changed" or "item_unavailable" => "The game does not have this item. Build and install, then restart.",
        // Also given for a held row whose bucket the game has no descriptor for.
        "item_definition" => "The game does not have this item or its bucket. Build and install, then restart.",
        "not_ready" => "The game is still reading its installed data.",
        "selected_character" => "Select a character in the game first.",
        "account_state" => "The game could not use its saved account.",
        "request_field" => "Studio sent a request the game could not read.",
        "pursuits_unsupported" => "The game cannot read pursuit objectives from its installed data.",
        "rewards_unsupported" => "The game cannot read rewards from its installed data.",
        "publication_peer" => "Sign in to the game with this account first.",
        "operation_reused" => "The game already holds a different operation with this id.",
        "receipt_pending" => "The operation has not finished.",
        "withdrawn" => "Withdrawn before the game ran it.",
        "editor_disconnected" => "Studio disconnected before the game ran it.",
        "allocation" or "reward_allocation" => "The game ran out of memory for this request.",
        "transaction" or "inventory_commit" or "reward_preflight" or "random_source" => "The game could not save the change.",
        "inventory_stage" or "inventory_encode" => "The game could not build the update for its client.",
        "inventory_row" or "experience_changed" or "reward_hash" or "reward_index" or "pursuit_changed" or "objective_changed"
            or "value_changed" => "It changed in the game. Try again.",
        "item_route" => "The game cannot place this kind of item.",
        "inventory_scope" => "The game keeps this item in another inventory.",
        "item_detail" or "item_identity" or "socket_owner" or "quest_state" => "The game could not resolve this item.",
        "reward_placement" or "inventory_destination" or "reward_count" or "loadout" => "The game could not place this item.",
        "acquisition_flag" or "perk_acquisition" or "wrapper_acquisition_flag" or "wrapper_flag_storage" =>
            "The game could not record this item as acquired.",
        "claim_rejected" => "The game's claim rules refused it.",
        "equipped" => "Equipped; unequip before editing.",
        // A grant is also bounded by the slots of the item's bucket and its recovery bucket, and an account perk by 1.
        "quantity" => "The quantity is out of range, or more than the account can hold of this item at once.",
        "unsupported_removal" => "The game cannot remove this item.",
        "experience_range" => "XP cannot be negative.",
        "experience_spent" => "XP cannot go below what the artifact has already spent.",
        "artifact_unsupported" => "Lowering XP needs the artifact's sale rows, which the game cannot read from its installed data.",
        "rank" => "The season rank is too low.",
        "already_claimed" => "Already claimed.",
        "not_claimed" => "Not claimed.",
        "automatic_reward" => "The game grants this reward by itself.",
        "reward_condition" => "Not available to this character.",
        "profile_capacity" or "instance_capacity" or "character_stack_capacity" or "stack_capacity" => "No room in the inventory.",
        "no_room" => "Its bucket is full and it has nowhere to overflow, so the game would drop it.",
        "item_progress_not_saved" => "This server does not save per-item progress.",
        "expiry_not_saved" => "This server does not save when a bounty expires.",
        "pursuit_contract" => "The game cannot read this pursuit's objectives.",
        "objective_source" => "This objective's progress is not a saved value.",
        "objective_sources" => "This objective combines several sources.",
        "objective_value" => "A flag is set or clear.",
        "flag_state" => "The game holds an unexpected value for this flag.",
        "value_unchanged" => "It already has that value.",
        "objective_target" => "No saved value completes every objective.",
        "objectives_complete" => "Every objective is already complete.",
        _ => reason
    };

    private static LiveBucket ReadBucket(ReadOnlySpan<byte> row, int index)
    {
        sbyte slot = (sbyte)row[BucketEquipmentSlotOffset];
        if (row[BucketIdOffset] == NoBucket || row[BucketArrayOffset] > LastArraySelector
            || slot < NoEquipmentSlot)
            throw new InvalidDataException("An inventory bucket record is invalid.");
        Zero(row[BucketReservedOffset..], "inventory bucket");
        return new(row[BucketIdOffset], row[BucketArrayOffset], U16(row, BucketFirstSlotOffset), U16(row, BucketSlotCountOffset), slot);
    }

    private static LiveProfileRow ReadProfileRow(ReadOnlySpan<byte> row, int index)
    {
        byte flags = (byte)Bits(row[ProfileFlagsOffset], ItemSeen, "profile row flags");
        Zero(row[ProfileReservedOffset..], "profile row");
        return new(U64(row, ProfileInstanceOffset), U32(row, ProfileHashOffset), I32(row, ProfileQuantityOffset),
            I32(row, ProfileSerialOffset), row[ProfileBucketOffset], flags != 0);
    }

    private static LiveCharacterItem ReadItem(ReadOnlySpan<byte> row, int index)
    {
        byte slot = row[ItemEquipmentSlotOffset], plugCount = row[ItemPlugCountOffset];
        ushort present = U16(row, ItemPlugPresentOffset);
        if (U64(row, ItemInstanceOffset) == 0 || (slot >= EquipmentSlotCount && slot != Unequipped)
            || row[ItemSocketPolicyOffset] > (byte)LiveSocketPolicy.Authored || plugCount > PlugCapacity || present >> plugCount != 0)
            throw new InvalidDataException("A character item record is invalid.");
        byte flags = (byte)Bits(row[ItemRecordFlagsOffset], ItemSeen, "character item flags");
        Zero(row.Slice(ItemReservedOffset, 1), "character item");
        Zero(row[ItemTailOffset..], "character item");
        var plugs = new uint?[plugCount];
        for (int lane = 0; lane < PlugCapacity; lane++)
        {
            uint plug = U32(row, ItemPlugsOffset + lane * sizeof(uint));
            if (lane < plugCount && (present & 1 << lane) != 0) plugs[lane] = plug;
            else if (plug != 0) throw new InvalidDataException("A character item has a value in an empty plug lane.");
        }
        return new(U64(row, ItemInstanceOffset), U32(row, ItemHashOffset), I32(row, ItemQuantityOffset), I32(row, ItemSerialOffset),
            I32(row, ItemLevelOffset), U32(row, ItemFlagsOffset), row[ItemBucketOffset], slot,
            (LiveSocketPolicy)row[ItemSocketPolicyOffset], plugs, flags != 0);
    }

    private static LiveCharacterStack ReadStack(ReadOnlySpan<byte> row, int index)
    {
        Zero(row[StackReservedOffset..], "character stack");
        return new(U32(row, StackHashOffset), I32(row, StackQuantityOffset), I32(row, StackSerialOffset), row[StackBucketOffset]);
    }

    private static LiveHeldPursuit ReadHeldPursuit(ReadOnlySpan<byte> row, int index)
    {
        int objectiveCount = Count(row[HeldObjectiveCountOffset], PursuitObjectiveCapacity, "pursuit objective");
        ushort reason = U16(row, HeldReasonOffset);
        if (U64(row, HeldInstanceOffset) == 0 || reason > (ushort)LivePursuitReason.ExternalValue)
            throw new InvalidDataException("A held pursuit record is invalid.");
        Zero(row.Slice(HeldReservedOffset, 1), "held pursuit");
        Zero(row[HeldTailOffset..HeldObjectivesOffset], "held pursuit");
        var objectives = Rows(row, HeldObjectivesOffset, ObjectiveBytes, objectiveCount, PursuitObjectiveCapacity, "pursuit objective",
            (objective, _) =>
            {
                var flags = (LivePursuitObjectiveFlags)Bits(objective[ObjectiveFlagsOffset], ObjectiveBits, "pursuit objective state");
                Zero(objective[ObjectiveReservedOffset..], "pursuit objective");
                return new LivePursuitObjective(U32(objective, ObjectiveHashOffset), I32(objective, ObjectiveValueOffset),
                    I32(objective, ObjectiveTargetOffset), flags);
            });
        return new(U64(row, HeldInstanceOffset), U32(row, HeldHashOffset), I32(row, HeldSerialOffset), I32(row, HeldQuantityOffset),
            U32(row, HeldItemFlagsOffset), (LivePursuitFlags)Bits(U16(row, HeldFlagsOffset), PursuitBits, "held pursuit state"),
            (LivePursuitReason)reason, objectives);
    }

    private delegate T RowReader<out T>(ReadOnlySpan<byte> row, int index);

    /** Decodes the counted prefix of a fixed array and requires every later slot to be zero. */
    private static T[] Rows<T>(ReadOnlySpan<byte> bytes, int offset, int stride, int count, int capacity, string what,
        RowReader<T> read)
    {
        var rows = new T[count];
        for (int index = 0; index < count; index++) rows[index] = read(bytes.Slice(offset + index * stride, stride), index);
        Zero(bytes.Slice(offset + count * stride, (capacity - count) * stride), what);
        return rows;
    }

    private static ReadOnlySpan<byte> Payload(LiveInvestmentReply reply, LiveInvestmentOpcode opcode, int size)
    {
        if (reply.Opcode != opcode || reply.Status != LiveInvestmentStatus.Ok || reply.Payload.Length != size)
            throw new InvalidDataException($"The investment reply holds no {opcode} payload.");
        return reply.Payload;
    }

    private static int Count(int count, int capacity, string what) =>
        count <= capacity ? count : throw new InvalidDataException($"The {what} count exceeds its capacity.");

    private static uint Bits(uint value, uint defined, string what) =>
        (value & ~defined) == 0 ? value : throw new InvalidDataException($"The {what} has unknown bits.");

    private static void Zero(ReadOnlySpan<byte> bytes, string what)
    {
        if (bytes.IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException($"The {what} has nonzero padding.");
    }

    /** The game zeroes the field before it copies a label, so every byte after the terminator is zero. */
    private static string ReadReason(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("The investment reason is not terminated.");
        var text = bytes[..end];
        if (text.IndexOfAnyExceptInRange((byte)' ', (byte)'~') >= 0) throw new InvalidDataException("The investment reason is not ASCII.");
        Zero(bytes[end..], "investment reason");
        return Encoding.ASCII.GetString(text);
    }

    private static LiveInvestmentContext ReadContext(ReadOnlySpan<byte> bytes) =>
        new(U64(bytes, 0), U64(bytes, sizeof(ulong)), U64(bytes, 2 * sizeof(ulong)));

    private static void PutContext(Span<byte> bytes, LiveInvestmentContext context)
    {
        PutU64(bytes, 0, context.AccountSoid);
        PutU64(bytes, sizeof(ulong), context.CharacterSoid);
        PutU64(bytes, 2 * sizeof(ulong), context.ContentHash);
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static int I32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void PutU16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);
    private static void PutU32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void PutI32(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void PutU64(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
}
