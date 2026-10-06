using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using APTOFI.FileSharing.Core;
using APTOFI.FileSharing.Network;
using WinForms = System.Windows.Forms;

namespace APTOFI.FileSharing
{
    public partial class SetupWizardWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly bool _firstRun;
        private int _step;
        private bool _busy;

        internal SetupWizardWindow(MainWindow owner, bool firstRun)
        {
            InitializeComponent();
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _firstRun = firstRun;
            Owner = owner;
            ApplyLanguage();
            ModeBox.SelectedIndex = 0;
            DnsModeBox.SelectedIndex = 1;
            DnsAlgorithmBox.SelectedIndex = 0;
            LoadFromOwner();
            UpdateStep();
        }

        private string T(string key)
        {
            return UiText.Get(_owner.CurrentLanguage, key);
        }

        private string F(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        private void ApplyLanguage()
        {
            Title = AppVersion.ProductName + " — " + T("setupWizard");
            WizardTitle.Text = T("wizardTitle");
            WizardSubtitle.Text = T("wizardSubtitle");
            StepStorageItem.Content = "1. " + T("storageTab");
            StepNetworkItem.Content = "2. " + T("networkTab");
            StepDomainItem.Content = "3. " + T("domainHttpsTab");
            StepAccountItem.Content = "4. " + T("accountTab");
            StepCheckItem.Content = "5. " + T("wizardFinalTitle");

            StorageTitleText.Text = T("storageTab");
            StorageHintText.Text = T("wizardStorageHint");
            StorageFolderLabel.Text = T("wizardStorageFolder");
            BrowseStorageButton.Content = T("browse");
            StorageCheckText.Text = T("wizardNotChecked");

            NetworkTitleText.Text = T("networkTab");
            NetworkHintText.Text = T("wizardNetworkHint");
            WizardModeLabel.Text = T("mode");
            ModeDirectItem.Content = T("direct");
            ModeVpsItem.Content = T("vps");
            ModeLocalItem.Content = T("local");
            WizardBindLabel.Text = T("bind");
            WizardPublicIpLabel.Text = T("publicIp");
            WizardHttpLabel.Text = T("http");
            WizardHttpsLabel.Text = T("https");
            WizardAdminPathLabel.Text = T("adminPath");
            WizardUserPathLabel.Text = T("userPath");
            WizardVpsSectionLabel.Text = T("vpsSetup");
            WizardVpsHostLabel.Text = T("vpsHost");
            WizardVpsPortLabel.Text = T("vpsPort");
            WizardVpsUserLabel.Text = T("vpsUser");
            WizardVpsPasswordLabel.Text = T("vpsPassword");
            VpsSudoBox.Content = T("vpsSudo");
            NetworkCheckText.Text = T("wizardNotChecked");

            DomainTitleText.Text = T("domainHttpsTab");
            WizardDomainHintText.Text = T("wizardDomainHint");
            WizardDomainLabel.Text = T("domain");
            WizardDnsModeLabel.Text = T("dnsMode");
            DnsManualItem.Content = T("dnsManual");
            DnsRfcItem.Content = T("dnsRfc2136");
            WizardDnsServerLabel.Text = T("dnsServer");
            WizardDnsZoneLabel.Text = T("dnsZone");
            WizardDnsKeyLabel.Text = T("dnsKeyName");
            WizardDnsAlgorithmLabel.Text = T("dnsAlgorithm");
            WizardDnsSecretLabel.Text = T("dnsSecret");
            DnsAutoAddressBox.Content = T("dnsAutoAddress");
            WizardAcmeEmailLabel.Text = T("acmeEmail");
            AcmeTermsBox.Content = T("caTerms");
            DnsCheckText.Text = T("wizardDnsFinalPending");

            AccountTitleText.Text = T("accountTab");
            AccountHintText.Text = T("wizardAccountHint");
            WizardEmailLabel.Text = T("email");
            WizardPasswordLabel.Text = T("password");
            WizardRepeatLabel.Text = T("repeat");
            TrayAutoStartBox.Content = T("trayAutostart");
            AccountCheckText.Text = T("wizardAccountPending");

            FinalTitleText.Text = T("wizardFinalTitle");
            FinalHintText.Text = T("wizardFinalHint");
            FinalResultText.Text = T("wizardReadyToCheck");
            CancelButton.Content = T("close");
            BackButton.Content = T("back");
            UpdateStep();
        }

        private void LoadFromOwner()
        {
            var data = _owner.GetWizardData();
            StoragePathBox.Text = data.StoragePath ?? string.Empty;
            SelectByTag(ModeBox, data.Mode ?? "Direct");
            BindBox.Text = string.IsNullOrWhiteSpace(data.Bind) ? "0.0.0.0" : data.Bind;
            PublicIpBox.Text = data.PublicIp ?? string.Empty;
            HttpPortBox.Text = string.IsNullOrWhiteSpace(data.HttpPort) ? "15745" : data.HttpPort;
            HttpsPortBox.Text = string.IsNullOrWhiteSpace(data.HttpsPort) ? "15746" : data.HttpsPort;
            AdminPathBox.Text = data.AdminPath ?? string.Empty;
            UserPathBox.Text = string.IsNullOrWhiteSpace(data.UserPath) ? "/user_login_disk" : data.UserPath;
            VpsHostBox.Text = data.VpsHost ?? string.Empty;
            VpsPortBox.Text = string.IsNullOrWhiteSpace(data.VpsPort) ? "22" : data.VpsPort;
            VpsUserBox.Text = string.IsNullOrWhiteSpace(data.VpsUser) ? "root" : data.VpsUser;
            VpsSudoBox.IsChecked = data.VpsUseSudo;
            DomainBox.Text = data.Domain ?? string.Empty;
            SelectByTag(DnsModeBox, data.DnsMode ?? "Rfc2136");
            DnsServerBox.Text = data.DnsServer ?? string.Empty;
            DnsZoneBox.Text = data.DnsZone ?? string.Empty;
            DnsKeyBox.Text = data.DnsKeyName ?? string.Empty;
            SelectByTag(DnsAlgorithmBox, data.DnsAlgorithm ?? "hmac-sha256");
            DnsAutoAddressBox.IsChecked = data.DnsAutoAddress;
            AcmeEmailBox.Text = data.AcmeEmail ?? string.Empty;
            AcmeTermsBox.IsChecked = data.AcmeTerms;
            EmailBox.Text = data.Email ?? string.Empty;
            TrayAutoStartBox.IsChecked = data.TrayAutoStart;
            ModeBox_OnSelectionChanged(null, null);
            DnsModeBox_OnSelectionChanged(null, null);
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(Convert.ToString(item.Tag), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private void BrowseStorageButton_OnClick(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = T("selectStorageDialog");
                dialog.SelectedPath = StoragePathBox.Text;
                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                    StoragePathBox.Text = dialog.SelectedPath;
            }
        }

        private void ModeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VpsPanel == null || ModeBox.SelectedItem == null)
                return;
            VpsPanel.Visibility = string.Equals(SelectedTag(ModeBox), "Vps", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DnsModeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DnsPanel == null || DnsModeBox.SelectedItem == null)
                return;
            var item = DnsModeBox.SelectedItem as ComboBoxItem;
            DnsPanel.Visibility = item != null && string.Equals(Convert.ToString(item.Tag), "Rfc2136", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void NextButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            try
            {
                _busy = true;
                SetButtons(false);
                if (_step == 0)
                    await ValidateStorageAsync();
                else if (_step == 1)
                    await ValidateNetworkAsync();
                else if (_step == 2)
                    ValidateDomain();
                else if (_step == 3)
                    ValidateAccount();
                else
                {
                    await FinishAsync();
                    return;
                }
                _step = Math.Min(4, _step + 1);
                UpdateStep();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "APTOFI File Sharing", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _busy = false;
                SetButtons(true);
            }
        }

        private void BackButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            _step = Math.Max(0, _step - 1);
            UpdateStep();
        }

        private void CancelButton_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async Task ValidateStorageAsync()
        {
            var path = (StoragePathBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(T("selectStorageError"));
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
            var testPath = Path.Combine(path, ".aptofi-setup-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(testPath, "APTOFI");
                var text = File.ReadAllText(testPath);
                if (!string.Equals(text, "APTOFI", StringComparison.Ordinal))
                    throw new IOException(T("storageReadMismatch"));
                var root = Path.GetPathRoot(path);
                var drive = new DriveInfo(root);
                StorageCheckText.Text = "✓ " + T("storageDirectoryAvailable") + "\n✓ " + T("storageReadWriteOk") + "\n✓ " + T("freePrefix") + " " + FormatBytes(drive.AvailableFreeSpace);
                StorageCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                StoragePathBox.Text = path;
            }
            finally
            {
                try { if (File.Exists(testPath)) File.Delete(testPath); } catch { }
            }
            await Task.CompletedTask;
        }

        private async Task ValidateNetworkAsync()
        {
            if (!int.TryParse(HttpPortBox.Text, out var httpPort) || httpPort < 1 || httpPort > 65535)
                throw new InvalidOperationException(T("invalidHttpPort"));
            if (!int.TryParse(HttpsPortBox.Text, out var httpsPort) || httpsPort < 1 || httpsPort > 65535 || httpsPort == httpPort)
                throw new InvalidOperationException(T("invalidHttpsPort"));
            var httpFree = CanBind(httpPort);
            var httpsFree = CanBind(httpsPort);
            if (_firstRun && (!httpFree || !httpsFree))
                throw new InvalidOperationException(F("wizardPortsBusy", httpPort, T(httpFree ? "portFree" : "portBusy"), httpsPort, T(httpsFree ? "portFree" : "portBusy")));
            if (string.IsNullOrWhiteSpace(AdminPathBox.Text) || string.IsNullOrWhiteSpace(UserPathBox.Text))
                throw new InvalidOperationException(T("secretPathsRequired"));
            if (string.Equals(SelectedTag(ModeBox), "Vps", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(VpsHostBox.Text) || string.IsNullOrWhiteSpace(VpsUserBox.Text))
                    throw new InvalidOperationException(T("vpsFieldsRequired"));
                if (!int.TryParse(VpsPortBox.Text, out var vpsPort) || vpsPort < 1 || vpsPort > 65535)
                    throw new InvalidOperationException(T("invalidVpsPort"));
                if (_firstRun && string.IsNullOrWhiteSpace(VpsPasswordBox.Password))
                    throw new InvalidOperationException(T("vpsPasswordRequired"));
            }
            var publicText = string.Empty;
            if (string.Equals(SelectedTag(ModeBox), "Direct", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    publicText = await PublicIpDetector.DetectIpv4TextAsync(TimeSpan.FromSeconds(8));
                    if (string.IsNullOrWhiteSpace(PublicIpBox.Text))
                        PublicIpBox.Text = publicText;
                }
                catch
                {
                    publicText = T("detectFailed");
                }
            }
            NetworkCheckText.Text = "✓ " + T("portsRangeOk") + "\n" +
                                    "✓ " + T(_firstRun ? "portsFreeFirstRun" : "portsExistingMayBeInUse") + "\n" +
                                    "✓ " + T("publicIpv4Label") + " " + (string.IsNullOrWhiteSpace(publicText) ? (PublicIpBox.Text ?? "—") : publicText);
            NetworkCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        }

        private static bool CanBind(int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { listener?.Stop(); } catch { }
            }
        }

        private void ValidateDomain()
        {
            var mode = SelectedTag(ModeBox);
            if (string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase))
            {
                DnsCheckText.Text = "✓ " + T("localDomainOptional");
                DnsCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                return;
            }
            if (string.IsNullOrWhiteSpace(DomainBox.Text))
                throw new InvalidOperationException(T("domainRequired"));
            if (AcmeTermsBox.IsChecked != true)
                throw new InvalidOperationException(T("caTermsRequired"));
            if (string.Equals(SelectedTag(DnsModeBox), "Rfc2136", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(DnsServerBox.Text) || string.IsNullOrWhiteSpace(DnsZoneBox.Text) || string.IsNullOrWhiteSpace(DnsKeyBox.Text))
                    throw new InvalidOperationException(T("rfcFieldsRequired"));
                if (_firstRun && string.IsNullOrWhiteSpace(DnsSecretBox.Password))
                    throw new InvalidOperationException(T("tsigSecretRequired"));
                DnsCheckText.Text = "✓ " + T("rfcFieldsOk") + "\n" + T("rfcFinalTestHint");
            }
            else
            {
                DnsCheckText.Text = "✓ " + T("manualDnsSelected") + " " + T("manualDnsHttp01Hint");
            }
            DnsCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        }

        private void ValidateAccount()
        {
            if (string.IsNullOrWhiteSpace(EmailBox.Text) || !EmailBox.Text.Contains("@"))
                throw new InvalidOperationException(T("adminEmailInvalid"));
            if (_firstRun)
            {
                if (PasswordBox.Password.Length < 8 || PasswordBox.Password != RepeatPasswordBox.Password)
                    throw new InvalidOperationException(T("firstPasswordInvalid"));
            }
            else if (PasswordBox.Password.Length > 0 && (PasswordBox.Password.Length < 8 || PasswordBox.Password != RepeatPasswordBox.Password))
            {
                throw new InvalidOperationException(T("newPasswordInvalid"));
            }
            AccountCheckText.Text = "✓ " + T("accountReady") + "\n✓ " + T("serviceAutoInstall");
            AccountCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        }

        private async Task FinishAsync()
        {
            FinalProgress.Value = 10;
            FinalResultText.Foreground = System.Windows.Media.Brushes.DimGray;
            FinalResultText.Text = T("savingConfiguration");
            var data = Capture();
            var result = await _owner.ApplyWizardAndStartAsync(data, progress => Dispatcher.Invoke(() =>
            {
                FinalProgress.Value = progress.Percent;
                FinalResultText.Text = progress.Text;
                FinalResultText.Foreground = progress.Error ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DimGray;
            }));
            FinalProgress.Value = 100;
            FinalResultText.Text = result;
            FinalResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            NextButton.Content = T("done");
            NextButton.Click -= NextButton_OnClick;
            NextButton.Click += (s, e) => { DialogResult = true; Close(); };
            BackButton.IsEnabled = false;
        }

        private SetupWizardData Capture()
        {
            return new SetupWizardData
            {
                StoragePath = StoragePathBox.Text.Trim(),
                Mode = SelectedTag(ModeBox),
                Bind = BindBox.Text.Trim(),
                PublicIp = PublicIpBox.Text.Trim(),
                HttpPort = HttpPortBox.Text.Trim(),
                HttpsPort = HttpsPortBox.Text.Trim(),
                AdminPath = AdminPathBox.Text.Trim(),
                UserPath = UserPathBox.Text.Trim(),
                VpsHost = VpsHostBox.Text.Trim(),
                VpsPort = VpsPortBox.Text.Trim(),
                VpsUser = VpsUserBox.Text.Trim(),
                VpsPassword = VpsPasswordBox.Password,
                VpsUseSudo = VpsSudoBox.IsChecked == true,
                Domain = DomainBox.Text.Trim(),
                DnsMode = SelectedTag(DnsModeBox),
                DnsServer = DnsServerBox.Text.Trim(),
                DnsZone = DnsZoneBox.Text.Trim(),
                DnsKeyName = DnsKeyBox.Text.Trim(),
                DnsAlgorithm = SelectedTag(DnsAlgorithmBox),
                DnsSecret = DnsSecretBox.Password.Trim(),
                DnsAutoAddress = DnsAutoAddressBox.IsChecked == true,
                AcmeEmail = AcmeEmailBox.Text.Trim(),
                AcmeTerms = AcmeTermsBox.IsChecked == true,
                Email = EmailBox.Text.Trim(),
                Password = PasswordBox.Password,
                RepeatPassword = RepeatPasswordBox.Password,
                TrayAutoStart = TrayAutoStartBox.IsChecked == true
            };
        }

        private static string SelectedTag(ComboBox combo)
        {
            var item = combo.SelectedItem as ComboBoxItem;
            return item == null ? string.Empty : Convert.ToString(item.Tag);
        }

        private void UpdateStep()
        {
            WizardTabs.SelectedIndex = _step;
            StepsList.SelectedIndex = _step;
            BackButton.IsEnabled = _step > 0;
            NextButton.Content = T(_step == 4 ? "checkAndStart" : "next");
        }

        private void SetButtons(bool enabled)
        {
            BackButton.IsEnabled = enabled && _step > 0;
            NextButton.IsEnabled = enabled;
            CancelButton.IsEnabled = enabled;
        }

        private static string FormatBytes(long value)
        {
            var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
            double n = Math.Max(0, value);
            var index = 0;
            while (n >= 1024d && index < units.Length - 1) { n /= 1024d; index++; }
            return n.ToString(index == 0 ? "0" : "0.##") + " " + units[index];
        }
    }

    internal sealed class WizardProgress
    {
        public int Percent { get; set; }
        public string Text { get; set; }
        public bool Error { get; set; }
    }
}
