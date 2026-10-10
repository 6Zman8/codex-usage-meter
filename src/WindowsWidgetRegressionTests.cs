using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageMeter
{
    internal static class WindowsWidgetRegressionTests
    {
        internal static void Run(Action<string> report, string evidenceDirectory)
        {
            DateTime now = new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc);
            AccountState linked = new AccountState { Number = 1, Label = "private-label@example.test",
                LastSnapshot = new AccountSnapshot { IsAuthenticated = true, Email = "private@example.test", PlanType = "plus",
                    RateLimitsObservedAtUtc = now, Primary = new RateWindow { RemainingPercent = 0, DurationMinutes = 300, ResetsAt = now.AddHours(1) } } };
            AccountState unlinked = new AccountState { Number = 2, LastSnapshot = new AccountSnapshot() };
            AccountState failed = new AccountState { Number = 3, LastSnapshot = new AccountSnapshot { Error = "secret error token", IsAuthenticated = true,
                Primary = new RateWindow { RemainingPercent = 90 } } };
            AccountState waiting = new AccountState { Number = 4 };
            AccountState[] states = { linked, unlinked, failed, waiting };
            string json = WindowsWidgetBridge.BuildJson(states, true, now);
            Dictionary<string, object> root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (!root.ContainsKey("schemaVersion") || Convert.ToInt32(root["schemaVersion"]) != 1)
                throw new InvalidOperationException("Widget snapshot schema is missing");
            object[] accounts = ((ArrayList)root["accounts"]).ToArray();
            if (accounts.Length != 4) throw new InvalidOperationException("Configured accounts were lost");
            Dictionary<string, object> first = (Dictionary<string, object>)accounts[0];
            if ((string)first["status"] != "ok" || first["secondary"] != null ||
                Convert.ToDouble(((Dictionary<string, object>)first["primary"])["remainingPercent"]) != 0)
                throw new InvalidOperationException("Zero and missing limits were conflated");
            report("PASS widget snapshot preserves a real 0 percent and an unavailable limit separately");
            if (json.Contains("private") || json.Contains("secret") || json.Contains("Email") || json.Contains("HistoryKey"))
                throw new InvalidOperationException("Widget snapshot contains private identity or error details");
            report("PASS widget payload excludes email, profile identity, raw errors and authentication");
            if ((string)((Dictionary<string, object>)accounts[1])["status"] != "unlinked" ||
                (string)((Dictionary<string, object>)accounts[2])["status"] != "error" ||
                ((Dictionary<string, object>)accounts[2])["primary"] != null ||
                (string)((Dictionary<string, object>)accounts[3])["status"] != "waiting")
                throw new InvalidOperationException("Failed or unlinked quota appeared current");
            report("PASS failed, unlinked and waiting accounts never expose an invented quota");
            if (!((string)first["observedAtUtc"]).EndsWith("Z") || !((string)root["writtenAtUtc"]).EndsWith("Z"))
                throw new InvalidOperationException("Observation timestamps are not UTC");
            report("PASS quota observation time is kept distinct from publication time");
            string directory = Path.Combine(evidenceDirectory, "widget-fixture-" + Guid.NewGuid().ToString("N"));
            WindowsWidgetBridge bridge = new WindowsWidgetBridge(directory);
            bridge.Publish(states, true);
            bridge.Publish(new[] { linked }, false);
            root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(directory, "snapshot.json")));
            if ((bool)root["running"] || ((ArrayList)root["accounts"]).Count != 1)
                throw new InvalidOperationException("Shutdown/account count did not replace previous snapshot");
            report("PASS atomic snapshot replacement records shutdown and the current account count");
            CheckRenderedWidget(report, directory);
            CheckPackageInputs(report, directory);
            CheckRegisteredUpdate(report);
            CheckRecognition(report);
            CheckSettings(320, 480, 2, directory);
            CheckSettings(460, 780, 1.5, directory);
            report("PASS Windows widget settings scroll and install action remain reachable at narrow widths and 150-200 percent text");
        }

        private static void CheckRecognition(Action<string> report)
        {
            string current = UpdateClient.CurrentVersionText + ".0";
            string detected = "{\"catalogReadSucceeded\":true,\"registered\":true,\"registeredVersion\":\"" + current +
                "\",\"extensionId\":\"CodexUsageMeterWindowsWidget\",\"runtime\":{\"nativeWinRtActivation\":true,\"providerComInterface\":true}}";
            WindowsWidgetInstallResult found = WindowsWidgetInstaller.ClassifyRecognition(detected);
            if (found.Recognition != WindowsWidgetRecognition.Recognized || !found.Message.Contains("고정된 상태까지 확인한 것은 아닙니다") ||
                !found.Message.Contains("Windows 키 + W") || !found.Message.Contains("진단 복사"))
                throw new InvalidOperationException("Catalog recognition was confused with a pinned, visible widget");
            foreach (string json in new[] { "{\"catalogReadSucceeded\":true,\"registered\":false}",
                detected.Replace(current, "1.6.0.0"), detected.Replace("CodexUsageMeterWindowsWidget", "wrong-extension") })
                if (WindowsWidgetInstaller.ClassifyRecognition(json).Recognition != WindowsWidgetRecognition.Missing)
                    throw new InvalidOperationException("Missing/stale widget extension was announced as available");
            foreach (string json in new[] { "broken", "{}", "{\"catalogReadSucceeded\":false,\"registered\":false}",
                detected.Replace("\"nativeWinRtActivation\":true", "\"nativeWinRtActivation\":false"),
                detected.Replace("\"providerComInterface\":true", "\"providerComInterface\":false"),
                "{\"catalogReadSucceeded\":true,\"registered\":true}" })
                if (WindowsWidgetInstaller.ClassifyRecognition(json).Recognition != WindowsWidgetRecognition.Unknown)
                    throw new InvalidOperationException("An unreadable catalog or failed runtime was announced as verified");
            report("PASS widget installation distinguishes recognized/missing/unknown extensions, checks runtime/version and never claims a visible or pinned widget");
        }

        private static void CheckRegisteredUpdate(Action<string> report)
        {
            Version current = new Version(1, 8, 0, 0);
            Func<Func<Version>, Action, bool> synchronize = (read, install) =>
                WindowsWidgetInstaller.SynchronizeRegisteredVersion(current, read, install);
            int installs = 0;
            foreach (Version installed in new[] { null, current, new Version(1, 9, 0, 0) })
            {
                if (synchronize(() => installed, () => installs++) || installs != 0)
                    throw new InvalidOperationException("Widget auto-update installed an unrequested widget or replaced a current/newer provider");
            }
            Version registered = new Version(1, 6, 0, 0);
            if (!synchronize(() => registered, () => { installs++; registered = current; }) || installs != 1 ||
                synchronize(() => registered, () => installs++) || installs != 1)
                throw new InvalidOperationException("An older registered widget was not upgraded exactly once with the main app");
            bool failed = false;
            registered = new Version(1, 6, 0, 0);
            try { synchronize(() => registered, () => { throw new IOException("offline fixture"); }); }
            catch (IOException) { failed = true; }
            if (!failed || registered != new Version(1, 6, 0, 0))
                throw new InvalidOperationException("Failed widget update was reported as installed or changed the prior registration");
            failed = false;
            try { synchronize(() => registered, () => { }); }
            catch (InvalidOperationException) { failed = true; }
            if (!failed) throw new InvalidOperationException("A completed installer without a matching registration was accepted");
            failed = false;
            installs = 0;
            try { synchronize(() => { throw new IOException("query fixture"); }, () => installs++); }
            catch (IOException) { failed = true; }
            if (!failed || installs != 0) throw new InvalidOperationException("Unknown registration state triggered an installation");
            report("PASS main-app update upgrades an existing old widget once, skips missing/current/newer providers and preserves failed registration");
        }

        private static void CheckPackageInputs(Action<string> report, string directory)
        {
            string zip = Path.Combine(directory, "malformed-package.zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (StreamWriter writer = new StreamWriter(archive.CreateEntry("safe.txt").Open())) writer.Write("fixture");
                using (StreamWriter writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("must not escape");
            }
            bool rejected = false;
            try { WindowsWidgetInstaller.ExtractPackage(zip, Path.Combine(directory, "extracted")); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected || File.Exists(Path.Combine(directory, "escape.txt")) || File.Exists(Path.Combine(directory, "extracted", "safe.txt")))
                throw new InvalidOperationException("Malformed package was partially extracted");
            report("PASS widget ZIP traversal is rejected before extracting any file");
            rejected = false;
            try { WindowsWidgetInstaller.VerifyDownload(zip, new FileInfo(zip).Length, new string('0', 64)); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Modified widget download was accepted");
            report("PASS widget package hash mismatch is rejected");
            string manifest = "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"CodexUsageMeter.WindowsWidget\" Publisher=\"CN=CodexUsageMeter\" Version=\"1.6.0.0\" ProcessorArchitecture=\"x64\"/></Package>";
            File.WriteAllText(Path.Combine(directory, "AppxManifest.xml"), manifest);
            WindowsWidgetInstaller.ValidateManifest(directory, "1.6.0.0");
            rejected = false;
            try { WindowsWidgetInstaller.ValidateManifest(directory, "1.6.1.0"); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Different widget version was accepted");
            File.WriteAllText(Path.Combine(directory, "AppxManifest.xml"), manifest.Replace("CN=CodexUsageMeter", "CN=SomeoneElse"));
            rejected = false;
            try { WindowsWidgetInstaller.ValidateManifest(directory, "1.6.0.0"); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Different widget publisher was accepted");
            report("PASS widget package identity, publisher, architecture and matching app version are checked");
        }

        private static void CheckRenderedWidget(Action<string> report, string directory)
        {
            DateTime now = DateTime.UtcNow;
            var accounts = Enumerable.Range(1, 4).Select(number => new AccountState { Number = number, Label = "private-label",
                LastSnapshot = new AccountSnapshot { Email = "private@example.test", IsAuthenticated = true, PlanType = "prolite", RateLimitsObservedAtUtc = now,
                    Primary = new RateWindow { Name = "5시간 한도", RemainingPercent = 0, DurationMinutes = 300, ResetsAt = now.AddHours(1) },
                    Secondary = new RateWindow { Name = "주간 한도", RemainingPercent = 41, DurationMinutes = 10080, ResetsAt = now.AddDays(2) } } }).ToArray();
            LayoutSettings settings = LayoutSettings.Defaults();
            settings.Widget.Move("pc", "account1");
            settings.Widget.Card("account1").SetItemVisible("subscription", false);
            settings.Widget.Card("account1").ItemLayouts.Add(new LayoutItemSettings { Id = "weekly", X = 0.05, Y = 0.25, Width = 0.4, Height = 0.5 });
            var system = new SystemSnapshot { CpuPercent = 28, MemoryPercent = 63 };
            using (var renderer = new WindowsWidgetRenderer())
            {
                var pages = renderer.Render(accounts, settings, system, 1.5, now);
                if (pages["Large"].Count != 3 || pages["Medium"].Count != 5 || pages["Small"].Count != 5 ||
                    !pages["Large"][0]["alt"].Contains("계정 1") || !pages["Large"][2]["alt"].Contains("계정 4") ||
                    !pages["Large"][0]["alt"].Contains("0%") || !pages["Large"][0]["alt"].Contains("41%") || !pages["Large"][0]["alt"].Contains("63%") ||
                    pages.Values.SelectMany(value => value).Any(page => page["alt"].Contains("private")))
                    throw new InvalidOperationException("Rendered widget lost accounts, real quota/PC values or leaked identity");
                for (int size = 0; size < WindowsWidgetRenderer.Sizes.Length; size++)
                {
                    var sizePages = pages[WindowsWidgetRenderer.Sizes[size]];
                    string text = String.Join(" · ", sizePages.Select(page => page["alt"]));
                    if (Enumerable.Range(1, 4).Any(number => !text.Contains("계정 " + number)))
                        throw new InvalidOperationException("Responsive pagination lost an account");
                    byte[] png = Convert.FromBase64String(sizePages[0]["image"].Substring("data:image/png;base64,".Length));
                    using (var stream = new MemoryStream(png))
                    {
                        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        if (frame.PixelWidth != 600 || frame.PixelHeight != WindowsWidgetRenderer.Heights[size] * 2)
                            throw new InvalidOperationException("Widget bitmap does not fit the requested host size");
                    }
                    File.WriteAllBytes(Path.Combine(directory, "widget-" + WindowsWidgetRenderer.Sizes[size] + ".png"), png);
                    for (int page = 0; page < sizePages.Count; page++)
                        File.WriteAllBytes(Path.Combine(directory, "widget-" + WindowsWidgetRenderer.Sizes[size] + "-" + (page + 1) + ".png"),
                            Convert.FromBase64String(sizePages[page]["image"].Substring("data:image/png;base64,".Length)));
                }
                CheckSmallGauge(pages["Small"][2]["image"], Color.FromRgb(167, 139, 250), "account weekly");
                CheckPcBars(renderer.Window, system);
                string json = WindowsWidgetBridge.BuildJson(accounts, true, now, pages);
                if (Encoding.UTF8.GetByteCount(json) > 512 * 1024) throw new InvalidOperationException("Rendered snapshot exceeds the provider input limit");
                File.WriteAllText(Path.Combine(directory, "rendered-snapshot.json"), json);
                settings.Widget.Card("account2").Visible = settings.Widget.Card("account3").Visible = settings.Widget.Card("account4").Visible = false;
                pages = renderer.Render(accounts, settings, system, 1.5, now);
                LayoutTile first = renderer.Layout.Tiles(true).Single(tile => tile.Settings.Id == "account1");
                if (pages["Large"].Count != 1 || !pages["Large"][0]["alt"].Contains("PC 상태") ||
                    first.ContentItems().Single(item => item.Id == "subscription").Element.IsVisible)
                    throw new InvalidOperationException("Windows widget did not follow the compact layout order/visibility");
                LayoutItemSettings position = CardContentLayout.Position(first, first.ContentItems().Single(item => item.Id == "weekly"));
                if (position.X < 0.049 || position.X + position.Width > 0.451 || position.Y < 0.249 || position.Y + position.Height > 0.751)
                    throw new InvalidOperationException("Windows widget ignored saved item bounds");
                accounts[0].LastSnapshot.Error = "secret error";
                pages = renderer.Render(accounts, settings, system, 1.5, now);
                if (!pages["Large"][0]["alt"].Contains("조회 실패") || pages["Large"][0]["alt"].Contains("secret") ||
                    ((TextBlock)renderer.Window.FindName("CompactAccount1PrimaryValue")).Text.Contains("%"))
                    throw new InvalidOperationException("Failed account exposed a current quota or raw error");
                accounts[0].LastSnapshot.Error = null;
                accounts[0].LastSnapshot.Primary.ResetsAt = now;
                pages = renderer.Render(accounts, settings, system, 1.5, now);
                if (!pages["Large"][0]["alt"].Contains("갱신 대기")) throw new InvalidOperationException("Expired quota was presented as current");
                CheckStablePcChart(renderer, directory);
            }
            report("PASS rendered Windows widget: shared gauges/layout, 3 sizes, paging, real zero/PC values, hidden items/order/bounds and private-data exclusion");
        }

        private static void CheckPcBars(Window window, SystemSnapshot source)
        {
            DashboardController.RenderCompactSystem(window, source);
            foreach (string name in new[] { "Cpu", "Gpu", "Memory", "Disk" })
            {
                Border bar = (Border)window.FindName("Compact" + name + "Bar");
                FrameworkElement track = (FrameworkElement)bar.Parent;
                double expected = name == "Cpu" ? 28 : name == "Memory" ? 63 : 0;
                if (track.Width >= track.Height || bar.VerticalAlignment != VerticalAlignment.Bottom ||
                    Math.Abs(bar.Height / track.Height * 100 - expected) > 0.000001)
                    throw new InvalidOperationException("PC vertical bar did not represent the measured percentage: " + name);
                if ((name == "Gpu" || name == "Disk") && ((TextBlock)window.FindName("Compact" + name + "Value")).Text != "N/A")
                    throw new InvalidOperationException("An unavailable device was shown as a measured zero");
            }
            foreach (double percent in new[] { 0.0, 50.0, 100.0 })
            {
                DashboardController.RenderCompactSystem(window, new SystemSnapshot { CpuPercent = percent });
                Border bar = (Border)window.FindName("CompactCpuBar");
                if (Math.Abs(bar.Height / ((FrameworkElement)bar.Parent).Height - percent / 100) > 0.000001)
                    throw new InvalidOperationException("PC vertical bar zero/half/full scale is incorrect");
            }
            DashboardController.RenderCompactSystem(window, source);
        }

        private static void CheckStablePcChart(WindowsWidgetRenderer renderer, string directory)
        {
            LayoutSettings settings = LayoutSettings.Defaults();
            string saved = settings.Copy().ToJson();
            Rect[] expected = null;
            foreach (double percent in new[] { 9.0, 10.0, 100.0, 0.0, 9.0 })
            {
                var system = new SystemSnapshot { CpuPercent = percent, MemoryPercent = percent };
                var pages = renderer.Render(new AccountState[0], settings, system, 1.5, DateTime.UtcNow);
                LayoutTile pc = renderer.Layout.Tiles(true).Single(tile => tile.Settings.Id == "pc");
                Rect[] bounds = new[] { "Cpu", "Gpu", "Memory", "Disk" }.Select(name => {
                    var track = (FrameworkElement)renderer.Window.FindName("Compact" + name + "Track");
                    return track.TransformToAncestor(pc.Content).TransformBounds(new Rect(track.RenderSize));
                }).ToArray();
                if (expected == null) expected = bounds;
                for (int i = 0; i < bounds.Length; i++)
                    if (Math.Abs(bounds[i].X - expected[i].X) > 0.05 || Math.Abs(bounds[i].Y - expected[i].Y) > 0.05 ||
                        Math.Abs(bounds[i].Width - expected[i].Width) > 0.05 || Math.Abs(bounds[i].Height - expected[i].Height) > 0.05)
                        throw new InvalidOperationException("Windows PC widget changes track geometry with the reading");
                if (!((Canvas)renderer.Window.FindName("CompactPcChartGuides")).IsVisible ||
                    ((TextBlock)renderer.Window.FindName("CompactGpuValue")).Text != "N/A" ||
                    settings.Copy().ToJson() != saved)
                    throw new InvalidOperationException("Windows PC widget lost its grid, missing-device state or saved settings");
                foreach (string size in WindowsWidgetRenderer.Sizes)
                {
                    if (pages[size].Count != 1 || !pages[size][0]["alt"].Contains(percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"))
                        throw new InvalidOperationException("PC widget lost its current reading in " + size);
                    if (percent == 100)
                        File.WriteAllBytes(Path.Combine(directory, "pc-chart-" + size + ".png"),
                            Convert.FromBase64String(pages[size][0]["image"].Substring("data:image/png;base64,".Length)));
                }
                Grid root = (Grid)((FrameworkElement)renderer.Window.FindName("CompactLayout")).Parent;
                foreach (int height in WindowsWidgetRenderer.Heights)
                {
                    root.Measure(new Size(300, height)); root.Arrange(new Rect(0, 0, 300, height)); root.UpdateLayout();
                    Canvas guides = (Canvas)renderer.Window.FindName("CompactPcChartGuides");
                    foreach (TextBlock label in guides.Children.OfType<TextBlock>())
                    {
                        Rect rectangle = label.TransformToAncestor(guides).TransformBounds(new Rect(label.RenderSize));
                        if (rectangle.Top < -0.1 || rectangle.Bottom > guides.ActualHeight + 0.1 ||
                            rectangle.Left < -0.1 || rectangle.Right > guides.ActualWidth + 0.1)
                            throw new InvalidOperationException("PC axis label is clipped at widget height " + height + ": " + label.Text);
                    }
                }
            }
        }

        private static void CheckSmallGauge(string image, Color ring, string label)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(image.Substring("data:image/png;base64,".Length))))
            {
                BitmapSource bitmap = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
                int stride = bitmap.PixelWidth * 4, left = bitmap.PixelWidth, right = -1;
                byte[] pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
                for (int y = 0; y < bitmap.PixelHeight; y++) for (int x = 0; x < bitmap.PixelWidth; x++)
                {
                    int offset = y * stride + x * 4;
                    if (pixels[offset + 3] > 200 && Math.Abs(pixels[offset] - ring.B) < 12 &&
                        Math.Abs(pixels[offset + 1] - ring.G) < 12 && Math.Abs(pixels[offset + 2] - ring.R) < 12)
                    { left = Math.Min(left, x); right = Math.Max(right, x); }
                }
                if (right - left < 48) throw new InvalidOperationException("Small " + label + " ring is too small to read: " + (right - left) + " pixels at 2x");
            }
        }

        private static void CheckSettings(int width, int height, double scale, string evidence)
        {
            Window window = DashboardController.LoadWindow();
            Grid root = (Grid)window.Content;
            foreach (UIElement child in root.Children) child.Visibility = Visibility.Collapsed;
            Grid settings = (Grid)window.FindName("SettingsOverlay"); settings.Visibility = Visibility.Visible;
            List<Action> fontChanges = new List<Action>(); CollectFontChanges(settings, scale, fontChanges);
            foreach (Action change in fontChanges) change();
            Button button = (Button)window.FindName("WindowsWidgetInstallButton");
            window.Content = null; root.Resources.MergedDictionaries.Add(window.Resources);
            root.SetValue(Control.FontFamilyProperty, window.FontFamily);
            root.SetValue(Control.ForegroundProperty, window.Foreground);
            root.SetValue(Control.FontSizeProperty, window.FontSize);
            HwndSourceParameters parameters = new HwndSourceParameters("CodexUsageMeter.WidgetSettingsTest") {
                Width = width, Height = height, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
            };
            using (HwndSource surface = new HwndSource(parameters))
            {
                surface.RootVisual = root;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                ScrollViewer scroll = FindChild<ScrollViewer>(settings);
                if (scroll == null) throw new InvalidOperationException("Widget settings cannot scroll");
                Point offset = button.TranslatePoint(new Point(), (UIElement)scroll.Content);
                scroll.ScrollToVerticalOffset(offset.Y); root.UpdateLayout();
                Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                if (bounds.Left < 0 || bounds.Right > width + 0.5 || bounds.Top < 0 || bounds.Bottom > height + 0.5)
                    throw new InvalidOperationException("Widget install action is clipped: " + bounds);
                Point center = button.TransformToAncestor(root).Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                DependencyObject hit = root.InputHitTest(center) as DependencyObject;
                while (hit != null && hit != button) hit = VisualTreeHelper.GetParent(hit);
                if (hit == null) throw new InvalidOperationException("Widget install action is not clickable");
                TextBlock label = button.Content as TextBlock;
                if (label == null || label.ActualWidth > button.ActualWidth)
                    throw new InvalidOperationException("Widget install label is clipped");
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (Stream output = File.Create(Path.Combine(evidence, "widget-settings-" + width + ".png"))) encoder.Save(output);
                surface.RootVisual = null;
            }
            window.Close();
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T match = child as T ?? FindChild<T>(child);
                if (match != null) return match;
            }
            return null;
        }

        private static void CollectFontChanges(DependencyObject element, double scale, List<Action> changes)
        {
            TextBlock text = element as TextBlock; Control control = element as Control;
            if (text != null) { double size = text.FontSize * scale; changes.Add(delegate { text.FontSize = size; }); }
            else if (control != null) { double size = control.FontSize * scale; changes.Add(delegate { control.FontSize = size; }); }
            foreach (object child in LogicalTreeHelper.GetChildren(element))
                if (child is DependencyObject) CollectFontChanges((DependencyObject)child, scale, changes);
        }
    }
}
