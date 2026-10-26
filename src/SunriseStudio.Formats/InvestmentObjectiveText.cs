using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Objective progress descriptions, read from the objective display table. */
internal static class InvestmentObjectiveText
{
    /** The display table's entry class and row class. Rows are 64 bytes. */
    private const uint TableClass = 0x808059EC, RowClass = 0x808059F0;
    private const int TableArray = 8, RowStride = 64;
    /** A row holds the objective hash, then the description's u16 string bank (zero padding follows) and u32 string hash. */
    private const int RowHash = 0, DescriptionBank = 24, DescriptionHash = 28;

    /**
     * Rows join objectives by index and must repeat the objective's hash. A missing or mismatched row gives no text.
     * @param tag The display table to read, or 0 to find the one installed entry of its class, as InvestmentRoot.Locate finds
     * the root.
     * @param text Resolves a (bank, string hash) pair, or returns "" when the bank or string is absent.
     * @return One description per objective, "" when there is none.
     */
    internal static string[] Read(PackageCatalog catalog, uint tag, IReadOnlyList<InvestmentObjective> objectives,
        Func<uint, uint, string> text, List<string> diagnostics, CancellationToken token)
    {
        if (objectives.Count == 0) return [];
        if (tag == 0)
        {
            var tables = catalog.Entries.Where(entry => entry.Type is 8 or 16 && entry.Reference == TableClass).Take(2).ToArray();
            if (tables.Length != 1)
                throw new InvalidDataException(tables.Length == 0 ? "The objective display table is not installed."
                    : "The objective display table is ambiguous.");
            tag = tables[0].TagHash;
        }
        if (!catalog.ContainsTag(tag)) throw new InvalidDataException("The objective display table is not installed.");
        InvestmentCatalog.RequireClass(catalog, tag, TableClass);
        var table = new TagReader(catalog.Read(tag));
        var rows = table.Array(TableArray, RowClass, RowStride);
        var descriptions = new string[objectives.Count];
        int unmatched = 0;
        for (int index = 0; index < descriptions.Length; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            int at = rows.Data + index * RowStride;
            if (index >= rows.Count || table.U32(at + RowHash) != objectives[index].Hash) { descriptions[index] = ""; unmatched++; continue; }
            descriptions[index] = text(table.U16(at + DescriptionBank), table.U32(at + DescriptionHash));
        }
        if (unmatched != 0) diagnostics.Add($"Objective text: {unmatched} objectives have no display row with their hash.");
        return descriptions;
    }
}
