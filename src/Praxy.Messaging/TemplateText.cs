using System.Text.RegularExpressions;

namespace Praxy.Messaging;

/// <summary>Pure <c>{{var}}</c> placeholder substitution, split out of <see cref="MessagingTemplatesService"/> so it's unit-testable without a database.</summary>
public static partial class TemplateText
{
    public static string Substitute(string text, IReadOnlyDictionary<string, string> vars) =>
        PlaceholderRegex().Replace(text, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
