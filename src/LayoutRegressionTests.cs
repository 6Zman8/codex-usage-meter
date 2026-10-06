using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageMeter
{
    internal static class LayoutRegressionTests
    {
        internal static int CheckReload(string path)
        {
            LayoutSettings loaded = LayoutSettings.Parse(File.ReadAllText(path));
            return loaded.Widget.Cards[0].Id == "pc" && loaded.Widget.VisibleAccounts(4).SequenceEqual(new[] { 3 }) &&
                loaded.Widget.Card("pc").Span == 2 && loaded.Widget.Card("pc").Size == 2 &&
                loaded.Expanded.VisibleAccounts(4).Length == 4 ? 0 : 1;
        }

        public static void Run(Action<string> report, string previewDirectory, string evidenceDirectory)
        {
            LayoutSettings saved = LayoutSettings.Defaults();
            LayoutSettings edit = saved.Copy();
            edit.Widget.UseSingleAccount(3, true);
            edit.Widget.Card("account3").SetSection("short", false);
            edit.Widget.Columns = 2;
            edit.Widget.Card("pc").Span = 2;
            edit.Widget.Card("pc").Size = 2;
            edit.Widget.Move("pc", "account1");
            Require(saved.Widget.VisibleAccounts(4).Length == 4 && saved.Widget.Card("account3").Shows("short"), "Editing a draft mutated the original layout.");
            LayoutSettings loaded = LayoutSettings.Parse(edit.ToJson());
            Require(loaded.Widget.VisibleAccounts(4).SequenceEqual(new[] { 3 }) && loaded.Widget.Cards[0].Id == "pc" &&
                loaded.Widget.Card("pc").Span == 2 && loaded.Widget.Card("pc").Size == 2 && !loaded.Widget.Card("account3").Shows("short"), "Saved layout did not round-trip.");
            Require(loaded.Expanded.VisibleAccounts(4).Length == 4 && loaded.Expanded.Columns == 3, "Widget editing changed the expanded screen.");
            report("PASS per-mode save/reload, order, dimensions, single-account preset and draft isolation");
            string fixturePath = Path.Combine(evidenceDirectory, "layout-storage-fixture.json");
            File.WriteAllText(fixturePath, edit.ToJson());
            using (Process child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--layout-reload-check \"" + fixturePath + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                Require(child.WaitForExit(10000) && child.ExitCode == 0, "Saved layout failed to reload in a fresh process.");
            report("PASS persisted layout reloads with identical selections in a fresh executable process");
            Require(LayoutSettings.Parse("broken").Widget.VisibleAccounts(4).Length == 4, "Damaged storage did not fall back safely.");
            LayoutSettings broken = LayoutSettings.Parse("{\"Version\":1,\"Widget\":{\"Columns\":-7,\"Cards\":[null,{\"Id\":\"account1\",\"Visible\":true,\"Span\":99,\"Size\":-4,\"Sections\":[\"weekly\",\"bad\"]},{\"Id\":\"account1\"},{\"Id\":\"bad\"}]}}");
            Require(broken.Widget.Columns == 1 && broken.Widget.Cards.Count == 5 && broken.Widget.Card("account1").Span == 3 && broken.Widget.Card("account1").Size == 0 && broken.Widget.Card("account1").Sections.SequenceEqual(new[] { "weekly" }), "Invalid or duplicate settings were not normalized.");
            report("PASS corrupt/partial settings recover without duplicate cards or invalid dimensions");

            using (Fixture fixture = new Fixture())
            {
                LayoutSettings layout = LayoutSettings.Defaults();
                layout.Expanded.UseSingleAccount(3, false);
                layout.Expanded.Card("account3").SetSection("short", false);
                fixture.Layout(layout, false);
                Require(fixture.First.State.Number == 3 && fixture.Second.State == null && fixture.Accounts.Count == 4, "Hidden accounts were removed or bound to the wrong card.");
                fixture.Render(1280, 820);
                Border first = fixture.First.Container;
                CardLayoutPanel panel = (CardLayoutPanel)((Viewbox)first.Parent).Parent;
                Require(panel.Tiles.Count(tile => tile.Host.Visibility == Visibility.Visible) == 2, "Single-account layout keeps an empty account slot.");
                Rect accountBounds = panel.Tiles.Single(tile => tile.Card == first).Bounds;
                Require(accountBounds.Width > 580, "Single-account layout failed to reclaim the unused column.");
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "expanded-single.png"));
                layout.Expanded.Move("pc", "account1"); fixture.Layout(layout, false); fixture.Render(1280, 820);
                Require(panel.Tiles.First(tile => tile.Host.Visibility == Visibility.Visible).Settings.Id == "pc", "Moving a card did not change the rendered order.");
                layout.Expanded.Card("pc").Span = 2; layout.Expanded.Card("pc").Size = 2;
                fixture.Layout(layout, false); fixture.Render(1280, 820);
                Require(panel.Tiles.Single(tile => tile.Settings.Id == "pc").Bounds.Width > 1200, "Card column span did not change its actual width.");
                fixture.Layout(LayoutSettings.Defaults(), false); fixture.Page(1);
                Require(fixture.First.State.Number == 3 && fixture.Second.State.Number == 4 && fixture.Accounts.Count == 4, "Restoring multiple accounts lost page 2.");
                fixture.Page(0); Require(fixture.First.State.Number == 1 && fixture.Second.State.Number == 2, "Account page restoration failed.");
                fixture.Render(1280, 820);
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "expanded-multiple.png"));
                fixture.FontScale(2); fixture.Render(900, 620);
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "expanded-200.png"));
                fixture.FontScale(1.5);
                report("PASS real account binding, hidden-account preservation, reclaimed columns, reorder, span and restored pages");

                layout = LayoutSettings.Defaults();
                layout.Widget.UseSingleAccount(1, true);
                layout.Widget.Card("account1").SetSection("short", true);
                fixture.Accounts[0].LastSnapshot.Primary = null;
                fixture.Layout(layout, true); fixture.Render(460, 780);
                Require(Ancestor<Canvas>((FrameworkElement)fixture.Window.FindName("CompactAccount1PrimaryTrack")).Visibility == Visibility.Collapsed, "Unavailable short quota was not hidden.");
                Require(fixture.First.CompactSecondaryRing.Data != null && !fixture.First.CompactSecondaryRing.Data.IsEmpty(), "Weekly quota disappeared.");
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "widget-single.png"));
                fixture.Accounts[0].LastSnapshot.Error = "temporary query error";
                fixture.Layout(layout, true);
                Require(Ancestor<Canvas>((FrameworkElement)fixture.Window.FindName("CompactAccount1PrimaryTrack")).Visibility == Visibility.Visible, "A query error incorrectly hid a quota.");
                fixture.Accounts[0].LastSnapshot.Error = null;
                layout.Widget.Card("pc").SetSection("gpu", false);
                layout.Widget.Card("pc").SetSection("network", false);
                fixture.Layout(layout, true);
                Require(((FrameworkElement)fixture.Window.FindName("CompactNetworkValue")).Visibility == Visibility.Collapsed, "Network hiding did not reach the real widget.");
                foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                {
                    fixture.FontScale(scale);
                    foreach (int[] size in new[] { new[] { 320, 480 }, new[] { 460, 780 }, new[] { 900, 500 } })
                    {
                        fixture.Render(size[0], size[1]);
                        CardLayoutPanel compactPanel = (CardLayoutPanel)((Viewbox)fixture.First.CompactContainer.Parent).Parent;
                        foreach (LayoutTile tile in compactPanel.Tiles.Where(tile => tile.Host.Visibility == Visibility.Visible))
                            Require(tile.Bounds.Width > 0 && tile.Bounds.X >= 0 && tile.Bounds.Right <= size[0] && !Double.IsNaN(tile.Bounds.Height), "A narrow window produced clipped or invalid card bounds.");
                        if (previewDirectory != null && scale == 2 && size[0] == 320) fixture.Capture(Path.Combine(previewDirectory, "widget-small-200.png"));
                    }
                }
                foreach (LayoutCardSettings card in layout.Widget.Cards) card.Visible = false;
                fixture.Layout(layout, true); fixture.Render(460, 780);
                Require(Descendants<TextBlock>(fixture.Root).Any(text => text.Visibility == Visibility.Visible && text.Text.StartsWith("표시할 카드가 없습니다.")), "Empty layout has no recovery instruction.");
                Require(fixture.Accounts.Count == 4, "Empty layout changed account data.");
                report("PASS unavailable-quota rules, PC visibility, empty-state recovery and 100-200% narrow-window rendering");
                layout = LayoutSettings.Defaults(); layout.Widget.UseSingleAccount(1, true); layout.Widget.Card("pc").Visible = false;
                fixture.Layout(layout, true); fixture.FontScale(1.5); fixture.Render(460, 250);
                Rect normal = fixture.First.CompactContainer.TransformToAncestor(fixture.Root).TransformBounds(new Rect(fixture.First.CompactContainer.RenderSize));
                layout.Widget.Card("account1").Size = 0; fixture.Layout(layout, true); fixture.Render(460, 250);
                Rect shortCard = fixture.First.CompactContainer.TransformToAncestor(fixture.Root).TransformBounds(new Rect(fixture.First.CompactContainer.RenderSize));
                bool fixedWidth = Math.Abs(normal.Width - shortCard.Width) < 1 && shortCard.Height < normal.Height;
                fixture.FontScale(2);
                ((Grid)fixture.Window.FindName("SettingsOverlay")).Visibility = Visibility.Visible;
                fixture.Render(320, 480);
                Button editButton = (Button)fixture.Window.FindName("LayoutEditButton");
                Rect editBounds = editButton.TransformToAncestor(fixture.Root).TransformBounds(new Rect(editButton.RenderSize));
                bool accessible = editBounds.Top >= 0 && editBounds.Bottom <= 480 && editBounds.Left >= 0 && editBounds.Right <= 320;
                if (!fixedWidth || !accessible) throw new InvalidOperationException("Card height preserves width=" + fixedWidth + "; small-window editor entry accessible=" + accessible);
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "settings-small-200.png"));
                report("PASS independent card height preserves width and editor entry stays accessible in small settings");
            }
            LayoutSettings accepted = null, previewed = null;
            LayoutEditor editor = new LayoutEditor(saved, true, 4, state => previewed = state, state => accepted = state);
            Grid editorRoot = (Grid)editor.Content;
            editorRoot.Measure(new Size(550, 820)); editorRoot.Arrange(new Rect(0, 0, 550, 820)); editorRoot.UpdateLayout();
            Border target = Descendants<Border>(editorRoot).First(row => row.AllowDrop);
            DragEventArgs drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { new DataObject("CodexMeter.Card", "pc"), DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, new Point(8, 8) }, null);
            drop.RoutedEvent = UIElement.DropEvent; target.RaiseEvent(drop);
            Require(drop.Handled, "The editor did not handle the real routed drop event.");
            Require(previewed.Widget.Cards[0].Id == "pc" && saved.Widget.Cards[0].Id == "account1", "Editor movement was not previewed independently.");
            Require(editor.TrySave() && accepted.Widget.Cards[0].Id == "pc", "Editor save did not commit the previewed layout.");
            LayoutSettings cancelledPreview = null;
            LayoutEditor cancelled = new LayoutEditor(saved, true, 4, state => cancelledPreview = state, state => { throw new Exception("Cancel must not save"); });
            cancelled.MoveCard("pc", "account1"); cancelled.Close();
            Require(!cancelled.Saved && cancelledPreview.Widget.Cards[0].Id == "account1", "Closing the editor did not undo the live preview.");
            LayoutEditor failed = new LayoutEditor(saved, false, 4, state => { }, state => { throw new IOException("fixture write failure"); });
            Require(!failed.TrySave() && !failed.Saved, "Editor reported success after persistence failed.");
            if (previewDirectory != null) CaptureEditor(editor, Path.Combine(previewDirectory, "layout-editor.png"));
            report("PASS routed editor drop/reorder/preview/save/cancel and persistence failure handling");
        }

        private static T Ancestor<T>(FrameworkElement element) where T : FrameworkElement
        { while (element != null && !(element is T)) element = element.Parent as FrameworkElement; return (T)element; }
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int n = 0; n < VisualTreeHelper.GetChildrenCount(parent); n++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, n);
                if (child is T) yield return (T)child;
                foreach (T nested in Descendants<T>(child)) yield return nested;
            }
        }
        private static void CaptureEditor(LayoutEditor editor, string path)
        {
            Grid content = (Grid)editor.Content; editor.Content = null;
            content.Resources.MergedDictionaries.Add(editor.Resources);
            content.Background = editor.Background; content.SetValue(Control.ForegroundProperty, editor.Foreground);
            content.SetValue(Control.FontFamilyProperty, editor.FontFamily); content.SetValue(Control.FontSizeProperty, editor.FontSize);
            using (HwndSource surface = Surface(550, 820))
            {
                surface.RootVisual = content; content.Measure(new Size(550, 820)); content.Arrange(new Rect(0, 0, 550, 820)); content.UpdateLayout();
                Capture(content, 550, 820, path);
            }
        }
        private static HwndSource Surface(int width, int height)
        {
            HwndSourceParameters parameters = new HwndSourceParameters("CodexUsageMeter.LayoutTest");
            parameters.WindowStyle = unchecked((int)0x80000000); parameters.ExtendedWindowStyle = 0x08000080;
            parameters.Width = width; parameters.Height = height;
            return new HwndSource(parameters);
        }
        private static void Capture(Visual visual, int width, int height, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream output = File.Create(path)) encoder.Save(output);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class Fixture : IDisposable
        {
            internal Window Window = DashboardController.LoadWindow();
            internal DashboardController Controller;
            internal AccountView First, Second;
            internal List<AccountState> Accounts = new List<AccountState>();
            internal Grid Root;
            private HwndSource _surface;
            private int _width, _height;
            private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
            internal Fixture()
            {
                Controller = (DashboardController)FormatterServices.GetUninitializedObject(typeof(DashboardController));
                Set("_window", Window);
                foreach (FieldInfo field in typeof(DashboardController).GetFields(Flags))
                {
                    if (typeof(FrameworkElement).IsAssignableFrom(field.FieldType))
                    {
                        string name = Char.ToUpperInvariant(field.Name[1]) + field.Name.Substring(2);
                        object value = Window.FindName(name);
                        if (value != null) field.SetValue(Controller, value);
                    }
                }
                foreach (string name in new[] { "_fontTargets", "_fontTargetElements", "_performanceCards", "_performanceHistory" })
                    Set(name, Activator.CreateInstance(typeof(DashboardController).GetField(name, Flags).FieldType));
                First = (AccountView)Call("CreateAccountView", 1, "계정 1", null);
                Second = (AccountView)Call("CreateAccountView", 2, "계정 2", null);
                Set("_account1", First); Set("_account2", Second); Set("_accounts", Accounts); Set("_accountCount", 4);
                for (int n = 1; n <= 4; n++)
                {
                    AccountSnapshot snapshot = (AccountSnapshot)typeof(DashboardController).GetMethod("CreatePreviewSnapshot", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { "예시 계정 " + n, n == 1 ? "prolite" : "plus", 75.0, 98.0, 2, 0 });
                    Accounts.Add(new AccountState { Number = n, Label = "계정 " + n, LastSnapshot = snapshot });
                }
                Set("_layouts", LayoutSettings.Defaults());
                Set("_layoutView", new DashboardLayoutView(Window));
                Root = (Grid)Window.Content;
                Root.Resources.MergedDictionaries.Add(Window.Resources);
                Root.SetValue(Control.FontFamilyProperty, Window.FontFamily); Root.SetValue(Control.ForegroundProperty, Window.Foreground);
                Call("CaptureFontTargets", Root);
                FontScale(1.5);
                SystemSnapshot system = new SystemSnapshot { CpuPercent = 28, MemoryPercent = 63, MemoryUsedGb = 20, MemoryTotalGb = 32 };
                system.Gpus.Add(new GpuSnapshot { Key = "gpu:0", Name = "예시 GPU", Percent = 41 });
                system.Disks.Add(new DiskSnapshot { Key = "disk:0", Name = "디스크 0", Percent = 12, Detail = "예시 SSD" });
                system.Networks.Add(new NetworkSnapshot { Key = "network:0", Name = "네트워크", Connected = true, Percent = 4 });
                object items = typeof(DashboardController).GetMethod("BuildPerformanceItems", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { system });
                Call("UpdatePerformanceCards", items);
                foreach (string prefix in new[] { "Cpu", "Gpu", "Memory", "Disk" })
                {
                    ((TextBlock)Window.FindName("Compact" + prefix + "Value")).Text = "28%";
                    ((System.Windows.Shapes.Path)Window.FindName("Compact" + prefix + "Ring")).Data = Geometry.Parse("M 48,13 A 35,35 0 0 1 82,57");
                }
                ((TextBlock)Window.FindName("CompactNetworkValue")).Text = "↓ 11.3 MB/s   ↑ 684 KB/s";
                Window.Content = null; _surface = Surface(1280, 820); _surface.RootVisual = Root;
            }
            internal void Layout(LayoutSettings settings, bool compact)
            {
                Set("_layouts", settings); Set("_compactMode", compact); Set("_accountPage", 0);
                ((Grid)Window.FindName("CompactLayout")).Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                ((Grid)Window.FindName("ExpandedLayout")).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                Call("BindAccountPage");
            }
            internal void Page(int page) { Call("SetAccountPage", page); }
            internal void FontScale(double scale) { Call("ApplyFontScale", scale, false); }
            internal void Render(int width, int height)
            {
                _width = width; _height = height;
                Root.Measure(new Size(width, height)); Root.Arrange(new Rect(0, 0, width, height)); Root.UpdateLayout();
            }
            internal void Capture(string path) { LayoutRegressionTests.Capture(Root, _width, _height, path); }
            private void Set(string name, object value) { typeof(DashboardController).GetField(name, Flags).SetValue(Controller, value); }
            private object Call(string name, params object[] args) { return typeof(DashboardController).GetMethod(name, Flags).Invoke(Controller, args); }
            public void Dispose() { _surface.Dispose(); }
        }
    }
}
