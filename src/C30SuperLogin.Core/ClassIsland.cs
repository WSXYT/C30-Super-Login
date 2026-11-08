using System.Text.Json;

namespace C30SuperLogin.Core;

public static class ClassIsland
{
    public static string Merge(string existing, string incoming)
    {
        var old = System.Text.Json.Nodes.JsonNode.Parse(existing) as System.Text.Json.Nodes.JsonArray ?? throw new InvalidDataException("Expected automation array");
        var added = System.Text.Json.Nodes.JsonNode.Parse(incoming) as System.Text.Json.Nodes.JsonArray ?? throw new InvalidDataException("Expected automation array");
        foreach (var rule in added)
        {
            string? id = rule?["ActionSet"]?["Guid"]?.GetValue<string>();
            for (int i = old.Count - 1; i >= 0; i--) if (id is not null && old[i]?["ActionSet"]?["Guid"]?.GetValue<string>() == id) old.RemoveAt(i);
            old.Add(rule?.DeepClone());
        }
        return old.ToJsonString(Json.Options);
    }
    public static IReadOnlyList<Subject> ReadSubjects(string profilePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
        if (!doc.RootElement.TryGetProperty("Subjects", out var subjects)) return [];
        return subjects.EnumerateObject().Select(x => new Subject(x.Name,
            x.Value.TryGetProperty("Name", out var name) ? name.GetString() ?? x.Name : x.Name)).ToList();
    }
    public static string Export(IEnumerable<CourseRule> rules, string executable, IReadOnlyList<AccountProfile>? profiles = null)
    {
        var entries = rules.Select(rule =>
        {
            if (rule.SubjectId.Length == 0 || rule.LeadSeconds is < 0 or > 600) throw new InvalidDataException("Invalid course rule");
            string accountArgument = profiles is null ? $"--profile {rule.ProfileId:D}" :
                profiles.SingleOrDefault(x => x.Id == rule.ProfileId) is { Number: > 0 } profile
                    ? "--id " + profile.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : throw new InvalidDataException("Course profile number is missing");
            return new
            {
                Ruleset = new { Mode = 1, IsReversed = false, Groups = new[] {
                    new { Mode = 0, IsReversed = false, IsEnabled = true, Rules = new[] {
                        new { IsReversed = false, Id = "classisland.lessons.nextSubject", Settings = new { SubjectId = rule.SubjectId } } } },
                    new { Mode = 0, IsReversed = false, IsEnabled = true, Rules = new[] {
                        new { IsReversed = true, Id = "classisland.lessons.previousSubject", Settings = new { SubjectId = rule.SubjectId } } } }
                } },
                ActionSet = new { IsEnabled = rule.Enabled, Name = "C30 Super Login · " + rule.SubjectName, Guid = rule.Id.ToString(), IsOn = false,
                    Actions = new[] { new { Id = "classisland.os.run", Settings = new { Value = executable,
                        Args = $"login {accountArgument} --trigger classisland --headless" } } }, IsRevertEnabled = false },
                Triggers = new[] { new { Id = "classisland.lessons.preTimePoint", Settings = new { TargetState = 1, TimeSeconds = rule.LeadSeconds } } },
                IsConditionEnabled = true
            };
        }).ToList();
        return Json.Serialize(entries);
    }
}
