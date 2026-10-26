using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public enum InvestmentSlotState { Loaded, Omitted, Missing, WrongClass }
/** One root slot's table. Bytes is empty unless the table loaded. */
public sealed record InvestmentSlotRead(int Slot, uint Tag, InvestmentSlotState State, ReadOnlyMemory<byte> Bytes, string Diagnostic)
{
    public bool Loaded => State == InvestmentSlotState.Loaded;
}

/** The root slots Studio reads, each with the Sunrise constant that names the same slot. */
public static class InvestmentRootSlots
{
    /** Investment constants blob (definition_index_table.h kInvestmentConstantsSlot). */
    public const int Constants = 11;
    /** Character classes. Each row names its class's default finisher (package_reward_build.cpp kClassSlot). */
    public const int Classes = 12;
    /** Inventory bucket descriptors (definition_index_table.h kBucketTableSlot). Sunrise reads it without a class check. */
    public const int Buckets = 17;
    /** The item index that pool, pass and preview item numbers refer to (kItemTableSlot). */
    public const int Items = 48;
    /** Objective definitions (kObjectiveTableSlot). */
    public const int Objectives = 58;
    /** Progression definitions, including the season pass ladders (kProgressionTableSlot). */
    public const int Progressions = 68;
    /**
     * The table pool entries and pursuit completions refer to (package_reward_build.cpp kSupplementalRewardSlot,
     * package_pursuit_build.cpp kCompletionOperationTableSlot). The installed build omits it, so its meaning is unconfirmed.
     */
    public const int Slot84 = 84;
    /** Reward pools (package_reward_build.cpp kPoolSlot). */
    public const int Pools = 88;
    /** Shared unlock expressions that opcode 12 inlines (kExpressionSlot). */
    public const int SharedExpressions = 109;
    /** Unlock flag and value mapping tables, and the slot tables keyed by unlock slot (kUnlock*TableSlot, kValueSlot). */
    public const int FlagMaps = 111, FlagSlots = 112, ValueMaps = 113, ValueSlots = 114;
}

/** The one installed investment root. Its table slots follow an 8-byte prefix and are 16 bytes each, tag first. */
public sealed class InvestmentRoot
{
    public const uint RootClass = 0x80807D84;
    /** A slot holding all ones names a table the installed build omits. */
    public const uint OmittedSlot = 0xFFFFFFFF;
    private const int SlotTable = 8, SlotStride = 16;
    private readonly ReadOnlyMemory<byte> bytes;
    public uint Tag { get; }
    public int SlotCount { get; }

    private InvestmentRoot(uint tag, ReadOnlyMemory<byte> bytes)
    { Tag = tag; this.bytes = bytes; SlotCount = Math.Max(0, (bytes.Length - SlotTable) / SlotStride); }

    /**
     * Finds the root as the one installed entry of the root class. It never picks between two.
     * @param explicitTag A root tag to use as-is, or 0 to search the installation.
     * @return The root, or null with a diagnostic when no single installed entry carries the root class.
     */
    public static InvestmentRoot? Locate(PackageCatalog catalog, uint explicitTag, out string diagnostic)
    {
        diagnostic = "";
        uint tag = explicitTag;
        if (tag == 0)
        {
            var roots = catalog.Entries.Where(entry => entry.Type is 8 or 16 && entry.Reference == RootClass).Take(2).ToArray();
            if (roots.Length != 1)
            {
                diagnostic = roots.Length == 0 ? "This installation has no investment root." : "The investment root is ambiguous.";
                return null;
            }
            tag = roots[0].TagHash;
        }
        else if (!catalog.ContainsTag(tag))
        {
            diagnostic = "The investment root is not installed.";
            return null;
        }
        return new(tag, catalog.Read(tag));
    }

    public uint SlotTag(int slot)
    {
        if (slot < 0 || slot >= SlotCount) throw new InvalidDataException($"The investment root has no slot {slot}.");
        return new TagReader(bytes).U32(SlotTable + slot * SlotStride);
    }

    /** Reads a slot's table and checks its entry class. Omitted reports the all-ones sentinel. */
    public InvestmentSlotRead ReadSlot(PackageCatalog catalog, int slot, uint tableClass)
    {
        uint tag = SlotTag(slot);
        if (tag == OmittedSlot) return new(slot, tag, InvestmentSlotState.Omitted, default, $"Slot {slot} is omitted by this installation.");
        if (!catalog.TryGetEntry(tag, out var entry)) return new(slot, tag, InvestmentSlotState.Missing, default, $"Slot {slot} names a table that is not installed.");
        if (entry.Type is not (8 or 16) || entry.Reference != tableClass)
            return new(slot, tag, InvestmentSlotState.WrongClass, default, $"Slot {slot} has an unexpected class.");
        return new(slot, tag, InvestmentSlotState.Loaded, catalog.Read(tag), "");
    }

    /** Reads a slot's table whatever its class, as Sunrise reads the bucket table. Omitted and Missing work as in ReadSlot. */
    public InvestmentSlotRead ReadSlotAnyClass(PackageCatalog catalog, int slot)
    {
        uint tag = SlotTag(slot);
        if (tag == OmittedSlot) return new(slot, tag, InvestmentSlotState.Omitted, default, $"Slot {slot} is omitted by this installation.");
        if (!catalog.ContainsTag(tag)) return new(slot, tag, InvestmentSlotState.Missing, default, $"Slot {slot} names a table that is not installed.");
        return new(slot, tag, InvestmentSlotState.Loaded, catalog.Read(tag), "");
    }
}
