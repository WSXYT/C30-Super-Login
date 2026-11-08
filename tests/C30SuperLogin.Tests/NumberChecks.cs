using System.Text.Json;
using C30SuperLogin.Core;

internal static class NumberChecks
{
    public static void Run(Action<bool, string> check)
    {
        var store = new Store(Path.Combine(Path.GetTempPath(), "C30SL-number-tests-" + Guid.NewGuid().ToString("N")));
        var settings = new Settings();
        var old = Guid.NewGuid();
        settings.Profiles.Add(new(old, "Legacy"));
        store.SaveSecret(old, new("LOCAL_NUMBER_A", "LOCAL_SYNTHETIC_PASSWORD"));
        var rule = new CourseRule(Guid.NewGuid(), old, "SUBJECT", "Fixture");
        settings.Rules.Add(rule); store.Save(settings);
        var book = new AccountBook(store, settings);
        check(book.Read().Single().Number == 1 && book.Resolve("1") == old && settings.Rules.Single() == rule && store.LoadSecret(old).Username == "LOCAL_NUMBER_A",
            "legacy migration assigns short ID 1 without changing credentials, GUID or course bindings");
        var two = book.Save(null, "Two", "LOCAL_NUMBER_B", "LOCAL_SYNTHETIC_PASSWORD");
        var three = book.Save(null, "Three", "LOCAL_NUMBER_C", "LOCAL_SYNTHETIC_PASSWORD");
        check(two.Number == 2 && three.Number == 3, "new account IDs are 1, 2, 3 rather than fixed-length random strings");
        var reloaded = new AccountBook(store, store.Load());
        check(reloaded.Resolve(" 2 ") == two.Id && reloaded.Read().Single(x => x.Id == two.Id).Number == 2,
            "short IDs survive reload and accept trimmed automation input");
        var duplicate = book.Save(null, "Renamed", "LOCAL_NUMBER_B", "LOCAL_SYNTHETIC_PASSWORD");
        book.MergeSaved([new("LOCAL_UID_B", "LOCAL_NUMBER_B", "Imported", true)]);
        check(duplicate.Number == 2 && settings.Profiles.Single(x => x.Id == two.Id).Number == 2 && settings.Profiles.Count == 3,
            "renaming and repeated C30 imports preserve the same short ID without duplicates");
        settings.Profiles.RemoveAll(x => x.Id == two.Id); store.DeleteSecret(two.Id); store.Save(settings);
        var four = book.Save(null, "Four", "LOCAL_NUMBER_D", "LOCAL_SYNTHETIC_PASSWORD");
        check(four.Number == 4 && book.Resolve("2") is null && book.Resolve("4") == four.Id,
            "deleted numbers are not recycled into a different automation account");
        var legacyDuplicate = Guid.NewGuid();
        settings.Profiles.Add(new(legacyDuplicate, "Duplicate", 5));
        store.SaveSecret(legacyDuplicate, new("LOCAL_NUMBER_A", "LOCAL_SYNTHETIC_PASSWORD"));
        check(book.Deduplicate() == 1 && book.Resolve("5") == old && book.Resolve("1") == old && book.Resolve(legacyDuplicate.ToString()) == old,
            "deduplication retains both old numeric and GUID automation aliases");
        check(new[] { "0", "-1", "+1", "1.0", "1e0", "999999", "not-an-id", "１" }.All(x => book.Resolve(x) is null)
              && book.Resolve(old.ToString()) == old,
            "invalid and unknown numbers are rejected; existing GUID calls still resolve");
        using var exported = JsonDocument.Parse(ClassIsland.Export([rule], @"C:\Fixture\C30SuperLogin.exe", settings.Profiles));
        string args = exported.RootElement[0].GetProperty("ActionSet").GetProperty("Actions")[0].GetProperty("Settings").GetProperty("Args").GetString()!;
        check(args == "login --id 1 --trigger classisland --headless" && !args.Contains(old.ToString()),
            "ClassIsland export uses the short numeric ID in its actual run command");
        bool missingRejected = false;
        try { ClassIsland.Export([rule with { ProfileId = Guid.NewGuid() }], "fixture.exe", settings.Profiles); }
        catch (InvalidDataException) { missingRejected = true; }
        check(missingRejected, "ClassIsland rejects missing numeric bindings instead of exporting an ambiguous login");
        var corrupt = new Settings { Profiles = [new(Guid.NewGuid(), "A", 7), new(Guid.NewGuid(), "B", 7)] };
        new AccountBook(store, corrupt).EnsureNumbers();
        check(corrupt.Profiles.Select(x => x.Number).SequenceEqual([7, 8]), "duplicate stored numbers are repaired without changing the first valid ID");
    }
}
