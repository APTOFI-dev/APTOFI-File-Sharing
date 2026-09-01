using System;
using System.Diagnostics;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Network;

namespace APTOFI.FileSharing.Service
{
    internal sealed class TrayAutoStartManager
    {
        public bool IsEnabled()
        {
            var result = WindowsNetworkService.Run("schtasks.exe", "/Query /TN \"" + AppVersion.TrayTaskName + "\"");
            return result.Success;
        }

        public void Enable()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var taskCommand = "\\\"" + exe + "\\\" --tray";
            var args = "/Create /TN \"" + AppVersion.TrayTaskName + "\" /TR \"" + taskCommand + "\" /SC ONLOGON /RL HIGHEST /F";
            var result = WindowsNetworkService.Run("schtasks.exe", args);
            if (!result.Success)
                throw new InvalidOperationException("Unable to create the elevated tray startup task: " + result.Output);
        }

        public void Disable()
        {
            WindowsNetworkService.Run("schtasks.exe", "/Delete /TN \"" + AppVersion.TrayTaskName + "\" /F");
        }
    }
}
