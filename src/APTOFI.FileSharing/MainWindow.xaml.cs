// Date: 2026-10-05
// Time: 09:45:00 +07:00
// File version: 1.1.36
// Description: Controls the tray UI with quiet lightweight runtime status, explicit deep diagnostics and non-fatal clipboard actions.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Data;
using APTOFI.FileSharing.Logging;
using APTOFI.FileSharing.Network;
using APTOFI.FileSharing.Security;
using APTOFI.FileSharing.Service;
using APTOFI.FileSharing.Storage;
using WinForms = System.Windows.Forms;

namespace APTOFI.FileSharing
{
    public partial class MainWindow : Window
    {
        private string _language = "ru";
        private readonly APTOFI.FileSharing.Service.ServiceInstaller _serviceInstaller = new APTOFI.FileSharing.Service.ServiceInstaller();
        private readonly TrayAutoStartManager _trayAutoStartManager = new TrayAutoStartManager();
        private readonly DispatcherTimer _statusTimer = new DispatcherTimer();
        private readonly DispatcherTimer _healthTimer = new DispatcherTimer();
        private readonly DispatcherTimer _logRefreshTimer = new DispatcherTimer();
        private readonly List<StorageLocationSetting> _storageLocations = new List<StorageLocationSetting>();
        private FileSystemWatcher _logWatcher;
        private volatile bool _logDirty;
        private WinForms.NotifyIcon _trayIcon;
        private WinForms.ContextMenuStrip _trayMenu;
        private WinForms.ToolStripMenuItem _trayOpenItem;
        private WinForms.ToolStripMenuItem _trayAdminItem;
        private WinForms.ToolStripMenuItem _trayUserItem;
        private WinForms.ToolStripMenuItem _trayServiceItem;
        private WinForms.ToolStripMenuItem _trayExitItem;
        private bool _configured;
        private string _siteName = AppVersion.ProductName;
        private bool _allowClose;
        private int _selectedStorageIndex = -1;
        private bool _healthBusy;
        private ControlHealthLevel _lastHealthLevel = ControlHealthLevel.Unknown;
        private bool _wizardAutoShown;
        private string _logFilter = "all";

        public bool IsConfigured => _configured;

        public MainWindow()
        {
            InitializeComponent();
            StateChanged += MainWindow_OnStateChanged;
            Closing += MainWindow_OnClosing;
            Closed += MainWindow_OnClosed;
            LanguageBox.SelectedIndex = 0;
            ModeBox.SelectedIndex = 0;
            DnsModeBox.SelectedIndex = 0;
            DnsAlgorithmBox.SelectedIndex = 0;
            LogFilterBox.SelectedIndex = 0;
            AccessLogModeBox.SelectedIndex = 0;
            GenerateAdminButton_OnClick(null, null);
            LoadExisting();
            if (_storageLocations.Count == 0)
                _storageLocations.Add(new StorageLocationSetting { Id = "primary", Path = string.Empty, Enabled = true });
            RefreshStorageList();
            if (_storageLocations.Count > 0)
                StorageLocationsList.SelectedIndex = 0;
            InitializeTray();
            EnsureConfiguredTrayAutoStart();
            ApplyLanguage();
            RefreshRuntimeSummary();
            _statusTimer.Interval = TimeSpan.FromSeconds(10);
            _statusTimer.Tick += (sender, args) => RefreshRuntimeSummary();
            _statusTimer.Start();
            _healthTimer.Interval = TimeSpan.FromMinutes(1);
            _healthTimer.Tick += async (sender, args) => await RefreshHealthAsync();
            _healthTimer.Start();
            InitializeLiveLogs();
            Loaded += async (sender, args) =>
            {
                await RefreshHealthAsync();
                if (!_configured && !_wizardAutoShown)
                {
                    _wizardAutoShown = true;
                    Dispatcher.BeginInvoke(new Action(() => OpenSetupWizard(true)));
                }
            };
            if (!AppPaths.IsBaseDirectoryWritable(out var error))
            {
                MessageText.Foreground = Brushes.Firebrick;
                MessageText.Text = "The folder containing afsharing.exe is not writable: " + error;
                SaveButton.IsEnabled = false;
                ApplyButton.IsEnabled = false;
            }
        }

        private void LoadExisting()
        {
            if (!File.Exists(AppPaths.DatabasePath) || !File.Exists(AppPaths.MasterKeyPath))
            {
                _configured = false;
                return;
            }
            try
            {
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                {
                    var s = db.GetSettings();
                    if (s == null)
                        return;
                    _configured = true;
                    _siteName = DisplaySiteName(s);
                    _language = s.Language ?? "ru";
                    SelectLanguage(_language);
                    _storageLocations.Clear();
                    foreach (var location in StorageService.GetLocations(s))
                        _storageLocations.Add(CloneLocation(location));
                    BindBox.Text = s.BindAddress;
                    PublicBox.Text = string.IsNullOrWhiteSpace(s.Domain) ? PublicHost(s.PublicBaseUrl) : string.Empty;
                    HttpPortBox.Text = s.HttpPort.ToString();
                    HttpsPortBox.Text = s.HttpsPort.ToString();
                    AdminPathBox.Text = s.AdminPath;
                    UserPathBox.Text = s.UserPath;
                    ServerQuotaBox.Text = s.ServerQuotaBytes.ToString();
                    VpsHostBox.Text = s.VpsHost ?? string.Empty;
                    VpsPortBox.Text = s.VpsPort.ToString();
                    VpsUserBox.Text = s.VpsUser ?? "root";
                    VpsSudoBox.IsChecked = s.VpsUseSudo;
                    SelectMode(s.PublicMode);
                    DomainBox.Text = s.Domain ?? string.Empty;
                    SelectDnsMode(s.DnsUpdateMode);
                    DnsServerBox.Text = s.DnsServer ?? string.Empty;
                    DnsZoneBox.Text = s.DnsZone ?? string.Empty;
                    DnsKeyNameBox.Text = s.DnsTsigKeyName ?? string.Empty;
                    SelectDnsAlgorithm(s.DnsTsigAlgorithm);
                    DnsAutoAddressBox.IsChecked = s.DnsAutoUpdateAddress;
                    AcmeEmailBox.Text = s.AcmeEmail ?? string.Empty;
                    AcmeTermsBox.IsChecked = s.AcmeTermsAccepted;
                    TrayAutoStartBox.IsChecked = s.TrayAutoStartEnabled;
                    LogRetentionDaysBox.Text = NormalizeLogValue(s.LogRetentionDays, 7).ToString(CultureInfo.InvariantCulture);
                    LogMaxFileBox.Text = NormalizeLogValue(s.LogMaxFileMiB, 20).ToString(CultureInfo.InvariantCulture);
                    LogMaxDirectoryBox.Text = NormalizeLogValue(s.LogMaxDirectoryMiB, 150).ToString(CultureInfo.InvariantCulture);
                    LogMaintenanceIntervalBox.Text = NormalizeLogValue(s.LogMaintenanceIntervalMinutes, 60).ToString(CultureInfo.InvariantCulture);
                    AccessAggregateMinutesBox.Text = NormalizeLogValue(s.AccessLogAggregateMinutes, 30).ToString(CultureInfo.InvariantCulture);
                    SelectAccessLogMode(s.AccessLogMode);
                    var admin = db.Users.FindOne(x => x.Role == "admin");
                    if (admin != null)
                    {
                        EmailBox.Text = admin.Email;
                        if (string.IsNullOrWhiteSpace(AcmeEmailBox.Text))
                            AcmeEmailBox.Text = admin.Email;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageText.Foreground = Brushes.Firebrick;
                MessageText.Text = ex.Message;
            }
        }

        private async void DetectButton_OnClick(object sender, RoutedEventArgs e)
        {
            DetectButton.IsEnabled = false;
            try
            {
                PublicBox.Text = await PublicIpDetector.DetectIpv4TextAsync(TimeSpan.FromSeconds(8));
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                DetectButton.IsEnabled = true;
            }
        }

        private void AddStorageButton_OnClick(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                if (dialog.ShowDialog() != WinForms.DialogResult.OK)
                    return;
                var path = Path.GetFullPath(dialog.SelectedPath);
                if (_storageLocations.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                    return;
                _storageLocations.Add(new StorageLocationSetting { Id = Guid.NewGuid().ToString("N"), Path = path, Enabled = true });
                RefreshStorageList();
                StorageLocationsList.SelectedIndex = _storageLocations.Count - 1;
            }
        }

        private void RemoveStorageButton_OnClick(object sender, RoutedEventArgs e)
        {
            var index = StorageLocationsList.SelectedIndex;
            if (index < 0 || index >= _storageLocations.Count)
                return;
            if (_storageLocations.Count == 1)
            {
                ShowError(UiText.Get(_language, "storageNeedOne"));
                return;
            }
            var location = _storageLocations[index];
            if (StorageLocationHasFiles(location))
            {
                ShowError(UiText.Get(_language, "storageHasFiles"));
                return;
            }
            _storageLocations.RemoveAt(index);
            _selectedStorageIndex = -1;
            RefreshStorageList();
            if (_storageLocations.Count > 0)
                StorageLocationsList.SelectedIndex = Math.Min(index, _storageLocations.Count - 1);
        }

        private bool StorageLocationHasFiles(StorageLocationSetting location)
        {
            if (location == null || string.IsNullOrWhiteSpace(location.Id) || !File.Exists(AppPaths.DatabasePath))
                return false;
            try
            {
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                {
                    var settings = db.GetSettings();
                    var locations = StorageService.GetLocations(settings);
                    var primaryId = locations.Count > 0 ? locations[0].Id : null;
                    var id = location.Id;
                    if (id == primaryId)
                        return db.Files.Exists(x => x.StorageLocationId == id || x.StorageLocationId == null);
                    return db.Files.Exists(x => x.StorageLocationId == id);
                }
            }
            catch
            {
                return location.UsedBytes > 0;
            }
        }

        private void StorageLocationsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedStorageIndex = StorageLocationsList.SelectedIndex;
            RefreshSelectedStorageEditor();
        }

        private void ApplyStorageQuotaButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplySelectedStorageQuotaFromText();
                RefreshStorageListSelectionSafe();
                RefreshSelectedStorageEditor();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void ApplySelectedStorageQuotaFromText()
        {
            if (_selectedStorageIndex < 0 || _selectedStorageIndex >= _storageLocations.Count)
                return;
            if (!long.TryParse(SelectedStorageQuotaBox.Text.Trim(), out var quota) || quota < 0)
                throw new InvalidOperationException("Invalid storage quota.");
            _storageLocations[_selectedStorageIndex].QuotaBytes = quota;
        }

        private void RefreshSelectedStorageEditor()
        {
            if (SelectedStorageQuotaBox == null || SelectedStorageProgress == null || SelectedStorageUsageText == null)
                return;
            if (_selectedStorageIndex < 0 || _selectedStorageIndex >= _storageLocations.Count)
            {
                SelectedStorageQuotaBox.Text = "0";
                SelectedStorageProgress.Value = 0;
                SelectedStorageUsageText.Text = string.Empty;
                ApplyStorageQuotaButton.IsEnabled = false;
                return;
            }
            ApplyStorageQuotaButton.IsEnabled = true;
            var location = _storageLocations[_selectedStorageIndex];
            SelectedStorageQuotaBox.Text = location.QuotaBytes.ToString();
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(location.Path)));
                var diskUsed = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
                SelectedStorageProgress.Value = drive.TotalSize > 0 ? diskUsed * 100d / drive.TotalSize : 0d;
                SelectedStorageUsageText.Text = UiText.Get(_language, "aptofiUsed") + ": " + FormatBytes(location.UsedBytes) + " · " + UiText.Get(_language, "diskFree") + ": " + FormatBytes(drive.AvailableFreeSpace);
            }
            catch (Exception ex)
            {
                SelectedStorageProgress.Value = 0;
                SelectedStorageUsageText.Text = ex.Message;
            }
        }

        private void GenerateAdminButton_OnClick(object sender, RoutedEventArgs e)
        {
            AdminPathBox.Text = "/" + GenerateSetupToken(18);
        }

        private void GenerateUserButton_OnClick(object sender, RoutedEventArgs e)
        {
            UserPathBox.Text = "/" + GenerateSetupToken(18);
        }

        private static string GenerateSetupToken(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
        {
            await SaveOnlyAsync();
        }

        private async Task SaveOnlyAsync()
        {
            SaveButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            try
            {
                await SaveConfigurationAsync();
                ApplyTrayAutoStartSetting();
                _configured = true;
                if (_trayIcon != null)
                    _trayIcon.Visible = true;
                ShowSuccess(UiText.Get(_language, "settingsSaved") + " " + AppPaths.SettingsFilePath);
                RefreshRuntimeSummary();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SaveButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;
            }
        }

        private async void ApplyButton_OnClick(object sender, RoutedEventArgs e)
        {
            SaveButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            try
            {
                await SaveConfigurationAsync();
                await ConfigureVpsIfNeededAsync();
                ApplyTrayAutoStartSetting();
                var windows = new WindowsNetworkService();
                AppSettings settings;
                var c = new CryptoService();
                using (var db = new Database(c))
                    settings = db.GetSettings();
                var firewall = windows.EnsureFirewall(settings.HttpPort, settings.HttpsPort);
                if (!firewall.Success)
                    throw new InvalidOperationException(firewall.Output);
                if (string.Equals(settings.PublicMode, "Direct", StringComparison.OrdinalIgnoreCase) && settings.EnableHttps)
                    await ConfigureHttpsAsync(false);
                if (!(string.Equals(settings.PublicMode, "Direct", StringComparison.OrdinalIgnoreCase) && settings.EnableHttps))
                {
                    var installed = _serviceInstaller.IsInstalled();
                    var serviceResult = installed ? _serviceInstaller.RestartInternal() : _serviceInstaller.InstallInternal();
                    if (!serviceResult.Success)
                        throw new InvalidOperationException(serviceResult.Output);
                }
                _configured = true;
                if (_trayIcon != null)
                    _trayIcon.Visible = true;
                MainTabs.SelectedItem = OverviewTab;
                ShowSuccess(UiText.Get(_language, "serverStarted"));
                RefreshRuntimeSummary();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SaveButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;
            }
        }

        private Task SaveConfigurationAsync()
        {
            var input = CaptureConfiguration();
            return Task.Run(() => PersistConfiguration(input));
        }

        private ConfigurationInput CaptureConfiguration()
        {
            ApplySelectedStorageQuotaFromText();
            if (_storageLocations.Count == 0 || _storageLocations.All(x => string.IsNullOrWhiteSpace(x.Path)))
                throw new InvalidOperationException(UiText.Get(_language, "storageRequired"));
            return new ConfigurationInput
            {
                StorageLocations = _storageLocations.Where(x => !string.IsNullOrWhiteSpace(x.Path)).Select(CloneLocation).ToList(),
                ServerQuotaText = ServerQuotaBox.Text.Trim(),
                Mode = ((ComboBoxItem)ModeBox.SelectedItem).Tag.ToString(),
                Bind = string.IsNullOrWhiteSpace(BindBox.Text) ? "0.0.0.0" : BindBox.Text.Trim(),
                PublicIp = PublicBox.Text.Trim(),
                HttpPortText = HttpPortBox.Text.Trim(),
                HttpsPortText = HttpsPortBox.Text.Trim(),
                AdminPath = AdminPathBox.Text,
                UserPath = UserPathBox.Text,
                Domain = DomainBox.Text.Trim(),
                DnsMode = ((ComboBoxItem)DnsModeBox.SelectedItem).Tag.ToString(),
                DnsServer = DnsServerBox.Text.Trim(),
                DnsZone = DnsZoneBox.Text.Trim(),
                DnsKeyName = DnsKeyNameBox.Text.Trim(),
                DnsAlgorithm = ((ComboBoxItem)DnsAlgorithmBox.SelectedItem).Tag.ToString(),
                DnsSecret = DnsSecretBox.Password.Trim(),
                DnsAutoAddress = DnsAutoAddressBox.IsChecked == true,
                AcmeEmail = AcmeEmailBox.Text.Trim().ToLowerInvariant(),
                AcmeTerms = AcmeTermsBox.IsChecked == true,
                VpsHost = VpsHostBox.Text.Trim(),
                VpsPortText = VpsPortBox.Text.Trim(),
                VpsUser = VpsUserBox.Text.Trim(),
                VpsPassword = VpsPasswordBox.Password,
                VpsUseSudo = VpsSudoBox.IsChecked == true,
                Email = EmailBox.Text.Trim().ToLowerInvariant(),
                Password = PasswordBox.Password,
                RepeatPassword = RepeatPasswordBox.Password,
                TrayAutoStart = TrayAutoStartBox.IsChecked == true,
                LogRetentionDaysText = LogRetentionDaysBox.Text.Trim(),
                LogMaxFileMiBText = LogMaxFileBox.Text.Trim(),
                LogMaxDirectoryMiBText = LogMaxDirectoryBox.Text.Trim(),
                LogMaintenanceIntervalMinutesText = LogMaintenanceIntervalBox.Text.Trim(),
                AccessLogMode = AccessLogModeBox.SelectedItem is ComboBoxItem accessModeItem ? Convert.ToString(accessModeItem.Tag) : "Compact",
                AccessLogAggregateMinutesText = AccessAggregateMinutesBox.Text.Trim(),
                Language = _language
            };
        }

        private void PersistConfiguration(ConfigurationInput input)
        {
            if (!int.TryParse(input.HttpPortText, out var httpPort) || httpPort < 1 || httpPort > 65535)
                throw new InvalidOperationException("Invalid HTTP port.");
            if (!int.TryParse(input.HttpsPortText, out var httpsPort) || httpsPort < 1 || httpsPort > 65535)
                throw new InvalidOperationException("Invalid HTTPS port.");
            if (!int.TryParse(input.VpsPortText, out var vpsPort) || vpsPort < 1 || vpsPort > 65535)
                throw new InvalidOperationException("Invalid VPS SSH port.");
            if (!long.TryParse(input.ServerQuotaText, out var serverQuota) || serverQuota < 0)
                throw new InvalidOperationException("Invalid server quota.");
            if (!int.TryParse(input.LogRetentionDaysText, out var logRetentionDays) || logRetentionDays < 1 || logRetentionDays > 3650)
                throw new InvalidOperationException("Log retention must be between 1 and 3650 days.");
            if (!int.TryParse(input.LogMaxFileMiBText, out var logMaxFileMiB) || logMaxFileMiB < 1 || logMaxFileMiB > 1024)
                throw new InvalidOperationException("Maximum log file size must be between 1 and 1024 MiB.");
            if (!int.TryParse(input.LogMaxDirectoryMiBText, out var logMaxDirectoryMiB) || logMaxDirectoryMiB < 10 || logMaxDirectoryMiB > 102400)
                throw new InvalidOperationException("Maximum log folder size must be between 10 and 102400 MiB.");
            if (!int.TryParse(input.LogMaintenanceIntervalMinutesText, out var logMaintenanceIntervalMinutes) || logMaintenanceIntervalMinutes < 5 || logMaintenanceIntervalMinutes > 1440)
                throw new InvalidOperationException("Log maintenance interval must be between 5 and 1440 minutes.");
            if (!int.TryParse(input.AccessLogAggregateMinutesText, out var accessLogAggregateMinutes) || accessLogAggregateMinutes < 1 || accessLogAggregateMinutes > 1440)
                throw new InvalidOperationException("Access aggregation interval must be between 1 and 1440 minutes.");
            if (!string.Equals(input.AccessLogMode, "Compact", StringComparison.OrdinalIgnoreCase) && !string.Equals(input.AccessLogMode, "Full", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid access log mode.");
            foreach (var location in input.StorageLocations)
            {
                location.Path = Path.GetFullPath(location.Path.Trim());
                Directory.CreateDirectory(location.Path);
            }
            var adminPath = HttpUtil.NormalizeSecretPath(input.AdminPath, "/admin_secret");
            var userPath = HttpUtil.NormalizeSecretPath(input.UserPath, "/user_login_disk");
            if (adminPath == userPath)
                throw new InvalidOperationException("Administrator and user paths must be different.");
            if (string.IsNullOrWhiteSpace(input.Email) || !input.Email.Contains("@"))
                throw new InvalidOperationException("Invalid administrator email.");
            if (!_configured && (input.Password.Length < 8 || input.Password != input.RepeatPassword))
                throw new InvalidOperationException("Administrator password must contain at least 8 characters and both password fields must match.");
            if (_configured && input.Password.Length > 0 && (input.Password.Length < 8 || input.Password != input.RepeatPassword))
                throw new InvalidOperationException("New administrator password must contain at least 8 characters and both password fields must match.");
            var identifier = !string.IsNullOrWhiteSpace(input.Domain) ? CleanHost(input.Domain) : CleanHost(input.PublicIp);
            if (input.Mode == "Direct" && string.IsNullOrWhiteSpace(identifier))
                throw new InvalidOperationException("Public IP or domain is required for direct Internet mode.");
            if (input.Mode != "Local" && !input.AcmeTerms)
                throw new InvalidOperationException("Automatic HTTPS requires acceptance of the certificate authority terms.");
            if (input.Mode == "Vps" && (string.IsNullOrWhiteSpace(input.VpsHost) || string.IsNullOrWhiteSpace(input.VpsUser)))
                throw new InvalidOperationException("VPS host and SSH user are required.");
            var crypto = new CryptoService();
            using (var db = new Database(crypto))
            {
                var settings = db.GetSettings() ?? new AppSettings();
                var currentLocations = StorageService.GetLocations(settings);
                var currentPrimaryId = currentLocations.Count > 0 ? currentLocations[0].Id : null;
                var existingIds = new HashSet<string>(input.StorageLocations.Select(x => x.Id), StringComparer.Ordinal);
                var primaryRemoved = !string.IsNullOrWhiteSpace(currentPrimaryId) && !existingIds.Contains(currentPrimaryId);
                var removedWithFiles = db.Files.FindAll().Any(x => string.IsNullOrWhiteSpace(x.StorageLocationId) ? primaryRemoved : !existingIds.Contains(x.StorageLocationId));
                if (removedWithFiles)
                    throw new InvalidOperationException(UiText.Get(input.Language, "storageHasFiles"));
                settings.StorageLocations = input.StorageLocations;
                settings.StorageRoot = input.StorageLocations[0].Path;
                settings.ServerQuotaBytes = serverQuota;
                settings.BindAddress = input.Bind;
                settings.HttpPort = httpPort;
                settings.HttpsPort = httpsPort;
                settings.PublicMode = input.Mode;
                settings.AdminPath = adminPath;
                settings.UserPath = userPath;
                settings.Language = input.Language;
                settings.TrayAutoStartEnabled = input.TrayAutoStart;
                settings.LogRetentionDays = logRetentionDays;
                settings.LogMaxFileMiB = logMaxFileMiB;
                settings.LogMaxDirectoryMiB = logMaxDirectoryMiB;
                settings.LogMaintenanceIntervalMinutes = logMaintenanceIntervalMinutes;
                settings.AccessLogMode = string.Equals(input.AccessLogMode, "Full", StringComparison.OrdinalIgnoreCase) ? "Full" : "Compact";
                settings.AccessLogAggregateMinutes = accessLogAggregateMinutes;
                settings.AcmeTermsAccepted = input.AcmeTerms;
                settings.EnableHttps = input.Mode != "Local";
                settings.AcmeEmail = string.IsNullOrWhiteSpace(input.AcmeEmail) ? input.Email : input.AcmeEmail;
                if (!string.Equals(settings.HttpsIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
                    settings.CertificateThumbprint = null;
                settings.Domain = !string.IsNullOrWhiteSpace(input.Domain) ? CleanHost(input.Domain) : null;
                settings.HttpsIdentifier = identifier;
                settings.PublicBaseUrl = BuildPublicBaseUrl(input.Mode, identifier, httpPort, httpsPort, settings.EnableHttps);
                settings.DnsUpdateMode = input.DnsMode;
                if (string.Equals(input.DnsMode, "Rfc2136", StringComparison.OrdinalIgnoreCase))
                {
                    settings.DnsServer = DnsUpdateService.NormalizeServer(input.DnsServer);
                    settings.DnsZone = DnsUpdateService.NormalizeZone(input.DnsZone);
                    settings.DnsTsigKeyName = DnsUpdateService.NormalizeKeyName(input.DnsKeyName);
                    settings.DnsTsigAlgorithm = DnsUpdateService.NormalizeAlgorithm(input.DnsAlgorithm);
                    settings.DnsAutoUpdateAddress = input.DnsAutoAddress;
                    if (string.IsNullOrWhiteSpace(settings.Domain))
                        throw new InvalidOperationException("A domain is required for DNS-01.");
                    if (string.IsNullOrWhiteSpace(settings.DnsServer) || string.IsNullOrWhiteSpace(settings.DnsZone) || string.IsNullOrWhiteSpace(settings.DnsTsigKeyName))
                        throw new InvalidOperationException("DNS server, zone and TSIG key name are required for RFC2136.");
                    if (!string.IsNullOrWhiteSpace(input.DnsSecret))
                        settings.DnsTsigSecretProtected = crypto.ProtectString(input.DnsSecret);
                    if (string.IsNullOrWhiteSpace(settings.DnsTsigSecretProtected))
                        throw new InvalidOperationException("TSIG secret is required for RFC2136.");
                }
                else
                {
                    settings.DnsServer = null;
                    settings.DnsZone = null;
                    settings.DnsTsigKeyName = null;
                    settings.DnsTsigAlgorithm = "hmac-sha256";
                    settings.DnsTsigSecretProtected = null;
                    settings.DnsAutoUpdateAddress = false;
                    settings.LastDnsError = null;
                    settings.LastDnsUpdateUtc = null;
                }
                if (input.Mode == "Vps")
                {
                    var newVpsHost = CleanHost(input.VpsHost);
                    if (!string.Equals(settings.VpsHost, newVpsHost, StringComparison.OrdinalIgnoreCase))
                        settings.VpsHostKeyFingerprint = null;
                    settings.VpsHost = newVpsHost;
                    settings.VpsUser = input.VpsUser;
                    settings.VpsPort = vpsPort;
                    settings.VpsUseSudo = input.VpsUseSudo;
                    if (!string.IsNullOrWhiteSpace(input.VpsPassword))
                        settings.VpsPasswordProtected = crypto.ProtectString(input.VpsPassword);
                }
                db.SaveSettingsPersisted(settings);
                var passwords = new PasswordService();
                var admin = db.Users.FindOne(x => x.Role == "admin");
                if (admin == null)
                {
                    admin = new UserRecord { Id = Guid.NewGuid().ToString("N"), Email = input.Email, PasswordHash = passwords.Hash(input.Password), Role = "admin", Language = input.Language, CreatedUtc = DateTime.UtcNow, Enabled = true };
                    db.Users.Insert(admin);
                }
                else
                {
                    admin.Email = input.Email;
                    admin.Language = input.Language;
                    if (!string.IsNullOrEmpty(input.Password))
                    {
                        admin.PasswordHash = passwords.Hash(input.Password);
                        db.Sessions.DeleteMany(x => x.UserId == admin.Id);
                    }
                    db.Users.Update(admin);
                }
            }
        }

        private async Task ConfigureVpsIfNeededAsync()
        {
            var mode = ((ComboBoxItem)ModeBox.SelectedItem).Tag.ToString();
            if (!string.Equals(mode, "Vps", StringComparison.OrdinalIgnoreCase))
                return;
            var crypto = new CryptoService();
            using (var db = new Database(crypto))
            using (var log = new LogService())
            using (var vps = new VpsTunnelService(db, crypto, log))
            {
                var settings = db.GetSettings();
                var result = await vps.ConfigureRemoteAsync(settings).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException(result.Message);
            }
        }

        private async void ConfigureHttpsButton_OnClick(object sender, RoutedEventArgs e)
        {
            ConfigureHttpsButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            try
            {
                ShowSuccess(UiText.Get(_language, "httpsIssuing"));
                await SaveConfigurationAsync();
                await ConfigureHttpsAsync(false);
                _configured = true;
                if (_trayIcon != null)
                    _trayIcon.Visible = true;
                Dispatcher.Invoke(() =>
                {
                    ShowSuccess(UiText.Get(_language, "httpsReady"));
                    RefreshRuntimeSummary();
                });
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                RefreshRuntimeSummary();
            }
            finally
            {
                ConfigureHttpsButton.IsEnabled = true;
                SaveButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;
            }
        }

        private async Task ConfigureHttpsAsync(bool force)
        {
            var crypto = new CryptoService();
            var windows = new WindowsNetworkService();
            CertificateIssueResult certificateResult;
            using (var db = new Database(crypto))
            using (var log = new LogService())
            {
                var settings = db.GetSettings();
                if (settings == null || !settings.EnableHttps)
                    throw new InvalidOperationException("HTTPS is not enabled in the current publishing mode.");
                var acme = new AcmeService(db, crypto, windows, log);
                certificateResult = await acme.EnsureCertificateAsync(force).ConfigureAwait(false);
            }
            if (!certificateResult.Success)
                throw new InvalidOperationException(certificateResult.Error);
            var serviceResult = _serviceInstaller.IsInstalled() ? _serviceInstaller.RestartInternal() : _serviceInstaller.InstallInternal();
            if (!serviceResult.Success)
                throw new InvalidOperationException(serviceResult.Output);
        }

        private void CopyPublicButton_OnClick(object sender, RoutedEventArgs e)
        {
            CopyOverviewValue(OverviewPublicBox.Text);
        }

        private void CopyUserLoginButton_OnClick(object sender, RoutedEventArgs e)
        {
            CopyOverviewValue(OverviewUserLoginBox.Text);
        }

        private void CopyOverviewValue(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value == "—")
                return;
            try
            {
                Clipboard.SetText(value);
                ShowSuccess(UiText.Get(_language, "publicCopied"));
            }
            catch
            {
            }
        }

        private void ModeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VpsPanel == null || ModeBox.SelectedItem == null)
                return;
            var mode = ((ComboBoxItem)ModeBox.SelectedItem).Tag.ToString();
            VpsPanel.Visibility = mode == "Vps" ? Visibility.Visible : Visibility.Collapsed;
            DetectButton.IsEnabled = mode != "Vps";
        }

        private void DnsModeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DnsPanel == null || DnsModeBox.SelectedItem == null)
                return;
            DnsPanel.Visibility = ((ComboBoxItem)DnsModeBox.SelectedItem).Tag.ToString() == "Rfc2136" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenAdminButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_configured)
                OpenBest(AdminPathBox.Text);
        }

        private void OpenUserButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_configured)
                OpenBest(UserPathBox.Text);
        }

        private void OpenBest(string path)
        {
            try
            {
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                {
                    var settings = db.GetSettings();
                    if (settings == null)
                        throw new InvalidOperationException("Settings are not configured.");
                    if (settings.PublicMode != "Local" && (string.IsNullOrWhiteSpace(settings.CertificateThumbprint) || string.IsNullOrWhiteSpace(settings.PublicBaseUrl)))
                        throw new InvalidOperationException(UiText.Get(_language, "publicNotReady"));
                    var baseUrl = settings.PublicMode == "Local"
                        ? "http://127.0.0.1:" + settings.HttpPort
                        : BuildCanonicalPublicBaseUrl(settings);
                    if (string.IsNullOrWhiteSpace(baseUrl))
                        throw new InvalidOperationException(UiText.Get(_language, "publicNotReady"));
                    var url = baseUrl.TrimEnd('/') + HttpUtil.NormalizeSecretPath(path, "/");
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void ServiceToggleButton_OnClick(object sender, RoutedEventArgs e)
        {
            var status = _serviceInstaller.Status();
            var result = status == ServiceControllerStatus.Running ? _serviceInstaller.StopElevated() : _serviceInstaller.StartElevated();
            if (!result.Success)
                ShowError(result.Output);
            _lastHealthLevel = ControlHealthLevel.Unknown;
            RefreshRuntimeSummary();
            _ = RefreshHealthAsync();
        }

        private void MinimizeTrayButton_OnClick(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void RefreshRuntimeSummary()
        {
            var status = _serviceInstaller.Status();
            var running = status == ServiceControllerStatus.Running;
            var pending = status == ServiceControllerStatus.StartPending;
            var stateKey = running ? "serviceRunning" : pending ? "serviceStarting" : "serviceStopped";
            if (_lastHealthLevel == ControlHealthLevel.Unknown)
            {
                HeaderStatusDot.Fill = running ? Brushes.SeaGreen : pending ? Brushes.DarkOrange : Brushes.Firebrick;
                OverviewStateDot.Fill = HeaderStatusDot.Fill;
                StatusText.Text = UiText.Get(_language, "service") + ": " + UiText.Get(_language, stateKey);
                OverviewStateText.Text = UiText.Get(_language, stateKey);
            }
            ServiceToggleButton.Content = UiText.Get(_language, running ? "stop" : "start");
            ServiceToggleButton.IsEnabled = !pending && _serviceInstaller.IsInstalled();
            RestartServiceButton.IsEnabled = running && !pending;
            OpenAdminButton.IsEnabled = _configured && running;
            OpenUserButton.IsEnabled = _configured && running;
            MinimizeTrayButton.IsEnabled = _configured;
            try
            {
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                {
                    var s = db.GetSettings();
                    if (s != null)
                    {
                        _siteName = DisplaySiteName(s);
                        Title = _siteName;
                        TitleText.Text = _siteName + " " + AppVersion.Version;
                        var secureBaseUrl = BuildCanonicalPublicBaseUrl(s);
                        var secureReady = s.PublicMode == "Local" || !s.EnableHttps || !string.IsNullOrWhiteSpace(s.CertificateThumbprint);
                        OverviewPublicBox.Text = string.IsNullOrWhiteSpace(secureBaseUrl) ? "—" : secureBaseUrl;
                        OverviewUserLoginBox.Text = secureReady && !string.IsNullOrWhiteSpace(secureBaseUrl) ? secureBaseUrl.TrimEnd('/') + HttpUtil.NormalizeSecretPath(s.UserPath, "/user_login_disk") : "—";
                        OverviewSchemeHint.Text = s.PublicMode == "Local" ? "HTTP " + s.HttpPort : "HTTP " + s.HttpPort + " → HTTPS " + s.HttpsPort + ". HTTPS " + s.HttpsPort + " accepts only https:// connections.";
                        OverviewHttpsText.Text = !s.EnableHttps ? UiText.Get(_language, "httpsDisabled") : string.IsNullOrWhiteSpace(s.CertificateThumbprint) ? UiText.Get(_language, "httpsPending") : UiText.Get(_language, "httpsReady");
                        CopyPublicButton.IsEnabled = !string.IsNullOrWhiteSpace(secureBaseUrl);
                        CopyUserLoginButton.IsEnabled = OverviewUserLoginBox.Text != "—";
                        ConfigureHttpsButton.IsEnabled = s.EnableHttps;
                        RefreshStorageUsagePanel(s);
                        InstallServiceBox.IsChecked = _serviceInstaller.IsAutomaticBootStart();
                        TrayAutoStartBox.IsChecked = s.TrayAutoStartEnabled;
                    }
                }
            }
            catch (Exception ex)
            {
                OverviewHttpsText.Text = ex.Message;
                try
                {
                    using (var log = new LogService())
                        log.App("control-status-error " + ex);
                }
                catch
                {
                }
            }
            UpdateTrayState(running);
        }

        private void RefreshStorageUsagePanel(AppSettings settings)
        {
            StorageUsagePanel.Children.Clear();
            foreach (var location in StorageService.GetLocations(settings))
            {
                var border = new Border { Background = new SolidColorBrush(Color.FromRgb(245, 247, 248)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = location.Path, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(location.Path)));
                    var diskUsed = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
                    var percent = drive.TotalSize > 0 ? diskUsed * 100d / drive.TotalSize : 0d;
                    panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = percent, Height = 12, Margin = new Thickness(0, 8, 0, 5) });
                    panel.Children.Add(new TextBlock { Text = UiText.Get(_language, "aptofiUsed") + ": " + FormatBytes(location.UsedBytes) + " · " + UiText.Get(_language, "diskFree") + ": " + FormatBytes(drive.AvailableFreeSpace), Foreground = Brushes.DimGray });
                }
                catch (Exception ex)
                {
                    panel.Children.Add(new TextBlock { Text = ex.Message, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) });
                }
                border.Child = panel;
                StorageUsagePanel.Children.Add(border);
            }
        }

        private void RefreshStorageList()
        {
            var selected = _selectedStorageIndex;
            StorageLocationsList.Items.Clear();
            foreach (var location in _storageLocations)
                StorageLocationsList.Items.Add(location.Path + (location.QuotaBytes > 0 ? " · " + FormatBytes(location.QuotaBytes) : string.Empty));
            if (selected >= 0 && selected < StorageLocationsList.Items.Count)
                StorageLocationsList.SelectedIndex = selected;
        }

        private void RefreshStorageListSelectionSafe()
        {
            var selected = _selectedStorageIndex;
            RefreshStorageList();
            if (selected >= 0 && selected < StorageLocationsList.Items.Count)
                StorageLocationsList.SelectedIndex = selected;
            RefreshSelectedStorageEditor();
        }

        private void CopyLogButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = LogTextBox.Text ?? string.Empty;
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);
            }
            catch
            {
            }
        }

        private void InitializeLiveLogs()
        {
            try
            {
                AppPaths.EnsureRuntimeDirectories();
                RefreshLogs();
                _logWatcher = new FileSystemWatcher(AppPaths.LogsDirectory, "*.log")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _logWatcher.Changed += (sender, args) => _logDirty = true;
                _logWatcher.Created += (sender, args) => _logDirty = true;
                _logWatcher.Deleted += (sender, args) => _logDirty = true;
                _logWatcher.Renamed += (sender, args) => _logDirty = true;
                _logWatcher.Error += (sender, args) => _logDirty = true;
                _logWatcher.EnableRaisingEvents = true;
                _logRefreshTimer.Interval = TimeSpan.FromMilliseconds(500);
                _logRefreshTimer.Tick += (sender, args) =>
                {
                    if (!_logDirty)
                        return;
                    _logDirty = false;
                    RefreshLogs();
                };
                _logRefreshTimer.Start();
            }
            catch (Exception ex)
            {
                LogTextBox.Text = ex.Message;
            }
        }

        private void OpenLogsButton_OnClick(object sender, RoutedEventArgs e)
        {
            AppPaths.EnsureRuntimeDirectories();
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.LogsDirectory + "\"") { UseShellExecute = true });
        }

        private void RefreshLogs()
        {
            try
            {
                AppPaths.EnsureRuntimeDirectories();
                var files = Directory.GetFiles(AppPaths.LogsDirectory, "*.log").Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTimeUtc).Take(5).OrderBy(x => x.LastWriteTimeUtc).ToList();
                var raw = new List<string>();
                foreach (var file in files)
                {
                    var lines = ReadLastLines(file.FullName, 240).ToList();
                    if (file.Name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase))
                        lines = RemoveClipboardCrashNoise(lines).ToList();
                    if (lines.Count == 0)
                        continue;
                    raw.Add("===== " + file.Name + " =====");
                    raw.AddRange(lines);
                }

                var rendered = AggregateLogLines(raw.Where(line => !IsDisplayNoise(line))).Where(LogLineMatchesFilter).ToList();
                LogListBox.Items.Clear();
                foreach (var line in rendered)
                {
                    var block = new TextBlock
                    {
                        Text = line,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(6, 2, 6, 2),
                        Foreground = LogBrush(line)
                    };
                    LogListBox.Items.Add(block);
                }
                LogTextBox.Text = string.Join(Environment.NewLine, rendered);
                if (LogListBox.Items.Count > 0)
                    LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
            }
            catch (Exception ex)
            {
                LogListBox.Items.Clear();
                LogListBox.Items.Add(new TextBlock { Text = ex.Message, Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap });
                LogTextBox.Text = ex.Message;
            }
        }

        private IEnumerable<string> AggregateLogLines(IEnumerable<string> lines)
        {
            string lastKey = null;
            string lastLine = null;
            var count = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("===== ", StringComparison.Ordinal))
                {
                    if (count > 0)
                        yield return count > 1 ? lastLine + "  ×" + count : lastLine;
                    lastKey = null;
                    lastLine = null;
                    count = 0;
                    yield return line;
                    continue;
                }
                var key = NormalizeLogKey(line);
                if (lastKey != null && string.Equals(lastKey, key, StringComparison.Ordinal))
                {
                    count++;
                    lastLine = line;
                    continue;
                }
                if (count > 0)
                    yield return count > 1 ? lastLine + "  ×" + count : lastLine;
                lastKey = key;
                lastLine = line;
                count = 1;
            }
            if (count > 0)
                yield return count > 1 ? lastLine + "  ×" + count : lastLine;
        }

        private static IEnumerable<string> RemoveClipboardCrashNoise(IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
                yield break;

            var block = new List<string>();
            foreach (var line in lines)
            {
                if (IsCrashEntryStart(line) && block.Count > 0)
                {
                    if (!block.Any(IsClipboardCrashLine))
                    {
                        foreach (var item in block)
                            yield return item;
                    }
                    block.Clear();
                }
                block.Add(line);
            }

            if (block.Count > 0 && !block.Any(IsClipboardCrashLine))
            {
                foreach (var item in block)
                    yield return item;
            }
        }

        private static bool IsCrashEntryStart(string line)
        {
            return !string.IsNullOrWhiteSpace(line) && line.Length > 20 &&
                   char.IsDigit(line[0]) && line[4] == '-' && line[7] == '-' && line[10] == 'T';
        }

        private static bool IsClipboardCrashLine(string line)
        {
            var text = line ?? string.Empty;
            return text.IndexOf("CLIPBRD_E_CANT_OPEN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("OpenClipboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("System.Windows.Clipboard", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDisplayNoise(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("===== ", StringComparison.Ordinal))
                return false;
            var text = line.Trim();
            if ((text.IndexOf("dns-maintenance-error", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("dns-address-error", StringComparison.OrdinalIgnoreCase) >= 0) &&
                (text.IndexOf("TaskCanceledException", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Отменена задача", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            if ((text.IndexOf("ip=127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("ip=::1", StringComparison.OrdinalIgnoreCase) >= 0) &&
                text.IndexOf("method=GET", StringComparison.OrdinalIgnoreCase) >= 0 &&
                text.IndexOf("path=/favicon.ico", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private static string NormalizeLogKey(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;
            var text = line.Trim();
            if (text.Length > 24 && char.IsDigit(text[0]) && text[4] == '-' && text[7] == '-')
            {
                var firstSpace = text.IndexOf(' ');
                if (firstSpace >= 0)
                {
                    var secondSpace = text.IndexOf(' ', firstSpace + 1);
                    if (secondSpace >= 0 && secondSpace + 1 < text.Length)
                        text = text.Substring(secondSpace + 1);
                }
            }
            if (text.StartsWith("dns-address-error ", StringComparison.OrdinalIgnoreCase))
                return "dns-address-error " + CollapseDnsError(text);
            if (text.StartsWith("dns-maintenance-error ", StringComparison.OrdinalIgnoreCase))
                return "dns-maintenance-error " + CollapseDnsError(text);
            return text;
        }

        private static string CollapseDnsError(string text)
        {
            if (text.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return "timeout";
            if (text.IndexOf("RCODE 5", StringComparison.OrdinalIgnoreCase) >= 0)
                return "rcode5";
            return text;
        }

        private bool LogLineMatchesFilter(string line)
        {
            if (line.StartsWith("===== ", StringComparison.Ordinal))
                return true;
            var level = LogLevel(line);
            switch (_logFilter)
            {
                case "error": return level == "error";
                case "warn": return level == "warn";
                case "info": return level == "info";
                default: return true;
            }
        }

        private static string LogLevel(string line)
        {
            var text = line ?? string.Empty;
            if (text.IndexOf("CRITICAL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("-error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf(" error ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
                return "error";
            if (text.IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("retry", StringComparison.OrdinalIgnoreCase) >= 0)
                return "warn";
            return "info";
        }

        private static Brush LogBrush(string line)
        {
            switch (LogLevel(line))
            {
                case "error": return Brushes.Firebrick;
                case "warn": return Brushes.DarkOrange;
                default: return Brushes.Black;
            }
        }

        private static IEnumerable<string> ReadLastLines(string path, int count)
        {
            var lines = new Queue<string>(Math.Max(1, count));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var tailBytes = Math.Min(stream.Length, 2L * 1024L * 1024L);
                stream.Seek(-tailBytes, SeekOrigin.End);
                using (var reader = new StreamReader(stream))
                {
                    if (tailBytes < stream.Length)
                        reader.ReadLine();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (lines.Count >= count)
                            lines.Dequeue();
                        lines.Enqueue(line);
                    }
                }
            }
            return lines.ToArray();
        }

        private void LanguageBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageBox.SelectedItem is ComboBoxItem item && item.Tag != null)
                _language = item.Tag.ToString();
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            if (!IsInitialized)
                return;
            var displayName = string.IsNullOrWhiteSpace(_siteName) ? AppVersion.ProductName : _siteName;
            Title = displayName;
            TitleText.Text = displayName + " " + AppVersion.Version;
            OverviewTab.Header = UiText.Get(_language, "overview");
            StorageTab.Header = UiText.Get(_language, "storageTab");
            NetworkTab.Header = UiText.Get(_language, "networkTab");
            DomainTab.Header = UiText.Get(_language, "domainHttpsTab");
            AccountTab.Header = UiText.Get(_language, "accountTab");
            LogsTab.Header = UiText.Get(_language, "logsTab");
            OverviewStateLabel.Text = UiText.Get(_language, "serverState");
            OverviewPublicLabel.Text = UiText.Get(_language, "publicAddress");
            OverviewUserLoginLabel.Text = UiText.Get(_language, "openUser");
            OverviewStorageLabel.Text = UiText.Get(_language, "storageState");
            StorageHintText.Text = UiText.Get(_language, "storageHint");
            ServerQuotaLabel.Text = UiText.Get(_language, "serverQuota");
            SelectedStorageQuotaLabel.Text = UiText.Get(_language, "selectedStorageQuota");
            ApplyStorageQuotaButton.Content = UiText.Get(_language, "applyStorageQuota");
            AddStorageButton.Content = UiText.Get(_language, "addStorage");
            RemoveStorageButton.Content = UiText.Get(_language, "removeStorage");
            PublicHintText.Text = UiText.Get(_language, "publicHint");
            ModeLabel.Text = UiText.Get(_language, "mode");
            ((ComboBoxItem)ModeBox.Items[0]).Content = UiText.Get(_language, "direct");
            ((ComboBoxItem)ModeBox.Items[1]).Content = UiText.Get(_language, "vps");
            ((ComboBoxItem)ModeBox.Items[2]).Content = UiText.Get(_language, "local");
            BindLabel.Text = UiText.Get(_language, "bind");
            PublicLabel.Text = UiText.Get(_language, "publicIp");
            DetectButton.Content = UiText.Get(_language, "detect");
            HttpLabel.Text = UiText.Get(_language, "http");
            HttpsLabel.Text = UiText.Get(_language, "https");
            AdminPathLabel.Text = UiText.Get(_language, "adminPath");
            UserPathLabel.Text = UiText.Get(_language, "userPath");
            GenerateAdminButton.Content = UiText.Get(_language, "generate");
            GenerateUserButton.Content = UiText.Get(_language, "generate");
            VpsSectionLabel.Text = UiText.Get(_language, "vpsSetup");
            VpsHostLabel.Text = UiText.Get(_language, "vpsHost");
            VpsPortLabel.Text = UiText.Get(_language, "vpsPort");
            VpsUserLabel.Text = UiText.Get(_language, "vpsUser");
            VpsPasswordLabel.Text = UiText.Get(_language, "vpsPassword");
            VpsSudoBox.Content = UiText.Get(_language, "vpsSudo");
            DomainHintText.Text = UiText.Get(_language, "domainHint");
            DomainLabel.Text = UiText.Get(_language, "domain");
            DnsModeLabel.Text = UiText.Get(_language, "dnsMode");
            ((ComboBoxItem)DnsModeBox.Items[0]).Content = UiText.Get(_language, "dnsManual");
            ((ComboBoxItem)DnsModeBox.Items[1]).Content = UiText.Get(_language, "dnsRfc2136");
            DnsServerLabel.Text = UiText.Get(_language, "dnsServer");
            DnsZoneLabel.Text = UiText.Get(_language, "dnsZone");
            DnsKeyNameLabel.Text = UiText.Get(_language, "dnsKeyName");
            DnsAlgorithmLabel.Text = UiText.Get(_language, "dnsAlgorithm");
            DnsSecretLabel.Text = UiText.Get(_language, "dnsSecret");
            DnsAutoAddressBox.Content = UiText.Get(_language, "dnsAutoAddress");
            TestDnsButton.Content = UiText.Get(_language, "testDns");
            ConfigureHttpsButton.Content = UiText.Get(_language, "issueHttps");
            CopyPublicButton.Content = UiText.Get(_language, "copy");
            CopyUserLoginButton.Content = UiText.Get(_language, "copy");
            AcmeEmailLabel.Text = UiText.Get(_language, "acmeEmail");
            AcmeTermsBox.Content = UiText.Get(_language, "caTerms");
            EmailLabel.Text = UiText.Get(_language, "email");
            PasswordLabel.Text = UiText.Get(_language, "password");
            RepeatLabel.Text = UiText.Get(_language, "repeat");
            InstallServiceBox.Content = UiText.Get(_language, "installService");
            TrayAutoStartBox.Content = UiText.Get(_language, "trayAutostart");
            LogFilterLabel.Text = UiText.Get(_language, "logFilter");
            ((ComboBoxItem)LogFilterBox.Items[0]).Content = UiText.Get(_language, "logAll");
            ((ComboBoxItem)LogFilterBox.Items[1]).Content = UiText.Get(_language, "logErrors");
            ((ComboBoxItem)LogFilterBox.Items[2]).Content = UiText.Get(_language, "logWarnings");
            ((ComboBoxItem)LogFilterBox.Items[3]).Content = UiText.Get(_language, "logInfo");
            RefreshLogsButton.Content = UiText.Get(_language, "refreshLogs");
            CopyLogButton.Content = UiText.Get(_language, "copyLog");
            OpenLogsButton.Content = UiText.Get(_language, "openLogs");
            ClearLogDisplayButton.Content = UiText.Get(_language, "clearLogDisplay");
            LogSettingsExpander.Header = UiText.Get(_language, "logSettings");
            LogSettingsHintText.Text = UiText.Get(_language, "logSettingsHint");
            LogRetentionDaysLabel.Text = UiText.Get(_language, "logRetentionDays");
            LogMaxFileLabel.Text = UiText.Get(_language, "logMaxFileMiB");
            LogMaxDirectoryLabel.Text = UiText.Get(_language, "logMaxDirectoryMiB");
            LogMaintenanceIntervalLabel.Text = UiText.Get(_language, "logMaintenanceInterval");
            AccessLogModeLabel.Text = UiText.Get(_language, "accessLogMode");
            ((ComboBoxItem)AccessLogModeBox.Items[0]).Content = UiText.Get(_language, "accessLogCompact");
            ((ComboBoxItem)AccessLogModeBox.Items[1]).Content = UiText.Get(_language, "accessLogFull");
            AccessAggregateMinutesLabel.Text = UiText.Get(_language, "accessAggregateMinutes");
            WizardButton.Content = UiText.Get(_language, "setupWizard");
            DiagnosticsButton.Content = UiText.Get(_language, "diagnostics");
            RestartServiceButton.Content = UiText.Get(_language, "restart");
            HealthServiceLabel.Text = UiText.Get(_language, "healthService");
            HealthHttpLabel.Text = UiText.Get(_language, "healthHttp");
            HealthHttpsLabel.Text = UiText.Get(_language, "healthHttps");
            HealthDnsLabel.Text = UiText.Get(_language, "healthDns");
            HealthCertificateLabel.Text = UiText.Get(_language, "healthCertificate");
            HealthStorageLabel.Text = UiText.Get(_language, "healthStorage");
            HealthExternalLabel.Text = UiText.Get(_language, "healthExternal");
            SaveButton.Content = UiText.Get(_language, "save");
            ApplyButton.Content = UiText.Get(_language, "saveStart");
            MinimizeTrayButton.Content = UiText.Get(_language, "minimizeTray");
            OpenAdminButton.Content = UiText.Get(_language, "openAdmin");
            OpenUserButton.Content = UiText.Get(_language, "openUser");
            ApplyTrayLanguage();
            RefreshRuntimeSummary();
            RefreshSelectedStorageEditor();
        }

        private void InitializeTray()
        {
            _trayMenu = new WinForms.ContextMenuStrip();
            _trayOpenItem = new WinForms.ToolStripMenuItem();
            _trayAdminItem = new WinForms.ToolStripMenuItem();
            _trayUserItem = new WinForms.ToolStripMenuItem();
            _trayServiceItem = new WinForms.ToolStripMenuItem();
            _trayExitItem = new WinForms.ToolStripMenuItem();
            _trayOpenItem.Click += (sender, args) => RestoreFromTray();
            _trayAdminItem.Click += (sender, args) => Dispatcher.BeginInvoke(new Action(() => OpenAdminButton_OnClick(null, null)));
            _trayUserItem.Click += (sender, args) => Dispatcher.BeginInvoke(new Action(() => OpenUserButton_OnClick(null, null)));
            _trayServiceItem.Click += (sender, args) => Dispatcher.BeginInvoke(new Action(() => ServiceToggleButton_OnClick(null, null)));
            _trayExitItem.Click += (sender, args) => Dispatcher.BeginInvoke(new Action(ExitTray));
            _trayMenu.Items.Add(_trayOpenItem);
            _trayMenu.Items.Add(_trayAdminItem);
            _trayMenu.Items.Add(_trayUserItem);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
            _trayMenu.Items.Add(_trayServiceItem);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
            _trayMenu.Items.Add(_trayExitItem);
            _trayIcon = new WinForms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName), Visible = _configured, ContextMenuStrip = _trayMenu };
            _trayIcon.DoubleClick += (sender, args) => RestoreFromTray();
            ApplyTrayLanguage();
        }

        private void MainWindow_OnStateChanged(object sender, EventArgs e)
        {
            if (_configured && WindowState == WindowState.Minimized)
                HideToTray();
        }

        private void MainWindow_OnClosing(object sender, CancelEventArgs e)
        {
            if (_allowClose || !_configured)
                return;
            e.Cancel = true;
            HideToTray();
        }

        private void MainWindow_OnClosed(object sender, EventArgs e)
        {
            _statusTimer.Stop();
            _healthTimer.Stop();
            _logRefreshTimer.Stop();
            if (_logWatcher != null)
            {
                _logWatcher.EnableRaisingEvents = false;
                _logWatcher.Dispose();
                _logWatcher = null;
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            if (_trayMenu != null)
            {
                _trayMenu.Dispose();
                _trayMenu = null;
            }
        }

        private void HideToTray()
        {
            if (!_configured)
                return;
            if (_trayIcon != null)
                _trayIcon.Visible = true;
            ShowInTaskbar = false;
            Hide();
            WindowState = WindowState.Normal;
        }

        public void RestoreFromTray()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Show();
                ShowInTaskbar = true;
                WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();
            }));
        }

        private void ExitTray()
        {
            _allowClose = true;
            Application.Current.Shutdown();
        }

        private void ApplyTrayAutoStartSetting()
        {
            if (TrayAutoStartBox.IsChecked == true)
                _trayAutoStartManager.Enable();
            else
                _trayAutoStartManager.Disable();
        }

        private void EnsureConfiguredTrayAutoStart()
        {
            if (!_configured)
                return;
            try
            {
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                {
                    var settings = db.GetSettings();
                    if (settings == null)
                        return;
                    TrayAutoStartBox.IsChecked = settings.TrayAutoStartEnabled;
                    if (settings.TrayAutoStartEnabled)
                        _trayAutoStartManager.Enable();
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void ApplyTrayLanguage()
        {
            if (_trayOpenItem == null)
                return;
            _trayOpenItem.Text = UiText.Get(_language, "trayOpen");
            _trayAdminItem.Text = UiText.Get(_language, "trayOpenAdmin");
            _trayUserItem.Text = UiText.Get(_language, "trayOpenUser");
            _trayExitItem.Text = UiText.Get(_language, "trayExit");
        }

        private void UpdateTrayState(bool running)
        {
            if (_trayIcon == null)
                return;
            _trayServiceItem.Text = UiText.Get(_language, running ? "stop" : "start");
            _trayAdminItem.Enabled = _configured && running;
            _trayUserItem.Enabled = _configured && running;
            var state = UiText.Get(_language, running ? "trayRunning" : "trayStopped");
            var text = (string.IsNullOrWhiteSpace(_siteName) ? AppVersion.ProductName : _siteName) + " " + AppVersion.Version + " - " + state;
            _trayIcon.Text = text.Length <= 63 ? text : text.Substring(0, 63);
        }

        private static string DisplaySiteName(AppSettings settings)
        {
            return string.IsNullOrWhiteSpace(settings?.SiteName) ? AppVersion.ProductName : settings.SiteName.Trim();
        }

        private void AuthorLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(AppVersion.AuthorUrl) { UseShellExecute = true });
            e.Handled = true;
        }

        private void SelectLanguage(string language)
        {
            for (var i = 0; i < LanguageBox.Items.Count; i++)
                if (((ComboBoxItem)LanguageBox.Items[i]).Tag.ToString() == language) { LanguageBox.SelectedIndex = i; return; }
        }

        private void SelectMode(string mode)
        {
            for (var i = 0; i < ModeBox.Items.Count; i++)
                if (((ComboBoxItem)ModeBox.Items[i]).Tag.ToString() == mode) { ModeBox.SelectedIndex = i; return; }
        }

        private void SelectDnsMode(string mode)
        {
            var target = string.Equals(mode, "Rfc2136", StringComparison.OrdinalIgnoreCase) ? "Rfc2136" : "Manual";
            for (var i = 0; i < DnsModeBox.Items.Count; i++)
                if (((ComboBoxItem)DnsModeBox.Items[i]).Tag.ToString() == target) { DnsModeBox.SelectedIndex = i; return; }
        }

        private void SelectDnsAlgorithm(string algorithm)
        {
            var target = string.IsNullOrWhiteSpace(algorithm) ? "hmac-sha256" : algorithm.ToLowerInvariant();
            for (var i = 0; i < DnsAlgorithmBox.Items.Count; i++)
                if (((ComboBoxItem)DnsAlgorithmBox.Items[i]).Tag.ToString() == target) { DnsAlgorithmBox.SelectedIndex = i; return; }
            DnsAlgorithmBox.SelectedIndex = 0;
        }

        private void SelectAccessLogMode(string value)
        {
            var target = string.Equals(value, "Full", StringComparison.OrdinalIgnoreCase) ? "Full" : "Compact";
            for (var i = 0; i < AccessLogModeBox.Items.Count; i++)
            {
                var item = AccessLogModeBox.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(Convert.ToString(item.Tag), target, StringComparison.OrdinalIgnoreCase))
                {
                    AccessLogModeBox.SelectedIndex = i;
                    return;
                }
            }
            AccessLogModeBox.SelectedIndex = 0;
        }

        private static int NormalizeLogValue(int value, int fallback)
        {
            return value > 0 ? value : fallback;
        }

        private static string BuildCanonicalPublicBaseUrl(AppSettings settings)
        {
            if (settings == null)
                return null;
            if (string.Equals(settings.PublicMode, "Local", StringComparison.OrdinalIgnoreCase))
                return "http://127.0.0.1:" + settings.HttpPort;
            if (string.Equals(settings.PublicMode, "Vps", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(settings.PublicBaseUrl) ? null : settings.PublicBaseUrl.TrimEnd('/');
            var host = !string.IsNullOrWhiteSpace(settings.HttpsIdentifier) ? settings.HttpsIdentifier : !string.IsNullOrWhiteSpace(settings.Domain) ? settings.Domain : CleanHost(settings.PublicBaseUrl);
            if (string.IsNullOrWhiteSpace(host))
                return null;
            return new UriBuilder("https", CleanHost(host), settings.HttpsPort).Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static string BuildPublicBaseUrl(string mode, string host, int httpPort, int httpsPort, bool https)
        {
            if (mode == "Local" || string.IsNullOrWhiteSpace(host))
                return "http://127.0.0.1:" + httpPort;
            var builder = new UriBuilder(https ? "https" : "http", CleanHost(host), https ? httpsPort : httpPort);
            return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static string CleanHost(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return uri.Host;
            var host = value.Trim();
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host.Count(c => c == ':') == 1)
                host = host.Substring(0, colon);
            return host.Trim().Trim('.').ToLowerInvariant();
        }

        private static string PublicHost(string baseUrl)
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl ?? string.Empty;
        }

        private static StorageLocationSetting CloneLocation(StorageLocationSetting location)
        {
            return new StorageLocationSetting { Id = location.Id, Path = location.Path, QuotaBytes = location.QuotaBytes, UsedBytes = location.UsedBytes, Enabled = location.Enabled };
        }

        private void ShowError(string text)
        {
            MessageText.Foreground = Brushes.Firebrick;
            MessageText.Text = text ?? string.Empty;
        }

        private void ShowSuccess(string text)
        {
            MessageText.Foreground = Brushes.SeaGreen;
            MessageText.Text = text ?? string.Empty;
        }

        private static string FormatBytes(long value)
        {
            var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
            double n = Math.Max(0, value);
            var index = 0;
            while (n >= 1024d && index < units.Length - 1) { n /= 1024d; index++; }
            return n.ToString(index == 0 ? "0" : "0.##") + " " + units[index];
        }

        private void WizardButton_OnClick(object sender, RoutedEventArgs e)
        {
            OpenSetupWizard(false);
        }

        private void OpenSetupWizard(bool firstRun)
        {
            try
            {
                var wizard = new SetupWizardWindow(this, firstRun);
                wizard.ShowDialog();
                RefreshRuntimeSummary();
                _ = RefreshHealthAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void DiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                new DiagnosticsWindow(this).ShowDialog();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void RestartServiceButton_OnClick(object sender, RoutedEventArgs e)
        {
            var result = _serviceInstaller.RestartInternal();
            if (!result.Success)
                ShowError(result.Output);
            else
                ShowSuccess(UiText.Get(_language, "serviceRestarted"));
            _lastHealthLevel = ControlHealthLevel.Unknown;
            RefreshRuntimeSummary();
            _ = RefreshHealthAsync();
        }

        private async void TestDnsButton_OnClick(object sender, RoutedEventArgs e)
        {
            TestDnsButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            try
            {
                ShowSuccess(UiText.Get(_language, "dnsTesting"));
                await SaveConfigurationAsync();
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                using (var log = new LogService())
                {
                    var service = new DnsUpdateService(db, crypto, log);
                    var result = await service.TestAsync(db.GetSettings());
                    if (!result.Success)
                        throw new InvalidOperationException(result.Error);
                    ShowSuccess(UiText.Get(_language, "dnsTestOk") + " " + result.Domain + " → " + result.Details);
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                TestDnsButton.IsEnabled = true;
                SaveButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;
                await RefreshHealthAsync();
            }
        }

        private void LogFilterBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LogFilterBox == null || LogFilterBox.SelectedItem == null)
                return;
            var item = LogFilterBox.SelectedItem as ComboBoxItem;
            _logFilter = item == null ? "all" : Convert.ToString(item.Tag) ?? "all";
            if (IsInitialized)
                RefreshLogs();
        }

        private void RefreshLogsButton_OnClick(object sender, RoutedEventArgs e)
        {
            RefreshLogs();
        }

        private void ClearLogDisplayButton_OnClick(object sender, RoutedEventArgs e)
        {
            LogListBox.Items.Clear();
            LogTextBox.Text = string.Empty;
        }

        internal SetupWizardData GetWizardData()
        {
            var primary = _storageLocations.FirstOrDefault();
            return new SetupWizardData
            {
                StoragePath = primary?.Path ?? string.Empty,
                Mode = ModeBox.SelectedItem is ComboBoxItem modeItem ? Convert.ToString(modeItem.Tag) : "Direct",
                Bind = BindBox.Text,
                PublicIp = PublicBox.Text,
                HttpPort = HttpPortBox.Text,
                HttpsPort = HttpsPortBox.Text,
                AdminPath = AdminPathBox.Text,
                UserPath = UserPathBox.Text,
                VpsHost = VpsHostBox.Text,
                VpsPort = VpsPortBox.Text,
                VpsUser = VpsUserBox.Text,
                VpsUseSudo = VpsSudoBox.IsChecked == true,
                Domain = DomainBox.Text,
                DnsMode = DnsModeBox.SelectedItem is ComboBoxItem dnsItem ? Convert.ToString(dnsItem.Tag) : "Manual",
                DnsServer = DnsServerBox.Text,
                DnsZone = DnsZoneBox.Text,
                DnsKeyName = DnsKeyNameBox.Text,
                DnsAlgorithm = DnsAlgorithmBox.SelectedItem is ComboBoxItem algorithmItem ? Convert.ToString(algorithmItem.Tag) : "hmac-sha256",
                DnsAutoAddress = DnsAutoAddressBox.IsChecked == true,
                AcmeEmail = AcmeEmailBox.Text,
                AcmeTerms = AcmeTermsBox.IsChecked == true,
                Email = EmailBox.Text,
                TrayAutoStart = TrayAutoStartBox.IsChecked == true
            };
        }

        internal async Task<string> ApplyWizardAndStartAsync(SetupWizardData data, Action<WizardProgress> progress)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            progress = progress ?? (_ => { });
            progress(new WizardProgress { Percent = 10, Text = "Применяем параметры мастера..." });
            if (_storageLocations.Count == 0)
                _storageLocations.Add(new StorageLocationSetting { Id = "primary", Enabled = true });
            _storageLocations[0].Path = data.StoragePath;
            RefreshStorageList();
            StorageLocationsList.SelectedIndex = 0;
            SelectMode(data.Mode);
            BindBox.Text = data.Bind;
            PublicBox.Text = data.PublicIp;
            HttpPortBox.Text = data.HttpPort;
            HttpsPortBox.Text = data.HttpsPort;
            AdminPathBox.Text = data.AdminPath;
            UserPathBox.Text = data.UserPath;
            VpsHostBox.Text = data.VpsHost ?? string.Empty;
            VpsPortBox.Text = string.IsNullOrWhiteSpace(data.VpsPort) ? "22" : data.VpsPort;
            VpsUserBox.Text = string.IsNullOrWhiteSpace(data.VpsUser) ? "root" : data.VpsUser;
            if (!string.IsNullOrWhiteSpace(data.VpsPassword))
                VpsPasswordBox.Password = data.VpsPassword;
            VpsSudoBox.IsChecked = data.VpsUseSudo;
            DomainBox.Text = data.Domain;
            SelectDnsMode(data.DnsMode);
            DnsServerBox.Text = data.DnsServer;
            DnsZoneBox.Text = data.DnsZone;
            DnsKeyNameBox.Text = data.DnsKeyName;
            SelectDnsAlgorithm(data.DnsAlgorithm);
            if (!string.IsNullOrWhiteSpace(data.DnsSecret))
                DnsSecretBox.Password = data.DnsSecret;
            DnsAutoAddressBox.IsChecked = data.DnsAutoAddress;
            AcmeEmailBox.Text = data.AcmeEmail;
            AcmeTermsBox.IsChecked = data.AcmeTerms;
            EmailBox.Text = data.Email;
            PasswordBox.Password = data.Password ?? string.Empty;
            RepeatPasswordBox.Password = data.RepeatPassword ?? string.Empty;
            TrayAutoStartBox.IsChecked = data.TrayAutoStart;
            ModeBox_OnSelectionChanged(null, null);
            DnsModeBox_OnSelectionChanged(null, null);

            progress(new WizardProgress { Percent = 25, Text = "Сохраняем конфигурацию и учётную запись..." });
            await SaveConfigurationAsync();
            ApplyTrayAutoStartSetting();

            var crypto = new CryptoService();
            using (var db = new Database(crypto))
            {
                var settings = db.GetSettings();
                if (DnsUpdateService.IsConfigured(settings))
                {
                    progress(new WizardProgress { Percent = 40, Text = "Проверяем RFC2136/TSIG и A-запись..." });
                    using (var log = new LogService())
                    {
                        var dns = new DnsUpdateService(db, crypto, log);
                        var dnsResult = await dns.TestAsync(settings);
                        if (!dnsResult.Success)
                            throw new InvalidOperationException("DNS: " + dnsResult.Error);
                    }
                }
            }

            progress(new WizardProgress { Percent = 55, Text = "Проверяем Windows Firewall..." });
            var windows = new WindowsNetworkService();
            AppSettings saved;
            using (var db = new Database(new CryptoService()))
                saved = db.GetSettings();
            var firewall = windows.EnsureFirewall(saved.HttpPort, saved.HttpsPort);
            if (!firewall.Success)
                throw new InvalidOperationException(firewall.Output);

            if (string.Equals(saved.PublicMode, "Vps", StringComparison.OrdinalIgnoreCase))
            {
                progress(new WizardProgress { Percent = 65, Text = "Настраиваем VPS-туннель..." });
                await ConfigureVpsIfNeededAsync();
            }

            if (string.Equals(saved.PublicMode, "Direct", StringComparison.OrdinalIgnoreCase) && saved.EnableHttps)
            {
                progress(new WizardProgress { Percent = 70, Text = "Выпускаем и устанавливаем HTTPS-сертификат..." });
                await ConfigureHttpsAsync(false);
            }
            else
            {
                progress(new WizardProgress { Percent = 75, Text = "Устанавливаем или перезапускаем службу Windows..." });
                var serviceResult = _serviceInstaller.IsInstalled() ? _serviceInstaller.RestartInternal() : _serviceInstaller.InstallInternal();
                if (!serviceResult.Success)
                    throw new InvalidOperationException(serviceResult.Output);
            }

            _configured = true;
            if (_trayIcon != null)
                _trayIcon.Visible = true;
            progress(new WizardProgress { Percent = 88, Text = "Проверяем фактическое состояние сервера..." });
            var report = await RunControlDiagnosticsAsync(true);
            if (report.Level == ControlHealthLevel.Error)
                throw new InvalidOperationException(report.Summary + " Откройте «Диагностика» для подробностей.");
            RefreshRuntimeSummary();
            MainTabs.SelectedItem = OverviewTab;
            progress(new WizardProgress { Percent = 100, Text = "APTOFI File Sharing готов к работе." });
            return report.Level == ControlHealthLevel.Warning
                ? "APTOFI File Sharing запущен, но есть предупреждения. Откройте «Диагностика» для проверки."
                : "APTOFI File Sharing настроен и работает. Все основные проверки пройдены.";
        }

        internal async Task<ControlHealthReport> RunControlDiagnosticsAsync(bool deepDnsTest)
        {
            var report = new ControlHealthReport { CheckedUtc = DateTime.UtcNow, Level = ControlHealthLevel.Ok };
            var status = _serviceInstaller.Status();
            var serviceRunning = status == ServiceControllerStatus.Running;
            AddHealth(report, "service", UiText.Get(_language, "healthService"), serviceRunning ? ControlHealthLevel.Ok : ControlHealthLevel.Error,
                serviceRunning ? UiText.Get(_language, "serviceRunning") : UiText.Get(_language, "serviceStopped"),
                serviceRunning ? null : UiText.Get(_language, "recommendStartService"));

            AppSettings settings = null;
            try
            {
                var crypto = new CryptoService();
                using (var db = new Database(crypto))
                    settings = db.GetSettings();
            }
            catch (Exception ex)
            {
                AddHealth(report, "database", "База данных", ControlHealthLevel.Error, ex.Message, "Проверьте файлы базы и права доступа рядом с afsharing.exe.");
            }

            if (settings != null)
            {
                bool httpOk;
                bool httpsOk;
                if (deepDnsTest)
                {
                    httpOk = serviceRunning && await IsHttpAliveAsync(settings.HttpPort);
                    httpsOk = !settings.EnableHttps || (serviceRunning && await IsHttpsAliveAsync(settings));
                }
                else
                {
                    httpOk = serviceRunning && IsTcpPortListening(settings.HttpPort);
                    httpsOk = !settings.EnableHttps || (serviceRunning && IsTcpPortListening(settings.HttpsPort));
                }

                AddHealth(report, "http", UiText.Get(_language, "healthHttp"), httpOk ? ControlHealthLevel.Ok : ControlHealthLevel.Error,
                    httpOk ? "HTTP " + settings.HttpPort + " слушается" : "HTTP " + settings.HttpPort + " не слушается",
                    httpOk ? null : "Проверьте службу Windows и занятость HTTP-порта.");

                if (settings.EnableHttps)
                {
                    AddHealth(report, "https", UiText.Get(_language, "healthHttps"), httpsOk ? ControlHealthLevel.Ok : ControlHealthLevel.Error,
                        httpsOk ? "HTTPS " + settings.HttpsPort + " слушается" : "HTTPS " + settings.HttpsPort + " не слушается",
                        httpsOk ? null : "Проверьте сертификат, HTTPS-привязку и службу Windows.");
                }
                else
                {
                    AddHealth(report, "https", UiText.Get(_language, "healthHttps"), ControlHealthLevel.Unknown, UiText.Get(_language, "httpsDisabled"), null);
                }

                var storageOk = true;
                var storageMessage = new List<string>();
                foreach (var location in StorageService.GetLocations(settings).Where(x => x.Enabled))
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(location.Path) || !Directory.Exists(location.Path))
                        {
                            storageOk = false;
                            storageMessage.Add((location.Path ?? "<empty>") + " недоступен");
                        }
                        else
                        {
                            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(location.Path)));
                            storageMessage.Add(location.Path + " — свободно " + FormatBytes(drive.AvailableFreeSpace));
                        }
                    }
                    catch (Exception ex)
                    {
                        storageOk = false;
                        storageMessage.Add((location.Path ?? "<storage>") + ": " + ex.Message);
                    }
                }
                AddHealth(report, "storage", UiText.Get(_language, "healthStorage"), storageOk ? ControlHealthLevel.Ok : ControlHealthLevel.Error,
                    storageMessage.Count == 0 ? "Хранилище не настроено" : string.Join("; ", storageMessage),
                    storageOk ? null : "Проверьте подключение диска, путь и права записи.");

                if (!string.IsNullOrWhiteSpace(settings.Domain))
                {
                    if (deepDnsTest)
                    {
                        try
                        {
                            var addresses = await Dns.GetHostAddressesAsync(settings.Domain);
                            var ipv4 = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                            var level = ipv4 == null ? ControlHealthLevel.Error : ControlHealthLevel.Ok;
                            var message = ipv4 == null ? "A-запись не найдена" : settings.Domain + " → " + ipv4;
                            AddHealth(report, "dns", UiText.Get(_language, "healthDns"), level, message,
                                level == ControlHealthLevel.Ok ? null : "Проверьте домен и DNS-сервер.");
                        }
                        catch (Exception ex)
                        {
                            AddHealth(report, "dns", UiText.Get(_language, "healthDns"), ControlHealthLevel.Error, ex.Message, "Проверьте домен и DNS-сервер.");
                        }
                    }
                    else if (DnsUpdateService.IsConfigured(settings))
                    {
                        var message = settings.DnsAutoUpdateAddress ? "RFC2136/TSIG настроен · автообновление A включено" : "RFC2136/TSIG настроен · автообновление A выключено";
                        if (settings.LastDnsUpdateUtc.HasValue)
                            message += " · последняя успешная синхронизация " + settings.LastDnsUpdateUtc.Value.ToLocalTime().ToString("G");
                        AddHealth(report, "dns", UiText.Get(_language, "healthDns"), ControlHealthLevel.Ok, message, null);
                    }
                    else
                    {
                        AddHealth(report, "dns", UiText.Get(_language, "healthDns"), ControlHealthLevel.Unknown, "Внешний DNS · проверяется только вручную", null);
                    }
                }
                else
                {
                    AddHealth(report, "dns", UiText.Get(_language, "healthDns"), ControlHealthLevel.Unknown, "Домен не настроен", null);
                }

                if (deepDnsTest && DnsUpdateService.IsConfigured(settings))
                {
                    try
                    {
                        var crypto = new CryptoService();
                        using (var db = new Database(crypto))
                        using (var log = new LogService())
                        {
                            var dns = new DnsUpdateService(db, crypto, log);
                            var result = await dns.TestAsync(settings);
                            AddHealth(report, "dns_update", "RFC2136 / TSIG", result.Success ? ControlHealthLevel.Ok : ControlHealthLevel.Error,
                                result.Success ? "DNS UPDATE выполнен успешно" : result.Error,
                                result.Success ? null : "Проверьте DNS zone, имя/алгоритм/secret TSIG и сетевой маршрут до DNS-провайдера.");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddHealth(report, "dns_update", "RFC2136 / TSIG", ControlHealthLevel.Error, ex.Message, "Проверьте параметры TSIG и журнал приложения.");
                    }
                }

                if (settings.EnableHttps)
                {
                    var certLevel = ControlHealthLevel.Error;
                    var certMessage = "Сертификат не установлен";
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
                        {
                            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                            {
                                store.Open(OpenFlags.ReadOnly);
                                var found = store.Certificates.Find(X509FindType.FindByThumbprint, settings.CertificateThumbprint, false);
                                if (found.Count > 0)
                                {
                                    var cert = found[0];
                                    var remaining = cert.NotAfter.ToUniversalTime() - DateTime.UtcNow;
                                    certLevel = remaining.TotalDays <= 7 ? ControlHealthLevel.Warning : remaining.TotalSeconds > 0 ? ControlHealthLevel.Ok : ControlHealthLevel.Error;
                                    certMessage = "Действителен до " + cert.NotAfter.ToLocalTime().ToString("G") + " (" + Math.Max(0, (int)remaining.TotalDays) + " дн.)";
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        certMessage = ex.Message;
                    }
                    AddHealth(report, "certificate", UiText.Get(_language, "healthCertificate"), certLevel, certMessage,
                        certLevel == ControlHealthLevel.Ok ? null : "На вкладке «Домен и HTTPS» проверьте DNS и настройте HTTPS.");
                }
                else
                {
                    AddHealth(report, "certificate", UiText.Get(_language, "healthCertificate"), ControlHealthLevel.Unknown, "Не требуется в локальном режиме", null);
                }

                if (string.Equals(settings.PublicMode, "Local", StringComparison.OrdinalIgnoreCase))
                    AddHealth(report, "external", UiText.Get(_language, "healthExternal"), ControlHealthLevel.Unknown, "Локальный режим", null);
                else if (deepDnsTest)
                    AddHealth(report, "external", UiText.Get(_language, "healthExternal"), ControlHealthLevel.Unknown, UiText.Get(_language, "externalNotChecked"), "Публичная доступность зависит от маршрутизации, NAT, firewall, DNS и провайдера. При необходимости проверьте с внешней сети.");
                else
                    AddHealth(report, "external", UiText.Get(_language, "healthExternal"), ControlHealthLevel.Unknown, "Не проверяется в обычном режиме", null);
            }

            report.Level = report.Items.Any(x => x.Level == ControlHealthLevel.Error) ? ControlHealthLevel.Error
                : report.Items.Any(x => x.Level == ControlHealthLevel.Warning) ? ControlHealthLevel.Warning
                : ControlHealthLevel.Ok;
            report.Summary = report.Level == ControlHealthLevel.Ok ? UiText.Get(_language, "healthAllOk")
                : report.Level == ControlHealthLevel.Warning ? UiText.Get(_language, "healthWarnings")
                : UiText.Get(_language, "healthProblems");
            return report;
        }

        private static void AddHealth(ControlHealthReport report, string key, string name, ControlHealthLevel level, string message, string recommendation)
        {
            report.Items.Add(new ControlHealthItem { Key = key, Name = name, Level = level, Message = message, Recommendation = recommendation });
        }

        private async Task RefreshHealthAsync()
        {
            if (_healthBusy || !_configured)
                return;
            _healthBusy = true;
            try
            {
                var report = await RunControlDiagnosticsAsync(false);
                ApplyHealthReport(report);
            }
            catch
            {
            }
            finally
            {
                _healthBusy = false;
            }
        }

        private void ApplyHealthReport(ControlHealthReport report)
        {
            if (report == null)
                return;
            SetHealthValue(HealthServiceValue, report.Items.FirstOrDefault(x => x.Key == "service"));
            SetHealthValue(HealthHttpValue, report.Items.FirstOrDefault(x => x.Key == "http"));
            SetHealthValue(HealthHttpsValue, report.Items.FirstOrDefault(x => x.Key == "https"));
            SetHealthValue(HealthDnsValue, report.Items.FirstOrDefault(x => x.Key == "dns"));
            SetHealthValue(HealthCertificateValue, report.Items.FirstOrDefault(x => x.Key == "certificate"));
            SetHealthValue(HealthStorageValue, report.Items.FirstOrDefault(x => x.Key == "storage"));
            SetHealthValue(HealthExternalValue, report.Items.FirstOrDefault(x => x.Key == "external"));

            var brush = report.Level == ControlHealthLevel.Ok ? Brushes.SeaGreen : report.Level == ControlHealthLevel.Warning ? Brushes.DarkOrange : Brushes.Firebrick;
            HeaderStatusDot.Fill = brush;
            OverviewStateDot.Fill = brush;
            OverviewStateText.Text = report.Level == ControlHealthLevel.Ok ? UiText.Get(_language, "healthRunning")
                : report.Level == ControlHealthLevel.Warning ? UiText.Get(_language, "healthRunningWarnings")
                : UiText.Get(_language, "healthError");
            StatusText.Text = OverviewStateText.Text;

            if (_lastHealthLevel != ControlHealthLevel.Unknown && _lastHealthLevel != report.Level && report.Level == ControlHealthLevel.Error && _trayIcon != null)
            {
                _trayIcon.BalloonTipTitle = AppVersion.ProductName;
                _trayIcon.BalloonTipText = "Обнаружена проблема в работе сервера. Откройте «Диагностика».";
                _trayIcon.ShowBalloonTip(5000);
            }
            _lastHealthLevel = report.Level;
        }

        private static void SetHealthValue(TextBlock target, ControlHealthItem item)
        {
            if (target == null)
                return;
            if (item == null)
            {
                target.Text = "—";
                target.Foreground = Brushes.DimGray;
                return;
            }
            target.Text = (item.Level == ControlHealthLevel.Ok ? "✓ " : item.Level == ControlHealthLevel.Warning ? "! " : item.Level == ControlHealthLevel.Error ? "✕ " : "• ") + item.Message;
            target.Foreground = item.Level == ControlHealthLevel.Ok ? Brushes.SeaGreen : item.Level == ControlHealthLevel.Warning ? Brushes.DarkOrange : item.Level == ControlHealthLevel.Error ? Brushes.Firebrick : Brushes.DimGray;
        }

        private static bool IsTcpPortListening(int port)
        {
            if (port < 1 || port > 65535)
                return false;
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == port);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> IsHttpAliveAsync(int port)
        {
            if (port < 1)
                return false;
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) })
                using (var response = await client.GetAsync("http://127.0.0.1:" + port + "/favicon.ico"))
                    return (int)response.StatusCode < 500;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> IsHttpsAliveAsync(AppSettings settings)
        {
            if (settings == null || settings.HttpsPort < 1)
                return false;
            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    var connect = client.ConnectAsync(IPAddress.Loopback, settings.HttpsPort);
                    var completed = await Task.WhenAny(connect, Task.Delay(3000));
                    if (completed != connect)
                        return false;
                    await connect;
                    string remoteThumbprint = null;
                    using (var ssl = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) =>
                    {
                        if (certificate != null)
                        {
                            using (var cert = new X509Certificate2(certificate))
                                remoteThumbprint = cert.Thumbprint;
                        }
                        return true;
                    }))
                    {
                        var target = !string.IsNullOrWhiteSpace(settings.HttpsIdentifier) ? settings.HttpsIdentifier :
                            !string.IsNullOrWhiteSpace(settings.Domain) ? settings.Domain : "localhost";
                        var auth = ssl.AuthenticateAsClientAsync(target, null, SslProtocols.Tls12, false);
                        var authCompleted = await Task.WhenAny(auth, Task.Delay(4000));
                        if (authCompleted != auth)
                            return false;
                        await auth;
                        if (!ssl.IsAuthenticated || !ssl.IsEncrypted)
                            return false;
                        if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint) && !string.IsNullOrWhiteSpace(remoteThumbprint))
                            return string.Equals(NormalizeThumbprint(settings.CertificateThumbprint), NormalizeThumbprint(remoteThumbprint), StringComparison.OrdinalIgnoreCase);
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeThumbprint(string value)
        {
            return (value ?? string.Empty).Replace(" ", string.Empty).Trim();
        }

        private sealed class ConfigurationInput
        {
            public List<StorageLocationSetting> StorageLocations { get; set; }
            public string ServerQuotaText { get; set; }
            public string Mode { get; set; }
            public string Bind { get; set; }
            public string PublicIp { get; set; }
            public string HttpPortText { get; set; }
            public string HttpsPortText { get; set; }
            public string AdminPath { get; set; }
            public string UserPath { get; set; }
            public string Domain { get; set; }
            public string DnsMode { get; set; }
            public string DnsServer { get; set; }
            public string DnsZone { get; set; }
            public string DnsKeyName { get; set; }
            public string DnsAlgorithm { get; set; }
            public string DnsSecret { get; set; }
            public bool DnsAutoAddress { get; set; }
            public string AcmeEmail { get; set; }
            public bool AcmeTerms { get; set; }
            public string VpsHost { get; set; }
            public string VpsPortText { get; set; }
            public string VpsUser { get; set; }
            public string VpsPassword { get; set; }
            public bool VpsUseSudo { get; set; }
            public string Email { get; set; }
            public string Password { get; set; }
            public string RepeatPassword { get; set; }
            public bool TrayAutoStart { get; set; }
            public string LogRetentionDaysText { get; set; }
            public string LogMaxFileMiBText { get; set; }
            public string LogMaxDirectoryMiBText { get; set; }
            public string LogMaintenanceIntervalMinutesText { get; set; }
            public string AccessLogMode { get; set; }
            public string AccessLogAggregateMinutesText { get; set; }
            public string Language { get; set; }
        }
    }
}
