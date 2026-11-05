using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace RainbowDashDesktop
{
    internal sealed partial class PetWindow
    {
        private Window quotaDetailsWindow;
        private TextBlock quotaDetailsText;

        private void OpenQuotaDetails()
        {
            if (quotaDetailsWindow != null)
            {
                quotaDetailsWindow.Activate();
                return;
            }

            quotaDetailsText = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
            Button refresh = new Button { Content = "刷新额度", MinWidth = 88, Padding = new Thickness(9, 4, 9, 4) };
            refresh.Click += delegate { RefreshQuota(); UpdateQuotaDetails(); };
            Button close = new Button { Content = "关闭", MinWidth = 70,
                Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(8, 0, 0, 0) };
            close.Click += delegate { CloseQuotaDetails(); };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(refresh);
            buttons.Children.Add(close);
            StackPanel content = new StackPanel { Margin = new Thickness(22) };
            content.Children.Add(quotaDetailsText);
            content.Children.Add(buttons);
            Window panel = new Window
            {
                Title = "云宝 · 额度详情", Width = 390, FontFamily = FontFamily, FontSize = 14,
                SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
                Content = content
            };
            panel.PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key != System.Windows.Input.Key.Escape) return;
                e.Handled = true;
                panel.Close();
            };
            panel.Closed += delegate { quotaDetailsWindow = null; quotaDetailsText = null; };
            quotaDetailsWindow = panel;
            UpdateQuotaDetails();
            panel.Show();
            panel.Activate();
            refresh.Focus();
        }

        private void UpdateQuotaDetails()
        {
            if (quotaDetailsText == null) return;
            PetSnapshot snapshot = lastSnapshot ?? new PetSnapshot();
            string summary = (demo ? "演示数据 · 非真实额度\n\n" : "") +
                "五小时剩余  " + QuotaRing.ValueLabel(QuotaRing.Normalize(snapshot.FiveHourRemaining)) +
                "\n重置时间  " + QuotaDetailTime(snapshot.FiveHourResetUtc) +
                "\n\n一周剩余      " + QuotaRing.ValueLabel(QuotaRing.Normalize(snapshot.WeekRemaining)) +
                "\n重置时间  " + QuotaDetailTime(snapshot.WeekResetUtc) +
                "\n\n最后同步  " + QuotaDetailTime(snapshot.QuotaUpdatedUtc);
            if (!demo)
            {
                string status = DateTime.UtcNow < refreshFeedbackUntil ? "正在刷新额度…" : QuotaStatusLabel(snapshot);
                if (!String.IsNullOrEmpty(status)) summary += "\n" + status;
            }
            if (quotaDetailsText.Text == summary) return;
            quotaDetailsText.Text = summary;
            AutomationProperties.SetName(quotaDetailsText, summary);
        }

        private static string QuotaDetailTime(DateTime? value)
        {
            return value.HasValue ? FormatLocal(value.Value) : "未知";
        }

        private void CloseQuotaDetails()
        {
            if (quotaDetailsWindow != null) quotaDetailsWindow.Close();
        }
    }
}
