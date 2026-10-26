namespace SunriseStudio.Formats;

/** One inventory bucket descriptor of root slot 17. Raw is the whole 36-byte descriptor. */
public sealed record InvestmentBucket(int Index, byte Id, int FirstSlot, int SlotCount, byte ArraySelector,
    byte Fifo, byte NoTransferOnEviction, ReadOnlyMemory<byte> Raw, string Problem = "")
{
    public int LastSlot => FirstSlot + SlotCount - 1;
}

/** Inventory bucket descriptors (root slot 17), read as package_root_tables.cpp build_buckets reads them. */
public static class InvestmentBuckets
{
    /** Table layout (definition_index_table.h kBucketCountOffset, kBucketFirstDescriptor, kBucketDescriptorSize, field offsets). */
    internal const int CountOffset = 140, FirstDescriptor = 144, DescriptorSize = 36, FirstSlot = 4, SlotCount = 8,
        ArraySelector = 24;
    /**
     * FIFO and no-transfer-on-eviction bytes (definition_index_table.h kBucketFifoOffset, kBucketNoTransferOnEvictionOffset).
     * A value above 1 in either refuses the whole table (package_root_tables.cpp build_buckets).
     */
    internal const int FifoByte = 25, NoTransferByte = 26;
    /** Sunrise accepts 1 to 255 descriptors (buckets/definition.h kDescriptorCapacity). */
    public const int Capacity = byte.MaxValue;
    /** Sunrise's buckets::valid refuses id 255, which it keeps for "no bucket". */
    internal const byte NoBucket = byte.MaxValue;
    /**
     * The highest array selector Sunrise knows: character, profile, small profile (buckets/definition.h ArraySelector). Any other
     * refuses the whole table (inventory_bucket_catalog.cpp range_fits).
     */
    public const byte LastArraySelector = 2;
    private const string Refused = "Sunrise refuses the whole bucket table: ";

    /** @param tableProblems Receives one line per problem for which Sunrise refuses the whole table. */
    internal static IReadOnlyList<InvestmentBucket> Read(InvestmentSlotRead slot, List<string> tableProblems, CancellationToken token)
    {
        if (!slot.Loaded) throw new InvalidDataException(slot.Diagnostic);
        var table = new TagReader(slot.Bytes);
        if (!table.Has(CountOffset, sizeof(int))) throw new InvalidDataException("the bucket table is too short to hold its descriptor count.");
        int count = table.I32(CountOffset);
        if (count is < 1 or > Capacity || !table.Has(FirstDescriptor, count * DescriptorSize))
            throw new InvalidDataException($"the bucket table declares {count} descriptors.");
        var buckets = new InvestmentBucket[count];
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            int at = FirstDescriptor + index * DescriptorSize;
            int first = table.I32(at + FirstSlot), slots = table.I32(at + SlotCount);
            byte selector = table.U8(at + ArraySelector);
            var problems = new List<string>();
            // Sunrise casts both to 16 bits without a range check.
            if (first is < 0 or > ushort.MaxValue || slots is < 0 or > ushort.MaxValue) problems.Add("Sunrise truncates this range to 16 bits.");
            buckets[index] = new(index, table.U8(at), first, slots, selector, table.U8(at + FifoByte), table.U8(at + NoTransferByte),
                slot.Bytes.Slice(at, DescriptorSize), string.Join(" ", problems));
        }
        foreach (var bucket in buckets.Where(bucket => bucket.ArraySelector > LastArraySelector))
            tableProblems.Add(Refused + $"bucket {bucket.Id}'s array selector {bucket.ArraySelector} is not one Sunrise knows.");
        foreach (var bucket in buckets.Where(bucket => bucket.Fifo > 1))
            tableProblems.Add(Refused + $"bucket {bucket.Id}'s FIFO byte is {bucket.Fifo}, not 0 or 1.");
        foreach (var bucket in buckets.Where(bucket => bucket.NoTransferOnEviction > 1))
            tableProblems.Add(Refused + $"bucket {bucket.Id}'s no-transfer byte is {bucket.NoTransferOnEviction}, not 0 or 1.");
        if (buckets.Any(bucket => bucket.Id == NoBucket)) tableProblems.Add(Refused + $"bucket {NoBucket} is reserved for no bucket.");
        foreach (var repeated in buckets.Where(bucket => bucket.Id != NoBucket).GroupBy(bucket => bucket.Id).Where(group => group.Count() > 1))
            tableProblems.Add(Refused + $"bucket {repeated.Key} has more than one descriptor.");
        return buckets;
    }
}
