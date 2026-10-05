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
            ModeBox.SelectedIndex = 0;
            DnsModeBox.SelectedIndex = 1;
            DnsAlgorithmBox.SelectedIndex = 0;
            LoadFromOwner();
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
                dialog.Description = "Выберите каталог хранения APTOFI File Sharing";
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
                throw new InvalidOperationException("Выберите каталог хранения.");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
            var testPath = Path.Combine(path, ".aptofi-setup-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(testPath, "APTOFI");
                var text = File.ReadAllText(testPath);
                if (!string.Equals(text, "APTOFI", StringComparison.Ordinal))
                    throw new IOException("Контрольное чтение тестового файла не совпало.");
                var root = Path.GetPathRoot(path);
                var drive = new DriveInfo(root);
                StorageCheckText.Text = "✓ Каталог доступен\n✓ Запись и чтение работают\n✓ Свободно: " + FormatBytes(drive.AvailableFreeSpace);
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
                throw new InvalidOperationException("Некорректный HTTP-порт.");
            if (!int.TryParse(HttpsPortBox.Text, out var httpsPort) || httpsPort < 1 || httpsPort > 65535 || httpsPort == httpPort)
                throw new InvalidOperationException("Некорректный HTTPS-порт или он совпадает с HTTP-портом.");
            var httpFree = CanBind(httpPort);
            var httpsFree = CanBind(httpsPort);
            if (_firstRun && (!httpFree || !httpsFree))
                throw new InvalidOperationException("Один из выбранных портов уже занят. HTTP " + httpPort + ": " + (httpFree ? "свободен" : "занят") + "; HTTPS " + httpsPort + ": " + (httpsFree ? "свободен" : "занят") + ".");
            if (string.IsNullOrWhiteSpace(AdminPathBox.Text) || string.IsNullOrWhiteSpace(UserPathBox.Text))
                throw new InvalidOperationException("Секретные пути администратора и пользователей не должны быть пустыми.");
            if (string.Equals(SelectedTag(ModeBox), "Vps", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(VpsHostBox.Text) || string.IsNullOrWhiteSpace(VpsUserBox.Text))
                    throw new InvalidOperationException("Для VPS-режима укажите IP/домен VPS и SSH-пользователя.");
                if (!int.TryParse(VpsPortBox.Text, out var vpsPort) || vpsPort < 1 || vpsPort > 65535)
                    throw new InvalidOperationException("Некорректный SSH-порт VPS.");
                if (_firstRun && string.IsNullOrWhiteSpace(VpsPasswordBox.Password))
                    throw new InvalidOperationException("Для первого запуска VPS-режима укажите SSH / sudo пароль.");
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
                    publicText = "не удалось определить";
                }
            }
            NetworkCheckText.Text = "✓ Порты имеют корректный диапазон\n" +
                                    (_firstRun ? "✓ Порты свободны для первого запуска\n" : "✓ Существующая конфигурация может использовать уже занятые службой порты\n") +
                                    "✓ Публичный IPv4: " + (string.IsNullOrWhiteSpace(publicText) ? (PublicIpBox.Text ?? "—") : publicText);
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
                DnsCheckText.Text = "✓ Локальный режим: домен и HTTPS не обязательны.";
                DnsCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                return;
            }
            if (string.IsNullOrWhiteSpace(DomainBox.Text))
                throw new InvalidOperationException("Для интернет-режима укажите домен.");
            if (AcmeTermsBox.IsChecked != true)
                throw new InvalidOperationException("Для автоматического HTTPS необходимо принять условия центра сертификации.");
            if (string.Equals(SelectedTag(DnsModeBox), "Rfc2136", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(DnsServerBox.Text) || string.IsNullOrWhiteSpace(DnsZoneBox.Text) || string.IsNullOrWhiteSpace(DnsKeyBox.Text))
                    throw new InvalidOperationException("Для RFC2136 заполните DNS-сервер, DNS-зону и имя TSIG-ключа.");
                if (_firstRun && string.IsNullOrWhiteSpace(DnsSecretBox.Password))
                    throw new InvalidOperationException("Укажите TSIG secret.");
                DnsCheckText.Text = "✓ Поля RFC2136 заполнены.\nНа финальном шаге будет выполнено реальное DNS UPDATE и затем DNS-01/HTTPS.";
            }
            else
            {
                DnsCheckText.Text = "✓ Выбран ручной DNS. Для сертификата потребуется доступность HTTP-01 на публичном TCP 80.";
            }
            DnsCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        }

        private void ValidateAccount()
        {
            if (string.IsNullOrWhiteSpace(EmailBox.Text) || !EmailBox.Text.Contains("@"))
                throw new InvalidOperationException("Укажите корректный email администратора.");
            if (_firstRun)
            {
                if (PasswordBox.Password.Length < 8 || PasswordBox.Password != RepeatPasswordBox.Password)
                    throw new InvalidOperationException("Пароль должен содержать не менее 8 символов, оба поля должны совпадать.");
            }
            else if (PasswordBox.Password.Length > 0 && (PasswordBox.Password.Length < 8 || PasswordBox.Password != RepeatPasswordBox.Password))
            {
                throw new InvalidOperationException("Новый пароль должен содержать не менее 8 символов, оба поля должны совпадать.");
            }
            AccountCheckText.Text = "✓ Учётная запись готова к сохранению.\n✓ Служба Windows будет установлена/перезапущена автоматически.";
            AccountCheckText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        }

        private async Task FinishAsync()
        {
            FinalProgress.Value = 10;
            FinalResultText.Foreground = System.Windows.Media.Brushes.DimGray;
            FinalResultText.Text = "Сохраняем конфигурацию...";
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
            NextButton.Content = "Готово";
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
            NextButton.Content = _step == 4 ? "Проверить и запустить" : "Далее";
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
