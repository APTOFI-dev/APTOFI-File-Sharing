using System;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Network;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Security;

namespace APTOFI.FileSharing.Service
{
    internal sealed class ServiceInstaller
    {
        private readonly WindowsNetworkService _windows = new WindowsNetworkService();

        public bool IsInstalled()
        {
            try
            {
                foreach (var service in ServiceController.GetServices())
                {
                    using (service)
                    {
                        if (string.Equals(service.ServiceName, AppVersion.ServiceName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public bool IsAutomaticBootStart()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + AppVersion.ServiceName, false))
                {
                    if (key == null)
                        return false;
                    var start = key.GetValue("Start");
                    var delayed = key.GetValue("DelayedAutostart");
                    var startValue = start == null ? -1 : Convert.ToInt32(start);
                    var delayedValue = delayed == null ? 0 : Convert.ToInt32(delayed);
                    return startValue == 2 && delayedValue == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public ServiceControllerStatus? Status()
        {
            try
            {
                using (var service = new ServiceController(AppVersion.ServiceName))
                    return service.Status;
            }
            catch
            {
                return null;
            }
        }

        public CommandResult InstallElevated()
        {
            if (_windows.IsAdministrator())
                return InstallInternal();
            return _windows.RunElevatedSelf("--install-service");
        }

        public CommandResult UninstallElevated()
        {
            if (_windows.IsAdministrator())
                return UninstallInternal();
            return _windows.RunElevatedSelf("--uninstall-service");
        }

        public CommandResult StartElevated()
        {
            if (_windows.IsAdministrator())
                return StartInternal();
            return _windows.RunElevatedSelf("--start-service");
        }

        public CommandResult StopElevated()
        {
            if (_windows.IsAdministrator())
                return WindowsNetworkService.Run("sc.exe", "stop " + AppVersion.ServiceName);
            return _windows.RunElevatedSelf("--stop-service");
        }

        public CommandResult RestartElevated()
        {
            if (_windows.IsAdministrator())
                return RestartInternal();
            return _windows.RunElevatedSelf("--restart-service");
        }

        public CommandResult InstallInternal()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var bin = "\\\"" + exe + "\\\" --service";
            var create = IsInstalled()
                ? WindowsNetworkService.Run("sc.exe", "config " + AppVersion.ServiceName + " binPath= \"" + bin + "\" start= auto obj= LocalSystem DisplayName= \"" + AppVersion.ServiceDisplayName + "\"")
                : WindowsNetworkService.Run("sc.exe", "create " + AppVersion.ServiceName + " binPath= \"" + bin + "\" start= auto obj= LocalSystem DisplayName= \"" + AppVersion.ServiceDisplayName + "\"");
            if (!create.Success)
                return create;
            var automatic = WindowsNetworkService.Run("sc.exe", "config " + AppVersion.ServiceName + " start= auto");
            if (!automatic.Success)
                return automatic;
            var immediate = WindowsNetworkService.Run("reg.exe", "add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\" + AppVersion.ServiceName + "\" /v DelayedAutostart /t REG_DWORD /d 0 /f");
            if (!immediate.Success)
                return immediate;
            WindowsNetworkService.Run("sc.exe", "description " + AppVersion.ServiceName + " \"APTOFI File Sharing server\"");
            WindowsNetworkService.Run("sc.exe", "failure " + AppVersion.ServiceName + " reset= 86400 actions= restart/5000/restart/30000/restart/60000");
            WindowsNetworkService.Run("sc.exe", "failureflag " + AppVersion.ServiceName + " 1");
            try
            {
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                {
                    var settings = db.GetSettings();
                    if (settings == null)
                        return CommandResult.Fail("Server settings are not configured.");
                    var firewall = _windows.EnsureFirewall(settings.HttpPort, settings.HttpsPort);
                    if (!firewall.Success)
                        return firewall;
                    settings.ServiceInstalled = true;
                    db.SaveSettingsPersisted(settings);
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Unable to persist server startup settings: " + ex.Message);
            }
            var start = StartInternal();
            return start.Success ? CommandResult.Ok(create.Output + start.Output) : start;
        }

        public CommandResult StartInternal()
        {
            try
            {
                using (var service = new ServiceController(AppVersion.ServiceName))
                {
                    service.Refresh();
                    if (service.Status == ServiceControllerStatus.Running)
                        return CommandResult.Ok("Service is already running.");
                    if (service.Status == ServiceControllerStatus.Stopped)
                        service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    service.Refresh();
                    return service.Status == ServiceControllerStatus.Running
                        ? CommandResult.Ok("Service reached Running state.")
                        : CommandResult.Fail("Service did not reach Running state.");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }
        }


        public CommandResult RestartInternal()
        {
            try
            {
                using (var service = new ServiceController(AppVersion.ServiceName))
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Stopped)
                    {
                        if (service.Status != ServiceControllerStatus.StopPending)
                            service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    service.Refresh();
                    return service.Status == ServiceControllerStatus.Running ? CommandResult.Ok("Service restarted and reached Running state.") : CommandResult.Fail("Service did not reach Running state after restart.");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }
        }

        public CommandResult UninstallInternal()
        {
            WindowsNetworkService.Run("sc.exe", "stop " + AppVersion.ServiceName);
            return WindowsNetworkService.Run("sc.exe", "delete " + AppVersion.ServiceName);
        }
    }
}
