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
    public const string Machines = "Machines view";
    public const string Updates = "Windows Update view";
    public const string Tips = "Tips & shortcuts";
    public const string Trouble = "Troubleshooting";

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
                "• A tab has two views: Machines (health & actions) and Windows Update (patching).",
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
                "Quick add: type a name in the \"Add computer…\" box (top-right) and press Enter.",
                "A list: click the Paste button (or File ▸ Paste computers…) and paste names, one per line.",
                "A saved list: File ▸ Open list ▸ pick one.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Save24, Title = "How do I save and reuse a machine list?",
            Keywords = "named lists save open delete",
            Lines =
            [
                "1. Load the machines you want in the tab.",
                "2. File ▸ Save tab as list… and give it a name.",
                "3. Later, File ▸ Open list to load it into a tab; File ▸ Delete list to remove a saved one.",
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
                "• Close: the \"✕\" on the tab, or Ctrl+W (it asks first if the tab has machines or a running op).",
                "• Right-click a tab for Close other tabs / Close tabs to the right (browser-style).",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Person24, Title = "How do I set the account Vivre uses?",
            Keywords = "credentials login password settings admin remote",
            Lines =
            [
                "1. Settings ▸ Credentials….",
                "2. Keep \"Use my Windows login\", or pick \"Use these credentials\" and enter Domain / Username / Password.",
                "These are used for all remote actions (health checks, Run Script, reboots, patching, Enable WinRM).",
            ],
            Tip = "Credentials are held in memory for the session only — never written to disk. Set an admin account here if your login can't reach the targets.",
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.ArrowSwap24, Title = "How do I switch between Machines and Windows Update?",
            Keywords = "mode view toggle switch update",
            Lines =
            [
                "Use the View menu ▸ Machines or Windows Update (or press Ctrl+M to toggle).",
                "The setting is per-tab, so different tabs can be in different views.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.PaintBrush24, Title = "How do I change the theme?",
            Keywords = "dark light system appearance color",
            Lines =
            [
                "Settings ▸ Theme ▸ Light / Dark / System. Your choice is remembered across launches.",
            ],
        },
        new HelpTopic
        {
            Category = GettingStarted, Icon = SymbolRegular.Filter24, Title = "How do I filter or find machines in a big list?",
            Keywords = "filter search find chip errors reboot pending offline done updates available subset narrow",
            Lines =
            [
                "Use the filter bar above the grid (works in both Machines and Windows Update views).",
                "• Type in the search box to show only machines whose name contains that text.",
                "• Click a chip to show only a state: Updates available · Reboot pending · Errors · Offline · Done. Click All to clear.",
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
                "File ▸ Export to CSV… saves the rows currently shown (it respects the filter) to a CSV.",
                "Columns: machine, online, status, updates available, update/reboot messages, last error, OS, and any scheduled task.",
                "Opens cleanly in Excel.",
            ],
            Tip = "Filter first to scope the report — e.g. filter to Done and export to record exactly what got patched this window.",
        },

        // ---------------- Machines view ----------------
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.ArrowSync24, Title = "How do I check if machines are online?",
            Keywords = "ping reachable online offline status dot",
            Lines =
            [
                "Click Ping All (or F5). Use the Ping All dropdown ▸ Ping Offline only to re-check just the dark ones.",
                "The Ping dot shows: green ✓ online · red ✕ offline · grey ? not checked yet.",
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
            ],
            Tip = "Triggering health/actions usually needs admin rights on the target — set credentials in Settings if you see \"access denied\".",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Pulse24, Title = "How do I see why a machine is sick? (Vitals)",
            Keywords = "vitals vitality score health disk memory cpu uptime stopped services event log unhealthy triage",
            Lines =
            [
                "Click Check Vitals — the same button that pulls SCCM client health also reads deep OS health: system-drive free space, memory and CPU load, uptime, plus (for context) stopped auto-start services and recent Critical/Error events.",
                "It rolls the reliable signals (disk, memory, CPU, uptime, reboot-pending) into a 0–100 vitality score shown as a coloured chip in the Vitals column: green = Healthy (80+), amber = Warning (50–79), red = Critical (under 50). Offline/Unknown show grey.",
                "Stopped services and event counts are shown for triage but NOT scored — they're too noisy (idle-by-design services, benign errors like DCOM 10016) to trust on their own.",
                "Hover the chip to see why (the top reasons), or right-click ▸ Details… ▸ Vitals for the full breakdown — drives, services by name, and recent events. The bottom bar tallies the fleet, e.g. \"Vitals: Healthy 40 · Warning 6 · Critical 2\".",
                "Use the Unhealthy filter chip to show just the Warning/Critical/Offline machines, then sort by the Vitals column to put the sickest first.",
            ],
            Tip = "Read-only — one click, no confirm. Reading services / the event log over WinRM needs admin rights on the target; anything it can't read is skipped rather than counted against the score.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Wrench24, Title = "How do I fix a sick machine? (triage actions)",
            Keywords = "fix repair remediate triage start service free disk space cleanup end kill process heal",
            Lines =
            [
                "Open right-click ▸ Details… ▸ Vitals. Once you've run Check Vitals, the breakdown lets you act in place:",
                "• Stopped auto-start service — click Start next to it to start it now.",
                "• Low drive — click Free up space… to clear TEMP, the Windows Update cache, and the recycle bin (asks first; reports how much it reclaimed).",
                "• A hog — click Load under Top processes, then End next to a runaway one (asks first).",
                "After each action Vivre re-checks that machine's vitals, so the score and readings update on the spot.",
            ],
            Tip = "Free up space and End process change the machine, so they confirm first; starting a service is one-click. All actions use your session/admin credential and land in the activity log.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Pulse24, Title = "How do I keep statuses live?",
            Keywords = "monitor watch continuous auto refresh",
            Lines =
            [
                "Turn on the Monitor toggle — Vivre re-checks online/offline for every machine on a timer.",
                "Click Stop to halt it (and any other running operation in the tab).",
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Play24, Title = "How do I run a PowerShell script?",
            Keywords = "run script powershell saved library command",
            Lines =
            [
                "1. Select the machines.",
                "2. Right-click ▸ Run script… (opens the Run Script window).",
                "3. Pick a saved script (grouped by category) or paste your own, review it, then click Run.",
                "Output lands per-machine in the Command result column and in the window's log.",
            ],
            Tip = "The window opens for review — it never auto-runs — so a stray click can't fire a script across your fleet.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Copy24, Title = "How do I stage software to machines (without installing)?",
            Keywords = "stage deploy software copy push package msi exe agent files folder script setup application install prep",
            Lines =
            [
                "1. Select the machines (or none = all), then right-click ▸ Stage software….",
                "2. Pick a package: a single .msi/.exe, or a folder of files (an installer plus your own install script).",
                "   • Pick from the package library dropdown, or Browse… to a file/folder anywhere. (Set package library folder… remembers a folder of packages for next time.)",
                "3. Set where to drop it (default C:\\Windows\\Temp\\VivrePackages). A single file lands right there; a folder gets its own subfolder so its files stay together.",
                "4. Click Stage. Vivre copies the files to each machine and reports the path per row (Command result): \"staged to C:\\Windows\\Temp\\VivrePackages\\…\".",
                "5. Install it your way — e.g. right-click ▸ Run script… pointing at the staged path (C:\\Windows\\Temp\\VivrePackages\\<package>\\install.ps1), or your normal batch / Company Portal flow.",
            ],
            Tip = "Vivre only delivers the files — it doesn't run them — so security agents (SentinelOne/CrowdStrike) that disrupt WinRM during an install can't break the copy, and you keep full control of the install. It copies over the fast SMB admin share (\\\\machine\\C$), falling back to WinRM (chunked + SHA-256 verified) where SMB is blocked. Either way it needs admin rights on the target.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Search24, Title = "How do I check what software is installed (e.g. is CrowdStrike on it)?",
            Keywords = "software installed check agent crowdstrike sentinelone version column present missing inventory product app",
            Lines =
            [
                "1. Select the machines (or none = all), then right-click ▸ Check software….",
                "2. Type a product name (or part of one) — e.g. CrowdStrike, SentinelOne, Chrome — or tap a quick-pick button, then Check (or press Enter).",
                "3. Optional: tick \"Also check its service is running\" to confirm the agent is actually live. The service name pre-fills (it's remembered per product — CrowdStrike → CSFalconService) — change it if needed.",
                "4. Vivre fills the Software column: green = installed (and running), amber = installed but its service isn't running, red = \"<name> — not found\".",
                "Click the Software column header to sort — e.g. the machines missing it (or amber) to the top.",
                "Need a report for your boss? After a check, right-click ▸ Export software report (CSV)… saves a per-machine CSV (machine, product, version, installed, service running). On-demand only — checking never writes a file by itself.",
            ],
            Tip = "It searches the registry's installed-programs list (fast, and won't trigger an MSI repair the way Win32_Product can), plus the service list when asked. Handy to confirm an agent actually landed AND is running after a deploy. Read-only — it never changes the machine.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Wrench24, Title = "How do I run an SCCM client action?",
            Keywords = "client action machine policy hardware inventory update scan trigger schedule",
            Lines =
            [
                "Select the machines, then right-click ▸ Client actions ▸ and pick one (Machine Policy, Hardware Inventory, Update Scan, …).",
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
            ],
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.ArrowClockwise24, Title = "How do I reboot machines now?",
            Keywords = "reboot restart force shutdown",
            Lines =
            [
                "Select the machines, right-click ▸ Reboot (force now)…, and confirm (it lists the count + names).",
                "Runs shutdown /r /f — any unsaved work on those machines is lost.",
            ],
            Tip = "To reboot at a set time instead, use right-click ▸ Schedule ▸ Reboot….",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Wrench24, Title = "How do I put machines into WhatsUp Gold maintenance?",
            Keywords = "whatsup gold wug maintenance mode monitoring alerts suppress enter exit patching",
            Lines =
            [
                "Select the machines (or none = all), right-click ▸ WhatsUp Gold maintenance….",
                "Pick Enter (suppress alerts) or Exit (resume) — the button label follows your choice — enter the WUG server + your WUG login, then click it.",
                "The window closes right away and the work runs in the background: each machine's row shows progress, with a summary in the activity log — handy to silence monitoring while you patch, then resume after.",
                "It matches your machine names to WUG devices (by name, then by IP) and calls out any that didn't match a WUG device.",
            ],
            Tip = "Runs on this PC against the WUG server (not on the targets). The WUG login is asked each time and never saved (only the server address is remembered); the WhatsUpGoldPS module auto-installs for your user if it's missing.",
        },
        new HelpTopic
        {
            Category = Machines, Icon = SymbolRegular.Eye24, Title = "How do I see one machine's full details?",
            Keywords = "details window os operating system per machine",
            Lines =
            [
                "Right-click the machine ▸ Details….",
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
                "Or open the full log any time: View ▸ Activity log (search by machine or text).",
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
                "• Name(s) or Selected rows (all columns) for the current selection.",
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
            Category = Updates, Icon = SymbolRegular.ArrowSwap24, Title = "How do I get to Windows Update?",
            Keywords = "switch update mode view patching",
            Lines =
            [
                "View ▸ Windows Update (or Ctrl+M). The grid swaps to the patch columns and the patch actions appear.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Tabs24, Title = "Where do a machine's updates and the activity log show?",
            Keywords = "bottom panel dock tab activity updates close hide collapse",
            Lines =
            [
                "Both share one panel at the bottom of the window, with tabs:",
                "• Updates — opens when you click a machine in Windows Update view; shows its Applicable / Installed updates, with a filter box and All / None.",
                "• Activity — the shared log (also opened by View ▸ Activity log).",
                "Click a machine to jump straight to its Updates tab. Drag the handle on the panel's top edge (the divider bar) to resize it.",
                "Click Close (top-right of the panel) to dismiss it and hand the grid back the full height.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Settings24, Title = "How do I choose the update source?",
            Keywords = "source windows microsoft update wsus managed drivers exclude",
            Lines =
            [
                "Updates menu ▸ Source ▸ Windows Update / Microsoft Update / Managed (WSUS/SCCM).",
                "Updates ▸ Include drivers to also scan/install drivers (off by default).",
                "Updates ▸ Exclude updates… to skip any update whose title contains your terms (e.g. \"SQL\").",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Search24, Title = "How do I scan for updates?",
            Keywords = "scan find applicable available count",
            Lines =
            [
                "Select machines (or none = all), then click Scan. To scan just one, click it and use the \"Scan this machine\" button in the Updates tab.",
                "Each row's Status chip and Windows update message show what was found; clicking a machine opens the Updates tab at the bottom, which lists that machine's updates.",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.CheckboxChecked24, Title = "How do I pick which updates to install?",
            Keywords = "select choose checklist applicable tick tab bottom panel filter",
            Lines =
            [
                "Click a machine — its Updates tab opens at the bottom. On the Applicable tab, tick/untick the updates you want.",
                "Use All / None to select quickly, or the filter box to find an update by KB or title.",
                "Install then targets only the ticked ones (or everything applicable if you don't tick anything).",
            ],
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.ArrowDownload24, Title = "How do I install updates?",
            Keywords = "install patch download progress reboot pending pre-flight winrm",
            Lines =
            [
                "Select machines, then click Install (it confirms the count first). Or Ctrl+Enter.",
                "If any target already has a reboot pending, Install warns first and offers to reboot those before installing — a pending reboot can jam WinRM and fail the install.",
                "Progress shows live download then install percent; the chip turns green (Done) or amber (reboot pending).",
                "A required reboot is reported, not forced — reboot when you're ready.",
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
            Keywords = "schedule task later time install reboot cancel one-time",
            Lines =
            [
                "Select machines, right-click ▸ Schedule ▸, then:",
                "• Install updates… — pick a date/time to install.",
                "• Reboot… — pick a date/time to force-reboot.",
                "• Cancel scheduled task — clears a pending one.",
                "The Scheduled task columns show what's queued and when; they clear once the time passes.",
            ],
            Tip = "Scheduling registers a one-time task that runs as SYSTEM, so it needs admin rights on the target.",
        },
        new HelpTopic
        {
            Category = Updates, Icon = SymbolRegular.Color24, Title = "How do I read the status chips and progress?",
            Keywords = "chip color status progress bar meaning idle scanning done error",
            Lines =
            [
                "The Status chip colour: grey = idle · blue = working (scanning/downloading/installing) · steel = updates available · amber = reboot pending · green = done · red = error.",
                "During install the Progress bar fills (download is the first half, install the second).",
                "When an operation finishes while you're watching, a banner summarises how many succeeded / failed.",
            ],
        },

        // ---------------- Tips & shortcuts ----------------
        new HelpTopic
        {
            Category = Tips, Icon = SymbolRegular.Keyboard24, Title = "Keyboard shortcuts",
            Keywords = "keyboard shortcuts hotkeys accelerators ctrl f5 f2",
            Lines =
            [
                "• Ctrl+T new tab · Ctrl+W close tab · F2 rename tab · Ctrl+L focus the add box.",
                "• Ctrl+M switch Machines / Windows Update · F5 Ping All · Ctrl+Enter Install.",
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
                "• Buttons and actions show what they'll target — \"Install on 12 machines\", \"Scan selected (3)\".",
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
                "• Try right-click ▸ Enable WinRM (over DCOM) to turn on remoting.",
                "• Set an admin account in Settings ▸ Credentials if your login can't reach the target.",
                "• Confirm the box is online (Ping All) and not blocked by firewall.",
            ],
        },
        new HelpTopic
        {
            Category = Trouble, Icon = SymbolRegular.PlugDisconnected24, Title = "\"WinRM unhealthy — reboot the target\"",
            Keywords = "winrm unhealthy reboot pending stuck initialsessionstate",
            Lines =
            [
                "A pending reboot has jammed that machine's remoting (a known Windows issue). Reboot the box to clear it.",
                "Vivre stops hammering it, re-tests every few minutes, and clears the message once it recovers.",
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
    ];
}
