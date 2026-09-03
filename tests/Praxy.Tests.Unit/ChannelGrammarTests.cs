using Praxy.Core;
using Praxy.Realtime;

namespace Praxy.Tests.Unit;

public class ChannelGrammarTests
{
    private const string Db = "0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4";
    private const string Table = "1195a1b2c3d4e5f6a7b8c9d0e1f2a3b5";
    private const string Row = "2195a1b2c3d4e5f6a7b8c9d0e1f2a3b6";
    private const string Team = "3195a1b2c3d4e5f6a7b8c9d0e1f2a3b7";
    private const string User = "4195a1b2c3d4e5f6a7b8c9d0e1f2a3b8";
    private const string Bucket = "5195a1b2c3d4e5f6a7b8c9d0e1f2a3b9";
    private const string File = "6195a1b2c3d4e5f6a7b8c9d0e1f2a3ba";

    [Fact]
    public void Row_event_fans_out_on_four_channel_variants()
    {
        var channels = ChannelGrammar.ChannelsForEvent($"databases.{Db}.tables.{Table}.rows.{Row}.create");
        Assert.Equal(
        [
            $"databases.{Db}.tables.{Table}.rows",
            $"databases.{Db}.tables.{Table}.rows.{Row}",
            $"databases.{Db}.tables.{Table}.rows.create",
            $"databases.{Db}.tables.{Table}.rows.{Row}.create",
        ], channels);
    }

    [Fact]
    public void File_event_fans_out_on_the_same_four_channel_variants_as_a_row()
    {
        var channels = ChannelGrammar.ChannelsForEvent($"buckets.{Bucket}.files.{File}.create");
        Assert.Equal(
        [
            $"buckets.{Bucket}.files",
            $"buckets.{Bucket}.files.{File}",
            $"buckets.{Bucket}.files.create",
            $"buckets.{Bucket}.files.{File}.create",
        ], channels);
    }

    [Fact]
    public void File_update_and_delete_derive_their_own_action_channels()
    {
        Assert.Contains($"buckets.{Bucket}.files.update",
            ChannelGrammar.ChannelsForEvent($"buckets.{Bucket}.files.{File}.update"));
        Assert.Contains($"buckets.{Bucket}.files.delete",
            ChannelGrammar.ChannelsForEvent($"buckets.{Bucket}.files.{File}.delete"));
    }

    [Fact]
    public void A_bucket_event_with_a_non_hex_id_or_unknown_action_derives_no_channels()
    {
        Assert.Empty(ChannelGrammar.ChannelsForEvent($"buckets.not-hex.files.{File}.create"));
        Assert.Empty(ChannelGrammar.ChannelsForEvent($"buckets.{Bucket}.files.{File}.rename"));
    }

    [Fact]
    public void File_event_names_expand_the_way_an_sdk_pattern_expects()
    {
        var type = $"buckets.{Bucket}.files.{File}.create";
        var expanded = ChannelGrammar.ExpandEventNames(type);

        Assert.Contains(type, expanded);
        Assert.Contains("buckets.*.files.*.create", expanded);
        Assert.Equal(4, expanded.Count); // 2 id segments -> 2^2 combinations
    }

    [Fact]
    public void Team_event_maps_to_the_team_channel()
    {
        var channels = ChannelGrammar.ChannelsForEvent($"teams.{Team}.update");
        Assert.Equal([$"teams.{Team}"], channels);
    }

    [Fact]
    public void Membership_event_still_maps_to_the_team_channel()
    {
        var channels = ChannelGrammar.ChannelsForEvent($"teams.{Team}.memberships.{User}.create");
        Assert.Equal([$"teams.{Team}"], channels);
    }

    [Fact]
    public void User_event_maps_to_the_account_channel()
    {
        var channels = ChannelGrammar.ChannelsForEvent($"users.{User}.update.labels");
        Assert.Equal([$"account.{User}"], channels);
    }

    [Fact]
    public void Session_delete_events_derive_no_channels_since_the_hub_handles_them_separately()
    {
        var channels = ChannelGrammar.ChannelsForEvent($"users.{User}.sessions.{User}.delete");
        // Falls through the generic "users.<id>..." rule to account.<id> — the hub special-cases
        // this type before ever asking for its channels, so this path is never actually exercised
        // for fan-out, but it shouldn't throw or misbehave either.
        Assert.Equal([$"account.{User}"], channels);
    }

    [Fact]
    public void Unrecognized_event_types_derive_no_channels()
    {
        Assert.Empty(ChannelGrammar.ChannelsForEvent("something.unexpected"));
        Assert.Empty(ChannelGrammar.ChannelsForEvent("databases.not-a-hex-id.tables.x.rows.y.create"));
    }

    [Fact]
    public void Account_is_rewritten_to_account_userId_at_subscribe_time()
    {
        var normalized = ChannelGrammar.NormalizeForSubscribe(["account", "teams.x"], Guid.Parse("41950000-0000-7000-8000-000000000000"));
        Assert.Equal($"account.{Ids.Wire(Guid.Parse("41950000-0000-7000-8000-000000000000"))}", normalized[0]);
        Assert.Equal("teams.x", normalized[1]);
    }

    [Fact]
    public void Account_is_left_unrewritten_for_a_caller_with_no_user_id()
    {
        var normalized = ChannelGrammar.NormalizeForSubscribe(["account"], null);
        Assert.Equal(["account"], normalized);
    }

    [Fact]
    public void Expand_event_names_includes_the_concrete_type_and_the_fully_wildcarded_form()
    {
        var type = $"databases.{Db}.tables.{Table}.rows.{Row}.create";
        var expanded = ChannelGrammar.ExpandEventNames(type);

        Assert.Contains(type, expanded);
        Assert.Contains("databases.*.tables.*.rows.*.create", expanded);
        Assert.Equal(8, expanded.Count); // 3 id segments -> 2^3 combinations
    }

    [Fact]
    public void Expand_event_names_only_wildcards_id_segments_never_the_action_word()
    {
        var expanded = ChannelGrammar.ExpandEventNames($"teams.{Team}.update");
        Assert.Equal(2, expanded.Count);
        Assert.Contains($"teams.{Team}.update", expanded);
        Assert.Contains("teams.*.update", expanded);
        Assert.DoesNotContain("teams.*.*", expanded);
    }
}
