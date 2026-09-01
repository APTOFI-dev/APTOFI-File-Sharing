using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace APTOFI.FileSharing.Network
{
    internal sealed class WindowsNetworkService
    {
        private const string AppId = "{6A2F48A4-5F68-4F88-9A83-C6A7F1D48130}";

        public bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public CommandResult EnsureFirewall(int httpPort, int httpsPort)
        {
            var a = Run("netsh.exe", "advfirewall firewall delete rule name=\"APTOFI File Sharing HTTP\"");
            var b = Run("netsh.exe", "advfirewall firewall add rule name=\"APTOFI File Sharing HTTP\" dir=in action=allow protocol=TCP localport=" + httpPort.ToString(CultureInfo.InvariantCulture));
            var c = Run("netsh.exe", "advfirewall firewall delete rule name=\"APTOFI File Sharing HTTPS\"");
            var d = Run("netsh.exe", "advfirewall firewall add rule name=\"APTOFI File Sharing HTTPS\" dir=in action=allow protocol=TCP localport=" + httpsPort.ToString(CultureInfo.InvariantCulture));
            return b.Success && d.Success ? CommandResult.Ok(b.Output + d.Output) : CommandResult.Fail(b.Output + d.Output + a.Output + c.Output);
        }

        public CommandResult BindCertificate(int port, string thumbprint)
        {
            var clean = (thumbprint ?? string.Empty).Replace(" ", string.Empty);
            Run("netsh.exe", "http delete sslcert ipport=0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture));
            return Run("netsh.exe", "http add sslcert ipport=0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture) + " certhash=" + clean + " appid=" + AppId + " certstorename=MY");
        }

        public PortOwner FindPortOwner(int port)
        {
            try
            {
                var result = Run("netstat.exe", "-ano -p tcp");
                if (!result.Success)
                    return null;
                var regex = new Regex(@"^\s*TCP\s+\S+:(\d+)\s+\S+\s+\S+\s+(\d+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                foreach (Match match in regex.Matches(result.Output))
                {
                    if (!int.TryParse(match.Groups[1].Value, out var p) || p != port)
                        continue;
                    if (!int.TryParse(match.Groups[2].Value, out var pid))
                        continue;
                    string name;
                    try { name = Process.GetProcessById(pid).ProcessName; } catch { name = "unknown"; }
                    return new PortOwner { Port = port, ProcessId = pid, ProcessName = name };
                }
            }
            catch
            {
            }
            return null;
        }

        public CommandResult RunElevatedSelf(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using (var process = Process.Start(psi))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0 ? CommandResult.Ok(string.Empty) : CommandResult.Fail("Exit code " + process.ExitCode);
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }
        }

        public static CommandResult Run(string fileName, string arguments)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(fileName, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    process.Start();
                    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0 ? CommandResult.Ok(output) : CommandResult.Fail(output);
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }
        }
    }

    internal sealed class CommandResult
    {
        public bool Success { get; private set; }
        public string Output { get; private set; }

        public static CommandResult Ok(string output)
        {
            return new CommandResult { Success = true, Output = output ?? string.Empty };
        }

        public static CommandResult Fail(string output)
        {
            return new CommandResult { Success = false, Output = output ?? string.Empty };
        }
    }

    internal sealed class PortOwner
    {
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
    }
}
