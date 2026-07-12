using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Vivre.Core.Logging;

namespace Vivre.Desktop;

// =====================================================================================================
// THROWAWAY DIAGNOSTIC — smoke-round-2 RDP drag-freeze instrument. REMOVE BEFORE THIS BRANCH MERGES
// (delete this file and strip every `_instr` line from RdpSessionView.xaml.cs).
//
// Discriminates the two live freeze mechanisms (see docs/vivre-rdp-scaling-and-fcm-findings.md):
//   (a) UI-thread hard block — a synchronous COM call into the STA OCX parks the UI thread.
//       Signal: [RDP uithread] gapMs grows large (the UI ticker stopped stamping while the
//       BACKGROUND sampler kept measuring).
//   (b) lost button-up / stuck capture — the OS says the button is UP while WPF still believes it
//       is DOWN. Signal: physBtn=0 wpfBtn=1 with a SMALL gapMs (UI alive, input state wedged).
//       physBtn and wpfBtn are DIFFERENT quantities; their disagreement IS the (b) signal.
//
// Approach reused from the cold-start freeze hunt (docs/cold-start-freeze-and-threadpool-findings.md):
// the watchdog samples FROM A BACKGROUND THREAD, so it keeps measuring even while the UI thread is
// dead. Lines go through IActivityLog.Info — the Serilog file write happens synchronously on the
// CALLING thread and the timestamp is stamped at call time, so the rolling file gets watchdog lines
// in real time during a block; the activity-panel copies appear when the dispatcher unblocks.
// Info-level on purpose: the operator runs a RELEASE publish build (Debug.WriteLine compiles out —
// the closed settings-save audit MED). Output is threshold-gated so one freeze produces a short
// pasteable block, not thousands of lines.
// =====================================================================================================
internal sealed class RdpFreezeInstrument
{
    private const int SampleMs = 250;        // UI ticker + background sampler cadence
    private const int GapAnomalyMs = 500;    // UI-thread gap that counts as blocked
    private const int ComReportMs = 50;      // COM calls slower than this get their own line

    private IActivityLog? _log;
    private string _title = "?";

    private readonly System.Windows.Threading.DispatcherTimer _uiTicker;
    private System.Threading.Timer? _watchdog;
    private HwndSource? _hwndSource;

    // Stamped on the UI thread every tick; read from the watchdog thread. wpfBtn/captured are
    // "as of the LAST COMPLETED UI tick" — during a hard block they are the last state WPF believed.
    private long _lastUiTickMs = Environment.TickCount64;
    private volatile bool _wpfBtn;    // WPF's message-derived button state (any of L/R/M pressed)
    private volatile bool _captured;  // Win32 GetCapture() != 0 on the UI thread (WPF, WinForms, or OCX)
    private volatile bool _modal;     // inside WM_ENTERSIZEMOVE .. WM_EXITSIZEMOVE

    // Watchdog bookkeeping (touched only on the background sampler thread).
    private bool _inBlock;
    private long _blockStartMs;
    private long _lastBlockLineMs;
    private int _mismatchStreak;
    private bool _inMismatch;
    private long _mismatchStartMs;

    // Modal-loop COM counters (touched only on the UI thread, where all timed calls run).
    private long _modalEnterMs;
    private int _modalComCalls;
    private long _modalComMsTotal;

    public RdpFreezeInstrument()
    {
        // Normal priority: a gap at this priority means the dispatcher could not run ANY normal work —
        // a genuine stall, not background starvation.
        _uiTicker = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(SampleMs),
        };
        _uiTicker.Tick += OnUiTick;
    }

    public void Start(IActivityLog log, string title, Window? window)
    {
        _log = log;
        _title = title;
        Volatile.Write(ref _lastUiTickMs, Environment.TickCount64);
        _uiTicker.Start();
        _watchdog = new System.Threading.Timer(OnWatchdogSample, null, SampleMs, SampleMs);

        if (window is not null && PresentationSource.FromVisual(window) is HwndSource source)
        {
            _hwndSource = source;
            _hwndSource.AddHook(WndProc);
        }

        Emit("[RDP instrument] armed (THROWAWAY smoke-round-2 build)");
    }

    public void Stop()
    {
        _uiTicker.Stop();
        _watchdog?.Dispose();
        _watchdog = null;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    /// <summary>Times a synchronous OCX touch on the UI thread. Reports when it exceeds the threshold
    /// (including when the call THREW — the stopwatch reports from finally, so a slow failing call is
    /// still captured) and feeds the modal-loop counters.</summary>
    public T Timed<T>(string call, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            ReportTimed(call, stopwatch.ElapsedMilliseconds);
        }
    }

    public void Timed(string call, Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            ReportTimed(call, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>For block timings that can't wrap in a lambda (nullable-flow) — same reporting path.</summary>
    public void ReportTimed(string call, long ms)
    {
        if (_modal)
        {
            _modalComCalls++;
            _modalComMsTotal += ms;
        }

        if (ms > ComReportMs)
        {
            Emit($"[RDP com] call={call} ms={ms} modal={Bit(_modal)} physBtn={Bit(IsPhysButtonDown())}");
        }
    }

    /// <summary>An engine action that EXECUTED an OCX mutation (not one that deferred) — records the
    /// modal and PHYSICAL button state at the instant of the call.</summary>
    public void RecordLanded(string action) =>
        Emit($"[RDP landed] action={action} modal={Bit(_modal)} physBtn={Bit(IsPhysButtonDown())}");

    private void OnUiTick(object? sender, EventArgs e)
    {
        Volatile.Write(ref _lastUiTickMs, Environment.TickCount64);
        _wpfBtn = Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed
            || Mouse.MiddleButton == MouseButtonState.Pressed;
        _captured = GetCapture() != IntPtr.Zero;
    }

    // BACKGROUND THREAD. Keeps sampling while the UI thread is dead — that is its entire purpose.
    private void OnWatchdogSample(object? state)
    {
        long now = Environment.TickCount64;
        long gap = now - Volatile.Read(ref _lastUiTickMs);
        bool physBtn = IsPhysButtonDown();
        bool wpfBtn = _wpfBtn;
        bool captured = _captured;
        bool modal = _modal;

        // (a) UI-thread liveness: gapMs large -> the UI thread is blocked. One line at onset, then at
        // most one per second while it persists, then one recovery line with the total.
        if (gap > GapAnomalyMs)
        {
            if (!_inBlock)
            {
                _inBlock = true;
                _blockStartMs = now - gap;
                _lastBlockLineMs = 0;
            }

            if (now - _lastBlockLineMs >= 1000)
            {
                _lastBlockLineMs = now;
                Emit($"[RDP uithread] gapMs={gap} modal={Bit(modal)} physBtn={Bit(physBtn)} wpfBtn={Bit(wpfBtn)} captured={Bit(captured)}");
            }
        }
        else if (_inBlock)
        {
            _inBlock = false;
            Emit($"[RDP uithread] recovered blockedMs={now - _blockStartMs}");
        }

        // (b) lost button-up: hardware UP while WPF still believes DOWN. Only meaningful when the UI
        // thread is alive (wpfBtn fresh); two consecutive samples filter the normal few-ms latency
        // between the hardware up and WPF processing the message.
        if (!physBtn && wpfBtn && gap <= GapAnomalyMs)
        {
            _mismatchStreak++;
            if (_mismatchStreak == 2)
            {
                _inMismatch = true;
                _mismatchStartMs = now - SampleMs;
                Emit($"[RDP uithread] gapMs={gap} modal={Bit(modal)} physBtn=0 wpfBtn=1 captured={Bit(captured)}");
            }
        }
        else
        {
            if (_inMismatch)
            {
                Emit($"[RDP uithread] mismatch-clear afterMs={now - _mismatchStartMs}");
            }

            _inMismatch = false;
            _mismatchStreak = 0;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmEnterSizeMove = 0x0231;
        const int WmExitSizeMove = 0x0232;
        if (msg == WmEnterSizeMove)
        {
            _modal = true;
            _modalEnterMs = Environment.TickCount64;
            _modalComCalls = 0;
            _modalComMsTotal = 0;
            Emit("[RDP modal] ENTER");
        }
        else if (msg == WmExitSizeMove)
        {
            _modal = false;
            Emit($"[RDP modal] EXIT durMs={Environment.TickCount64 - _modalEnterMs} comCalls={_modalComCalls} comMsTotal={_modalComMsTotal}");
        }

        return IntPtr.Zero;
    }

    private void Emit(string line) => _log?.Info(_title, line);

    private static int Bit(bool value) => value ? 1 : 0;

    private static bool IsPhysButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 ||   // VK_LBUTTON
        (GetAsyncKeyState(0x02) & 0x8000) != 0 ||   // VK_RBUTTON
        (GetAsyncKeyState(0x04) & 0x8000) != 0;     // VK_MBUTTON

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
}
