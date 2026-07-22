# Vivre — WUG state-check findings (IP substring match + cold-start mass-unknown)

> **Point-in-time case file (2026-07-21, releases 1.16.3 → 1.16.4).** The two root causes fixed in
> this cycle, recorded so the reasoning is never re-derived. Like the other findings docs, this is
> never edited after the fact. Resolver code: `source/Vivre.Core/Wug/WugMaintenance.cs`
> (`ResolveFunctionScript`, `SslTrustInstallScript`). Module examined: WhatsUpGoldPS **0.1.21**.

## 1. IP substring-match reclassification (`c6463d2`)

WUG's `Get-WUGDevice -SearchValue` is a **SUBSTRING** search: an IP search for `x.y.z.10` also
returns `x.y.z.101`, `.109`, etc. The resolver's DNS→IP fall-through (used when a name isn't in WUG)
treated *any* returned rows as evidence and classified 0-exact-match rows as `Ambiguous` → the grid
read "state unknown" for boxes that simply aren't in WUG.

**Fix — classify by the count of EXACT `networkAddress -eq $ip` matches:**

| exact matches | outcome | grid text |
|---|---|---|
| 1 | `MatchedByIp` (the exact device, never a sibling) | real state |
| 0 (rows returned, none equal) | `NoDevice` | "no matching device" |
| 2+ (devices genuinely share the IP) | `Ambiguous` — deliberate change from the old silent first-pick | "state unknown" |

Live evidence: **AZRPWDEGWEB** (.10 → returned .109/.108, 0 exact) and **AZRLIC8** (.12 → .120/.124,
0 exact) now read "no matching device", not "unknown". Invariant preserved from `b67ed55`: an
**errored** search is always `LookupError`/unknown — never a false `NoDevice`; a NoDevice verdict may
only come from a clean search. (History: before `b67ed55`, the resolver took `$results[0]` /
`$results2[0]` of the substring hits — the ".10 matched .101" false-identity bug.)

## 2. Cold-start mass-unknown SSL chain (`a19d150` → `c49b4da`)

**Symptom:** first 329-box state check after a cold start returned "state unknown" for ~308 rows;
the rerun was clean. Captured error: `CmdletInvocationException … The underlying connection was
closed … There is no Runspace available to run scripts in this thread … at System.Net.TlsStream.EndWrite
… WebRequestPSCmdlet.GetResponse`.

**Root cause:** `Connect-WUGServer -IgnoreSSLErrors` installs a PowerShell **scriptblock** as the
process-wide `ServerCertificateValidationCallback` (Connect-WUGServer.ps1:432-455), and the module's
shared request wrapper **re-arms it on every API call** whenever it finds the callback null
(Get-WUGAPIResponse.ps1:70-88, in `begin{}`, before its `Invoke-RestMethod`). A scriptblock callback
can only run inside a runspace; cold TLS handshakes complete on I/O-completion-port threads, which
never have one → the callback throws → `WebException` → one `LookupError` ("state unknown") per
lookup. The warm rerun self-heals (fresh process each run, so the surviving explanation is OS-level
Schannel session resumption making run-2 handshakes cheap — labeled inference).

**Two failed attempts, recorded so they aren't retried:**
- `4b158c5` nulled the scriptblock at HEAD and after each worker connect — **lost to the per-call
  re-arm**: the wrapper re-installs the scriptblock on the next lookup, so it governed the whole run.
- The compiled `ICertificatePolicy` (`VivreWugTrustAll`) was **dead weight**: on .NET Framework a
  non-null `ServerCertificateValidationCallback` wins and `CertificatePolicy` is ignored.

**The fix (`a19d150`):** drop `-IgnoreSSLErrors` at **all four** connect sites (set path, state HEAD,
worker tail, preflight) — every module callback site (Connect install / wrapper re-arm / Disconnect
null) is gated on that flag, so the module never touches the callback again — and install a
**compiled, runspace-free `RemoteCertificateValidationCallback` delegate** (`VivreWugCertValidator`,
assignment inside compiled `Install()`) once at script HEAD, before the first connect. Trust is
**non-optional**: a failed delegate install hard-fails with *"Couldn't establish a trusted connection
to WhatsUp Gold"* before any connect — never a silent continue, never an insecure fallback.

**Field follow-up (`c49b4da`):** on the target box the `Add-Type` compile failed — `X509Chain` is
**type-forwarded** out of `System.dll` there and Add-Type doesn't auto-reference the target assembly
(the dev box compiles reference-less, so a green build/dev test could not catch it; the hard-fail
surfaced it exactly as designed). Fix: build `-ReferencedAssemblies` **at runtime from the live
types' `Assembly.Location`** (reflection follows forwards — each box references whatever actually
holds the type; no guessed assembly names, plus mscorlib because the parameter replaces Add-Type's
defaults).

**Proven in the field:** three cold starts, real states every time — mass-unknown resolved.

## Invariants to preserve (do not undo)

- **No `-IgnoreSSLErrors` at any connect site** — reintroducing the flag re-enables all three module
  callback sites and the per-call re-arm.
- **The compiled delegate installs before the first connect** (and before the state read's fan-out);
  workers rely on the process-wide install and must never null or reinstall the callback.
- **The reference set stays runtime-resolved** (`…X509Chain].Assembly.Location`) — hardcoding an
  assembly name fixes one box and breaks another.
- **Trust install stays hard-fail** on the connecting paths — a silent continue would now fail every
  cert with no fallback.
- The tripwire tests lock all of this: `WugSslTrustTests` (10 locks: no-flag, delegate-before-connect,
  hard-fail-before-connect, old-policy-absent, runtime-resolved references) and the resolver process
  tests in `WugResolverProcessTests` (IP exact-count outcomes + error honesty). A module update that
  changes callback behavior is caught by these, not in the field.
