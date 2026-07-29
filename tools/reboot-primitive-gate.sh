#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────────────────────────────
# Vivre — REBOOT PRIMITIVE GATE
#
# Guards the reboot cardinal ("NOTHING auto-reboots") by pinning the COMPLETE inventory of
# reboot-issuing sites. Run it after ANY commit that touches reboot code.
#
#     bash tools/reboot-primitive-gate.sh
#
# WHY THIS EXISTS, AND WHY IT IS NOT A ONE-LINE GREP.
# The old gate was `grep -rl --include=*.cs "Win32Shutdown" source/` → expect exactly one file. That
# worked only because the WMI token is exotic: it appears nowhere else, so "one file" was a valid
# invariant and the convention "don't write the token in other prose" cost nothing. It covered ONE of
# FIVE confirmed primitives (reboot finding #5).
#
# A grep for `shutdown.exe` cannot be built the same way, for three reasons:
#   1. LEGITIMATE MULTIPLICITY — the shipped reboot scripts contain it by design, and the C# doc
#      comments discuss it at length. "Exactly one file" is simply false for this token.
#   2. SELF-REFERENCE — a gate written as prose in CLAUDE.md that greps for the token MATCHES ITSELF,
#      so its own documentation perturbs the count. A script can exclude itself by path, deterministically
#      (see EXCLUDE_SELF below). Prose that the reader copy-pastes cannot. This, not convenience, is the
#      load-bearing reason the gate is a file and not a paragraph.
#   3. PROSE vs CODE — grep cannot tell the primitive `internal const string Script = "shutdown.exe …"`
#      from the sentence `/// the normal path: shutdown.exe sent over WinRM`.
#
# So the invariant shifts from "exactly one file" to "exactly this known set of files, at these exact
# counts". Two layers:
#   • PRIMITIVE CHECKS — five narrow, execution-shaped patterns, one per confirmed primitive, each with
#     an exact expected count. These are the assertions that matter.
#   • CONTAINMENT CHECK — the set of files containing ANY reboot token must equal the known manifest
#     exactly. A NEW file fails. A CHANGED COUNT in a known file fails, which is what catches a sixth
#     primitive added inside a file that already legitimately mentions one.
#
# FAILURE IS LOUD AND UNAMBIGUOUS: every deviation prints EXPECTED vs ACTUAL and the script exits 1.
# There is no "probably fine" outcome.
#
# MAINTENANCE RULE: if you add or move a reboot primitive, this file changes IN THE SAME COMMIT. If the
# gate fails and you do not know why, do not adjust the numbers — read
# docs/reboot-path-and-guardrail-findings.md ▸ finding 5 first. The gate adapts to the code only after a
# human has agreed the code is right.
#
# RESIDUAL LIMITS — stated so a PASS is not over-read. This is a text-matching gate, so:
#   • An OBFUSCATED command evades it — a base64 -EncodedCommand, a runtime-concatenated string, a reflected
#     P/Invoke. No grep-based gate can close this; only a human read of the diff can.
#   • Paths OUTSIDE source/ scripts/ tools/ are not scanned. A new top-level directory needs SCAN_ROOTS
#     widened.
#   • It is not wired into the build, so it only fires when someone runs it. Wiring it into `dotnet test`
#     as a repo-scanning test is the standing recommendation for closing that.
# What it DOES guarantee: no plainly-written reboot primitive can be added, moved, or duplicated anywhere
# under those roots — in any file type — without this gate going red.
# ─────────────────────────────────────────────────────────────────────────────────────────────────────
set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || { echo "GATE ERROR: cannot reach the repo root"; exit 2; }

# Self-exclusion by BASENAME — this file names every token it hunts for, so without this the gate
# reports itself as an unknown primitive site. This is the mechanism prose cannot have.
EXCLUDE_SELF="--exclude=reboot-primitive-gate.sh"
SCAN_ROOTS="source scripts tools"

# NO EXTENSION ALLOWLIST, DELIBERATELY. An earlier draft scanned only *.cs/*.ps1/*.xaml, and a red-team
# pass proved a `.cmd` file containing a literal reboot command was INVISIBLE to it. `-I` skips binaries
# instead, so every text file under the scan roots is covered whatever its extension.
SCAN_TYPES="-I"

# Every token that could ISSUE a machine reboot, across every transport and language in the repo.
# The last five were added by the same red-team pass: the WMI `Reboot()` method is a DIFFERENT method
# from the one SITE 1 pins, and `wmic … call reboot` / `Start-Process shutdown` / `Process.Start("shutdown"`
# spell the command without ever writing `shutdown.exe` or `shutdown /`.
# `"Reboot"` alone was TRIED AND REJECTED — it matches the script library's own category name and the
# reboot-pending UI strings, which would have made the gate noisy enough to be ignored.
ALL_TOKENS='shutdown\.exe|Win32Shutdown|InitiateSystemShutdown|ExitWindowsEx|NtShutdownSystem|SetSystemPowerState|Restart-Computer|Stop-Computer|shutdown /|call reboot|MethodName Reboot|FilePath shutdown|Process\.Start\("shutdown|Start-Process shutdown'

fail_count=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; fail_count=$((fail_count + 1)); }

# check_count <label> <expected> <pattern> [path...]
check_count() {
  local label="$1" expected="$2" pattern="$3"; shift 3
  local paths=("$@"); [ ${#paths[@]} -eq 0 ] && paths=($SCAN_ROOTS)
  local actual
  actual=$(grep -rE "$pattern" "${paths[@]}" $SCAN_TYPES $EXCLUDE_SELF 2>/dev/null | grep -vE '/(bin|obj)/' | wc -l | tr -d ' ')
  if [ "$actual" = "$expected" ]; then
    pass "$label — $actual occurrence(s), as expected"
  else
    fail "$label — EXPECTED $expected occurrence(s), FOUND $actual"
    printf '        offending matches:\n'
    grep -rnE "$pattern" "${paths[@]}" $SCAN_TYPES $EXCLUDE_SELF 2>/dev/null | grep -vE '/(bin|obj)/' | sed 's/^/          /'
  fi
}

echo "=============================================================================="
echo " VIVRE REBOOT PRIMITIVE GATE"
echo " HEAD: $(git rev-parse --short HEAD 2>/dev/null || echo '(not a git repo)')"
echo "=============================================================================="
echo
echo "LAYER 1 — the five confirmed reboot primitives (exact counts)"
echo

# ── SITE 1: the DCOM/WMI shutdown method. This is the ONLY site the pre-existing one-line gate saw.
#    The old check (`grep -rl --include=*.cs "Win32Shutdown" source/` → exactly one file) is SUBSUMED
#    here, not replaced: same token, same expectation, now one component of five.
check_count "SITE 1/5  WMI shutdown method (DcomRebootTrigger)" 1 'Win32Shutdown' source

# ── SITE 2: the SMB/SCM one-shot service image. Lives in the same file as SITE 1 but is a DIFFERENT
#    primitive — the old token-based gate never matched this line.
check_count "SITE 2/5  SMB/SCM service image (DcomRebootTrigger)" 1 '"cmd /c shutdown ' source

# ── SITE 3: the WinRM command line Force reboot sends.
check_count "SITE 3/5  WinRM command line (ForceRebootRunner)" 1 'const string Script = "shutdown\.exe' source

# ── SITE 4: the scheduled-task action. A DIFFERENT PROJECT (Vivre.Desktop) — invisible to any check
#    scoped to Vivre.Core.
check_count "SITE 4/5  scheduled-task action (WorkspaceViewModel)" 1 "New-ScheduledTaskAction -Execute 'shutdown\.exe'" source

# ── SITE 5: the shipped script library. NOT .cs at all, so `--include=*.cs` structurally excluded it.
#    This is where the product's only warned, non-forced reboot (/r /t 300) lives.
check_count "SITE 5/5  shipped reboot scripts (scripts/Reboot)" 4 'shutdown\.exe' scripts

echo
echo "LAYER 2 — containment: no reboot token may appear in any unlisted file"
echo

# The known manifest: every tracked source/script/tool file that may contain a reboot token, and how
# many. PRIMITIVE-BEARING files are marked (P); the rest legitimately DISCUSS a primitive without
# issuing one — operator help text, dialog copy, doc comments, and one test fixture.
#
# Keeping prose and test files in a SEPARATE list from the five primitives is deliberate: the primitive
# inventory stays exactly five, and a reviewer can tell at a glance whether a new entry is a new reboot
# path (serious) or a new sentence about one (not).
read -r -d '' EXPECTED_MANIFEST <<'EOF'
scripts/Reboot/Restart - cancel pending.ps1:1
scripts/Reboot/Restart - force now.ps1:1
scripts/Reboot/Restart - if reboot pending.ps1:1
scripts/Reboot/Restart - warn users (5 min).ps1:1
source/Vivre.Core.Tests/Scripts/ScriptLibraryTests.cs:1
source/Vivre.Core/Updates/DcomRebootTrigger.cs:8
source/Vivre.Core/Updates/ForceRebootRunner.cs:6
source/Vivre.Desktop/HelpContent.cs:1
source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs:5
source/Vivre.Desktop/WorkspaceView.xaml.cs:2
EOF

ACTUAL_MANIFEST=$(grep -rEc "$ALL_TOKENS" $SCAN_ROOTS $SCAN_TYPES $EXCLUDE_SELF 2>/dev/null \
  | grep -v ':0$' | grep -vE '/(bin|obj)/' | sort)

if [ "$ACTUAL_MANIFEST" = "$(printf '%s' "$EXPECTED_MANIFEST" | sort)" ]; then
  pass "containment — 10 known files, counts unchanged"
else
  fail "containment — the token manifest CHANGED"
  echo "        --- EXPECTED (file:count) ---"
  printf '%s' "$EXPECTED_MANIFEST" | sort | sed 's/^/          /'
  echo "        --- ACTUAL (file:count) ---"
  printf '%s\n' "$ACTUAL_MANIFEST" | sed 's/^/          /'
  echo "        --- DIFFERENCES ---"
  diff <(printf '%s' "$EXPECTED_MANIFEST" | sort) <(printf '%s\n' "$ACTUAL_MANIFEST") \
    | sed 's/^/          /' || true
  echo
  echo "        A NEW FILE here may be a SIXTH REBOOT PRIMITIVE. Classify it before touching this gate:"
  echo "        does it ISSUE a reboot, or only discuss one? See"
  echo "        docs/reboot-path-and-guardrail-findings.md ▸ finding 5."
fi

echo
echo "=============================================================================="
if [ "$fail_count" -eq 0 ]; then
  echo " RESULT: PASS — all 5 primitives pinned, containment intact."
  echo "=============================================================================="
  exit 0
fi
echo " RESULT: FAIL — $fail_count check(s) failed. The reboot primitive inventory has CHANGED."
echo " Do NOT update the numbers to make this pass until a human has agreed the code is right."
echo "=============================================================================="
exit 1
