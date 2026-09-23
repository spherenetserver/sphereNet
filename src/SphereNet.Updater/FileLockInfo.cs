using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SphereNet.Updater;

/// <summary>
/// Kilitli bir dosyayi kimin tuttugunu Windows Restart Manager'a sorar
/// (RmStartSession / RmRegisterResources / RmGetList). "Access denied" tek basina
/// sebebi soylemez: calisan Host, acik bir Explorer penceresi, antivirus ya da
/// yonetici olarak baslatilmis bir surec olabilir. Guncelleme durdugunda
/// operatore hangi programi kapatmasi gerektigini yazmak icin kullanilir.
/// Windows disinda ve her hata durumunda bos liste doner.
/// </summary>
internal static class FileLockInfo
{
    public static IReadOnlyList<string> WhoIsLocking(IEnumerable<string> paths)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        string[] files = paths.Where(File.Exists).Take(64).ToArray();
        if (files.Length == 0)
            return [];

        var result = new List<string>();
        uint handle = 0;
        try
        {
            string key = Guid.NewGuid().ToString("N");
            if (RmStartSession(out handle, 0, key) != 0)
                return [];
            if (RmRegisterResources(handle, (uint)files.Length, files, 0, null, 0, null) != 0)
                return [];

            uint needed = 0, count = 0, reasons = 0;
            int rc = RmGetList(handle, out needed, ref count, null, ref reasons);
            if (rc == 234 && needed > 0) // ERROR_MORE_DATA
            {
                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(handle, out needed, ref count, infos, ref reasons) == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int pid = infos[i].Process.dwProcessId;
                        string name = infos[i].strAppName;
                        try { name = Process.GetProcessById(pid).ProcessName + ".exe"; } catch { }
                        result.Add($"{name} (pid {pid})");
                    }
                }
            }
        }
        catch
        {
            return result;
        }
        finally
        {
            if (handle != 0)
                RmEndSession(handle);
        }
        return result.Distinct().ToList();
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
