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
using System.Windows.Input;
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
            CheckQuotaSizingAndScrollTheme(report, previewDirectory);
            SubscriptionRegressionTests.Run(report);
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
            Require(broken.Widget.Columns == 1 && broken.Widget.Cards.Count == 6 && broken.Widget.Card("account1").Span == 3 && broken.Widget.Card("account1").Size == 0 && broken.Widget.Card("account1").Sections.SequenceEqual(new[] { "weekly" }), "Invalid or duplicate settings were not normalized.");
            Require(!broken.Widget.Card("subscriptions").Visible && broken.Subscriptions.Count == 0, "Old layouts unexpectedly enabled the new card.");
            LayoutSettings reminders = LayoutSettings.Defaults();
            reminders.Subscriptions.Add(new SubscriptionEntry { Name = "예시 구독", AnchorDate = "2026-01-31", Cycle = "monthly" });
            reminders.Widget.Card("subscriptions").Visible = true;
            LayoutSettings remindersReloaded = LayoutSettings.Parse(reminders.ToJson());
            Require(remindersReloaded.Subscriptions.Count == 1 && remindersReloaded.Subscriptions[0].NextRenewal(new DateTime(2026, 3, 1)) == new DateTime(2026, 3, 31) && remindersReloaded.Widget.VisibleAccounts(4).Length == 4, "Subscription storage broke recurrence or account selection.");
            remindersReloaded.Widget.UseSingleAccount(1, true);
            Require(remindersReloaded.Widget.Card("subscriptions").Visible && remindersReloaded.Subscriptions.Count == 1, "Single-account preset discarded subscription visibility or data.");
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
                ((Grid)fixture.Window.FindName("SettingsOverlay")).Visibility = Visibility.Collapsed;
                LayoutSettings subscriptionLayout = LayoutSettings.Defaults();
                subscriptionLayout.Subscriptions.Add(new SubscriptionEntry { Name = "예시 구독 A", AnchorDate = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd"), Cycle = "monthly" });
                subscriptionLayout.Subscriptions.Add(new SubscriptionEntry { Name = "예시 구독 B", AnchorDate = DateTime.Today.AddDays(12).ToString("yyyy-MM-dd"), Cycle = "yearly" });
                subscriptionLayout.Widget.UseSingleAccount(1, true); subscriptionLayout.Widget.Card("pc").Visible = false;
                subscriptionLayout.Widget.Card("subscriptions").Visible = true;
                fixture.FontScale(1.5); fixture.Layout(subscriptionLayout, true); fixture.Render(460, 780);
                Require(Descendants<TextBlock>(fixture.Root).Any(text => text.IsVisible && text.Text == "예시 구독 A"), "Enabled subscription card did not render in the dashboard.");
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "widget-subscriptions.png"));
            }
            using (Fixture fixture = new Fixture())
            {
                LayoutSettings accepted = null, previewed = null;
                LayoutEditor editor = fixture.Editor(saved, true, state => previewed = state, state => accepted = state);
                RenderEditor(editor, root => {
                    Require(Descendants<CardLayoutPanel>(root).Any(), "Layout editor has no actual dashboard preview for direct card editing.");
                    Require(Descendants<Grid>(root).Contains(fixture.Root), "The editor replaced the real dashboard with a mock preview.");
                    Require(Descendants<Button>(fixture.Root).Where(button => !button.Name.Contains("AccountPage")).All(button => !button.IsHitTestVisible && !button.Focusable), "The preview allows a live dashboard action through mouse or keyboard.");
                    Require(!((Border)fixture.Window.FindName("TitleBar")).IsHitTestVisible && !((Border)fixture.Window.FindName("CompactTitleBar")).IsHitTestVisible, "Preview title bars can still move or maximize the live dashboard.");
                    Border target = Descendants<Border>(root).FirstOrDefault(row => row.AllowDrop);
                    Require(target != null, "The preview has no card drag targets. Adorners=" + Descendants<LayoutCardAdorner>(root).Count());
                    DragEventArgs drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                        null, new object[] { new DataObject("CodexMeter.Card", "pc"), DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, new Point(8, 8) }, null);
                    drop.RoutedEvent = UIElement.DropEvent; target.RaiseEvent(drop);
                    Require(drop.Handled && previewed.Widget.Cards[0].Id == "pc" && saved.Widget.Cards[0].Id == "account1", "Dragging a preview card did not move it independently.");
                    root.UpdateLayout();
                    LayoutTile pcTile = Descendants<CardLayoutPanel>(root).SelectMany(panel => panel.Tiles).First(tile => tile.Settings.Id == "pc" && tile.Card.IsVisible);
                    double beforeHeight = pcTile.Bounds.Height;
                    LayoutCardAdorner pcAdorner = Descendants<LayoutCardAdorner>(root).Single(item => item.AdornedElement == pcTile.Card);
                    Thumb handle = Descendants<Thumb>(pcAdorner).Single();
                    Point dragOrigin = handle.TransformToAncestor(root).Transform(new Point());
                    double dragScale = handle.TransformToAncestor(root).Transform(new Point(0, 1)).Y - dragOrigin.Y;
                    handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    foreach (double offset in new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 31, 50, 70, 90, 90 })
                    {
                        Point pointer = root.TransformToDescendant(handle).Transform(new Point(dragOrigin.X, dragOrigin.Y + offset * dragScale));
                        handle.RaiseEvent(new DragDeltaEventArgs(pointer.X, pointer.Y) { RoutedEvent = Thumb.DragDeltaEvent });
                        root.UpdateLayout();
                        Require(pcTile.Settings.Size == (offset < 30 ? 1 : 2), "Small repeated pointer movements accumulated or the snapped size oscillated.");
                    }
                    handle.RaiseEvent(new DragCompletedEventArgs(0, 90, false) { RoutedEvent = Thumb.DragCompletedEvent });
                    root.UpdateLayout();
                    Require(previewed.Widget.Card("pc").Size == 2 && previewed.Expanded.Card("pc").Size == 1 && pcTile.Bounds.Height > beforeHeight, "Dragging a size handle did not resize only the edited mode.");
                    CheckAdornerAlignment(root);
                    ScrollViewer scroll = Descendants<ScrollViewer>(root).First(item => item.IsVisible && item.Content is CardLayoutPanel);
                    scroll.ScrollToTop(); root.UpdateLayout();
                    MouseWheelEventArgs wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    pcAdorner.RaiseEvent(wheel); root.UpdateLayout();
                    Require(wheel.Handled && scroll.VerticalOffset > 0, "Mouse wheel over a preview card did not scroll the dashboard.");
                    CheckAdornerAlignment(root);
                    Button thirdAccount = Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "계정 3");
                    thirdAccount.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
                    Require(fixture.First.State.Number == 3, "Selecting an account on another page did not show it in the preview.");
                    Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "계정 1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "PC 상태").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    root.UpdateLayout(); CheckAdornerAlignment(root);
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "layout-editor-widget.png"));
                editor.SwitchMode(false);
                RenderEditor(editor, root => {
                    Require(fixture.First.Container.IsVisible && !fixture.First.CompactContainer.IsVisible, "Mode switch did not show the real expanded preview.");
                    CheckAdornerAlignment(root);
                    ComboBox viewport = Descendants<ComboBox>(root).Single(); viewport.SelectedIndex = 1; root.UpdateLayout();
                    CheckAdornerAlignment(root);
                    viewport.SelectedIndex = 0; root.UpdateLayout();
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "layout-editor-expanded.png"));
                RenderEditor(editor, root => {
                    foreach (Button button in Descendants<Button>(root).Where(item => Convert.ToString(item.Content) == "배치 저장" || Convert.ToString(item.Content) == "취소"))
                    {
                        Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                        Require(bounds.Left >= 0 && bounds.Right <= 780 && bounds.Top >= 0 && bounds.Bottom <= 540, "Small editor hides save or cancel.");
                    }
                    CheckAdornerAlignment(root);
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "layout-editor-small.png"), 780, 540);
                Require(editor.TrySave() && accepted.Widget.Cards[0].Id == "pc", "Editor save did not commit both mode drafts.");
                editor.Close();
                Require(Object.ReferenceEquals(fixture.Window.Content, fixture.Root) && Double.IsNaN(fixture.Root.Width), "Closing the editor did not restore the live dashboard and its size.");
                Require(((Button)fixture.Window.FindName("CloseButton")).Focusable && ((Button)fixture.Window.FindName("CloseButton")).IsHitTestVisible && ((Border)fixture.Window.FindName("TitleBar")).IsHitTestVisible, "Closing the editor did not restore dashboard interaction.");
                LayoutSettings cancelledPreview = null;
                LayoutEditor cancelled = fixture.Editor(saved, true, state => cancelledPreview = state, state => { throw new Exception("Cancel must not save"); });
                cancelled.MoveCard("pc", "account1"); cancelled.SwitchMode(false); cancelled.Close();
                Require(!cancelled.Saved && cancelledPreview.Widget.Cards[0].Id == "account1", "Closing the editor did not undo the live preview.");
                LayoutEditor failed = fixture.Editor(saved, false, state => { }, state => { throw new IOException("fixture write failure"); });
                Require(!failed.TrySave() && !failed.Saved, "Editor reported success after persistence failed."); failed.Close();
                bool initializationFailed = false;
                try { fixture.Editor(saved, true, state => { throw new IOException("fixture preview failure"); }, state => { }); }
                catch (IOException) { initializationFailed = true; }
                Require(initializationFailed && Object.ReferenceEquals(fixture.Window.Content, fixture.Root), "Preview initialization failure did not restore the live dashboard.");
                report("PASS real preview, routed drag/drop/resize/wheel, handle alignment, account/mode/viewport changes, save/cancel and failure restoration");
            }
        }

        private static void CheckQuotaSizingAndScrollTheme(Action<string> report, string previewDirectory)
        {
            List<string> failures = new List<string>();
            using (Fixture fixture = new Fixture())
            {
                LayoutSettings layout = LayoutSettings.Defaults();
                fixture.Accounts[0].LastSnapshot.Primary = null;
                fixture.Layout(layout, false); fixture.Render(1280, 820);
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "expanded-mixed-quotas.png"));
                double firstHeight = fixture.First.Container.ActualHeight, secondHeight = fixture.Second.Container.ActualHeight;
                if (Math.Abs(firstHeight - secondHeight) > 1)
                    failures.Add("Equal-size account cards have unequal heights after hiding a quota: " + firstHeight + " / " + secondHeight);
                fixture.Layout(layout, true); fixture.Render(460, 780);
                FrameworkElement weekly = Ancestor<Canvas>((FrameworkElement)fixture.Window.FindName("CompactAccount1SecondaryTrack"));
                FrameworkElement otherWeekly = Ancestor<Canvas>((FrameworkElement)fixture.Window.FindName("CompactAccount2SecondaryTrack"));
                if (((FrameworkElement)weekly.Parent).ActualWidth < ((FrameworkElement)otherWeekly.Parent).ActualWidth - 1)
                    failures.Add("Single quota shrinks the content group instead of using the available card width.");
                fixture.Render(320, 480);
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "widget-scroll.png"));
                ScrollBar bar = Descendants<ScrollBar>(fixture.Root).FirstOrDefault(item => item.IsVisible && item.Orientation == Orientation.Vertical && item.ActualHeight > 50);
                Require(bar != null, "Small widget did not reproduce a scrollable dashboard.");
                Rect barBounds = bar.TransformToAncestor(fixture.Root).TransformBounds(new Rect(bar.RenderSize));
                RenderTargetBitmap bitmap = new RenderTargetBitmap(320, 480, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(fixture.Root);
                Int32Rect crop = new Int32Rect((int)barBounds.X, (int)barBounds.Y, (int)barBounds.Width, (int)barBounds.Height);
                byte[] pixels = new byte[crop.Width * crop.Height * 4]; bitmap.CopyPixels(crop, pixels, crop.Width * 4, 0);
                int light = 0;
                for (int n = 0; n < pixels.Length; n += 4) if (pixels[n + 3] > 200 && pixels[n] > 160 && pixels[n + 1] > 160 && pixels[n + 2] > 160) light++;
                if (light > crop.Width * crop.Height / 10) failures.Add("Scrollbar contains a light Windows theme surface.");
            }
            foreach (string failure in failures) report("FAIL " + failure);
            Require(failures.Count == 0, "Dashboard sizing/theme regressions: " + failures.Count);
            report("PASS mixed quota card alignment, single quota width and dark rendered scrollbar");
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
        private static void RenderEditor(LayoutEditor editor, Action<Grid> check, string path, int width = 1220, int height = 860)
        {
            Grid content = (Grid)editor.Content; editor.Content = null;
            content.Resources.MergedDictionaries.Add(editor.Resources);
            content.Background = editor.Background; content.SetValue(Control.ForegroundProperty, editor.Foreground);
            content.SetValue(Control.FontFamilyProperty, editor.FontFamily); content.SetValue(Control.FontSizeProperty, editor.FontSize);
            try
            {
                using (HwndSource surface = Surface(width, height))
                {
                    surface.RootVisual = content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                    content.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(delegate { })); content.UpdateLayout();
                    check(content); content.UpdateLayout();
                    if (path != null) Capture(content, width, height, path);
                    surface.RootVisual = null;
                }
            }
            finally { content.Resources.MergedDictionaries.Remove(editor.Resources); editor.Content = content; }
        }
        private static void CheckAdornerAlignment(Grid root)
        {
            foreach (LayoutCardAdorner adorner in Descendants<LayoutCardAdorner>(root).Where(item => item.IsVisible))
            {
                Rect actual = adorner.TransformToAncestor(root).TransformBounds(new Rect(adorner.RenderSize));
                FrameworkElement card = (FrameworkElement)adorner.AdornedElement;
                Rect expected = card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
                Require(Math.Abs(actual.X - expected.X) < 2 && Math.Abs(actual.Y - expected.Y) < 2 && Math.Abs(actual.Width - expected.Width) < 2 && Math.Abs(actual.Height - expected.Height) < 2,
                    "Editing handles do not match their actual card bounds: " + card.Name + " " + actual + " / " + expected);
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
                    if (n == 1) snapshot.Primary = null;
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
            internal LayoutEditor Editor(LayoutSettings settings, bool compact, Action<LayoutSettings> preview, Action<LayoutSettings> save)
            {
                _surface.RootVisual = null;
                if (Window.Content == null) Window.Content = Root;
                return new LayoutEditor(settings, compact, 4, Window, (DashboardLayoutView)typeof(DashboardController).GetField("_layoutView", Flags).GetValue(Controller),
                    (draft, mode, selected) => {
                        Layout(draft, mode);
                        if (selected != null && selected.StartsWith("account"))
                        {
                            int index = Array.IndexOf(draft.Mode(mode).VisibleAccounts(4), Int32.Parse(selected.Substring(7)));
                            if (index >= 0) Page(index / 2);
                        }
                        preview(draft);
                    }, save);
            }
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
