using SunriseStudio.Packages;
using static SunriseStudio.Creation.Tests.Fixture;

namespace SunriseStudio.Creation.Tests;

/** The writer keeps every template byte it does not decode, and the installed reader reads what it wrote. */
internal static class StringBankChecks
{
    private const uint TemplateContainer = 0x80A00000, TemplateData = 0x80A00001;
    private const uint NewContainer = 0x80A02000, NewData = 0x80A02001;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string root)
    {
        Check(StringBankWriter.Hash("") == EmptyHash && StringBankWriter.Hash("a") == 0x050C5D7E, "String hash is not FNV-1.");
        string cafe = "Caf" + (char)0xE9 + " au lait", epee = (char)0xC9 + "p" + (char)0xE9 + "e";
        var installed = new (uint, string)[] { (0x00000010, "Hand Cannon"), (0x00000020, cafe), (0x00000030, "Rare") };
        var (container, data) = Bank(TemplateData, installed);
        var template = StringBankTemplate.Read(TemplateContainer, container, TemplateData, data, _ => true);
        Check(template.StringCount == 3, "Template string count differs.");

        var added = new List<StringBankText>
        {
            new(StringBankWriter.Hash("Sunrise Rifle"), "Sunrise Rifle"),
            new(StringBankWriter.Hash("Forged for the dawn."), "Forged for the dawn."),
            new(StringBankWriter.Hash(epee), epee)
        };
        var (writtenContainer, writtenData) = StringBankWriter.Write(template, NewData, added);
        Check(U32(writtenContainer, 0x4C) == ContainerMarker, "An undecoded container word was not kept.");
        for (int slot = 0; slot < 13; slot++) Check(U32(writtenContainer, 0x18 + slot * 4) == NewData, "A language slot does not name the new data.");
        Check(I64(writtenContainer, 0) == writtenContainer.Length && I64(writtenData, 0) == writtenData.Length, "Size headers were not rewritten.");
        Check(I64(writtenData, 24) == 0 && I64(writtenData, 32) == 0, "The undecoded data array is not empty.");
        int wideHeader = (int)(48 + I64(writtenData, 48));
        Check(I64(writtenData, 40) == 1 && U32(writtenData, wideHeader - 4) == ArrayMarker && I64(writtenData, wideHeader) == 1
            && U32(writtenData, wideHeader + 8) == 0x80800006 && U16(writtenData, wideHeader + 16) == 0,
            "The written bank lost the template's empty wide array.");
        Check(wideHeader < (int)(64 + I64(writtenData, 64)), "The wide array does not precede the text array.");
        var hashes = Enumerable.Range(0, 3).Select(i => U32(writtenContainer, 0x70 + i * 4)).ToArray();
        Check(hashes.SequenceEqual(hashes.Order()), "A sorted template did not give sorted hashes.");
        int partStart = 0x70;
        for (int i = 0; i < 3; i++)
        {
            int part = partStart + i * 32;
            Check(writtenData.AsSpan(part, 8).ToArray().All(value => value == PartHead)
                && writtenData.AsSpan(part + 0x1A, 6).ToArray().All(value => value == PartTail), "Undecoded part bytes were not kept.");
            long text = part + 8 + I64(writtenData, part + 8);
            int bytes = U16(writtenData, part + 0x14);
            Check(writtenData[(int)text + bytes] == 0, "A terminated template did not give terminated text.");
        }
        var roundTrip = StringBankWriter.Read(writtenContainer, writtenData);
        Check(roundTrip.Count == 3 && added.All(row => roundTrip.Contains(row)), "The written bank does not read back.");
        int position = roundTrip.ToList().FindIndex(row => row.Text == epee);
        Check(U16(writtenData, partStart + position * 32 + 0x16) == 4 && U16(writtenData, partStart + position * 32 + 0x14) == 6,
            "The character length did not follow the template's counting rule.");

        string packages = Path.Combine(root, "install", "packages");
        PackageWriter.WriteNew(Path.Combine(packages, "w64_creation_fixture_0100_0.pkg"), 0x100,
            [new(StringBankWriter.ContainerClass, 8 << 9, container), new(StringBankWriter.DataClass, 8 << 9, data)]);
        using (var catalog = PackageCatalog.Open(Path.Combine(root, "install"), cacheOptions: new() { Enabled = false }))
        {
            var found = StringBankWriter.FindTemplate(catalog, [0x80A00005, TemplateContainer], added);
            Check(found.ContainerTag == TemplateContainer && found.DataTag == TemplateData, "The installed template was not found.");
        }
        PackageWriter.WriteNew(Path.Combine(packages, "w64_creation_fixture_0101_0.pkg"), 0x101,
            [new(StringBankWriter.ContainerClass, 8 << 9, writtenContainer), new(StringBankWriter.DataClass, 8 << 9, writtenData)]);
        using (var catalog = PackageCatalog.Open(Path.Combine(root, "install"), cacheOptions: new() { Enabled = false }))
        {
            var strings = LocalizedStrings.LoadBanks(catalog, [NewContainer, TemplateContainer]);
            Check(strings.FailedContainerCount == 0 && strings.ContainerCount == 2, "The reader refused a bank: " + string.Join("; ", strings.Diagnostics));
            foreach (var row in added)
                Check(strings.Get(NewContainer, row.Hash) == row.Text, $"The reader did not find \"{row.Text}\".");
            Check(strings.Get(TemplateContainer, 0x20) == cafe, "The fixture template does not read.");
        }

        var ascii = Bank(TemplateData, [(0x10, "Auto Rifle"), (0x20, "Legendary")]);
        var asciiTemplate = StringBankTemplate.Read(TemplateContainer, ascii.Container, TemplateData, ascii.Data);
        StringBankWriter.Validate(asciiTemplate, [new(StringBankWriter.Hash("Plain"), "Plain")]);
        Reject<InvalidDataException>(() => StringBankWriter.Validate(asciiTemplate, [new(StringBankWriter.Hash(epee), epee)]),
            "An all-ASCII template accepted text whose length rule it cannot show.");
        var bytesRule = Bank(TemplateData, installed, lengthIsBytes: true);
        var bytesTemplate = StringBankTemplate.Read(TemplateContainer, bytesRule.Container, TemplateData, bytesRule.Data);
        var bytesWritten = StringBankWriter.Write(bytesTemplate, NewData, [new(StringBankWriter.Hash(epee), epee)]);
        Check(U16(bytesWritten.Data, 0x70 + 0x16) == 6, "A byte-count template did not write a byte count.");
        var open = Bank(TemplateData, installed, terminate: false);
        var openWritten = StringBankWriter.Write(StringBankTemplate.Read(TemplateContainer, open.Container, TemplateData, open.Data),
            NewData, [new(1, "ab"), new(2, "cd")]);
        Check(StringBankWriter.Read(openWritten.Container, openWritten.Data).Select(row => row.Text).SequenceEqual(["ab", "cd"]),
            "Unterminated text did not read back.");

        var undecoded = Bank(TemplateData, installed, undecoded: 1);
        Reject<InvalidDataException>(() => StringBankTemplate.Read(TemplateContainer, undecoded.Container, TemplateData, undecoded.Data),
            "A template with an undecoded array was accepted.");
        var narrow = Bank(TemplateData, installed, wide: []);
        var narrowWritten = StringBankWriter.Write(StringBankTemplate.Read(TemplateContainer, narrow.Container, TemplateData, narrow.Data),
            NewData, [new(1, "ab")]);
        Check(I64(narrowWritten.Data, 40) == 0 && StringBankWriter.Read(narrowWritten.Container, narrowWritten.Data).Single().Text == "ab",
            "A template without a wide array did not write one without it.");
        var wideText = Bank(TemplateData, installed, wide: [0x41, 0]);
        Reject<InvalidDataException>(() => StringBankTemplate.Read(TemplateContainer, wideText.Container, TemplateData, wideText.Data),
            "A template holding wide text was accepted.");
        var wideParts = Bank(TemplateData, installed, tail: 0x22);
        Reject<InvalidDataException>(() => StringBankTemplate.Read(TemplateContainer, wideParts.Container, TemplateData, wideParts.Data),
            "A template whose plain parts are wide was accepted.");
        var headed = Bank(TemplateData, installed, head: 0x11);
        Reject<InvalidDataException>(() => StringBankTemplate.Read(TemplateContainer, headed.Container, TemplateData, headed.Data),
            "A template whose plain parts use the head field was accepted.");
        var split = Bank(TemplateData, installed, splitPrototype: true);
        Reject<InvalidDataException>(() => StringBankTemplate.Read(TemplateContainer, split.Container, TemplateData, split.Data),
            "A template whose parts disagree in undecoded bytes was accepted.");
        Reject<InvalidDataException>(() => StringBankTemplate.Read(TemplateContainer, container, TemplateData, data, _ => false),
            "A template whose language slots name other classes was accepted.");
        Reject<InvalidDataException>(() => StringBankWriter.Write(template, NewData, [new(EmptyHash, "Empty marker")]),
            "The empty marker named text.");
        Reject<InvalidDataException>(() => StringBankWriter.Write(template, NewData, [new(5, "One"), new(5, "Two")]),
            "A repeated hash was accepted.");
        Reject<InvalidDataException>(() => StringBankWriter.Write(template, NewData, [new(5, "")]), "Empty text was accepted.");
        Console.WriteLine("creation string_bank=pass");
    }
}
