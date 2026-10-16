using SunriseStudio.Packages;

/** Checks the object type and slot type name tables against native values. */
internal static class NativeTableChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run()
    {
        Check(ObjectTypeNames.Count == 29 && ObjectTypeNames.Name(0) == "inherited" && ObjectTypeNames.Name(28) == "system"
            && ObjectTypeNames.Name(29) is null && ObjectTypeNames.Name(-1) is null, "Object type names differ.");
        Check(SlotTypeNames.Count == 67 && SlotTypeNames.Name(23) == "device sensor" && SlotTypeNames.Name(60) == "trigger volume"
            && new[] { 0, 7, 9, 55, 56, 59, 73 }.All(type => SlotTypeNames.Name(type) is null), "Slot type labels differ.");
        Console.WriteLine($"native_tables object_types={ObjectTypeNames.Count} slot_types={SlotTypeNames.Count}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
