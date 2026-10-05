using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using APTOFI.FileSharing.Core;

namespace APTOFI.FileSharing
{
    public partial class DiagnosticsWindow : Window
    {
        private readonly MainWindow _owner;
        private ControlHealthReport _lastReport;

        internal DiagnosticsWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Owner = owner;
            Loaded += async (s, e) => await RunAsync();
        }

        private async void RunButton_OnClick(object sender, RoutedEventArgs e)
        {
            await RunAsync();
        }

        private async Task RunAsync()
        {
            RunButton.IsEnabled = false;
            ResultsList.Items.Clear();
            SummaryText.Text = "Выполняется диагностика...";
            try
            {
                _lastReport = await _owner.RunControlDiagnosticsAsync(true);
                SummaryText.Text = _lastReport.Summary + "  Проверено: " + _lastReport.CheckedUtc.ToLocalTime().ToString("G");
                SummaryText.Foreground = BrushFor(_lastReport.Level);
                foreach (var item in _lastReport.Items)
                {
                    var border = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(232, 235, 239)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10) };
                    var panel = new StackPanel();
                    var title = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = BrushFor(item.Level), Text = Prefix(item.Level) + " " + item.Name + " — " + item.Message, TextWrapping = TextWrapping.Wrap };
                    panel.Children.Add(title);
                    if (!string.IsNullOrWhiteSpace(item.Recommendation))
                        panel.Children.Add(new TextBlock { Margin = new Thickness(0, 4, 0, 0), Foreground = Brushes.DimGray, Text = "Рекомендация: " + item.Recommendation, TextWrapping = TextWrapping.Wrap });
                    border.Child = panel;
                    ResultsList.Items.Add(border);
                }
            }
            catch (Exception ex)
            {
                SummaryText.Text = ex.Message;
                SummaryText.Foreground = Brushes.Firebrick;
            }
            finally
            {
                RunButton.IsEnabled = true;
            }
        }

        private void CopyButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_lastReport == null)
                return;
            var sb = new StringBuilder();
            sb.AppendLine("APTOFI File Sharing " + AppVersion.Version);
            sb.AppendLine(_lastReport.Summary);
            foreach (var item in _lastReport.Items)
            {
                sb.AppendLine(Prefix(item.Level) + " " + item.Name + ": " + item.Message);
                if (!string.IsNullOrWhiteSpace(item.Recommendation))
                    sb.AppendLine("  Recommendation: " + item.Recommendation);
            }
            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch
            {
            }
        }

        private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static string Prefix(ControlHealthLevel level)
        {
            switch (level)
            {
                case ControlHealthLevel.Ok: return "✓";
                case ControlHealthLevel.Warning: return "!";
                case ControlHealthLevel.Error: return "✕";
                default: return "•";
            }
        }

        private static Brush BrushFor(ControlHealthLevel level)
        {
            switch (level)
            {
                case ControlHealthLevel.Ok: return Brushes.SeaGreen;
                case ControlHealthLevel.Warning: return Brushes.DarkOrange;
                case ControlHealthLevel.Error: return Brushes.Firebrick;
                default: return Brushes.DimGray;
            }
        }
    }
}
