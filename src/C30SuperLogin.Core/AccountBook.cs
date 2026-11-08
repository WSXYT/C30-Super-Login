namespace C30SuperLogin.Core;

/// <summary>Stable profile IDs; identifiers remain local and never enter diagnostics.</summary>
public sealed class AccountBook(Store store, Settings settings)
{
    // Short local automation IDs start at 1. GUIDs remain the storage identity;
    // numbers are persistent aliases, never renumbered or recycled after deletion.
    public void EnsureNumbers()
    {
        var used = new HashSet<int>(settings.ProfileNumberAliases.Keys.Where(x => x > 0));
        long next = Math.Max(1, settings.NextProfileNumber);
        foreach (int number in settings.Profiles.Select(x => x.Number).Concat(used))
            if (number > 0) next = Math.Max(next, (long)number + 1);
        bool changed = settings.NextProfileNumber != next;
        settings.NextProfileNumber = next;
        for (int i = 0; i < settings.Profiles.Count; i++)
        {
            var profile = settings.Profiles[i];
            if (profile.Number > 0 && used.Add(profile.Number)) continue;
            settings.Profiles[i] = profile with { Number = AllocateNumber() };
            used.Add(settings.Profiles[i].Number); changed = true;
        }
        if (changed) store.Save(settings);
    }
    private int AllocateNumber()
    {
        if (settings.NextProfileNumber is < 1 or > int.MaxValue) throw new InvalidDataException("Profile number space exhausted");
        return (int)settings.NextProfileNumber++;
    }
    public IReadOnlyList<AccountRecord> Read()
    {
        EnsureNumbers();
        var result = new List<AccountRecord>();
        foreach (var profile in settings.Profiles)
        {
            try
            {
                var secret = store.LoadSecret(profile.Id);
                result.Add(new(profile.Id, profile.Label, secret.Username, secret.UseC30Saved ? "C30 记录" : string.IsNullOrEmpty(secret.C30Uid) ? "助手保存" : "助手 + C30", secret.UseC30Saved ? secret.SavedPasswordAvailable : secret.Password.Length > 0, profile.Number));
            }
            catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
            { result.Add(new(profile.Id, profile.Label, "", "待重新保存", false, profile.Number)); }
        }
        return result;
    }
    public AccountProfile Save(Guid? requestedId, string label, string username, string password, bool updateExisting = false)
    {
        EnsureNumbers();
        username = username.Trim(); label = label.Trim();
        if (label.Length is 0 or > 128 || username.Length is 0 or > 256 || password.Length > 4096) throw new InvalidDataException("Invalid account fields");
        var existing = settings.Profiles.FirstOrDefault(x => x.Id == requestedId);
        if (existing is not null && store.LoadSecret(existing.Id).Username != username && !updateExisting) throw new InvalidDataException("Profile ID already belongs to another account");
        var same = Read().FirstOrDefault(x => x.Username == username);
        if (existing is not null && same is not null && same.Id != existing.Id) throw new InvalidDataException("Account already has another profile ID");
        Guid id = same?.Id ?? requestedId ?? Guid.NewGuid();
        if (id == Guid.Empty) throw new InvalidDataException("Empty profile ID");
        Credentials? previous = settings.Profiles.Any(x => x.Id == id) ? store.LoadSecret(id) : null;
        if (previous?.UseC30Saved == true && previous.Username != username && password.Length == 0) throw new InvalidDataException("Changed account requires password");
        if (password.Length == 0) password = previous?.Password ?? "";
        if (password.Length == 0 && previous?.UseC30Saved != true) throw new InvalidDataException("Password required for a new account");
        bool sameIdentity = previous?.Username == username;
        store.SaveSecret(id, new(username, password, sameIdentity ? previous!.C30Uid : "", password.Length == 0 && previous?.UseC30Saved == true, sameIdentity && previous!.SavedPasswordAvailable));
        var profile = new AccountProfile(id, label, settings.Profiles.FirstOrDefault(x => x.Id == id)?.Number ?? AllocateNumber());
        settings.Profiles.RemoveAll(x => x.Id == id); settings.Profiles.Add(profile); store.Save(settings);
        return profile;
    }
    public AccountMerge MergeSaved(IReadOnlyList<SavedAccount> accounts)
    {
        EnsureNumbers();
        int added = 0, matched = 0;
        foreach (var account in accounts)
        {
            string username = account.Username.Trim();
            if (username.Length is 0 or > 256 || account.Uid.Length > 128 || account.DisplayName.Length > 128) continue;
            AccountProfile? existing = null; Credentials? previous = null;
            foreach (var profile in settings.Profiles)
            {
                try
                {
                    var candidate = store.LoadSecret(profile.Id);
                    if (candidate.Username == username || (account.Uid.Length > 0 && candidate.C30Uid == account.Uid))
                    { existing = profile; previous = candidate; break; }
                }
                catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException) { }
            }
            Guid id = existing?.Id ?? Guid.NewGuid();
            // Preserve a locally saved password and the stable automation/profile ID.
            store.SaveSecret(id, new(username, previous?.Password ?? "", account.Uid, previous?.Password.Length is not > 0, account.HasSavedPassword));
            if (existing is null)
            {
                settings.Profiles.Add(new(id, string.IsNullOrWhiteSpace(account.DisplayName) ? username : account.DisplayName, AllocateNumber())); added++;
            }
            else matched++;
        }
        store.Save(settings);
        return new(added, matched);
    }
    public int Deduplicate()
    {
        EnsureNumbers();
        var seen = new List<(AccountProfile Profile, Credentials Secret)>(); var removed = new List<Guid>();
        foreach (var profile in settings.Profiles.ToArray())
        {
            Credentials secret;
            try { secret = store.LoadSecret(profile.Id); }
            catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException) { continue; }
            int match = seen.FindIndex(x => x.Secret.Username.Trim() == secret.Username.Trim() || (secret.C30Uid.Length > 0 && secret.C30Uid == x.Secret.C30Uid));
            if (match < 0) { seen.Add((profile, secret)); continue; }
            var canonical = seen[match];
            string password = canonical.Secret.Password.Length > 0 ? canonical.Secret.Password : secret.Password;
            var merged = canonical.Secret with { Password = password, C30Uid = canonical.Secret.C30Uid.Length > 0 ? canonical.Secret.C30Uid : secret.C30Uid,
                UseC30Saved = password.Length == 0 && (canonical.Secret.UseC30Saved || secret.UseC30Saved), SavedPasswordAvailable = canonical.Secret.SavedPasswordAvailable || secret.SavedPasswordAvailable };
            store.SaveSecret(canonical.Profile.Id, merged); seen[match] = (canonical.Profile, merged);
            settings.Profiles.RemoveAll(x => x.Id == profile.Id); settings.ProfileAliases[profile.Id] = canonical.Profile.Id;
            settings.ProfileNumberAliases[profile.Number] = canonical.Profile.Id;
            for (int i = 0; i < settings.Rules.Count; i++) if (settings.Rules[i].ProfileId == profile.Id) settings.Rules[i] = settings.Rules[i] with { ProfileId = canonical.Profile.Id };
            removed.Add(profile.Id);
        }
        if (removed.Count > 0) { store.Save(settings); foreach (var id in removed) store.DeleteSecret(id); }
        return removed.Count;
    }
    public Guid? Resolve(string? value)
    {
        EnsureNumbers();
        value = value?.Trim();
        Guid id;
        if (!string.IsNullOrEmpty(value) && value.All(x => x is >= '0' and <= '9') &&
            int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) && number > 0)
        {
            id = settings.Profiles.FirstOrDefault(x => x.Number == number)?.Id ?? settings.ProfileNumberAliases.GetValueOrDefault(number);
        }
        else if (!Guid.TryParse(value, out id)) return null;
        for (int i = 0; i < 16 && settings.ProfileAliases.TryGetValue(id, out var canonical); i++) id = canonical;
        return settings.Profiles.Any(x => x.Id == id) ? id : null;
    }
}
