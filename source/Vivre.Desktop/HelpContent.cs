using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// One how-to entry in the Help window — a collapsible, searchable card. Task-oriented ("How do
/// I…"), grouped by <see cref="Category"/>, with concise verb-first steps.
/// </summary>
public partial class HelpTopic : ObservableObject
{
    public required string Category { get; init; }
    public required string Title { get; init; }
    public SymbolRegular Icon { get; init; } = SymbolRegular.Info24;

    /// <summary>Extra search terms not already in the title/steps.</summary>
    public string Keywords { get; init; } = string.Empty;

    /// <summary>The steps / points — already prefixed ("1.", "2.", "•") in the text.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>Optional tip / caveat shown in a subtle box at the bottom.</summary>
    public string? Tip { get; init; }

    /// <summary>Collapsed by default; the search auto-expands matches.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Lower-cased text the search matches against.</summary>
    public string Haystack => $"{Title} {Keywords} {string.Join(" ", Lines)} {Tip}".ToLowerInvariant();
}

/// <summary>The full "How to use Vivre" guide, in usage order. Edit here to add/adjust topics.</summary>
public static class HelpContent
{
    public const string GettingStarted = "Getting started";
    public const string Machines = "Fleet ▸ Health";
    public const string Updates = "Fleet ▸ Patching";
    public const string Tips = "Tips & shortcuts";
    public const string Trouble = "Troubleshooting";
    public const string CrossDomainRdp = "Cross-Domain RDP";

    public static IReadOnlyList<HelpTopic> Topics { get; } =
    [
        // ---------------- Getting started ----------------
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Info24, Title = "What is Vivre?",
            Keywords = "overview about intro what does",
            Lines =
            [
                "Vivre manages your Windows / SCCM machines at scale from one tabbed grid.",
                "• Each row is one machine; ping it, pull its health, run scripts or SCCM actions, and patch it.",
                "• The left nav has two Fleet sections: Health (ping, vitals, SCCM actions) and Patching (Windows Update).",
                "• Pick machines, then act on them from the toolbar or the right-click menu.",
            ],
            Tip = "Most per-machine actions live on the right-click menu. Select rows first, then right-click.",
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Add24, Title = "How do I load machines into a tab?",
            Keywords = "add computer paste import names list",
            Lines =
            [
                "Quick add: type a name in the \"Add computer…\" box (top-right) and press Enter — several at once works too (separate them with commas, semicolons or spaces).",
                "A list: click the Paste button (clipboard icon, top-right) and paste names, one per line.",
                "A saved list: click the Lists ▾ button (top-right) ▸ Open list ▸ pick one.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Save24, Title = "How do I save and reuse a machine list?",
            Keywords = "named lists save open delete rename tab title",
            Lines =
            [
                "1. Load the machines you want in the tab.",
                "2. Click Lists ▾ (top-right command bar) ▸ Save tab as list… — the name box prefills with the tab's current title (rename the tab first if you want a tidy name; you can also overtype it in the prompt).",
                "3. Later, Lists ▾ ▸ Open list to load it into a tab — the tab automatically takes the list's name.",
                "Lists ▾ ▸ Delete list to remove a saved one.",
            ],
            Tip = "Lists are plain .txt files under %APPDATA%\\Vivre\\Lists — you can edit or back them up outside Vivre.",
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Tabs24, Title = "How do I work with tabs?",
            Keywords = "tab new close rename workspace multiple close others right",
            Lines =
            [
                "Each tab is independent — its own machine list, selection, and running operations.",
                "• New tab: the \"+\" by the tabs, or Ctrl+T.",
                "• Rename: double-click the tab (or F2).",
                "• Close: the \"✕\" on the tab, middle-click it, or Ctrl+W (it asks first if the tab has machines or a running op).",
                "• Right-click a tab for Close other tabs / Close tabs to the right (browser-style) — plus Clear this tab and Close all tabs.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Person24, Title = "How do I set the account Vivre uses?",
            Keywords = "credentials login password settings admin remote",
            Lines =
            [
                "1. Open Settings (left navigation) ▸ Remote credentials.",
                "2. Keep \"Use my Windows login\", or pick \"Use these credentials\", enter Domain / Username / Password, then click Apply.",
                "This is the account Vivre uses to reach your machines — for health checks, patching, Run Script, SCCM client actions, and Enable WinRM.",
            ],
            Tip = "Credentials are held in memory for the session only — never written to disk. Set an admin account here if your login can't reach the targets.",
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.ArrowSwap24, Title = "How do I switch between Health and Patching?",
            Keywords = "mode view toggle switch update health patching fleet section nav",
            Lines =
            [
                "Click Fleet ▸ Health or Fleet ▸ Patching in the left navigation.",
                "Or press Ctrl+M to toggle between them — the nav highlight follows.",
                "Health shows the machines / health-check view; Patching shows the Windows Update view.",
                "Each section has its own independent set of tabs, so you can have different machine lists in Health and Patching simultaneously.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.PaintBrush24, Title = "How do I change the theme?",
            Keywords = "dark light system appearance color",
            Lines =
            [
                "Settings (left navigation) ▸ Appearance ▸ App theme ▸ Light / Dark / System. Your choice is remembered across launches.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Filter24, Title = "How do I filter or find machines in a big list?",
            Keywords = "filter search find chip errors reboot pending offline done up to date unhealthy not scanned scheduled updates available subset narrow clear",
            Lines =
            [
                "Use the filter bar above the grid (works in both Machines and Windows Update views).",
                "• Type in the search box to show only machines whose name contains that text.",
                "• Click a chip to show only a state. The chips differ by view — Health: All · Reboot pending · Errors · Offline · Done · Unhealthy; Patching adds Updates · Up to date · Not scanned · Scheduled (and Server 2016 (N) once a vitals check confirms a 2016 box).",
                "• Click the lit chip again (or All) to clear the filter; if a filter leaves the grid empty, the overlay offers a Clear filter button.",
                "The \"Showing N of M\" count tells you how many match. Filters update live — a machine that errors mid-run pops into the Errors chip on its own.",
            ],
            Tip = "Filtering is the fast way to work a large fleet: filter to Errors to see only what failed, or Reboot pending to find what still needs a restart.",
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.CheckboxChecked24, Title = "How do I act on just the failed (or filtered) machines?",
            Keywords = "select shown subset retry failed errors reinstall reboot filtered just those",
            Lines =
            [
                "1. Filter the grid to the subset you want (e.g. the Errors chip).",
                "2. Click Select shown — it selects every row the filter is showing.",
                "3. Run the action (Install, Scan, right-click ▸ Reboot, etc.) — it acts on just that selection.",
                "So \"retry the failures\" is: Errors → Select shown → Install.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Save24, Title = "How do I export a report (for a ticket / write-up)?",
            Keywords = "export csv report excel ticket maintenance window save what got patched",
            Lines =
            [
                "Right-click ▸ Export ▸ Shown rows + columns (CSV)… — saves the rows currently shown (it respects the filter) to a CSV.",
                "Columns: machine, online, status, updates available, update/reboot messages, reboot pending, last error, OS, scheduled task, plus any custom columns you've added.",
                "Opens cleanly in Excel.",
            ],
            Tip = "Filter first to scope the report — e.g. filter to Up to date (Done in the Health view) and export to record exactly what got patched this window.",
        },

        // ---------------- Machines view ----------------
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.ArrowSync24, Title = "How do I check if machines are online?",
            Keywords = "ping reachable online offline status dot",
            Lines =
            [
                "Click Ping All (or F5). Use the Ping All dropdown ▸ Ping Offline only to re-check just the offline and never-checked ones.",
                "The dot (the \"Online\" column in Health; \"Ping\" in Patching) shows: green ✓ online · red ✕ offline · grey ? not checked yet.",
            ],
            Tip = "Ping is ICMP. If you've set explicit credentials, Vivre also tries an authenticated WMI check, so ping-blocked boxes can still show online.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Heart24, Title = "How do I pull SCCM client health?",
            Keywords = "check all health site code agent version reboot pending missing updates",
            Lines =
            [
                "Click Check Vitals. For each machine it pulls SCCM client health — site code, agent version, last reboot, the health dots — and its Vitals score in the same pass (see the Vitals topic).",
                "Health dots (green = good, red = needs attention): Reboot pending · Updates missing · Install running · Users online.",
                "A grey ? means that reading couldn't be taken — treat it as unknown, not good.",
                "By default this happens automatically when you load a list — Vivre pings + checks vitals on those machines so the grid fills itself, and Monitor keeps the online dot live (in Patching tabs it also keeps Reboot pending live). Turn it off in Settings (left navigation) ▸ Auto-check on load for a frozen snapshot; the buttons (Ping All / Check Vitals) re-check on demand either way. It only ever touches the machines you loaded, never the wider network.",
            ],
            Tip = "Triggering health/actions usually needs admin rights on the target — set credentials in Settings if you see \"access denied\".",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Pulse24, Title = "How do I see why a machine is sick? (Vitals)",
            Keywords = "vitals vitality score health disk memory cpu uptime stopped services event log unhealthy triage",
            Lines =
            [
                "Click Check Vitals — the same button that pulls SCCM client health also reads deep OS health: system-drive free space, memory and CPU load, uptime, plus (for context) any stopped auto-start services.",
                "It rolls the reliable signals (disk, memory, CPU, uptime, reboot-pending) into a 0–100 vitality score shown as a coloured chip in the Vitals column: green = Healthy (80+), amber = Warning (50–79), red = Critical (under 50). Offline/Unknown show grey. A box whose health was read over the WinRM backup channel is floored to amber even with a high number — see the amber-flag topic.",
                "Stopped auto-start services are shown for triage but NOT scored — they're too noisy (idle-by-design services) to trust on their own; the section only appears when something is actually stopped.",
                "Hover the chip to see why (the top reasons), or right-click ▸ Details… ▸ Vitals for the full breakdown — drives, plus stopped services by name when present. The bottom bar tallies the fleet, e.g. \"Vitals: Healthy 40 · Warning 6 · Critical 2\".",
                "Use the Unhealthy filter chip to show just the Warning/Critical/Offline machines, then sort by the Vitals column to put the sickest first.",
            ],
            Tip = "Read-only — one click, no confirm. Reading the service list over WinRM needs admin rights on the target; anything it can't read is skipped rather than counted against the score. See \"What does the Vitality score mean?\" for the full scoring breakdown.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.HeartPulse24, Title = "What does the Vitality score mean?",
            Keywords = "vitality score band healthy warning critical offline unknown penalty points rubric calculation formula disk memory cpu uptime reboot how scored why number",
            Lines =
            [
                "Every machine starts at 100. Check Vitals subtracts points for each problem found; the total clamps to 0–100 and maps to a colour band.",
                "Bands: 80–100 Healthy (green) · 50–79 Warning (amber) · below 50 Critical (red) · Offline (dark grey — no score) · Unknown (grey — vitals never read).",
                "Disk (system drive free): below 15% → −8 · below 10% → −20 · below 5% → −40.",
                "Memory used: above 90% → −10 · above 95% → −20.",
                "CPU load: above 85% → −6 · above 95% → −15.",
                "Reboot pending (CBS / Windows Update / SCCM signals) → −10.",
                "Uptime over 60 days → −5.",
                "WinRM unusable (health read over the DCOM/SMB backup channel) → −12, and the chip is floored to amber (Warning) even when the number is 80+ — see the amber-flag topic.",
                "A signal that can't be read is \"unknown\" and is never penalised — a box that only answers some probes still scores fairly.",
                "The Vitals chip shows the top 3 worst reasons (the highest-penalty problems) as the \"why\" behind the number. Hover the chip to read them; open right-click ▸ Details… ▸ Vitals for the full breakdown and inline triage actions.",
                "CPU and memory are point-in-time samples taken at check time — a momentary spike during the check can temporarily lower a score. Re-run Check Vitals when the box is idle to clear it.",
                "The Unhealthy filter chip includes Offline machines — not just Warning/Critical. An offline box counts as unhealthy even though its internals are unknown (you can't read the vitals of a machine that doesn't answer).",
                "Stopped auto-start services are gathered but deliberately NOT scored — they're dominated by benign noise on healthy boxes (trigger/delayed-start services, updaters), so they appear in Details ▸ Vitals (with a Start button) when any are stopped, but never move the number.",
            ],
            Tip = "\"Vitality 100 (Healthy)\" means no penalty fired. The only ways to drop below 80 are a scored signal (disk, memory, CPU, reboot pending, uptime) exceeding its threshold — or the WinRM-degraded penalty.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.PlugDisconnected24,
            Title = "Why is a machine amber-flagged or showing \"WinRM unavailable\"?",
            Keywords = "winrm kerberos dcom smb fallback connection unavailable degraded needs attention amber 0x80090322 0x80090303 spn quickconfig session ended not domain joined",
            Lines =
            [
                "Some machines refuse WinRM (the fast remote-PowerShell channel) — a Kerberos/SPN problem, a stopped WinRM service, or a dropped session. Rather than fail, Vivre automatically reads their health over a backup channel (DCOM/SMB) using your current Windows login — no password prompt, the same way SCCM reaches them.",
                "So you still get real vitals — but the row is flagged amber and \"needs attention\" so a machine on the backup channel never looks identical to a fully-healthy one.",
                "To see exactly what happened: right-click ▸ Details… ▸ Vitals. A Connection box at the top shows a plain-English summary, and the \"What happened & how to fix\" expander reveals the actual WinRM error (selectable, so you can copy it for a ticket) plus the fix.",
                "Most common (0x80090322): the host runs a web app under a domain service account — e.g. SSRS on the Deltek Vision servers — and that account owns the host's HTTP SPN for report single sign-on. Kerberos can't hand the same hostname to WinRM too, so WinRM is rejected. This is BY DESIGN — don't 'fix' the SPN or you'll break the app's SSO. Vivre just uses the DCOM/SMB backup; nothing to do.",
                "Other causes: the box genuinely isn't domain-joined / has no SPN (0x80090303) → join it / register the SPN only if you need WinRM; or the WinRM service isn't running → run \"winrm quickconfig\" on the host; or a one-off dropped session, which usually clears itself on the next Check Vitals.",
            ],
            Tip = "This is health-channel only — scans, installs and other actions still just work on these machines (over the same backup channel where needed); they don't show any \"degraded\" wording — and the Check software action reads over that same backup channel, so it works on these boxes too. The amber flag is only there so you know the box could use a WinRM fix when you have time.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Wrench24, Title = "How do I fix a sick machine? (triage actions)",
            Keywords = "fix repair remediate triage start service free disk space cleanup end kill process heal",
            Lines =
            [
                "Open right-click ▸ Details… ▸ Vitals. If vitals haven't been read yet, click Check Vitals in the Vitals tab — it runs health and vitals for just this machine in one pass (the toolbar's Check Vitals sweeps the whole tab). Once vitals are populated, the breakdown lets you act in place:",
                "• Stopped auto-start service — click Start next to it to start it now.",
                "• Low drive — click Free up space… to clear TEMP, the Windows Update cache, and the recycle bin (asks first; reports how much it reclaimed).",
                "• A hog — click Load under Top processes, then End next to a runaway one (asks first).",
                "After Start service and Free up space (and after Check Vitals) Vivre re-checks that machine's vitals, so the score and readings update on the spot.",
            ],
            Tip = "Free up space and End process change the machine, so they confirm first; starting a service is one-click. All actions use your session/admin credential and land in the activity log.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Pulse24, Title = "How do I keep statuses live?",
            Keywords = "monitor watch continuous auto refresh default pause toggle",
            Lines =
            [
                "Monitor is ON by default — Vivre starts watching online/offline the moment a list loads, re-checking every machine on a timer.",
                "Turn the Monitor toggle off for a frozen snapshot and back on to resume. The toggle is the ONLY way to pause monitoring — the Stop button cancels running operations, not the monitor.",
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Play24, Title = "How do I run a PowerShell script?",
            Keywords = "run script powershell saved library command right-click",
            Lines =
            [
                "1. Select the machines.",
                "2. Right-click ▸ Run script ▸ Selected machines… (or All machines…) — opens the Run Script window.",
                "3. Pick a saved script (grouped by category) or paste your own, review it, then click Run.",
                "Output lands per-machine in the Command result column and in the window's log.",
                "Note: double-clicking a row opens that machine's Details, not the script runner.",
            ],
            Tip = "The window opens for review — it never auto-runs — so a stray click can't fire a script across your fleet.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Copy24, Title = "How do I stage software to machines (without installing)?",
            Keywords = "stage deploy software copy push package msi exe agent files folder script setup application install prep",
            Lines =
            [
                "1. Select the machines (or none = all), then right-click ▸ Software ▸ Stage software….",
                "2. Pick a package: a single .msi/.exe, or a folder of files (an installer plus your own install script).",
                "   • Pick from the package library dropdown, or Browse file… / Browse folder… to anything anywhere. (Set package library folder… remembers a folder of packages for next time.)",
                "3. Set where to drop it (default C:\\Windows\\Temp\\VivrePackages). A single file lands right there; a folder gets its own subfolder so its files stay together.",
                "4. Click Stage. Vivre copies the files to each machine and reports the path per row (Command result): \"staged to C:\\Windows\\Temp\\VivrePackages\\…\".",
                "5. Install it your way — e.g. right-click ▸ Run script ▸ pointing at the staged path (C:\\Windows\\Temp\\VivrePackages\\<package>\\install.ps1), or your normal batch / Company Portal flow.",
            ],
            Tip = "Vivre only delivers the files — it doesn't run them — so security agents (SentinelOne/CrowdStrike) that disrupt WinRM during an install can't break the copy, and you keep full control of the install. It copies over the fast SMB admin share (\\\\machine\\C$), falling back to WinRM (chunked + SHA-256 verified) where SMB is blocked. Either way it needs admin rights on the target.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Search24, Title = "How do I check what software is installed (e.g. is CrowdStrike on it)?",
            Keywords = "software installed check agent crowdstrike sentinelone version column present missing inventory product app",
            Lines =
            [
                "1. Select the machines (or none = all), then right-click ▸ Software ▸ Check software….",
                "2. Type a product name (or part of one) — e.g. CrowdStrike, SentinelOne, Chrome — or tap a quick-pick button, then Check (or press Enter).",
                "3. Optional: tick \"Also check its service is running\" to confirm the agent is actually live. The service name pre-fills (it's remembered per product — CrowdStrike → CSFalconService) — change it if needed.",
                "4. Vivre fills the Software column: green = installed (and running), amber = installed but its service isn't running, red = \"<name> — not found\". A box that's fully offline just shows \"Offline\" (no scary WinRM error) and gets a real answer the next time you check it.",
                "Click the Software column header to sort — e.g. the machines missing it (or amber) to the top.",
                "Need a report for your boss? After a check, right-click ▸ Export ▸ Software report (CSV)… saves a per-machine CSV (machine, product, version, installed, service running). On-demand only — checking never writes a file by itself.",
            ],
            Tip = "It searches the registry's installed-programs list (fast, and won't trigger an MSI repair the way Win32_Product can), plus the service list when asked. Handy to confirm an agent actually landed AND is running after a deploy. It even works on boxes where WinRM is unavailable for any reason (Kerberos-rejected, service stopped, session dropped) — it falls back to the same read-only DCOM backup channel the health numbers use, running on your ambient Windows login (an explicit alternate credential applies to the WinRM path only). Read-only — it never changes the machine.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Settings24, Title = "How do I customize the grid columns?",
            Keywords = "columns custom hide show add column grid layout serial script one-liner customize remove tailor probe cancelled stop",
            Lines =
            [
                "Open it from Settings (left navigation) ▸ Grid columns ▸ Manage columns…, or right-click the grid ▸ Columns… — works even before you've added any machines.",
                "• Hide columns you don't use — untick them under \"Show columns\" (Name always stays). Saved across launches.",
                "• Add a predefined column from the gallery (Serial, Model, Days since reboot, Free C: (GB), Logged-on user, OS) — one click.",
                "• Add your own — a column name + a PowerShell one-liner. It runs on every machine and whatever it prints fills the cell, e.g. (Get-CimInstance Win32_BIOS).SerialNumber.",
                "Custom columns sort (numeric-aware) and are included when you export (right-click ▸ Export ▸ Shown rows + columns (CSV)…). Use Refresh values to re-run them.",
                "Stop cancels a running fill — cells still waiting show \"cancelled\" and the progress counter freezes where it was; removing a column mid-fill cancels just that column's fill (other columns keep filling).",
            ],
            Tip = "Custom columns are read-only — they run your one-liner per machine and show the result, nothing more. A column whose script errors shows \"ERR: …\" for that machine; the rest still fill. A cell can also read \"Offline\", \"timed out\", \"WinRM n/a\" or \"error\" for a box that couldn't be read.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Wrench24, Title = "How do I run an SCCM client action?",
            Keywords = "client action machine policy hardware inventory update scan trigger schedule stop cancel timed out busy",
            Lines =
            [
                "Select the machines, then right-click ▸ Client actions ▸ and pick one (Machine Policy Retrieval & Evaluation, Hardware Inventory, Software Update Scan, …).",
                "Actions run on all selected machines at once; each row shows its own result, and Stop cancels the batch.",
                "A box that doesn't answer shows \"Timed out\" (after 60s), \"WinRM busy\" (try again shortly), or \"WinRM unavailable\" (for installed software on that box, Software ▸ Check software… still works) — the rest of the selection is unaffected.",
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.PlugConnected24, Title = "How do I enable WinRM on a machine?",
            Keywords = "winrm psremoting enable dcom remoting unreachable",
            Lines =
            [
                "Select the machine(s), right-click ▸ Enable WinRM (PSRemoting)…, and confirm.",
                "It runs Enable-PSRemoting over DCOM — a different channel — so it works on boxes WinRM can't reach yet.",
                "Machines run in parallel (a few at a time), and a box that doesn't answer times out after ~25 seconds instead of holding up the rest. The Stop button cancels whatever hasn't finished.",
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.ArrowClockwise24, Title = "How do I reboot machines now?",
            Keywords = "reboot restart force shutdown",
            Lines =
            [
                "Select the machines, right-click ▸ Reboot (force now)…, and confirm (it lists the count + names).",
                "Runs shutdown /r /f /t 5 (a 5-second grace so the command returns cleanly) — any unsaved work on those machines is lost.",
            ],
            Tip = "To reboot at a set time instead, use right-click ▸ Schedule ▸ Reboot…. Housekeeping is automatic: when a list loads (with auto-check on load on), Vivre quietly removes any leftover Vivre_Reboot_* helper services an earlier reboot left behind.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Wrench24, Title = "How do I put machines into WhatsUp Gold maintenance?",
            Keywords = "whatsup gold wug maintenance mode monitoring alerts suppress enter exit patching check state read",
            Lines =
            [
                "Select the machines (or none = all), right-click ▸ WhatsUp Gold maintenance….",
                "Pick Enter (suppress alerts) or Exit (resume) — the button label follows your choice — enter the WUG server + your WUG login, then click it. The Reason field only appears when you pick Enter (it's the note WUG records; Exit doesn't need one).",
                "The window closes right away and the work runs in the background: each machine's row shows progress, with a summary in the activity log — handy to silence monitoring while you patch, then resume after.",
                "It matches your machine names to WUG devices (by name, then by IP) and calls out any that didn't match a WUG device.",
                "Not sure it'll connect? Click Test connection first — it checks the server and your login. If the WhatsUp Gold PowerShell module isn't installed yet, an Install module button appears to add it for you.",
                "Want to see where things stand first? Right-click ▸ Check WhatsUp Gold state… (works from both the Health and Patching grids). The server is pre-filled from Settings (read-only) — enter your WUG login and click Check. It reads each in-scope machine's current WUG state without changing anything; results land per row in the Command result column, with a summary in the activity log. Unknown means the state couldn't be read — not that the machine is out of maintenance.",
            ],
            Tip = "Runs on this PC against the WUG server (not on the targets). The WUG login is asked each time and never saved (only the server address is remembered); the WhatsUpGoldPS module auto-installs for your user if it's missing.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Eye24, Title = "How do I see one machine's full details?",
            Keywords = "details window os operating system per machine double click",
            Lines =
            [
                "Double-click a row to open its Details window. Or right-click the machine ▸ Details….",
                "Shows its OS (caption + build), update state, reboot status, scheduled task, and that machine's messages.",
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.History24, Title = "How do I see a machine's history?",
            Keywords = "messages activity log history per machine",
            Lines =
            [
                "Right-click the machine ▸ Show messages — opens the activity log filtered to just that machine.",
                "Or open the full log any time: click Activity log in the status bar (bottom-right) to open the dock — search by machine or text.",
                "In the log, right-click a line ▸ Copy (or Copy all) to copy entries out — they paste as tab-separated time / machine / message.",
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Copy24, Title = "How do I copy info out (e.g. to Excel)?",
            Keywords = "copy export clipboard names rows online offline",
            Lines =
            [
                "Right-click ▸ Copy ▸, then choose what to copy:",
                "• The clicked row's Update message / Reboot message / Command result / Last error.",
                "• Name(s) or Selected rows (name, online state, site code, agent version, status, last error, command result) for the current selection.",
                "• All online devices / All offline devices.",
                "Everything is copied newline-separated, ready to paste into Excel.",
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Delete24, Title = "How do I remove machines from a tab?",
            Keywords = "remove delete offline clear",
            Lines =
            [
                "Remove Offline drops every offline (red) machine from the tab.",
                "Or select machines and press Delete (it confirms for large selections).",
            ],
            Tip = "Removing only takes them out of this tab — they stay in any saved list, so you can re-load them.",
        },

        // ---------------- Windows Update view ----------------
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.ArrowSwap24, Title = "How do I get to Windows Update / Patching?",
            Keywords = "switch update mode view patching fleet section nav",
            Lines =
            [
                "Click Fleet ▸ Patching in the left navigation (or press Ctrl+M from Health). The grid swaps to the patch columns and the patch actions appear.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Tabs24, Title = "Where do a machine's updates and the activity log show?",
            Keywords = "bottom panel dock tab activity updates close hide collapse",
            Lines =
            [
                "Both share one panel at the bottom of the window. It opens ONLY via the status-bar toggle (bottom-right) — labelled \"Updates & Activity\" in Patching, \"Activity log\" in Health.",
                "• Updates — shows the clicked machine's Applicable / Installed updates, with a filter box and All / None.",
                "• Activity — the shared log.",
                "While the panel is open, clicking a machine switches it to that machine's Updates tab. Drag the handle on the panel's top edge (the divider bar) to resize it.",
                "Click Close (top-right of the panel) to dismiss it and hand the grid back the full height.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Settings24, Title = "How do I choose the update source?",
            Keywords = "source windows microsoft update wsus managed drivers exclude",
            Lines =
            [
                "Click Update options ▾ in the command bar (visible in Patching mode) ▸ Source ▸ Windows Update / Microsoft Update / Managed (WSUS/SCCM).",
                "Update options ▾ ▸ Include drivers to also scan/install drivers (off by default).",
                "Update options ▾ ▸ Exclude updates… to skip any update whose title contains your terms (e.g. \"SQL\").",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Search24, Title = "How do I scan for updates?",
            Keywords = "scan find applicable available count",
            Lines =
            [
                "Select machines (or none = all), then click Scan all — or Scan (N) with a selection (right-click: Scan selected (N)).",
                "Each row's Status chip and Windows update message show what was found. To see a machine's update list, open the bottom panel (the Updates & Activity toggle, bottom-right) and click the machine; a never-scanned machine's Updates tab offers a \"Scan this machine\" button.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.CheckboxChecked24, Title = "How do I pick which updates to install?",
            Keywords = "select choose checklist applicable tick tab bottom panel filter size catalog mb download",
            Lines =
            [
                "Open the bottom panel (the Updates & Activity toggle, bottom-right), then click a machine — the panel shows its updates. On the Applicable tab, tick/untick the updates you want.",
                "Use All / None to select quickly, or the filter box to find an update by KB or title.",
                "Updates come back ticked after a scan — untick what you don't want. Untick everything and that machine is skipped (\"No updates selected\"). A machine that's never been scanned installs everything applicable.",
                "The Size column shows each update's download size — the size Windows reports, the same as BatchPatch. Once in a while Windows reports a wildly wrong size for a big cumulative update; when that happens Vivre shows the real download size from Microsoft's update catalog instead. A dash (—) means no size was available — usually one of those big updates on a machine that can't reach the catalog (offline or locked-down). It never blocks installing.",
                "After an install, updates that landed show a session chip (\"Installed\" / \"Installed — reboot pending\") and the header counts \"N installed this session\"; a red banner calls out partial failures, and while a reboot is pending an amber banner shows and Install checked is disabled until the restart.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.ArrowDownload24, Title = "How do I install updates?",
            Keywords = "install patch download progress reboot pending pre-flight winrm",
            Lines =
            [
                "Select machines, then click Install all — or Install (N) with a selection (the confirm reads \"Install on N\"). Or Ctrl+Enter.",
                "If any target already has a reboot pending, Install warns first and offers to reboot those before installing — a pending reboot can jam WinRM and fail the install.",
                "Progress shows live download then install percent; the chip turns green (Done) or amber (reboot pending).",
                "A required reboot is reported, not forced — reboot when you're ready.",
                "A box whose OS build is unknown is skipped with \"Unknown OS build — run Check Vitals first so Vivre can pick the right update lane.\"",
                "Settings ▸ Max simultaneous installs bounds how many boxes install at once (default 50).",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Subtract24, Title = "How do I uninstall updates?",
            Keywords = "uninstall remove dism installed scope",
            Lines =
            [
                "1. Click a machine to open its Updates tab, switch to the Installed tab, and Scan.",
                "2. Tick the updates to remove (greyed rows can't be removed by any engine), then click Uninstall checked.",
                "Vivre uses Windows Update's remover first, then DISM.",
            ],
            Tip = "Cumulative & servicing-stack updates are permanent — Windows refuses to remove them (error 0x800F0825). That's expected, not a bug.",
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.CalendarClock24, Title = "How do I schedule an install or reboot for later?",
            Keywords = "schedule task later time install reboot cancel one-time time zone timezone utc azure your time",
            Lines =
            [
                "Select machines, right-click ▸ Schedule ▸, then:",
                "• Install updates… — pick a date/time to install.",
                "• Reboot… — pick a date/time to force-reboot.",
                "• Cancel scheduled task — clears a pending one; the row then reads 'Scheduled task cancelled' until its next action ('Cancel had errors' if the target reported problems removing it). If the machine couldn't actually remove the task, the row KEEPS its Scheduled marker and reads 'Cancel failed — task may still fire', so you never mistake a failed cancel for a cancelled task.",
                "The time you pick is YOUR local time (this PC's). Every selected machine runs at that same moment — a box in another time zone (e.g. a UTC cloud VM) still fires at your chosen instant, not its own local clock.",
                "A scheduled task shows as a neutral 'Scheduled' status pill and a '<action> scheduled for <time> (your time)' message in the Windows update row; the Scheduled filter chip lists those machines. It clears once the time passes.",
            ],
            Tip = "Scheduling registers a one-time task that runs as SYSTEM, so it needs admin rights on the target.",
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Color24, Title = "How do I read the status chips and progress?",
            Keywords = "chip color status progress bar meaning idle scanning done error reach windows update retry transient",
            Lines =
            [
                "The Status chip colour: grey = idle · blue = working (scanning/downloading/installing) · accent = updates available · amber = reboot pending · green = up to date (\"Done\" in Health) · red = error. 2016 boxes also show Staging / Cleaning up / Rebooting / Cleaned pills.",
                "During install the Progress bar fills (download is the first half, install the second).",
                "When an operation finishes while you're watching, a banner summarises how many succeeded / failed — and if the window isn't focused, a Windows tray notification announces it instead.",
                "\"Can't reach WU\" (red): the machine couldn't reach Windows Update — usually a brief network blip. Vivre quietly retries a few times first (you'll see \"Couldn't reach Windows Update — retrying (N/3)…\"); if it still can't get through it stops and tells you, rather than wrongly showing the box as \"Up to date.\" Just run Scan/Install again once the network settles.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.ArrowClockwiseDashes24, Title = "Reboot and verify after patching",
            Keywords = "reboot verify wave confirm rescan ubr build 2016 wua outcome back online remaining up to date installed",
            Lines =
            [
                "After installing updates, right-click the machine(s) and choose Reboot & verify…, then confirm the list — nothing reboots without your confirm.",
                "1. Select the machines you want to reboot.",
                "2. Right-click ▸ Reboot & verify…",
                "3. Review the list and click Reboot & verify to start.",
                "Reboot & verify… appears only in Patching, on rows that are actually reboot-pending — if you don't see it, the box has nothing pending.",
                "Each box is rebooted gracefully (lets SQL/services flush). If it doesn't go down within 8 minutes (20 for a staged-2016 box, whose commit is slower) Vivre escalates to a forced reboot to complete the one you ordered.",
                "While offline, Vivre keeps watching — the row shows how long it's been down, flagging \"Overdue\" past ~90 minutes. After ~4½ hours it stops live-tracking and marks the row red (\"hasn't returned after N min — no longer tracking it live. Use Verify once it's back up.\"); a box that never goes down after the forced reboot is also marked red rather than watched forever.",
                "When a box comes back online:",
                "• Server 2016 boxes flagged for staged patching: the build number (UBR) is read over DCOM and compared to the expected value. If it matches, the update committed — the row turns green. A rolled-back UBR is caught and shown as failed (red). A non-flagged 2016 box verifies via the normal re-scan like any other box.",
                "• All other boxes: Vivre re-scans Windows Update (read-only — no installs, no further reboots). The outcome reads \"Back online · installed N · up to date\" (green), \"N remaining\" (if more updates apply), or \"couldn't rescan\" if the scan didn't complete. \"installed N\" only appears when an install in this session actually installed (or failed) something — a standalone reboot never claims \"installed 0\".",
                "Outcomes at a glance:",
                "  Back online · installed N · up to date — fully patched.",
                "  Back online · installed N · N remaining — more updates still apply; run Install again.",
                "  Back online · installed N · M failed (· R remaining) — some updates failed; see the activity log.",
                "  Installed N · up to date — the install needed no reboot at all.",
                "  Back online · reboot still pending — re-check — another reboot is needed; run again when ready.",
                "  Back online · couldn't rescan — the re-scan didn't complete; use Scan to re-check.",
                "  Back online · couldn't confirm reboot state — the reboot-pending check didn't answer (it's bounded at ~2 minutes); the Pending Reboot column shows \"?\" — re-check when convenient. Never shown as \"up to date\".",
                "If a box outlasts the live watch, re-check it once it's back up — that's exactly what the red no-longer-tracking row is asking for. For a Server 2016 box, click Verify in the 2016 action bar; for any other box, select it and click Scan (or use \"Scan this machine\" in its Updates tab) to confirm it landed.",
            ],
            Tip = "Reboot & verify reboots ONLY the machines you select and confirm. It never touches the rest of the fleet. To reboot without a post-reboot rescan, use right-click ▸ Reboot (force now).",
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Box24, Title = "Patch a Windows Server 2016 box",
            Keywords = "2016 14393 cumulative cu lcu stage reboot wave verify kb msu catalog",
            Lines =
            [
                "Server 2016 boxes get their monthly OS update through their own lane (regular Windows Update chronically fails on them). Vivre finds them for you:",
                "1. Check Vitals on your machines — any confirmed 2016 box makes a \"Server 2016 (N)\" filter chip appear.",
                "2. Click the chip: the grid filters to those boxes and the 2016 action bar shows the month's CU (e.g. KB5094122 / 9234).",
                "   Stage and Verify act only on boxes you've marked for staged patching (right-click a row ▸ Mark as Staged patching — see \"staged vs direct\" below). If none are marked, the button shows a reminder instead of doing nothing. Clean up is different — it works on any 2016 box: the selected ones, or all of them when nothing's selected.",
                "3. Stage (daytime, safe): delivers + installs the CU but does NOT reboot. The row turns amber with \"STAGED — needs Reboot Wave\". Boxes must have been scanned this session — otherwise Stage shows a \"Scan before staging\" reminder first.",
                "4. Reboot Wave (night): select the staged boxes, click, and confirm. Each reboots and is tracked until its build confirms the update took.",
                "5. Verify (any time): re-checks a box's build — use it on anything that came back late.",
                "Why Clean up? Windows Server 2016 accumulates old update pieces in a hidden store over time. Clean up tells Windows to clear that backlog — it speeds up normal Windows Update on any 2016 box and makes room before you stage a CU (without it, staging can stall or hang). It acts on the selected 2016 boxes, or all of them when none are selected; it never reboots the box and is safe to run any time.",
                "Clean up on a backlogged box can run for hours. The row shows a live \"Cleaning — 12m\" readout (with a percent when Windows reports one) so you can see it's still working even while the percent sits still; it only flags \"looks stalled (may still be working)\" or \"still going, check the box\" as a heads-up — it won't give up on a working box. When it finishes you'll see one of these results: \"Cleaned — ready to Stage\" (green, good to go), \"Cleaned — reboot-pending\" (amber — reboot before you Stage), \"Cleaned · locked files (see log)\" (green — the backlog cleared, but security software held some files open so Windows couldn't remove the rest; staging isn't blocked, and the activity log explains how to reclaim it), or, if the box already has a reboot waiting, \"Couldn't clean up — reboot to clear the pending state first\" (it didn't run, to avoid clashing with Windows servicing).",
            ],
            Tip = "Set the month's KB + target UBR in Settings ▸ Server 2016 cumulative update (the card also holds the update-history URL — copy this month's KB/UBR from it — and the architecture), and drop the Catalog .msu in the CU package folder before staging.",
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Box24, Title = "Patch a Windows Server 2016 box — staged vs direct",
            Keywords = "2016 14393 staged direct flag mark dism windows update decision dialog minor updates cumulative cu",
            Lines =
            [
                "Most Server 2016 boxes patch fine through normal Windows Update — that's the default now, the same as a 2019 or 2022 box. Only the boxes whose monthly cumulative update keeps failing through Windows Update need the staging lane.",
                "What the Staged flag means: a box you mark as \"Staged patching\" gets its monthly cumulative update delivered and installed with the full package (DISM) instead of through Windows Update. Flag only the boxes that need it.",
                "1. Flag a box: right-click its row in a Patching tab ▸ Mark as Staged patching. A small \"Staged\" pill appears on the row. To undo, right-click ▸ Remove Staged flag.",
                "2. Click Install (or Install all). If the run includes a flagged box whose cumulative update isn't staged yet, Vivre asks what to do (it also warns when the Settings KB doesn't match what the scan actually found):",
                "   • Stage CU first (recommended) — delivers + stages this month's cumulative update on those boxes (large, ~30–60 min); you commit it later with the Reboot Wave. Other machines in the run install normally at the same time.",
                "   • Install minor updates only — installs everything except the cumulative update now (the CU is staged separately). You'll be reminded that minor updates may need a reboot.",
                "   • Cancel — skips just those flagged boxes for now; the rest of the run still installs.",
                "3. A box already at this month's build is detected up front and skipped automatically (\"Already current — skipped\") — it just installs its minor updates.",
                "Manage your flagged boxes in Settings ▸ Staged patching machines: see the whole list, remove a box (sends it back to normal Windows Update), or Clear all.",
            ],
            Tip = "Set this month's KB + target UBR in Settings ▸ Server 2016 cumulative update before staging or using \"Install minor updates only\" — both need to know which update is the cumulative one. Flagging is remembered between sessions.",
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Box24, Title = "A flagged 2016 box with only small updates installs them normally",
            Keywords = "staged 2016 flagged minor updates no cumulative cu install office defender dotnet needs staging dialog skip everyday",
            Lines =
            [
                "Marking a Server 2016 box for staged patching only changes how its monthly cumulative update is delivered — it doesn't hold back the box's other updates.",
                "So if you click Install on a flagged box that has no cumulative update waiting after a scan this session (just smaller things like Office, Defender or .NET), Vivre installs those normally through Windows Update — it won't stop to ask you to stage anything. (An unscanned flagged box still asks, to be safe.)",
                "You're only asked to Stage (or to choose \"Install minor updates only\") when a flagged box actually has this month's cumulative update pending.",
            ],
            Tip = "Flagging a box is safe: it never blocks that box's everyday updates — it only routes the monthly cumulative update through the staging lane when one is due.",
        },

        // ---------------- Tips & shortcuts ----------------
        new HelpTopic
        {
            Category = Tips, Icon = SymbolRegular.Keyboard24, Title = "Keyboard shortcuts",
            Keywords = "keyboard shortcuts hotkeys accelerators ctrl f5 f2",
            Lines =
            [
                "• Ctrl+T new tab · Ctrl+W close tab · F2 rename tab · Ctrl+L focus the add box.",
                "• Ctrl+M toggle Fleet ▸ Health / Patching · F5 Ping All · Ctrl+Enter Install.",
                "• Shift+F10 (or the Menu key) opens the right-click menu from the keyboard.",
                "• Delete removes the selected machines · Ctrl+C copies the selected rows.",
                "• F1 opens this guide.",
            ],
        },
        new HelpTopic
        {
            Category = Tips, Icon = SymbolRegular.Cursor24, Title = "Getting around faster",
            Keywords = "tips selection multi-select right click count",
            Lines =
            [
                "• Right-click is the hub — Reboot, Schedule, Details, Run script, Client actions, Enable WinRM all live there.",
                "• Select multiple machines with Ctrl+click / Shift+click; the bottom bar shows the selected count.",
                "• Status dots use colour AND shape (✓ / ✕ / ?), so they're readable at a glance.",
                "• Buttons and actions show what they'll target — \"Install on 12\", \"Scan selected (3)\".",
            ],
        },

        // ---------------- Troubleshooting ----------------
        new HelpTopic
        {
            Category = Trouble, Icon = SymbolRegular.Warning24, Title = "\"Online · no ConfigMgr client\"",
            Keywords = "no configmgr client sccm not installed health",
            Lines =
            [
                "The machine is reachable but has no SCCM client, so client actions and SCCM health won't apply.",
                "Ping/Run Script/reboot/Windows Update still work (those don't need the SCCM client).",
            ],
        },
        new HelpTopic
        {
            Category = Trouble, Icon = SymbolRegular.PlugDisconnected24, Title = "A machine won't respond to remote actions",
            Keywords = "winrm unreachable access denied credentials cannot connect",
            Lines =
            [
                "• Try right-click ▸ Enable WinRM (PSRemoting)… — it works over DCOM, a different channel — to turn on remoting.",
                "• Set an admin account in Settings (left navigation) ▸ Remote credentials if your login can't reach the target.",
                "• Confirm the box is online (Ping All) and not blocked by firewall.",
            ],
        },
        new HelpTopic
        {
            Category = Trouble, Icon = SymbolRegular.PlugDisconnected24, Title = "\"WinRM temporarily unavailable\"",
            Keywords = "winrm unhealthy temporarily unavailable reboot pending stuck initialsessionstate shells maxshellsperuser",
            Lines =
            [
                "A WinRM shell couldn't start on that machine. This is usually transient — the box was momentarily busy, or too many remoting shells were open at once — and it clears on its own; Vivre backs off and retries automatically.",
                "Vivre won't tell you to reboot a healthy box. If the machine genuinely is reboot-pending, the row already shows the \"Reboot pending\" pill (and offers Reboot & verify) — that's the only case a reboot is the fix.",
            ],
        },
        new HelpTopic
        {
            Category = Trouble, Icon = SymbolRegular.Prohibited24, Title = "An update \"could not be removed\"",
            Keywords = "uninstall failed 0x800F0825 permanent cumulative servicing stack",
            Lines =
            [
                "Cumulative and servicing-stack updates are permanent — Windows blocks removal (error 0x800F0825).",
                "This is by design, on any tool. The per-update reason is shown so you can tell it apart from a real failure.",
            ],
        },
        new HelpTopic
        {
            Category = Trouble, Icon = SymbolRegular.Clock24, Title = "An install seems stuck / \"No response\"",
            Keywords = "stuck hung no response frozen downloading timeout dead session",
            Lines =
            [
                "A genuinely slow update keeps sending heartbeats and is fine — leave it.",
                "If the session goes fully silent (~90s, no heartbeat), Vivre flags \"No response\" — the box dropped or hung.",
                "Hit Stop, then re-scan once it's reachable to see its true state.",
            ],
        },

        // ---------------- Cross-Domain RDP ----------------
        new HelpTopic
        {
            Category = CrossDomainRdp, Icon = SymbolRegular.Desktop24, Title = "What is Cross-Domain RDP?",
            Keywords = "rdp remote desktop cross domain sessions tree hosts folders",
            Lines =
            [
                "Cross-Domain RDP is an embedded remote-desktop manager built into Vivre.",
                "• Open it from Cross-Domain RDP in the left navigation.",
                "• A folder tree of hosts on the left; live, tabbed RDP sessions on the right.",
                "• Sessions stay connected when you switch to another section and back.",
            ],
            Tip = "It's for hosts on other domains — Vivre hands the saved credentials straight to the session.",
        },
        new HelpTopic
        {
            Category = CrossDomainRdp, Icon = SymbolRegular.Folder24, Title = "How do I add a folder or host (and save its login)?",
            Keywords = "add folder host credentials password domain username save dpapi inherit move drag",
            Lines =
            [
                "1. Select where it should go (a folder to nest inside, or nothing for the top level).",
                "2. Click \"Folder\" or \"Host\" and fill in the details.",
                "3. For a host, enter the Server, then a Domain / Username / Password to log in with.",
                "4. Leave a host's Username blank to inherit the folder's credentials instead.",
                "Right-click an item for Edit / Remove / Connect; drag a host or folder to move it.",
            ],
            Tip = "Passwords are encrypted with Windows DPAPI (your Windows account, this PC) in %APPDATA%\\Vivre\\rdpcreds.json — they don't roam to other users or machines.",
        },
        new HelpTopic
        {
            Category = CrossDomainRdp, Icon = SymbolRegular.Desktop24, Title = "How do I connect, go full-screen, and disconnect?",
            Keywords = "connect double click full screen disconnect reconnect dropped session tab",
            Lines =
            [
                "Connect: select a host and click Connect, or just double-click it — it opens a session tab on the right.",
                "Switch sessions with the tabs above the remote view; switch to a machine tab and back without dropping them.",
                "Full screen: click Full screen (Ctrl+Alt+Break toggles back out).",
                "Disconnect: click the ✕ on a session tab, or select it and click Disconnect.",
                "Dropped connection: if a session drops (network blip, server reboot, idle timeout) it stays open as disconnected — click Reconnect to rebuild it. Signing out inside the remote closes its tab automatically.",
            ],
        },
    ];
}
