using System;
using System.IO;
using System.ServiceProcess;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Network;

namespace APTOFI.FileSharing.Service
{
    internal sealed class AfSharingWindowsService : ServiceBase
    {
        private ServerRuntime _runtime;

        public AfSharingWindowsService()
        {
            ServiceName = AppVersion.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                _runtime = new ServerRuntime();
                _runtime.Start();
            }
            catch (Exception ex)
            {
                try
                {
                    AppPaths.EnsureRuntimeDirectories();
                    File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, "service-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"), DateTime.Now.ToString("O") + " " + ex + Environment.NewLine);
                }
                catch
                {
                }
                throw;
            }
        }

        protected override void OnStop()
        {
            _runtime?.Dispose();
            _runtime = null;
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }
    }
}
