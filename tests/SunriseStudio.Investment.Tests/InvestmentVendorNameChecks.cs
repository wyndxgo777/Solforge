using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

/** Checks vendor display name lookup (64-bit bank index, localized pairs) so a vendor never shows another entry name or a truncated one. */
internal static class InvestmentVendorNameChecks
{
    /** Vendor labels use only their paired display's native name field and 16-bit bank index. */
    internal static void Run(PackageCatalog catalog, InvestmentSources sources, uint displayTag)
    {
        byte[] source = catalog.Read(displayTag), index = catalog.Read(sources.VendorDisplays);
        var prior = catalog.ReadOverride;
        try
        {
            byte[] decoy = source.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(decoy.AsSpan(36), 5);
            catalog.ReadOverride = tag => tag == displayTag ? decoy : prior?.Invoke(tag);
            Check(Read().Name == "Quartermaster", "A preceding localized pair replaced the vendor name.");
            byte[] changed = source.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(66), 0xCAFE);
            catalog.ReadOverride = tag => tag == displayTag ? changed : prior?.Invoke(tag);
            Check(Read().Name == "Quartermaster", "Vendor name treated the native bank index as 32 bits.");
            BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(64), ushort.MaxValue);
            Check(Read().Name == "Vendor 0", "Absent vendor name used an unrelated localized pair.");
            changed = source[..64];
            var truncated = InvestmentCatalog.Load(catalog, sources: sources);
            Check(truncated.Vendors.Single().Name == "Vendor 0"
                && truncated.Diagnostics.Any(text => text.Contains("Vendor display name is truncated.", StringComparison.Ordinal)),
                "Truncated vendor name was accepted without a diagnostic.");
            byte[] wrongClass = index.ToArray();
            int rows = checked(16 + (int)BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(16)) + 16);
            BinaryPrimitives.WriteUInt32LittleEndian(wrongClass.AsSpan(rows + 16), sources.Items);
            catalog.ReadOverride = tag => tag == sources.VendorDisplays ? wrongClass : prior?.Invoke(tag);
            var invalid = InvestmentCatalog.Load(catalog, sources: sources);
            Check(invalid.Vendors.Single().Name == "Vendor 0"
                && invalid.Diagnostics.Any(text => text.StartsWith("Vendor label 0:", StringComparison.Ordinal)),
                "A display target with another class supplied a vendor name.");
            byte[] originalBanks = catalog.Read(sources.StringBanks);
            const int count = short.MaxValue + 2;
            byte[] manyBanks = new byte[48 + count * 8]; originalBanks.AsSpan(0, 48).CopyTo(manyBanks);
            BinaryPrimitives.WriteInt64LittleEndian(manyBanks.AsSpan(8), count);
            BinaryPrimitives.WriteInt64LittleEndian(manyBanks.AsSpan(32), count);
            for (int i = 0; i < count; i++) originalBanks.AsSpan(48, 8).CopyTo(manyBanks.AsSpan(48 + i * 8));
            changed = source.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(64), 0x8000);
            catalog.ReadOverride = tag => tag == sources.StringBanks ? manyBanks : tag == displayTag ? changed : prior?.Invoke(tag);
            Check(Read().Name == "Vendor 0", "A negative native signed bank index resolved through an oversized table.");
        }
        finally { catalog.ReadOverride = prior; }
        Console.WriteLine("investment_vendor_names field=64 bank=u16 unrelated=ignored missing=fallback class=checked");

        InvestmentVendor Read() => InvestmentCatalog.Load(catalog, sources: sources).Vendors.Single();
    }

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidDataException(message); }
}
