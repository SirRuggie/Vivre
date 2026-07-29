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

# THE INVENTORY IS DATA, NOT CODE. Tokens, the five sites and the containment manifest all live in
# reboot-primitive-gate.manifest so that this script and the xUnit guard
# (Vivre.Core.Tests/Guards/RebootPrimitiveGateTests.cs, which runs under `dotnet test`) consume ONE source
# of truth. The two scanners are necessarily separate implementations — bash grep vs .NET Regex — but they
# can never disagree about WHAT to look for or HOW MANY to expect. Editing the numbers in one place cannot
# leave the other stale.
MANIFEST="reboot-primitive-gate.manifest"
[ -r "tools/$MANIFEST" ] || { echo "GATE ERROR: cannot read tools/$MANIFEST — the inventory is missing; refusing to report PASS"; exit 2; }
MANIFEST="tools/$MANIFEST"

# Self-exclusion by BASENAME — this file and the manifest both name every token they hunt for, so without
# this the gate reports ITSELF as an unknown primitive site. This is the mechanism prose cannot have.
EXCLUDE_SELF="--exclude=reboot-primitive-gate.sh --exclude=reboot-primitive-gate.manifest"
SCAN_ROOTS="source scripts tools"

# NO EXTENSION ALLOWLIST, DELIBERATELY. An earlier draft scanned only *.cs/*.ps1/*.xaml, and a red-team
# pass proved a `.cmd` file containing a literal reboot command was INVISIBLE to it. `-I` skips binaries
# instead, so every text file under the scan roots is covered whatever its extension.
SCAN_TYPES="-I"

# Every token that could ISSUE a machine reboot, across every transport and language in the repo — read
# from the manifest. `InitiateSystemShutdown` covers the `…Ex` variant too, since these are substring
# matches. `"Reboot"` alone was TRIED AND REJECTED: it matches the script library's own category name and
# the reboot-pending UI strings, which would have made the gate noisy enough to be ignored.
ALL_TOKENS=$(awk -F'\t' '$1=="TOKENS"{print $2; exit}' "$MANIFEST")
[ -n "$ALL_TOKENS" ] || { echo "GATE ERROR: no TOKENS line in $MANIFEST — refusing to report PASS"; exit 2; }

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

# Driven entirely from the manifest's SITE lines, so this script and the xUnit guard check the same five.
# For the record, what each one pins: 1 = the DCOM/WMI method (the ONLY site the old one-line gate saw;
# that check is SUBSUMED here, same token, same expectation); 2 = the SMB/SCM service image, same FILE as
# site 1 but a different primitive the old gate never matched; 3 = the WinRM command line; 4 = the
# scheduled-task action, in a DIFFERENT project; 5 = the shipped script library, not .cs at all, and the
# home of the product's only warned non-forced reboot (/r /t 300).
site_lines=0
while IFS=$'\t' read -r kind expected root label pattern; do
  [ "$kind" = "SITE" ] || continue
  site_lines=$((site_lines + 1))
  check_count "$label" "$expected" "$pattern" "$root"
done < "$MANIFEST"

if [ "$site_lines" -eq 0 ]; then
  fail "no SITE lines found in $MANIFEST — the primitive inventory is EMPTY, which is never correct"
fi

echo
echo "LAYER 2 — containment: no reboot token may appear in any unlisted file"
echo

# The known manifest, from the manifest file's FILE lines: every file under the scan roots that may
# contain a reboot token, and how many. Some are primitive-bearing; the rest legitimately DISCUSS a
# primitive without issuing one — operator help text, dialog copy, doc comments, and one test fixture.
#
# Keeping prose and test files in this list while the five primitives stay in the SITE lines is
# deliberate: the primitive inventory stays exactly five, and a reviewer can tell at a glance whether a
# new entry is a new reboot path (serious) or a new sentence about one (not).
EXPECTED_MANIFEST=$(awk -F'\t' '$1=="FILE"{printf "%s:%s\n", $3, $2}' "$MANIFEST")
if [ -z "$EXPECTED_MANIFEST" ]; then
  fail "no FILE lines found in $MANIFEST — the containment manifest is EMPTY, which is never correct"
fi

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
