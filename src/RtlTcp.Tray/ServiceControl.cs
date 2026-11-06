using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace RtlTcp.Tray;

/// <summary>The rtltcp.server Windows service: query as user, control elevated (UAC).</summary>
internal static class ServiceControl
{
    public const string ServiceName = "rtltcp.server";

    /// <summary>Current state, null when the service is not installed.</summary>
    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Runs this exe again with "--service &lt;action&gt;" and the UAC prompt. False when the user said no.</summary>
    public static bool RunElevated(string action)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--service {action}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return false;
        }
    }

    /// <summary>Elevated part: start / stop / restart and wait until done. Exit code 0 = ok.</summary>
    public static int Run(string action)
    {
        if (action is not ("start" or "stop" or "restart")) return 2;
        try
        {
            using var sc = new ServiceController(ServiceName);
            var timeout = TimeSpan.FromSeconds(60);
            if (action is "stop" or "restart" && sc.Status != ServiceControllerStatus.Stopped)
            {
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }
            if (action is "start" or "restart")
            {
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                }
            }
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            return 1;
        }
    }
}
