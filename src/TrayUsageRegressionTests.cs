using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexUsageMeter
{
    internal static class TrayUsageRegressionTests
    {
        internal static void Run(Action<string> report, string directory)
        {
            DateTime now = new DateTime(2030, 1, 2, 12, 0, 0, DateTimeKind.Utc);
            AccountState[] accounts = Enumerable.Range(1, 4).Select(number => new AccountState {
                Number = number,
                LastSnapshot = new AccountSnapshot {
                    IsAuthenticated = true, RateLimitsObservedAtUtc = now,
                    Email = "private@example.test", Error = null,
                    Primary = new RateWindow { Name = "5시간 한도", RemainingPercent = 61, ResetsAt = now.AddHours(1) },
                    Secondary = new RateWindow { Name = "주간 한도", RemainingPercent = 15, ResetsAt = now.AddDays(1) }
                }
            }).ToArray();
            var settings = new TrayUsageSettings { AccountNumber = 3 };
            TrayUsageDisplay display = TrayUsageDisplay.Build(accounts, settings, now);
            Require(display.Text == "15" && display.ToolTip == "Codex · 계정 3 · 주간 한도 · 잔여 15%", "Selected account/long limit");
            settings.Secondary = false;
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "61", "Short limit selection");
            settings.AccountNumber = 4;
            accounts[3].LastSnapshot.Primary.RemainingPercent = 0;
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "0", "Real zero was lost");
            accounts[3].LastSnapshot.Primary.RemainingPercent = 100;
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "100", "Full quota was lost");
            accounts[3].LastSnapshot.Primary.RemainingPercent = 18.6;
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "19", "Rounding differs from the dashboard");
            report("PASS selected accounts 3/4 and both limits, real 0/100 and dashboard rounding");

            settings.AccountNumber = 1;
            AccountSnapshot snapshot = accounts[0].LastSnapshot;
            snapshot.Primary = null;
            display = TrayUsageDisplay.Build(accounts, settings, now);
            Require(display.Text == "--" && display.ToolTip.EndsWith("미제공"), "Unavailable short quota silently used long quota");
            settings.Secondary = true;
            snapshot.Secondary.Name = "30일 한도";
            Require(TrayUsageDisplay.Build(accounts, settings, now).ToolTip.Contains("30일 한도 · 잔여 15%"), "Monthly label");
            foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
            {
                snapshot.Secondary.RemainingPercent = invalid;
                Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "--", "Invalid quota shown as a number");
            }
            snapshot.Secondary.RemainingPercent = 15;
            snapshot.Error = "secret authentication error";
            display = TrayUsageDisplay.Build(accounts, settings, now);
            Require(display.Text == "--" && display.ToolTip.EndsWith("조회 실패") &&
                !display.ToolTip.Contains("secret") && !display.ToolTip.Contains("private"), "Failure/privacy");
            snapshot.Error = null; snapshot.IsAuthenticated = false;
            Require(TrayUsageDisplay.Build(accounts, settings, now).ToolTip.EndsWith("연결 안 됨"), "Unlinked quota");
            snapshot.IsAuthenticated = true; snapshot.RateLimitsObservedAtUtc = default(DateTime);
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "--", "Unobserved quota");
            foreach (DateTime observed in new[] { now.AddMinutes(-4), now.AddMinutes(2) })
            {
                snapshot.RateLimitsObservedAtUtc = observed;
                Require(TrayUsageDisplay.Build(accounts, settings, now).ToolTip.EndsWith("갱신 대기"), "Stale/future quota");
            }
            snapshot.RateLimitsObservedAtUtc = now; snapshot.Secondary.ResetsAt = now;
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "--", "Expired quota");
            snapshot.Secondary.ResetsAt = now.AddDays(1);
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "15", "Recovery after refresh");
            Require(TrayUsageDisplay.Build(new AccountState[0], settings, now).ToolTip.EndsWith("계정 없음"), "Removed account");
            accounts[0].LastSnapshot = null;
            Require(TrayUsageDisplay.Build(accounts, settings, now).Text == "--", "Initial loading");
            report("PASS unavailable/monthly/error/unlinked/stale/expired/loading states and refresh recovery; no private data");

            CheckSettingsPersistence();
            report("PASS tray preference defaults and save/reopen round trip in an isolated registry key");
            CheckIcons(directory);
            report("PASS numeric icons at 16/24/32 pixels, tooltip updates, icon reuse, disable/restore and native handle lifetime");
            CheckClicks();
            report("PASS one left click opens the meter; right/middle clicks do not open it");
            CheckSettingsLayout(360, 640, 1.5);
            CheckSettingsLayout(460, 780, 2.0);
            report("PASS tray settings remain reachable at narrow widths and 150/200 percent text");
        }

        private static void CheckSettingsPersistence()
        {
            string path = "Software\\CodexUsageMeter\\Tests\\TrayUsage-" + Guid.NewGuid().ToString("N");
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
                {
                    TrayUsageSettings defaults = TrayUsageSettings.Load(key);
                    Require(defaults.Enabled && defaults.AccountNumber == 1 && defaults.Secondary, "Default selection");
                    new TrayUsageSettings { Enabled = false, AccountNumber = 4, Secondary = false }.Save(key);
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
                {
                    TrayUsageSettings restored = TrayUsageSettings.Load(key);
                    Require(!restored.Enabled && restored.AccountNumber == 4 && !restored.Secondary, "Preferences did not persist");
                }
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path); }
        }

        private static void CheckIcons(string directory)
        {
            string[] texts = { "--", "0", "1", "15", "99", "100" };
            int[] sizes = { 16, 24, 32 };
            using (Bitmap preview = new Bitmap(420, 210))
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.Clear(Drawing.Color.FromArgb(235, 235, 235));
                for (int row = 0; row < sizes.Length; row++)
                for (int column = 0; column < texts.Length; column++)
                using (Icon icon = TrayUsageIcon.Render(texts[column], sizes[row]))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    int size = sizes[row], ink = 0;
                    Require(bitmap.Width == size && bitmap.Height == size, "Icon dimensions");
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        Drawing.Color pixel = bitmap.GetPixel(x, y);
                        if (pixel.R > 35 && pixel.G > 35 && pixel.B > 35)
                        {
                            ink++;
                            Require(x > 0 && x < size - 1 && y > 0 && y < size - 1, "Icon text clipped at " + size + ": " + texts[column]);
                        }
                    }
                    Require(ink > 0, "Blank icon at " + size + ": " + texts[column]);
                    graphics.DrawImageUnscaled(bitmap, column * 70 + (70 - size) / 2, row * 70 + (70 - size) / 2);
                }
                preview.Save(Path.Combine(directory, "tray-icons.png"), ImageFormat.Png);
            }
            using (var notify = new Forms.NotifyIcon { Icon = SystemIcons.Application })
            using (var indicator = new TrayUsageIcon(notify, delegate { }))
            {
                var display = new TrayUsageDisplay { Text = "15", ToolTip = "Codex · 계정 1 · 주간 한도 · 잔여 15%" };
                indicator.Update(display, true);
                Icon first = notify.Icon;
                display.ToolTip = "Codex · 계정 4 · 주간 한도 · 잔여 15%";
                indicator.Update(display, true);
                Require(Object.ReferenceEquals(first, notify.Icon) && notify.Text == display.ToolTip, "Unchanged icon/changed tooltip");
                IntPtr process = Process.GetCurrentProcess().Handle;
                int gdi = GetGuiResources(process, 0), user = GetGuiResources(process, 1);
                for (int i = 0; i < 200; i++)
                {
                    display.Text = texts[i % texts.Length];
                    indicator.Update(display, true);
                }
                Require(GetGuiResources(process, 0) <= gdi + 2 && GetGuiResources(process, 1) <= user + 2, "Native icon handle leak");
                indicator.Update(display, false);
                Require(Object.ReferenceEquals(notify.Icon, SystemIcons.Application) && notify.Text == "Codex 사용량 미터기", "Disabled icon");
                indicator.Update(display, true);
                Require(!Object.ReferenceEquals(notify.Icon, SystemIcons.Application), "Re-enabled icon");
            }
        }

        private static void CheckClicks()
        {
            int opened = 0;
            using (var notify = new Forms.NotifyIcon { Icon = SystemIcons.Application })
            using (var indicator = new TrayUsageIcon(notify, delegate { opened++; }))
            {
                MethodInfo click = typeof(Forms.NotifyIcon).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (Forms.MouseButtons button in new[] { Forms.MouseButtons.Right, Forms.MouseButtons.Middle, Forms.MouseButtons.Left })
                    click.Invoke(notify, new object[] { new Forms.MouseEventArgs(button, 1, 0, 0, 0) });
                Require(opened == 1, "Left-click activation");
                indicator.Update(new TrayUsageDisplay(), false);
                click.Invoke(notify, new object[] { new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0) });
                Require(opened == 2, "Activation with numeric display disabled");
            }
        }

        private static void CheckSettingsLayout(int width, int height, double scale)
        {
            Window window = DashboardController.LoadWindow();
            Grid root = (Grid)window.Content;
            foreach (UIElement child in root.Children) child.Visibility = Visibility.Collapsed;
            Grid settings = (Grid)window.FindName("SettingsOverlay");
            settings.Visibility = Visibility.Visible;
            var fonts = new List<DashboardController.FontTarget>();
            DashboardController.CollectFontTargets(settings, window, fonts, new HashSet<DependencyObject>());
            DashboardController.ScaleFontTargets(fonts, scale);
            ComboBox account = (ComboBox)window.FindName("TrayAccountComboBox");
            for (int i = 1; i <= 4; i++) account.Items.Add("계정 " + i);
            account.SelectedIndex = 3;
            ComboBox limit = (ComboBox)window.FindName("TrayLimitComboBox");
            limit.SelectedIndex = 1;
            Require(limit.Items.Count == 2, "Both limit choices are required");
            window.Content = null; root.Resources.MergedDictionaries.Add(window.Resources);
            root.SetValue(Control.FontFamilyProperty, window.FontFamily);
            root.SetValue(Control.ForegroundProperty, window.Foreground);
            root.SetValue(Control.FontSizeProperty, window.FontSize);
            using (HwndSource surface = new HwndSource(new HwndSourceParameters("CodexUsageMeter.TraySettingsTest") {
                Width = width, Height = height, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
            }))
            {
                surface.RootVisual = root;
                root.Measure(new System.Windows.Size(width, height));
                root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                foreach (FrameworkElement control in new FrameworkElement[] { (CheckBox)window.FindName("TrayUsageCheckBox"), account, limit })
                {
                    control.BringIntoView(); root.UpdateLayout();
                    Rect bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                    Require(bounds.Left >= 0 && bounds.Right <= width + 0.5 && bounds.Top >= 0 && bounds.Bottom <= height + 0.5,
                        "Tray setting clipped: " + control.Name);
                    System.Windows.Point center = control.TransformToAncestor(root).Transform(new System.Windows.Point(control.ActualWidth / 2, control.ActualHeight / 2));
                    DependencyObject hit = root.InputHitTest(center) as DependencyObject;
                    while (hit != null && hit != control) hit = VisualTreeHelper.GetParent(hit);
                    Require(hit == control, "Tray setting is not clickable: " + control.Name);
                }
                surface.RootVisual = null;
            }
            window.Close();
        }

        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, int flags);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
