using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

public enum InvestmentCategory { Weapons, Armor, Items, Currencies }
/** The installed tables Studio reads. Root and ObjectiveDisplays of 0 are found by their entry class. */
public sealed record InvestmentSources(uint Items = InvestmentTags.ItemIndex, uint Displays = InvestmentTags.ItemDisplayIndex,
    uint StringBanks = 0x81A27211, uint StatNames = 0x81613D07, uint Vendors = InvestmentTags.VendorIndex,
    uint VendorDisplays = InvestmentTags.VendorDisplayIndex, uint PlugSets = 0x81319349, uint Icons = 0x81A291C2,
    uint Perks = 0x81613D28, uint StatGroups = 0x81613D08, uint Collectibles = 0x81327CDE,
    uint CollectibleDisplays = 0x81327D67, uint PresentationNodes = 0x8131933F, uint PresentationNodeDisplays = 0x81613CF8,
    uint ItemArt = InvestmentItemArt.TableTag, uint Root = 0, uint ObjectiveDisplays = 0);
/** Named is false when Name falls back to the package name or "Item {index}" because the display strings give none. */
public sealed record InvestmentItem(int Index, uint Hash, uint Tag, uint DisplayTag, string Name,
    string TypeName, string Description, byte Bucket, byte Tier, int MaximumStack, bool Instanced, bool Named)
{
    public InvestmentCategory Category => Bucket <= 2 ? InvestmentCategory.Weapons : Bucket is >= 3 and <= 7
        ? InvestmentCategory.Armor : InvestmentItemKinds.IsCurrency(Bucket, TypeName)
            ? InvestmentCategory.Currencies : InvestmentCategory.Items;
    public string Rarity => InvestmentItemKinds.TierName(Tier);
}
public sealed record InvestmentVendor(int Index, uint Hash, uint Tag, uint DisplayTag, string Name,
    string LabelSource = "Package name or index");
public sealed record InvestmentStat(byte Index, string Name, int Value, AssetProperty Field,
    bool Conditional = false, bool Computed = false);
public sealed record InvestmentStatDefinition(byte Index, string Name);
public sealed record InvestmentSocket(int Index, ushort Type, ushort InitialPlug, string PlugName,
    AssetProperty TypeField, AssetProperty PlugField, IReadOnlyList<ushort> AllowedPlugs);
public sealed record InvestmentPerk(ushort Index, string Name, AssetProperty Field, bool Conditional = false);
public sealed record InvestmentPerkDefinition(int Index, uint Hash, string Name, string Description, ushort IconIndex);
public sealed record InvestmentItemDetail(InvestmentItem Item, IReadOnlyList<InvestmentStat> Stats,
    IReadOnlyList<InvestmentSocket> Sockets, IReadOnlyList<InvestmentPerk> Perks,
    IReadOnlyList<AssetProperty> Fields, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyDictionary<byte, InvestmentStatCurve> DisplayCurves { get; init; } = new Dictionary<byte, InvestmentStatCurve>();
    public InvestmentRewardWrapper? Wrapper { get; init; }
    public InvestmentPursuit? Pursuit { get; init; }
}
public sealed record InvestmentCost(ushort ItemIndex, string Name, uint Quantity, AssetProperty ItemField,
    AssetProperty QuantityField, bool Conditional);
public sealed record InvestmentSale(int Index, ushort ItemIndex, string Name, int Category,
    AssetProperty ItemField, AssetProperty CategoryField, IReadOnlyList<InvestmentCost> Costs)
{
    public uint OwnerTag { get; init; }
    public string SourceSha256 { get; init; } = "";
}
public sealed record InvestmentVendorDetail(InvestmentVendor Vendor, IReadOnlyList<InvestmentSale> Sales,
    uint ResetIntervalRaw, uint ResetPhaseRaw, IReadOnlyList<string> Diagnostics);
/** Which vendors sell each item: vendor indices in vendor order, read from every vendor's sale rows. */
public sealed class InvestmentVendorSales(IReadOnlyDictionary<int, int[]> sellers, int rows, IReadOnlyList<string> problems)
{
    /** Sale rows read, counting rows that name no item. */
    public int Rows { get; } = rows;
    /** One line per vendor whose sale rows could not be read. */
    public IReadOnlyList<string> Problems { get; } = problems;
    /** Vendor indices whose sale rows name the item. */
    public IReadOnlyList<int> Selling(int item) => sellers.GetValueOrDefault(item) ?? [];
}

/** Item identities join only when the definition and display tables agree on their hash. */
public sealed partial class InvestmentCatalog
{
    public const uint ItemIndexTag = InvestmentTags.ItemIndex, DisplayIndexTag = InvestmentTags.ItemDisplayIndex;
    public const uint VendorIndexTag = InvestmentTags.VendorIndex, VendorDisplayIndexTag = InvestmentTags.VendorDisplayIndex;
    public const uint ItemClass = InvestmentTags.ItemClass, DisplayClass = InvestmentTags.ItemDisplayClass,
        VendorClass = InvestmentTags.VendorClass;
    /** Item definition fixed fields at +180: the maximum stack (i32), then the bucket, tier and instanced bytes. */
    internal const int FixedFields = 180, FixedFieldsSize = 8, BucketField = 184, TierField = 186, InstancedField = 187;
    private readonly PackageCatalog catalog;
    private readonly LocalizedStrings strings;
    private readonly uint[] banks;
    private readonly Dictionary<uint, InvestmentItem> byTag;
    private readonly Dictionary<uint, InvestmentItem> byHash;
    private readonly Dictionary<uint, InvestmentVendor> vendorsByTag;
    private readonly Dictionary<byte, string> statNames;
    private readonly InvestmentSources sources;
    private readonly InvestmentItemLinks[] links;
    public IReadOnlyList<InvestmentItem> Items { get; }
    public IReadOnlyList<InvestmentVendor> Vendors { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<InvestmentStatDefinition> StatDefinitions { get; }
    public IReadOnlyList<InvestmentPerkDefinition> PerkDefinitions { get; }
    public int PerkCount => PerkDefinitions.Count;

    private InvestmentCatalog(PackageCatalog catalog, LocalizedStrings strings, uint[] banks,
        InvestmentItem[] items, InvestmentVendor[] vendors, Dictionary<byte, string> statNames, string[] diagnostics,
        InvestmentSources sources, InvestmentPerkDefinition[] perks, InvestmentItemLinks[] links)
    {
        this.catalog = catalog; this.strings = strings; this.banks = banks;
        this.sources = sources; this.links = links;
        Items = items; Vendors = vendors; this.statNames = statNames; Diagnostics = diagnostics;
        StatDefinitions = statNames.Select(pair => new InvestmentStatDefinition(pair.Key, pair.Value))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Index).ToArray();
        PerkDefinitions = perks;
        byTag = new(); byHash = new(); vendorsByTag = new();
        foreach (var item in items)
        {
            byTag.TryAdd(item.Tag, item); byHash.TryAdd(item.Hash, item);
            if (item.DisplayTag != 0) byTag.TryAdd(item.DisplayTag, item);
        }
        foreach (var vendor in vendors) { vendorsByTag.TryAdd(vendor.Tag, vendor); if (vendor.DisplayTag != 0) vendorsByTag.TryAdd(vendor.DisplayTag, vendor); }
    }

    public InvestmentItem? FindItem(uint tag) => byTag.GetValueOrDefault(tag);
    /** The item with this definition hash. When index rows share a hash, the first one wins. */
    public InvestmentItem? FindByHash(uint hash) => byHash.GetValueOrDefault(hash);
    public InvestmentVendor? FindVendor(uint tag) => vendorsByTag.GetValueOrDefault(tag);
    public string ItemName(int index) => index == ushort.MaxValue ? "None" : index >= 0 && index < Items.Count
        ? Items[index].Name : $"Item {index}";
    public string StatName(byte index) => statNames.GetValueOrDefault(index, $"Stat {index}");
    /** Resolves a {bank index, string hash} pair through the bank index. */
    public string Text(uint bank, uint hash) => bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
    /** Collections pages that list collectibles, read from the current package bytes. */
    public IReadOnlyList<InvestmentCollectionNode> CollectionNodes() => InvestmentCollections.ReadNodes(
        catalog.Read(sources.PresentationNodes), catalog.Read(sources.PresentationNodeDisplays), Text);
    /** Unnamed perks keep their index so two of them never share a label. */
    public string PerkName(int index) => index >= 0 && index < PerkDefinitions.Count && PerkDefinitions[index].Name.Length != 0
        ? PerkDefinitions[index].Name : $"Sandbox perk {index}";

    /** Package reads run on the caller's worker and never write extracted content to the project. */
    public static InvestmentCatalog Load(PackageCatalog catalog, LocalizedStrings? strings = null,
        CancellationToken token = default, Action<string>? progress = null, InvestmentSources? sources = null)
    {
        token.ThrowIfCancellationRequested();
        sources ??= new();
        var diagnostics = new List<string>();
        uint[] banks = [];
        if (catalog.ContainsTag(sources.StringBanks))
        {
            var data = new TagReader(catalog.Read(sources.StringBanks));
            var array = data.Array(8, stride: 8);
            banks = Enumerable.Range(0, array.Count).Select(index => data.U32(array.Data + index * 8 + 4)).ToArray();
        }
        strings ??= LocalizedStrings.LoadBanks(catalog, banks, token);
        diagnostics.AddRange(strings.Diagnostics);
        string Text(TagReader data, int offset)
        {
            if (!data.Has(offset, 8)) return "";
            uint bank = data.U32(offset), hash = data.U32(offset + 4);
            return bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
        }
        var definitions = ReadIndex(catalog, sources.Items);
        var displays = ReadIndex(catalog, sources.Displays);
        var items = new InvestmentItem[definitions.Length];
        var links = new InvestmentItemLinks[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var definition = definitions[i];
            uint displayTag = i < displays.Length && displays[i].Hash == definition.Hash ? displays[i].Tag : 0;
            string name = "", type = "", description = ""; byte bucket = 255, tier = 255; int maximum = 0; bool instanced = false;
            links[i] = InvestmentItemLinks.None;
            try
            {
                RequireClass(catalog, definition.Tag, ItemClass);
                var data = new TagReader(catalog.Read(definition.Tag));
                links[i] = InvestmentItemLinks.Read(data);
                if (!data.Has(FixedFields, FixedFieldsSize)) throw new InvalidDataException("Item fixed fields are truncated.");
                maximum = data.I32(FixedFields); bucket = ReadBucket(data); tier = data.U8(TierField); instanced = data.U8(InstancedField) != 0;
                if (displayTag != 0)
                {
                    RequireClass(catalog, displayTag, DisplayClass);
                    var display = new TagReader(catalog.Read(displayTag));
                    name = Text(display, 132); type = Text(display, 144); description = Text(display, 152);
                }
            }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add($"Item {i}: {error.Message}"); }
            bool named = name.Length != 0;
            if (!named) name = catalog.GetName(definition.Tag) ?? $"Item {i}";
            items[i] = new(i, definition.Hash, definition.Tag, displayTag, name, type, description, bucket, tier, maximum, instanced, named);
            if ((i & 255) == 0) progress?.Invoke($"Reading items {i + 1}/{definitions.Length}");
        }
        var stats = new Dictionary<byte, string>();
        if (catalog.ContainsTag(sources.StatNames))
            try
            {
                var data = new TagReader(catalog.Read(sources.StatNames)); var array = data.Array(8, stride: 36);
                for (int i = 0; i < array.Count && i < 256; i++)
                { string name = Text(data, array.Data + i * 36 + 4); stats[(byte)i] = name.Length != 0 ? name : $"Stat {i}"; }
            }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add("Stat names: " + error.Message); }
        var vendorRows = ReadIndex(catalog, sources.Vendors, optional: true);
        var vendorDisplays = ReadIndex(catalog, sources.VendorDisplays, optional: true);
        var vendors = new InvestmentVendor[vendorRows.Length];
        for (int i = 0; i < vendors.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            uint display = i < vendorDisplays.Length && vendorDisplays[i].Hash == vendorRows[i].Hash ? vendorDisplays[i].Tag : 0;
            string name = catalog.GetName(vendorRows[i].Tag) ?? $"Vendor {i}";
            string labelSource = "Package name or index";
            if (display != 0)
                try
                {
                    RequireClass(catalog, display, VendorDisplayClass);
                    var data = new TagReader(catalog.Read(display));
                    if (!data.Has(64, 8)) throw new InvalidDataException("Vendor display name is truncated.");
                    ushort bank = data.U16(64);
                    string text = bank <= short.MaxValue && bank < banks.Length
                        ? strings.Get(banks[bank], data.U32(68))?.Trim() ?? "" : "";
                    if (text.Length != 0)
                    { name = text.Split('\n')[0]; labelSource = "Authored vendor display name"; }
                }
                catch (Exception error) when (Recoverable(error)) { diagnostics.Add($"Vendor label {i}: {error.Message}"); }
            vendors[i] = new(i, vendorRows[i].Hash, vendorRows[i].Tag, display, name, labelSource);
        }
        InvestmentPerkDefinition[] perks = [];
        if (catalog.ContainsTag(sources.Perks))
            try
            {
                RequireClass(catalog, sources.Perks, SandboxPerkDisplays.TableClass);
                string Pair(uint bank, uint hash) => bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
                perks = SandboxPerkDisplays.Read(catalog, sources.Perks).Select(row => new InvestmentPerkDefinition(
                    row.Index, row.Hash, Pair(row.NameBank, row.NameHash), Pair(row.DescriptionBank, row.DescriptionHash), row.IconIndex)).ToArray();
                if (perks.Length > ushort.MaxValue) throw new InvalidDataException("Sandbox perk index exceeds its native range.");
            }
            catch (Exception error) when (Recoverable(error)) { perks = []; diagnostics.Add("Sandbox perks: " + error.Message); }
        return new(catalog, strings, banks, items, vendors, stats, diagnostics.ToArray(), sources, perks, links);
    }

    private InvestmentVendorSales? vendorSales;
    public bool VendorSalesLoaded => Volatile.Read(ref vendorSales) is not null;

    /**
     * Every vendor's sale rows indexed by item, read once per catalog. The first call reads every vendor on the caller's
     * worker, and a cancelled read caches nothing.
     */
    public InvestmentVendorSales VendorSales(CancellationToken token = default)
    {
        if (Volatile.Read(ref vendorSales) is { } loaded) return loaded;
        var sellers = new Dictionary<int, List<int>>();
        var problems = new List<string>();
        int rows = 0;
        foreach (var vendor in Vendors)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var sales = ReadVendor(vendor.Tag, catalog.Read(vendor.Tag)).Sales;
                rows += sales.Count;
                // A sale names its item by u16 index. 0xFFFF and indices past the item table name no item.
                foreach (int item in sales.Select(sale => (int)sale.ItemIndex).Where(item => item < Items.Count).Distinct())
                {
                    if (!sellers.TryGetValue(item, out var list)) sellers[item] = list = [];
                    list.Add(vendor.Index);
                }
            }
            catch (Exception error) when (Recoverable(error)) { problems.Add($"{vendor.Name}: {error.Message}"); }
        }
        var built = new InvestmentVendorSales(sellers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), rows, problems);
        return Interlocked.CompareExchange(ref vendorSales, built, null) ?? built;
    }

    /** The row index is the native identity; missing rows never shift later indices. */
    private static (uint Hash, uint Tag)[] ReadIndex(PackageCatalog catalog, uint tag, bool optional = false)
    {
        if (optional && !catalog.ContainsTag(tag)) return [];
        var data = new TagReader(catalog.Read(tag)); var array = data.Array(8, stride: 24);
        if (array.Count > ushort.MaxValue) throw new InvalidDataException("Investment index exceeds its native range.");
        return Enumerable.Range(0, array.Count).Select(index =>
        { int p = array.Data + index * 24; return (data.U32(p), data.U32(p + 16)); }).ToArray();
    }

    /** The item's bucket byte, or NoBucket when its fixed fields are truncated. */
    internal static byte ReadBucket(TagReader item) => item.Has(FixedFields, FixedFieldsSize) ? item.U8(BucketField) : InvestmentBuckets.NoBucket;

    internal static void RequireClass(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16) || entry.Reference != expected)
            throw new InvalidDataException("Investment reference has an unexpected class.");
    }
    internal static bool Recoverable(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or IndexOutOfRangeException or OverflowException or KeyNotFoundException;
}
