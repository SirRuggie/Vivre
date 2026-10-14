using Vivre.Core.Models;
using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Updates;

public sealed class RebootApprovalTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(300)]
    public void Force_prompt_discloses_count_scope_and_loss_without_names(int count)
    {
        var rows = Enumerable.Range(0, count).Select(i => new Computer($"secret-host-{i}"));
        var approval = RebootApproval.Selected(rows);
        Assert.Contains($"{count} selected machine(s)", approval.ForcePrompt);
        Assert.Contains("Force-reboot", approval.ForcePrompt);
        Assert.Contains("unsaved work will be lost", approval.ForcePrompt);
        Assert.DoesNotContain("secret-host", approval.ForcePrompt);
        Assert.True(approval.ForcePrompt.Length < 200);
    }

    [Theory]
    [InlineData(true, "selected install targets")]
    [InlineData(false, "install targets in this tab")]
    public void Pending_prompt_states_subset_and_original_scope(bool selected, string scope)
    {
        var rows = Enumerable.Range(0, 30).Select(i => new Computer($"host-{i}") { RebootRequired = i < 5 });
        var approval = RebootApproval.Pending(rows, selected);
        Assert.Equal(5, approval.Count);
        Assert.Contains($"5 reboot-pending machine(s) out of 30 {scope}", approval.ForcePrompt);
        Assert.Contains("Force-reboot", approval.ForcePrompt);
        Assert.DoesNotContain("host-", approval.ForcePrompt);
    }

    [Fact]
    public async Task Selection_change_while_approval_is_pending_cannot_substitute_targets()
    {
        var original = new Computer("original");
        var selection = new List<Computer> { original };
        var approval = RebootApproval.Selected(selection);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<Computer>? executed = null;
        async Task ConfirmThenExecute()
        {
            await approval.ExecuteAsync(await answer.Task, rows => { executed = rows; return Task.CompletedTask; }, _ => Assert.Fail("Unexpected rename"));
        }
        Task pending = ConfirmThenExecute();
        selection.Clear();
        selection.Add(new Computer("replacement"));
        answer.SetResult(true);
        await pending;
        Assert.Same(original, Assert.Single(executed!));
    }

    [Fact]
    public async Task Pending_changes_do_not_add_or_substitute_reboot_targets()
    {
        var pending = new Computer("pending") { RebootRequired = true };
        var other = new Computer("other") { RebootRequired = false };
        var approval = RebootApproval.Pending([pending, other], false);
        pending.RebootRequired = false;
        other.RebootRequired = true;
        await approval.ExecuteAsync(true, rows => { Assert.Same(pending, Assert.Single(rows)); return Task.CompletedTask; }, _ => Assert.Fail("Unexpected rename"));
    }

    [Fact]
    public async Task Cancellation_and_empty_targets_never_invoke_dispatch()
    {
        int calls = 0;
        Task Dispatch(IReadOnlyList<Computer> rows) { calls++; return Task.CompletedTask; }
        await RebootApproval.Selected([new Computer("host")]).ExecuteAsync(false, Dispatch, _ => Assert.Fail("Unexpected warning"));
        await RebootApproval.Selected([]).ExecuteAsync(true, Dispatch, _ => Assert.Fail("Unexpected warning"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Changed_host_identity_invalidates_approval_without_dispatch()
    {
        var row = new Computer("approved-host");
        var approval = RebootApproval.Selected([row]);
        row.Name = "different-host";
        string? warning = null;
        await approval.ExecuteAsync(true, _ => throw new InvalidOperationException("Must not dispatch"), text => warning = text);
        Assert.Contains("target name changed", warning);
    }

    [Fact]
    public void Wave_prompt_keeps_conditional_force_and_existing_timing()
    {
        var approval = RebootApproval.Selected([new Computer("private-host-123")]);
        Assert.Contains("Starts gracefully", approval.VerifyPrompt);
        Assert.Contains("refused", approval.VerifyPrompt);
        Assert.Contains("8 minutes (20 for staged Server 2016)", approval.VerifyPrompt);
        Assert.Contains("unsaved work will be lost", approval.VerifyPrompt);
        Assert.Contains("1 selected machine(s)", approval.VerifyPrompt);
        Assert.DoesNotContain("private-host-123", approval.VerifyPrompt);
    }

    [Fact]
    public void Local_warning_tracks_the_same_captured_targets()
    {
        var selection = new List<Computer> { new("localhost") };
        var approval = RebootApproval.Selected(selection);
        selection.Clear();
        Assert.NotNull(approval.LocalWarning);
    }
}
