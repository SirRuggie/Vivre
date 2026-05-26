using System.Management.Automation;
using System.Security;
using Vivre.Core.PowerShell;

// ---------------------------------------------------------------------------
// RemoteRun — dev runner for IPowerShellHost.RunRemoteAsync (Session 4 remote).
//
// Verifies WinRM remoting against a real target (e.g. NYC-FP1) by driving the
// PRODUCTION PSRunspaceHost. Not part of the shipped app.
//
//   dotnet run --project tools/RemoteRun -- <host> "<script>" [options]
//
// Options:
//   --user <DOMAIN\user>   authenticate as this account (prompts for password,
//                          masked). Omit to use your current Windows login.
//   --port <n>             WinRM port (default 5985 HTTP, 5986 for SSL).
//   --ssl                  connect over HTTPS.
//
// Examples:
//   dotnet run --project tools/RemoteRun -- NYC-FP1 "hostname; whoami"
//   dotnet run --project tools/RemoteRun -- NYC-FP1 "Get-Service WinRM" --user CONTOSO\admin
// ---------------------------------------------------------------------------

if (args.Length < 2 || args[0] is "-h" or "--help" or "/?")
{
    PrintUsage();
    return 2;
}

string host = args[0];
string script = args[1];

string? userName = null;
int port = 5985;
bool useSsl = false;

for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--user" when i + 1 < args.Length:
            userName = args[++i];
            break;
        case "--port" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out port))
            {
                Console.Error.WriteLine($"Invalid --port value: {args[i]}");
                return 2;
            }
            break;
        case "--ssl":
            useSsl = true;
            if (port == 5985)
            {
                port = 5986;
            }
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 2;
    }
}

PSCredential? credential = null;
if (userName is not null)
{
    SecureString password = ReadMaskedPassword($"Password for {userName}: ");
    if (password.Length == 0)
    {
        Console.Error.WriteLine("No password entered; aborting.");
        return 2;
    }

    credential = new PSCredential(userName, password);
}

string transport = useSsl ? "HTTPS" : "HTTP";
string identity = userName ?? "current Windows login";
Console.WriteLine($"Connecting to {host}:{port} ({transport}) as {identity} ...\n");

// Ctrl+C cancels the run cleanly via the host's CancellationToken plumbing.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n(cancelling...)");
};

var watch = System.Diagnostics.Stopwatch.StartNew();
try
{
    IPowerShellHost psHost = new PSRunspaceHost();
    PSExecutionResult result = await psHost.RunRemoteAsync(host, script, credential, port, useSsl, cts.Token);
    watch.Stop();

    Console.WriteLine($"--- output ({result.Output.Count} object(s), {watch.ElapsedMilliseconds} ms) ---");
    foreach (PSObject o in result.Output)
    {
        Console.WriteLine("  " + o);
    }

    if (result.Warnings.Count > 0)
    {
        Console.WriteLine("\n--- warnings ---");
        foreach (string w in result.Warnings)
        {
            Console.WriteLine("  WARN : " + w);
        }
    }

    if (result.Errors.Count > 0)
    {
        Console.WriteLine("\n--- errors ---");
        foreach (string err in result.Errors)
        {
            Console.WriteLine("  ERR  : " + err);
        }
    }

    Console.WriteLine($"\nHadErrors = {result.HadErrors}");
    // Connectivity succeeded even if the script itself wrote errors — that's the
    // thing we're verifying. Non-zero exit only when the engine flagged errors.
    return result.HadErrors ? 1 : 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    watch.Stop();
    Console.Error.WriteLine($"\nFAILED after {watch.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
    {
        Console.Error.WriteLine($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    PrintTroubleshooting(host, useSsl, userName is not null);
    return 1;
}

static SecureString ReadMaskedPassword(string prompt)
{
    Console.Write(prompt);
    var secure = new SecureString();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (secure.Length > 0)
            {
                secure.RemoveAt(secure.Length - 1);
                Console.Write("\b \b");
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            secure.AppendChar(key.KeyChar);
            Console.Write('*');
        }
    }

    secure.MakeReadOnly();
    return secure;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        RemoteRun — try PowerShell remoting against a host.

          dotnet run --project tools/RemoteRun -- <host> "<script>" [options]

        Options:
          --user <DOMAIN\user>   authenticate as this account (prompts for password)
          --port <n>             WinRM port (default 5985, or 5986 with --ssl)
          --ssl                  connect over HTTPS

        Examples:
          dotnet run --project tools/RemoteRun -- NYC-FP1 "hostname; whoami"
          dotnet run --project tools/RemoteRun -- NYC-FP1 "Get-Service WinRM" --user CONTOSO\admin
        """);
}

static void PrintTroubleshooting(string host, bool useSsl, bool explicitCreds)
{
    Console.Error.WriteLine(
        $"""

        Troubleshooting:
          • Is WinRM running/enabled on {host}?  (on the target: Enable-PSRemoting -Force)
          • Reachable on the port?               Test-NetConnection {host} -Port {(useSsl ? 5986 : 5985)}
          • Quick baseline from PowerShell:      Test-WSMan {host}
        """);

    if (explicitCreds && !useSsl)
    {
        Console.Error.WriteLine(
            $"""
          • Explicit creds over HTTP often use NTLM. If you connect by hostname/IP or the
            target isn't domain-joined, add it to the client's TrustedHosts:
                Set-Item WSMan:\localhost\Client\TrustedHosts -Value {host} -Concatenate -Force
            or connect with --ssl (5986) using a cert on the target.
        """);
    }
}
