using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Windows;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Network;
using APTOFI.FileSharing.Service;

namespace APTOFI.FileSharing
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
            if (command == "--service")
            {
                ServiceBase.Run(new AfSharingWindowsService());
                return 0;
            }
            if (!IsAdministrator())
                return RelaunchElevated(args);
            var installer = new APTOFI.FileSharing.Service.ServiceInstaller();
            if (command == "--install-service")
                return installer.InstallInternal().Success ? 0 : 1;
            if (command == "--uninstall-service")
                return installer.UninstallInternal().Success ? 0 : 1;
            if (command == "--start-service")
                return installer.StartInternal().Success ? 0 : 1;
            if (command == "--restart-service")
                return installer.RestartInternal().Success ? 0 : 1;
            if (command == "--stop-service")
                return WindowsNetworkService.Run("sc.exe", "stop " + AppVersion.ServiceName).Success ? 0 : 1;

            var startHidden = command == "--tray";
            var mutex = new Mutex(true, "Local\\APTOFIFileSharingUi", out var created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\APTOFIFileSharingShow");
            if (!created)
            {
                showEvent.Set();
                showEvent.Dispose();
                mutex.Dispose();
                return 0;
            }
            var shutdownEvent = new ManualResetEvent(false);
            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                if (command.Length == 0 && window.IsConfigured)
                    startHidden = true;
                app.MainWindow = window;
                var waitHandles = new WaitHandle[] { showEvent, shutdownEvent };
                var listener = new Thread(() =>
                {
                    while (WaitHandle.WaitAny(waitHandles) == 0)
                    {
                        if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
                            break;
                        app.Dispatcher.BeginInvoke(new Action(window.RestoreFromTray));
                    }
                });
                listener.IsBackground = true;
                listener.Name = "APTOFITrayActivation";
                listener.Start();
                if (!startHidden || !window.IsConfigured)
                    window.Show();
                var result = app.Run();
                shutdownEvent.Set();
                listener.Join(1000);
                return result;
            }
            finally
            {
                shutdownEvent.Set();
                shutdownEvent.Dispose();
                showEvent.Dispose();
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static int RelaunchElevated(string[] args)
        {
            try
            {
                var executable = Process.GetCurrentProcess().MainModule.FileName;
                var arguments = args == null ? string.Empty : string.Join(" ", Array.ConvertAll(args, QuoteArgument));
                Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, Verb = "runas" });
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            return value.IndexOfAny(new[] { ' ', '\t', '\"' }) >= 0 ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                AppPaths.EnsureRuntimeDirectories();
                File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, "crash-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"), DateTime.Now.ToString("O") + " " + e.ExceptionObject + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
