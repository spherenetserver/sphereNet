using System.Runtime.InteropServices;

namespace SphereNet.Server;

/// <summary>
/// Windows scheduling settings for the main loop's short sleeps.
///
/// <see cref="Thread.Sleep(int)"/> rounds up to the system timer tick, 15.6 ms by
/// default, so the 1-5 ms sleeps of TickSleepMode 1 and 3 really lasted a full
/// tick. timeBeginPeriod(1) asks for a 1 ms tick for this process. Windows 11
/// ignores that request from a process whose windows are not visible - a server
/// minimised, behind other windows or without a console - and may also move such a
/// process to its efficiency (EcoQoS) class, where the main thread can wait
/// hundreds of milliseconds for a core. The process therefore opts out of both
/// throttles. Everything here is a no-op off Windows, where the kernel timer is
/// already fine-grained.
/// </summary>
internal static class HostTimerResolution
{
    private const uint PeriodMs = 1;
    private static bool _periodSet;

    /// <summary>Apply the settings; returns what took effect, for the startup log.</summary>
    public static string Enable()
    {
        if (!OperatingSystem.IsWindows())
            return "not Windows, unchanged";

        bool throttleOff = DisablePowerThrottling();
        try
        {
            _periodSet = timeBeginPeriod(PeriodMs) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _periodSet = false;
        }
        return $"timer {(_periodSet ? $"{PeriodMs} ms" : "unchanged")}, " +
               $"power throttling {(throttleOff ? "off" : "unchanged")}";
    }

    /// <summary>Release the timer request made by <see cref="Enable"/>.</summary>
    public static void Disable()
    {
        if (!_periodSet)
            return;
        _periodSet = false;
        try { timeEndPeriod(PeriodMs); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static bool DisablePowerThrottling()
    {
        try
        {
            // ControlMask names the throttles this call decides; StateMask = 0 turns
            // them off: no EcoQoS, and the timer request is honoured while hidden.
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed | ProcessPowerThrottlingIgnoreTimerResolution,
                StateMask = 0,
            };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;   // older Windows without SetProcessInformation
        }
    }

    private const int ProcessPowerThrottling = 4;              // PROCESS_INFORMATION_CLASS
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint hProcess, int processInformationClass,
        ref ProcessPowerThrottlingState processInformation, uint processInformationSize);
}
