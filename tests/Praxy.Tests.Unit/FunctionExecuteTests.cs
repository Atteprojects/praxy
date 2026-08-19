using Praxy.Core;
using Praxy.Functions;
using Praxy.Persistence.Entities;

namespace Praxy.Tests.Unit;

/// <summary>
/// The deny-by-default gate on function invocation (roadmap rule 3). <c>CanExecute</c> is the whole
/// decision — the endpoint only supplies the caller's resolved roles — so the empty-list case is the
/// one that matters most here.
/// </summary>
public class FunctionExecuteTests
{
    private static FunctionDef Function(params string[] execute) => new()
    {
        Id = Ids.NewUuid(),
        ProjectId = "acme",
        Key = "greeter",
        Name = "Greeter",
        Runtime = "node",
        Entrypoint = "index.js",
        Execute = execute,
    };

    [Fact]
    public void A_new_function_denies_everyone()
    {
        var fn = Function();
        Assert.Empty(fn.Execute);
        Assert.False(FunctionsService.CanExecute(fn, ["any", "users", "user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4"]));
    }

    [Fact]
    public void Guests_reach_a_function_that_grants_any() =>
        Assert.True(FunctionsService.CanExecute(Function("any"), ["any", "guests"]));

    [Fact]
    public void Guests_do_not_reach_a_function_that_only_grants_users() =>
        Assert.False(FunctionsService.CanExecute(Function("users"), ["any", "guests"]));

    [Fact]
    public void A_signed_in_user_reaches_a_users_only_function() =>
        Assert.True(FunctionsService.CanExecute(Function("users"), ["any", "users", "user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4"]));

    [Fact]
    public void One_matching_role_out_of_several_is_enough() =>
        Assert.True(FunctionsService.CanExecute(
            Function("team:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4/owner", "label:vip"),
            ["any", "users", "label:vip"]));

    /// <summary>
    /// An API key resolves to exactly <c>["any"]</c> (<c>RoleResolver</c>), so a key without the
    /// row-permission bypass reaches only functions that are public anyway — the same shape a key
    /// has against a table it holds no permission on.
    /// </summary>
    [Fact]
    public void An_api_keys_roles_do_not_open_a_users_only_function() =>
        Assert.False(FunctionsService.CanExecute(Function("users"), ["any"]));
}

public class RolesGrammarTests
{
    [Theory]
    [InlineData("any")]
    [InlineData("guests")]
    [InlineData("users")]
    [InlineData("users/verified")]
    [InlineData("user:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")]
    [InlineData("team:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4/owner")]
    [InlineData("member:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")]
    [InlineData("label:vip")]
    public void Valid_roles_pass(string role) => Assert.True(Roles.IsValid(role));

    [Theory]
    [InlineData("")]
    [InlineData("everyone")]
    [InlineData("execute")]
    [InlineData("read(\"any\")")]
    [InlineData("user:not-a-guid")]
    [InlineData("label:has spaces")]
    public void Invalid_roles_fail(string role) => Assert.False(Roles.IsValid(role));

    /// <summary>
    /// Tables and functions must not drift apart: <c>PermissionStrings.IsValidRole</c> delegates
    /// here rather than keeping a second copy of the regexes.
    /// </summary>
    [Theory]
    [InlineData("any")]
    [InlineData("team:0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4")]
    [InlineData("not-a-role")]
    public void Table_permissions_and_function_execute_share_one_grammar(string role) =>
        Assert.Equal(Roles.IsValid(role), Praxy.Tables.PermissionStrings.IsValidRole(role));
}
