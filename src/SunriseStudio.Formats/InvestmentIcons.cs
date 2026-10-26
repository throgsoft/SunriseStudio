using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public sealed record InvestmentIcon(uint ContainerTag, uint BitmapTag, TextureData Texture);

public sealed partial class InvestmentCatalog
{
    /** Display icon indices select shared rows; their hashes need not match the item hash. */
    public InvestmentIcon? ReadIcon(InvestmentItem item, CancellationToken token = default)
    {
        byte[]? table = null;
        return ReadIcon(item, ref table, token);
    }

    /** Reuses the icon table bytes in table, or reads them into table on first use. */
    internal InvestmentIcon? ReadIcon(InvestmentItem item, ref byte[]? table, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.DisplayTag == 0 || !catalog.ContainsTag(sources.Icons)) return null;
        RequireClass(catalog, item.DisplayTag, DisplayClass);
        var display = new TagReader(catalog.Read(item.DisplayTag));
        if (!display.Has(128, 2)) throw new InvalidDataException("Item icon index is truncated.");
        ushort index = display.U16(128);
        return index == ushort.MaxValue ? null : ReadIcon(index, ref table, token);
    }

    public InvestmentIcon? ReadPerkIcon(int perk, CancellationToken token = default)
    {
        byte[]? table = null;
        return perk >= 0 && perk < PerkDefinitions.Count && PerkDefinitions[perk].IconIndex != ushort.MaxValue
            && catalog.ContainsTag(sources.Icons) ? ReadIcon(PerkDefinitions[perk].IconIndex, ref table, token) : null;
    }

    /** Icon rows are shared by items and perks; a row names a container, which names a bitmap. */
    private InvestmentIcon? ReadIcon(int index, ref byte[]? table, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (table is null)
        {
            RequireClass(catalog, sources.Icons, 0x80802951);
            table = catalog.Read(sources.Icons);
        }
        var icons = new TagReader(table);
        var rows = icons.Array(8, stride: 24);
        if (index >= rows.Count) throw new InvalidDataException("Icon index is outside its table.");
        uint containerTag = icons.U32(rows.Data + index * 24 + 16);
        RequireClass(catalog, containerTag, 0x80804A53);
        var container = new TagReader(catalog.Read(containerTag));
        if (!container.Has(0, 128)) throw new InvalidDataException("Icon container is truncated.");
        uint bitmapTag = container.U32(20);
        if (bitmapTag is 0 or uint.MaxValue) return null;
        RequireClass(catalog, bitmapTag, 0x80804A69);
        uint textureTag = CuiBitmap.ReadTexture(catalog.Read(bitmapTag));
        token.ThrowIfCancellationRequested();
        return new(containerTag, bitmapTag, new TextureDataReader(catalog).Read(textureTag));
    }
}

/** Reads many item icons with one copy of the shared icon table, which ReadIcon reads again on every call. Not thread-safe. */
public sealed class InvestmentIconReader(InvestmentCatalog catalog)
{
    private byte[]? table;

    public InvestmentIcon? Read(InvestmentItem item, CancellationToken token = default) => catalog.ReadIcon(item, ref table, token);
}
