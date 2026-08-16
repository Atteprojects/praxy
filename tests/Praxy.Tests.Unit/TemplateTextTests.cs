using Praxy.Auth;
using Praxy.Messaging;

namespace Praxy.Tests.Unit;

public class TemplateTextTests
{
    [Fact]
    public void Substitutes_every_known_placeholder()
    {
        var result = TemplateText.Substitute(
            "Hi {{name}}, verify at {{url}} within {{expiryMinutes}} minutes.",
            new Dictionary<string, string> { ["name"] = "Ada", ["url"] = "https://x.test/v", ["expiryMinutes"] = "60" });
        Assert.Equal("Hi Ada, verify at https://x.test/v within 60 minutes.", result);
    }

    [Fact]
    public void Unknown_placeholder_is_left_untouched()
    {
        var result = TemplateText.Substitute("{{known}} and {{unknown}}", new Dictionary<string, string> { ["known"] = "x" });
        Assert.Equal("x and {{unknown}}", result);
    }

    [Fact]
    public void Text_with_no_placeholders_passes_through_unchanged()
    {
        Assert.Equal("plain text", TemplateText.Substitute("plain text", new Dictionary<string, string>()));
    }

    [Fact]
    public void Every_default_template_placeholder_is_satisfiable()
    {
        // Cross-checks MessagingTemplatesService.Defaults against the exact vars
        // AppAuthService/TeamsService supply for each key — a template referencing a var
        // the caller never provides would silently render "{{typo}}" in a real email.
        var samples = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [AuthEmailTemplateKeys.Verification] = new Dictionary<string, string>
            {
                ["url"] = "u", ["project"] = "p", ["expiryMinutes"] = "60",
            },
            [AuthEmailTemplateKeys.Recovery] = new Dictionary<string, string>
            {
                ["url"] = "u", ["project"] = "p", ["expiryMinutes"] = "60",
            },
            [AuthEmailTemplateKeys.Invitation] = new Dictionary<string, string>
            {
                ["url"] = "u", ["project"] = "p", ["teamName"] = "t",
            },
        };

        foreach (var (key, vars) in samples)
        {
            var (subject, body) = MessagingTemplatesService.Defaults[key];
            Assert.DoesNotContain("{{", TemplateText.Substitute(subject, vars));
            Assert.DoesNotContain("{{", TemplateText.Substitute(body, vars));
        }
    }
}
