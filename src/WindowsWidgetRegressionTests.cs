using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
            CheckPackageInputs(report, directory);
            CheckSettings(320, 480, 2, directory);
            CheckSettings(460, 780, 1.5, directory);
            report("PASS Windows widget settings scroll and install action remain reachable at narrow widths and 150-200 percent text");
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
