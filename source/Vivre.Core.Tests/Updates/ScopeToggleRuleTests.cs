using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="ScopeToggleRule.PreservesMessageOnScopeToggle"/>.
///
/// The rule: a scope-toggle NEVER blanks a terminal row status (success or failure) or an
/// in-flight row. Only a non-terminal scanned state (e.g. Available) swaps to the target
/// scope's cached scan message.
/// </summary>
public class ScopeToggleRuleTests
{
    // ── PRESERVES (returns true) ──────────────────────────────────────────────

    [Fact]
    public void Terminal_statuses_and_in_flight_rows_preserve_their_message()
    {
        // Error (incl. "Can't reach WU" / Unreachable) — the original Root C guarantee, now explicit.
        Assert.True(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Error, isPatching: false));

        // Done — a successful "Installed N updates" / "Up to date" / "Cleaned" summary persists.
        Assert.True(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Done, isPatching: false));

        // RebootPending — a reboot-pending install/cleanup summary (incl. 2016 Deferred) persists.
        Assert.True(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.RebootPending, isPatching: false));

        // In-flight: isPatching:true for any state preserves live progress detail.
        Assert.True(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Available, isPatching: true));
        Assert.True(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Idle, isPatching: true));
        Assert.True(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Done, isPatching: true));
    }

    // ── SWAPS (returns false) ─────────────────────────────────────────────────

    [Fact]
    public void Available_scanned_rows_still_swap()
    {
        // Available with isPatching:false — the row was scanned and has a real cached message on
        // both sides, so it SHOULD swap to the target scope's scan result.
        Assert.False(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Available, isPatching: false));
    }

    [Fact]
    public void Idle_and_Scanning_rows_swap()
    {
        // Idle — not yet scanned; the target cached message is null on both sides, so swapping is
        // harmless (null → null) and consistent: don't special-case it.
        Assert.False(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Idle, isPatching: false));

        // Scanning — a live scan is in progress via isPatching; but the state alone (not patching)
        // is a swap candidate (won't typically occur because isPatching would be true, but the
        // predicate is state + isPatching, and state-only Scanning is not a terminal).
        Assert.False(ScopeToggleRule.PreservesMessageOnScopeToggle(PatchState.Scanning, isPatching: false));
    }
}
