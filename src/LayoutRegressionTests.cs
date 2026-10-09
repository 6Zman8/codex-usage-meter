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
            string serialized = File.ReadAllText(path);
            LayoutSettings loaded = LayoutSettings.Parse(serialized);
            return loaded.ToJson() == serialized && loaded.Widget.Cards[0].Id == "pc" && loaded.Widget.VisibleAccounts(4).SequenceEqual(new[] { 3 }) &&
                loaded.Widget.Card("pc").Span == 2 && loaded.Widget.Card("pc").Size == 2 &&
                loaded.Widget.Card("pc").ItemLayouts.Any(item => item.Id == "cpu" && item.X == 0.25 && item.Width == 0.4) &&
                !loaded.Widget.Card("pc").ShowsItem("network") &&
                loaded.Expanded.VisibleAccounts(4).Length == 4 ? 0 : 1;
        }

        public static void Run(Action<string> report, string previewDirectory, string evidenceDirectory)
        {
            LayoutSettings itemLayout = LayoutSettings.Parse("{\"Version\":1,\"Widget\":{\"Columns\":1,\"Cards\":[{\"Id\":\"account1\",\"Visible\":true,\"Span\":1,\"Size\":1,\"ItemLayouts\":[{\"Id\":\"weekly\",\"X\":0.2,\"Y\":0.3,\"Width\":0.4,\"Height\":0.5}]}]}}");
            Require(itemLayout.Copy().ToJson().Contains("\"X\":0.2"), "individual item positions and sizes did not survive save/reload");
            VerifyEditorZoom(report, previewDirectory);
            VerifyDefaultSpacing(report, previewDirectory);
            VerifyContentLayout(report, previewDirectory);
            VerifyItemVisibility(report);
            VerifySnapping(report, previewDirectory);
            VerifyUpgradeLayout(report);
            CheckAccountAlignmentAndFit(report, previewDirectory);
            SubscriptionRegressionTests.Run(report);
            AccountSubscriptionRegressionTests.Run(report);
            WebSubscriptionTests.RunData(report, evidenceDirectory);
            ChromeSubscriptionTests.Run(report, evidenceDirectory);
            LayoutSettings saved = LayoutSettings.Defaults();
            LayoutSettings edit = saved.Copy();
            edit.Widget.UseSingleAccount(3, true);
            edit.Widget.Card("account3").SetSection("short", false);
            edit.Widget.Columns = 2;
            edit.Widget.Card("pc").Span = 2;
            edit.Widget.Card("pc").Size = 2;
            edit.Widget.Card("pc").ItemLayouts.Add(new LayoutItemSettings { Id = "cpu", X = 0.25, Y = 0.15, Width = 0.4, Height = 0.35 });
            edit.Widget.Card("pc").SetItemVisible("network", false);
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
            LayoutSettings reminders = LayoutSettings.Defaults();
            reminders.Subscriptions.Add(new SubscriptionEntry { Name = "예시 구독", AnchorDate = "2026-01-31", Cycle = "monthly" });
            reminders.Widget.Cards.Add(new LayoutCardSettings { Id = "subscriptions", Visible = true });
            LayoutSettings remindersReloaded = LayoutSettings.Parse(reminders.ToJson());
            Require(remindersReloaded.Subscriptions.Count == 1 && remindersReloaded.Subscriptions[0].NextRenewal(new DateTime(2026, 3, 1)) == new DateTime(2026, 3, 31) && remindersReloaded.Widget.VisibleAccounts(4).Length == 4, "Subscription storage broke recurrence or account selection.");
            remindersReloaded.Widget.UseSingleAccount(1, true);
            Require(!remindersReloaded.Widget.Cards.Any(card => card.Id == "subscriptions") && remindersReloaded.Subscriptions.Count == 1, "Legacy service data was erased or its removed card reappeared.");
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
                Require(Ancestor<Canvas>((FrameworkElement)fixture.Window.FindName("CompactAccount1PrimaryTrack")).Visibility == Visibility.Visible, "Unavailable short quota lost its reserved place.");
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
                fixture.Layout(layout, true); fixture.FontScale(1.5); fixture.Render(460, 780);
                Rect normal = fixture.First.CompactContainer.TransformToAncestor(fixture.Root).TransformBounds(new Rect(fixture.First.CompactContainer.RenderSize));
                layout.Widget.Card("account1").Size = 0; fixture.Layout(layout, true); fixture.Render(460, 780);
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
                fixture.FontScale(1.5); fixture.Layout(remindersReloaded, true); fixture.Render(460, 780);
                Require(!Descendants<TextBlock>(fixture.Root).Any(text => text.IsVisible && text.Text == "예시 구독"), "Removed generic subscription card is still displayed.");
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
                    Require(!Descendants<ScrollViewer>(root).Any(item => item.IsVisible && item.Content is CardLayoutPanel), "Layout preview still requires scrolling after a card resize.");
                    foreach (CardLayoutPanel panel in Descendants<CardLayoutPanel>(root).Where(item => item.IsVisible))
                        Require(panel.Tiles.Where(item => item.Card.IsVisible).All(item => item.Bounds.Bottom <= panel.ActualHeight + 1), "Resizing a preview card pushed another card below the viewport.");
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
                report("PASS real preview, routed drag/drop/resize without overflow, handle alignment, account/mode/viewport changes, save/cancel and failure restoration");
            }
        }

        private static void VerifyEditorZoom(Action<string> report, string previewDirectory)
        {
            using (Fixture fixture = new Fixture())
            {
                LayoutSettings settings = LayoutSettings.Defaults();
                LayoutEditor editor = fixture.Editor(settings, false, state => { }, state => { });
                new WindowInteropHelper(editor).EnsureHandle();
                double originalWidth = editor.Width, originalHeight = editor.Height;
                double originalLeft = editor.Left, originalTop = editor.Top;
                string originalLayout = editor.Draft.ToJson();
                RenderEditor(editor, root => {
                    Button maximize = Descendants<Button>(root).FirstOrDefault(button => button.Name == "LayoutMaximizeButton");
                    Require(maximize != null, "layout editor has no maximize/restore control");
                    maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(Convert.ToString(maximize.ToolTip) == "이전 크기로 복원" && editor.Width >= originalWidth && editor.Height >= originalHeight,
                        "maximize did not enlarge the real hidden editor window");
                    maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(editor.Width == originalWidth && editor.Height == originalHeight && editor.Left == originalLeft && editor.Top == originalTop,
                        "restore lost the original editor bounds");
                    Border title = Descendants<Border>(root).Single(item => item.Name == "LayoutTitleBar");
                    for (int n = 0; n < 2; n++)
                    {
                        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                        typeof(MouseButtonEventArgs).GetProperty("ClickCount").SetValue(click, 2, null); title.RaiseEvent(click);
                        Require(click.Handled && Convert.ToString(maximize.Content) == (n == 0 ? "❐" : "□"), "title double-click did not toggle maximize");
                    }
                    ScrollViewer scroll = Descendants<ScrollViewer>(root).Single(item => item.Name == "LayoutPreviewScroll");
                    editor.SetPreviewZoom(2); root.UpdateLayout();
                    double displayedScale = fixture.Root.TransformToAncestor(root).Transform(new Point(1, 0)).X - fixture.Root.TransformToAncestor(root).Transform(new Point()).X;
                    Require(Math.Abs(displayedScale - 2) < 0.001 && scroll.ScrollableWidth > 0 && scroll.ScrollableHeight > 0,
                        "200% zoom did not enlarge the real preview with scrollable overflow");
                    scroll.ScrollToHorizontalOffset(240); scroll.ScrollToVerticalOffset(180); root.UpdateLayout();
                    Point anchor = new Point(180, 140), originalPoint = scroll.TranslatePoint(anchor, fixture.Root);
                    editor.ZoomWithWheel(120, anchor); editor.ZoomWithWheel(120, anchor); root.UpdateLayout();
                    Point afterZoom = fixture.Root.TranslatePoint(originalPoint, scroll);
                    Require(editor.PreviewZoom > 2 && Math.Abs(afterZoom.X - anchor.X) < 1 && Math.Abs(afterZoom.Y - anchor.Y) < 1,
                        "consecutive wheel zoom moved the content away from the pointer: " + afterZoom);
                    CheckAdornerAlignment(root);
                    if (previewDirectory != null) Capture(root, 1220, 860, Path.Combine(previewDirectory, "editor-zoom-scrolled.png"));
                    editor.SetPreviewZoom(99); root.UpdateLayout();
                    Require(editor.PreviewZoom == 4 && !Descendants<Button>(root).Single(button => button.Name == "LayoutZoomIn").IsEnabled, "zoom upper bound failed");
                    editor.SetPreviewZoom(0); root.UpdateLayout();
                    Require(editor.PreviewZoom == 0.25 && !Descendants<Button>(root).Single(button => button.Name == "LayoutZoomOut").IsEnabled, "zoom lower bound failed");
                    editor.FitPreview(); root.UpdateLayout();
                    Require(scroll.ScrollableWidth < 1 && scroll.ScrollableHeight < 1 && editor.Draft.ToJson() == originalLayout,
                        "fit-to-screen changed saved layout or left unreachable content");
                    editor.SetPreviewZoom(1.5); root.UpdateLayout();
                    editor.SetItemEditing(true); root.UpdateLayout();
                    scroll.ScrollToTop(); scroll.ScrollToLeftEnd(); root.UpdateLayout();
                    Thumb weekly = Descendants<Thumb>(root).Single(thumb => Convert.ToString(thumb.Tag) == "move:weekly");
                    LayoutTile tile = Descendants<CardLayoutPanel>(root).Single(panel => panel.IsVisible).Tiles.First();
                    LayoutItemSettings before = CardContentLayout.Position(tile, tile.ContentItems().Single(item => item.Id == "weekly"));
                    DragItem(root, weekly, 0, 20);
                    LayoutItemSettings after = CardContentLayout.Position(tile, tile.ContentItems().Single(item => item.Id == "weekly"));
                    Require(after.Y > before.Y && Math.Abs(after.Width - before.Width) < 0.001, "item dragging broke in the zoomed preview");
                    editor.FitPreview(); root.UpdateLayout();
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "editor-zoom-normal.png"));
                editor.SwitchMode(true);
                RenderEditor(editor, root => {
                    editor.FitPreview(); root.UpdateLayout();
                    Require(editor.PreviewZoom > 1, "maximized work area still refuses to enlarge a widget preview");
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "editor-zoom-large.png"), 1920, 1200);
                RenderEditor(editor, root => {
                    foreach (Button button in Descendants<Button>(root).Where(item => item.Name.StartsWith("LayoutZoom") || item.Name == "LayoutMaximizeButton" || Convert.ToString(item.Content) == "배치 저장"))
                    {
                        Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                        Require(bounds.Left >= 0 && bounds.Right <= 780 && bounds.Top >= 0 && bounds.Bottom <= 540, "small editor hides a zoom or window control: " + button.Name);
                    }
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "editor-zoom-small.png"), 780, 540);
                editor.Close();
            }
            report("PASS editor: maximize/restore and preview zoom remain usable without changing saved card dimensions");
        }

        private static void VerifyDefaultSpacing(Action<string> report, string previewDirectory)
        {
            using (Fixture fixture = new Fixture())
            {
                fixture.Layout(LayoutSettings.Defaults(), true); fixture.Render(460, 780);
                LayoutTile tile = Descendants<CardLayoutPanel>(fixture.Root).Single(panel => panel.IsVisible).Tiles.First();
                Func<string, Rect> bounds = id => {
                    FrameworkElement element = tile.ContentItems().Single(item => item.Id == id).Element;
                    return element.TransformToAncestor(tile.Card).TransformBounds(new Rect(element.RenderSize));
                };
                double narrowGap = bounds("short").Left - bounds("weekly").Right;
                double ringWidth = bounds("weekly").Width;
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "default-spacing-widget.png"));
                Require(narrowGap >= 12, "default widget gauges are crowded: gap=" + narrowGap);
                fixture.Render(900, 780);
                double wideGap = bounds("short").Left - bounds("weekly").Right;
                Require(wideGap > narrowGap + 20 && bounds("weekly").Width >= ringWidth - 1, "wider cards keep their contents clustered at a fixed width");
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "default-spacing-wide.png"));
                foreach (double font in new[] { 1.0, 1.5, 2.0 })
                    foreach (int height in new[] { 780, 1000 })
                    {
                        fixture.FontScale(font); fixture.Render(320, height);
                        Rect inner = CardContentLayout.InnerBounds(tile);
                        foreach (string id in new[] { "weekly", "short", "countdown" })
                        {
                            Rect item = bounds(id);
                            Require(item.Left >= inner.Left - 1 && item.Right <= inner.Right + 1,
                                "roomier defaults overflow a narrow card: " + id + ", " + item + ", inner=" + inner);
                        }
                    }
                fixture.FontScale(1.5);
                LayoutSettings legacy = LayoutSettings.Parse("{\"Version\":1,\"Widget\":{\"Columns\":1}}");
                legacy.Widget.Card("account1").ItemLayouts.Add(new LayoutItemSettings { Id = "countdown", X = 0.5501, Y = 0.3028, Width = 0.1271, Height = 0.4169 });
                fixture.Layout(legacy, true); fixture.Render(900, 780);
                LayoutItemSettings restored = CardContentLayout.Position(tile, tile.ContentItems().Single(item => item.Id == "countdown"));
                Require(Math.Abs(restored.Y - 0.3028) < 0.002 && Math.Abs(restored.Height - 0.4169) < 0.002,
                    "v1.5.0 saved countdown was shrunk by the roomier default: y=" + restored.Y + ", h=" + restored.Height);
                fixture.Layout(LayoutSettings.Defaults(), false); fixture.Render(1280, 820);
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "default-spacing-expanded.png"));
            }
            report("PASS defaults: separated quota groups spread across wider cards without shrinking the gauges");
        }

        private static void CheckAccountAlignmentAndFit(Action<string> report, string previewDirectory)
        {
            List<string> failures = new List<string>();
            using (Fixture fixture = new Fixture())
            {
                LayoutSettings settings = LayoutSettings.Defaults();
                fixture.Layout(settings, false); fixture.Render(1280, 820);
                foreach (string suffix in new[] { "PrimaryValue", "SecondaryValue", "ResetCreditsValue", "LifetimeValue" })
                {
                    FrameworkElement first = (FrameworkElement)fixture.Window.FindName("Account1" + suffix);
                    FrameworkElement second = (FrameworkElement)fixture.Window.FindName("Account2" + suffix);
                    if (!first.IsVisible || Math.Abs(first.TransformToAncestor(fixture.Root).Transform(new Point()).Y - second.TransformToAncestor(fixture.Root).Transform(new Point()).Y) > 1)
                        failures.Add("Missing 5-hour limit shifts the account row: " + suffix);
                }
                fixture.Accounts[0].LastSnapshot.UsageError = "fixture usage failure";
                fixture.Layout(settings, false); fixture.Render(1280, 820);
                foreach (string suffix in new[] { "PrimaryValue", "SecondaryValue", "ResetCreditsValue", "LifetimeValue", "CalendarTitle" })
                {
                    FrameworkElement first = (FrameworkElement)fixture.Window.FindName("Account1" + suffix);
                    FrameworkElement second = (FrameworkElement)fixture.Window.FindName("Account2" + suffix);
                    if (Math.Abs(first.TransformToAncestor(fixture.Root).Transform(new Point()).Y - second.TransformToAncestor(fixture.Root).Transform(new Point()).Y) > 1)
                        failures.Add("An account status message shifts the aligned rows: " + suffix);
                }
                fixture.Accounts[0].LastSnapshot.UsageError = null;
                foreach (bool compact in new[] { false, true })
                {
                    fixture.Layout(settings, compact);
                    foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                    {
                        fixture.FontScale(scale);
                        foreach (int[] size in compact ? new[] { new[] { 460, 780 }, new[] { 320, 480 } } : new[] { new[] { 1280, 820 }, new[] { 900, 620 } })
                        {
                            fixture.Render(size[0], size[1]);
                            CardLayoutPanel panel = Descendants<CardLayoutPanel>(fixture.Root).Single(item => item.IsVisible);
                            Rect viewport = panel.TransformToAncestor(fixture.Root).TransformBounds(new Rect(panel.RenderSize));
                            foreach (LayoutTile tile in panel.Tiles.Where(item => item.Card.IsVisible))
                            {
                                Rect bounds = tile.Card.TransformToAncestor(fixture.Root).TransformBounds(new Rect(tile.Card.RenderSize));
                                if (bounds.Left < viewport.Left - 1 || bounds.Right > viewport.Right + 1 || bounds.Top < viewport.Top - 1 || bounds.Bottom > Math.Min(viewport.Bottom, size[1] - (compact ? 32 : 42)) + 1)
                                    failures.Add("A dashboard card is outside the visible viewport: " + compact + ", " + size[0] + "x" + size[1] + ", " + scale);
                                foreach (TextBlock text in Descendants<TextBlock>(tile.Card).Where(item => item.IsVisible && item.ActualWidth > 0 && item.ActualHeight > 0))
                                {
                                    Rect textBounds = text.TransformToAncestor(fixture.Root).TransformBounds(new Rect(text.RenderSize));
                                    if (textBounds.Top < bounds.Top - 1 || textBounds.Bottom > bounds.Bottom + 1)
                                        failures.Add("A card hides overflowing text: " + text.Name + ", " + compact + ", " + size[0] + ", " + scale);
                                }
                            }
                            if (!compact)
                                foreach (string suffix in new[] { "PrimaryValue", "SecondaryValue", "ResetCreditsValue", "SubscriptionValue", "LifetimeValue", "CalendarTitle" })
                                {
                                    FrameworkElement first = (FrameworkElement)fixture.Window.FindName("Account1" + suffix);
                                    FrameworkElement second = (FrameworkElement)fixture.Window.FindName("Account2" + suffix);
                                    if (Math.Abs(first.TransformToAncestor(fixture.Root).Transform(new Point()).Y - second.TransformToAncestor(fixture.Root).Transform(new Point()).Y) > 1)
                                        failures.Add("Account rows lost alignment at font scale " + scale + ": " + suffix);
                                }
                            if (Descendants<ScrollViewer>(fixture.Root).Any(item => item.IsVisible && item.Content is CardLayoutPanel)) failures.Add("Dashboard still scrolls.");
                            if (previewDirectory != null && scale == 1.5) fixture.Capture(Path.Combine(previewDirectory, (compact ? "widget" : "expanded") + "-fit-" + size[0] + ".png"));
                        }
                    }
                }
                if (!(fixture.Window.FindName("Account1SubscriptionValue") is TextBlock) || !(fixture.Window.FindName("CompactAccount1SubscriptionValue") is TextBlock))
                    failures.Add("Subscription date is missing below the account credits.");
                Require(fixture.First.SubscriptionValue.Text.Contains("→ Plus") && fixture.Second.SubscriptionValue.Text.Contains("갱신") &&
                    fixture.First.SubscriptionValue.Text == fixture.First.CompactSubscriptionValue.Text, "Account subscription type/date does not follow the account across both views.");
                fixture.Page(1);
                Require(fixture.First.State.Number == 3 && fixture.First.SubscriptionValue.Text.Contains("조회 불가"), "Paging retained another account's subscription date.");
                fixture.Accounts[2].LastSnapshot.Subscription = new AccountSubscriptionInfo { Date = DateTime.Today.AddDays(2), Kind = "period", Error = "최근 24시간 이내 확인 · HTTP 403", CheckedAt = DateTime.Now };
                fixture.Page(1);
                Require(Convert.ToString(fixture.First.SubscriptionValue.ToolTip).Contains("HTTP 403") &&
                    Convert.ToString(fixture.First.CompactSubscriptionValue.ToolTip).Contains("24시간"), "A cached period hides the automatic billing lookup failure.");
                settings.Widget.UseSingleAccount(2, true); settings.Widget.Card("pc").Visible = false;
                fixture.Accounts[1].LastSnapshot.Primary.RemainingPercent = 100;
                fixture.Accounts[1].LastSnapshot.Secondary.RemainingPercent = 100;
                fixture.Layout(settings, true); fixture.FontScale(2); fixture.Render(460, 780);
                foreach (string name in new[] { "CompactAccount1PrimaryValue", "CompactAccount1SecondaryValue" })
                {
                    TextBlock value = (TextBlock)fixture.Window.FindName(name);
                    Geometry clip = LayoutInformation.GetLayoutClip(value);
                    Require(value.Text == "100%" && (clip == null || clip.Bounds.Width >= value.ActualWidth - 1), "The full 100% quota text is clipped at 200% fonts: " + name);
                    Canvas canvas = Ancestor<Canvas>(value);
                    Rect labelBounds = value.TransformToAncestor(canvas).TransformBounds(new Rect(value.RenderSize));
                    Require(labelBounds.Left >= 0 && labelBounds.Right <= canvas.ActualWidth && labelBounds.Top >= 0 && labelBounds.Bottom <= canvas.ActualHeight,
                        "A quota percentage leaves its circle at 200% fonts: " + name);
                }
                foreach (string period in new[] { "Primary", "Secondary" })
                {
                    TextBlock name = (TextBlock)fixture.Window.FindName("CompactAccount1" + period + "TimeName");
                    TextBlock value = (TextBlock)fixture.Window.FindName("CompactAccount1" + period + "TimeValue");
                    Rect nameBounds = name.TransformToAncestor(fixture.Root).TransformBounds(new Rect(name.RenderSize));
                    Rect valueBounds = value.TransformToAncestor(fixture.Root).TransformBounds(new Rect(value.RenderSize));
                    Require(nameBounds.Right + 1 < valueBounds.Left, "Quota name overlaps its countdown at 200% fonts: " + period);
                }
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "widget-quota-100-200.png"));
            }
            foreach (string failure in failures.Distinct()) report("FAIL " + failure);
            Require(failures.Count == 0, "Account row/viewport regressions: " + failures.Count);
            report("PASS missing-quota row alignment, account subscription line and complete dashboards without scrolling at 100-200% fonts");
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

        private static void VerifyContentLayout(Action<string> report, string previewDirectory)
        {
            using (Fixture fixture = new Fixture())
            {
                foreach (bool compact in new[] { false, true })
                {
                    LayoutSettings settings = LayoutSettings.Defaults(); fixture.Layout(settings, compact); fixture.Render(1280, 820);
                    DashboardLayoutView layout = (DashboardLayoutView)typeof(DashboardController).GetField("_layoutView", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(fixture.Controller);
                    LayoutTile tile = layout.Tiles(compact).First(t => t.Settings.Id == "account1");
                    Rect card = tile.Bounds;
                    settings.Mode(compact).Card("account1").ItemLayouts = CardContentLayout.Capture(tile);
                    LayoutItemSettings weekly = settings.Mode(compact).Card("account1").ItemLayouts.Single(i => i.Id == "weekly");
                    weekly.X = 0.5; weekly.Y = 0.3; weekly.Width = 0.4; weekly.Height = 0.2;
                    fixture.Layout(settings, compact); fixture.Render(1280, 820);
                    LayoutContentItem element = tile.ContentItems().Single(i => i.Id == "weekly");
                    LayoutItemSettings shown = CardContentLayout.Position(tile, element);
                    Require(shown.X >= 0.499 && shown.X + shown.Width <= 0.901 && shown.Y >= 0.299 && shown.Y + shown.Height <= 0.501 && tile.Bounds == card,
                        "custom item did not stay in its own bounds without resizing the card: " + shown.X + "," + shown.Y + "," + shown.Width + "," + shown.Height +
                        " compact=" + compact + " element=" + element.Element.RenderSize + " transform=" + element.Element.RenderTransform.Value + " surface=" + tile.Surface.RenderSize + " content=" + tile.Content.RenderSize + " card=" + tile.Card.RenderSize);
                    fixture.FontScale(2); fixture.Render(900, 620);
                    shown = CardContentLayout.Position(tile, element);
                    Require(shown.X >= 0 && shown.Y >= 0 && shown.X + shown.Width <= 1.001 && shown.Y + shown.Height <= 1.001, "custom item escaped a narrow card");
                    LayoutTile pc = layout.Tiles(compact).Single(t => t.Settings.Id == "pc");
                    settings.Mode(compact).Card("pc").ItemLayouts = CardContentLayout.Capture(pc);
                    LayoutItemSettings cpu = settings.Mode(compact).Card("pc").ItemLayouts.Single(i => i.Id == "cpu");
                    cpu.X = 0.1; cpu.Y = 0.55; cpu.Width = 0.35; cpu.Height = 0.25;
                    fixture.Layout(settings, compact); fixture.Render(900, 620);
                    shown = CardContentLayout.Position(pc, pc.ContentItems().Single(i => i.Id == "cpu"));
                    Require(shown.X >= 0.099 && shown.X + shown.Width <= 0.451 && shown.Y >= 0.549 && shown.Y + shown.Height <= 0.801,
                        "PC metric did not move and resize independently");
                    LayoutItemSettings header = settings.Mode(compact).Card("account1").ItemLayouts.Single(i => i.Id == "header");
                    header.X = 0.05; header.Y = 0.02; header.Width = 0.8; header.Height = 0.1;
                    fixture.Layout(settings, compact); fixture.Render(900, 620);
                    Button history = (Button)fixture.Window.FindName((compact ? "Compact" : "") + "Account1HistoryButton");
                    Point hitPoint = history.TransformToAncestor(fixture.Root).Transform(new Point(history.ActualWidth / 2, history.ActualHeight / 2));
                    DependencyObject hit = fixture.Root.InputHitTest(hitPoint) as DependencyObject;
                    bool buttonHit = false; string hitPath = "";
                    while (hit != null) { hitPath += hit.GetType().Name + " " + (hit is FrameworkElement ? ((FrameworkElement)hit).Name : "") + "/"; if (hit == history) { buttonHit = true; break; } hit = VisualTreeHelper.GetParent(hit); }
                    if (!buttonHit && previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "content-hit-failure.png"));
                    Require(buttonHit, "moving the header disconnected its visible button from hit testing: compact=" + compact + " point=" + hitPoint + " hit=" + hitPath);
                    fixture.Page(1); fixture.Render(900, 620);
                    Require(tile.ContentItems().All(i => i.Element.RenderTransform.Value.IsIdentity), "new account page inherited a previous account's item layout");
                    settings.Mode(compact).Card("account1").ItemLayouts.Clear(); fixture.Layout(settings, compact); fixture.Render(900, 620);
                    Require(tile.ContentItems().All(i => i.Element.RenderTransform.Value.IsIdentity), "reset did not restore automatic item layout");
                    fixture.FontScale(1.5);
                }
                LayoutSettings original = LayoutSettings.Defaults(), previewed = null, accepted = null;
                LayoutEditor editor = fixture.Editor(original, true, state => previewed = state, state => accepted = state);
                RenderEditor(editor, root => {
                    Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "항목 편집").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
                    Descendants<Button>(root).Single(button => Convert.ToString(button.Tag) == "item:weekly").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
                    LayoutContentAdorner adorner = Descendants<LayoutContentAdorner>(root).Single();
                    Thumb move = Descendants<Thumb>(adorner).Single(thumb => Convert.ToString(thumb.Tag) == "move:weekly");
                    LayoutTile tile = Descendants<CardLayoutPanel>(root).Where(panel => panel.IsVisible).SelectMany(panel => panel.Tiles).Single(t => t.Settings.Id == "account1");
                    LayoutItemSettings before = CardContentLayout.Position(tile, tile.ContentItems().Single(i => i.Id == "weekly"));
                    DragItem(root, move, 18, 0);
                    LayoutItemSettings moved = previewed.Widget.Card("account1").ItemLayouts.Single(item => item.Id == "weekly");
                    Require(moved.X > before.X && Math.Abs(moved.Width - before.Width) < 0.001 && original.Widget.Card("account1").ItemLayouts.Count == 0,
                        "drag did not move only the selected item in the draft");
                    Thumb resize = Descendants<Thumb>(adorner).Single(thumb => Convert.ToString(thumb.Tag) == "resize:weekly");
                    LayoutItemSettings afterMove = CardContentLayout.Position(tile, tile.ContentItems().Single(i => i.Id == "weekly"));
                    Require(Math.Abs(afterMove.Width - moved.Width) < 0.001 && Math.Abs(afterMove.Height - moved.Height) < 0.001,
                        "moving changed actual size: saved=" + moved.Width + "," + moved.Height + " actual=" + afterMove.Width + "," + afterMove.Height);
                    double previousWidth = moved.Width;
                    DragItem(root, resize, -10, -10);
                    LayoutItemSettings resized = previewed.Widget.Card("account1").ItemLayouts.Single(item => item.Id == "weekly");
                    Require(resized.Width < previousWidth && resized.Height < moved.Height && previewed.Expanded.Card("account1").ItemLayouts.Count == 0 &&
                        previewed.Widget.Card("account2").ItemLayouts.Count == 0, "resize changed unrelated card/mode or failed to shrink: before=" + moved.Width + "," + moved.Height + " after=" + resized.Width + "," + resized.Height);
                    Rect marker = move.TransformToAncestor(root).TransformBounds(new Rect(move.RenderSize));
                    Rect actual = tile.ContentItems().Single(i => i.Id == "weekly").Element.TransformToAncestor(root).TransformBounds(
                        new Rect(tile.ContentItems().Single(i => i.Id == "weekly").Element.RenderSize));
                    Require(Math.Abs(marker.Left - actual.Left) < 1 && Math.Abs(marker.Width - actual.Width) < 1, "item drag handle is detached from rendered content");
                    Require(!tile.Content.IsHitTestVisible, "item editor exposes live dashboard interactions");
                    DragItem(root, move, -10000, -10000);
                    LayoutItemSettings edge = CardContentLayout.Position(tile, tile.ContentItems().Single(i => i.Id == "weekly"));
                    Require(edge.X < 0.001 && edge.Y < 0.001, "item movement escaped the top-left card boundary");
                    DragItem(root, resize, 10000, 10000);
                    edge = CardContentLayout.Position(tile, tile.ContentItems().Single(i => i.Id == "weekly"));
                    Require(edge.X + edge.Width <= 1.001 && edge.Y + edge.Height <= 1.001, "resizing escaped the card boundary");
                    DragItem(root, resize, -10000, -10000);
                    edge = CardContentLayout.Position(tile, tile.ContentItems().Single(i => i.Id == "weekly"));
                    Require(edge.Width >= 0.024 && edge.Height >= 0.019, "resizing made an item disappear");
                    editor.EditItem(resized.Id, resized.X, resized.Y, resized.Width, resized.Height); root.UpdateLayout();
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "content-editor-widget.png"));
                Require(editor.TrySave() && accepted.Widget.Card("account1").ItemLayouts.Count > 0, "custom item settings were not saved");
                editor.Close();
                Require(fixture.First.CompactContainer.IsHitTestVisible, "closing item editor did not restore interaction");
                Require(((Button)fixture.Window.FindName("CompactAccount1HistoryButton")).IsHitTestVisible,
                    "closing the editor left a dashboard button disabled by its former parent");
                fixture.Layout(accepted, true); fixture.Render(460, 780);
                if (previewDirectory != null) fixture.Capture(Path.Combine(previewDirectory, "content-custom-widget.png"));
                LayoutSettings restored = null;
                LayoutEditor cancel = fixture.Editor(accepted, true, state => restored = state, state => { throw new InvalidOperationException("cancel saved"); });
                RenderEditor(cancel, root => {
                    cancel.SetItemEditing(true); root.UpdateLayout();
                    cancel.EditItem("weekly", 0.75, 0.65, 0.2, 0.25); root.UpdateLayout();
                    Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "카드 안쪽 모두 원래대로").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
                    Require(cancel.Draft.Widget.Card("account1").ItemLayouts.Count == 0, "card item reset did not clear custom positions");
                }, null, 780, 540);
                cancel.Close();
                Require(restored.Widget.Card("account1").ItemLayouts.Count == accepted.Widget.Card("account1").ItemLayouts.Count,
                    "cancel did not restore previously saved item positions");
                LayoutSettings devices = LayoutSettings.Defaults(); devices.Expanded.Card("pc").SetSection("gpu", false);
                LayoutEditor pcEditor = fixture.Editor(devices, false, state => { }, state => { });
                RenderEditor(pcEditor, root => {
                    pcEditor.SetItemEditing(true); root.UpdateLayout();
                    foreach (bool visible in new[] { false, true })
                    {
                        CheckBox weekly = Descendants<CheckBox>(root).Single(box => Convert.ToString(box.Content) == "주간 한도");
                        weekly.IsChecked = visible; weekly.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); root.UpdateLayout();
                        Require(Descendants<Button>(root).Any(button => Convert.ToString(button.Tag) == "item:weekly") &&
                            Descendants<CheckBox>(root).Single(box => Convert.ToString(box.Tag) == "item-visible:weekly").IsChecked == visible,
                            "hidden section must remain in the item list with its current visibility");
                    }
                    Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "PC 상태").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    pcEditor.SetItemEditing(true); root.UpdateLayout();
                    Thumb cpuHandle = Descendants<Thumb>(root).Single(thumb => Convert.ToString(thumb.Tag) == "move:cpu");
                    fixture.UpdatePc(true); root.UpdateLayout();
                    DragItem(root, cpuHandle, 4, 8);
                    Require(pcEditor.Draft.Expanded.Card("pc").ItemLayouts.Any(item => item.Id == "cpu"), "recreated PC metric could not be dragged");
                    CheckBox gpu = Descendants<CheckBox>(root).Single(box => Convert.ToString(box.Content) == "GPU");
                    gpu.IsChecked = true; gpu.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); root.UpdateLayout();
                    Require(Descendants<Button>(root).Any(button => Convert.ToString(button.Tag) == "item:gpu:1"), "a new device is missing from the item selection list");
                }, previewDirectory == null ? null : Path.Combine(previewDirectory, "content-editor-pc.png"));
                pcEditor.Close();
            }
            report("PASS content: custom positions and sizes fit card bounds, survive viewport/font changes and reset independently");
            report("PASS content: routed item move/resize, aligned handles, draft isolation, save, cancel and restored interaction");
        }

        private static void VerifySnapping(Action<string> report, string previewDirectory)
        {
            foreach (double zoom in new[] { 0.25, 0.5, 1.0, 2.0, 4.0 })
            {
                double screenWidth = 600 * zoom, screenHeight = 400 * zoom;
                foreach (double anchor in new[] { 0.0, 0.5, 1.0 })
                {
                    double? gx, gy;
                    Rect input = new Rect(0.5 - 0.3 * anchor + 5 / screenWidth, 0.13, 0.3, 0.2);
                    Rect output = LayoutSnap.Apply(input, new Rect[0], false, 6 / screenWidth, 6 / screenHeight, out gx, out gy);
                    Require(Math.Abs(output.X + output.Width * anchor - 0.5) < 0.000001 && gx == 0.5,
                        "item edge/center did not snap at five screen DIPs, zoom=" + zoom);
                    input.X = 0.5 - 0.3 * anchor + 7 / screenWidth;
                    output = LayoutSnap.Apply(input, new Rect[0], false, 6 / screenWidth, 6 / screenHeight, out gx, out gy);
                    Require(output.X == input.X && !gx.HasValue, "snap captured an item beyond six screen DIPs");
                }
                double? x, y;
                Rect sibling = new Rect(0.61, 0.61, 0.2, 0.2);
                Rect near = new Rect(0.21, 0.21, 0.395, 0.395);
                Rect resized = LayoutSnap.Apply(near, new[] { sibling }, true, 6 / screenWidth, 6 / screenHeight, out x, out y);
                if (0.005 * Math.Max(screenWidth, screenHeight) <= 6)
                    Require(Math.Abs(resized.Right - sibling.Left) < 0.000001 && Math.Abs(resized.Bottom - sibling.Top) < 0.000001,
                        "aspect-preserving resize missed a sibling boundary");
                Require(resized.X == near.X && resized.Y == near.Y && Math.Abs(resized.Width / resized.Height - 1) < 0.000001 &&
                    resized.Right <= 1 && resized.Bottom <= 1, "resize snap changed its anchor, ratio or card boundary");
            }
            using (Fixture fixture = new Fixture())
            {
                foreach (bool compact in new[] { false, true })
                {
                    LayoutSettings settings = LayoutSettings.Defaults();
                    settings.Mode(compact).UseSingleAccount(1, compact);
                    LayoutCardSettings card = settings.Mode(compact).Card("account1");
                    card.HiddenItems.AddRange(new[] { "header", "short", "countdown", "credits", "subscription", "stats", "calendar", "status" });
                    string original = settings.ToJson();
                    LayoutEditor editor = fixture.Editor(settings, compact, state => { }, state => { });
                    RenderEditor(editor, root => {
                        editor.SetItemEditing(true); root.UpdateLayout();
                        foreach (double font in new[] { 1.0, 2.0 })
                        {
                            fixture.FontScale(font); root.UpdateLayout();
                            foreach (double zoom in new[] { 0.25, 0.5, 1.0, 2.0, 4.0 })
                            {
                                editor.SetPreviewZoom(zoom); root.UpdateLayout();
                                editor.EditItem("weekly", 0.1, 0.15, 0.3, 0.3); root.UpdateLayout();
                                LayoutContentAdorner adorner = Descendants<LayoutContentAdorner>(root).Single();
                                LayoutTile tile = Descendants<CardLayoutPanel>(root).Where(panel => panel.IsVisible)
                                    .SelectMany(panel => panel.Tiles).Single(t => t.Settings.Id == "account1");
                                LayoutContentItem item = tile.ContentItems().Single(value => value.Id == "weekly");
                                LayoutItemSettings before = CardContentLayout.Position(tile, item);
                                Rect inner = CardContentLayout.InnerBounds(tile);
                                GeneralTransform transform = tile.Card.TransformToAncestor(root);
                                Point origin = transform.Transform(new Point());
                                double sx = transform.Transform(new Point(1, 0)).X - origin.X;
                                double sy = transform.Transform(new Point(0, 1)).Y - origin.Y;
                                // Keep the center closer than either edge even for a thin row at 25% zoom.
                                double nearCenter = Math.Min(3, Math.Min(before.Width * inner.Width * sx, before.Height * inner.Height * sy) / 8);
                                Thumb move = Descendants<Thumb>(adorner).Single(handle => Convert.ToString(handle.Tag) == "move:weekly");
                                DragItem(root, move, (0.5 - before.X - before.Width / 2) * inner.Width * sx + nearCenter,
                                    (0.5 - before.Y - before.Height / 2) * inner.Height * sy + nearCenter,
                                    delegate {
                                        Require(Descendants<System.Windows.Shapes.Line>(adorner).Count(line => Convert.ToString(line.Tag) == "layout-snap-guide") == 2,
                                            "snapped item has no edge/center guides");
                                    });
                                LayoutItemSettings after = CardContentLayout.Position(tile, item);
                                LayoutItemSettings stored = editor.Draft.Mode(compact).Card("account1").ItemLayouts.Single(value => value.Id == "weekly");
                                // Saved coordinates are exact; WPF's rendered layout can round by a device pixel.
                                Require(Math.Abs(stored.X + stored.Width / 2 - 0.5) < 0.00001 &&
                                    Math.Abs(stored.Y + stored.Height / 2 - 0.5) < 0.00001 &&
                                    Math.Abs(after.X + after.Width / 2 - 0.5) * inner.Width * sx < 1 &&
                                    Math.Abs(after.Y + after.Height / 2 - 0.5) * inner.Height * sy < 1, "routed drag missed the card center: compact=" + compact +
                                    " zoom=" + zoom + " font=" + font + " x=" + after.X + " y=" + after.Y + " w=" + after.Width + " h=" + after.Height +
                                    " stored=" + stored.X + "," + stored.Y + "," + stored.Width + "," + stored.Height +
                                    " inner=" + CardContentLayout.InnerBounds(tile) + " surface=" + tile.Surface.RenderSize);
                                Require(!Descendants<System.Windows.Shapes.Line>(adorner).Any(), "release left stale alignment guides");
                                adorner.ReadModifiers = () => ModifierKeys.Alt;
                                DragItem(root, move, 3, 0, delegate { Require(!Descendants<System.Windows.Shapes.Line>(adorner).Any(), "Alt did not hide snap guides"); }, true);
                                LayoutItemSettings free = CardContentLayout.Position(tile, item);
                                Require(Math.Abs((free.X - after.X) * inner.Width * sx - 3) < 1, "Alt did not allow unsnapped movement");
                                adorner.ReadModifiers = () => ModifierKeys.None;
                                editor.EditItem("weekly", 0.1, 0.1, 0.3, 0.3); root.UpdateLayout();
                                before = CardContentLayout.Position(tile, item);
                                double fx = (0.5 - before.X) / before.Width, fy = (0.5 - before.Y) / before.Height;
                                double factor = Math.Min(fx, fy);
                                Thumb resize = Descendants<Thumb>(adorner).Single(handle => Convert.ToString(handle.Tag) == "resize:weekly");
                                DragItem(root, resize, before.Width * (factor - 1) * inner.Width * sx,
                                    before.Height * (factor - 1) * inner.Height * sy,
                                    delegate { Require(Descendants<System.Windows.Shapes.Line>(adorner).Any(), "resize missed its alignment guide"); }, true);
                                stored = editor.Draft.Mode(compact).Card("account1").ItemLayouts.Single(value => value.Id == "weekly");
                                Require(Math.Abs((fx <= fy ? stored.X + stored.Width : stored.Y + stored.Height) - 0.5) < 0.00001 &&
                                    Math.Abs(stored.Width / stored.Height - before.Width / before.Height) < 0.00001,
                                    "routed resize lost center alignment or aspect ratio at zoom=" + zoom);
                                Require(!Descendants<System.Windows.Shapes.Line>(adorner).Any(), "cancelled resize left stale guides");
                            }
                        }
                        if (previewDirectory != null) Capture(root, 1220, 860, Path.Combine(previewDirectory, compact ? "snap-widget.png" : "snap-expanded.png"));
                    }, null);
                    editor.Close();
                    Require(settings.ToJson() == original, "snapping or cancelling rewrote the saved layout");
                }
            }
            report("PASS magnetic alignment: item/card edges and centers, aspect resize, six screen DIPs at 25-400% zoom, 100-200% text, Alt, guide cleanup and cancel");
        }

        private static void VerifyUpgradeLayout(Action<string> report)
        {
            // Legacy LayoutV1 values deliberately sit off the new magnetic lines.
            LayoutSettings saved = LayoutSettings.Parse("{\"Version\":1,\"Expanded\":{\"Columns\":2,\"Cards\":[{\"Id\":\"pc\",\"Visible\":true,\"Span\":2,\"Size\":2,\"Sections\":[\"cpu\",\"ram\"],\"ItemLayouts\":[{\"Id\":\"cpu\",\"X\":0.173,\"Y\":0.267,\"Width\":0.319,\"Height\":0.283}],\"HiddenItems\":[\"network\"]}]},\"Widget\":{\"Columns\":1,\"Cards\":[{\"Id\":\"account3\",\"Visible\":true,\"Span\":1,\"Size\":0,\"Sections\":[\"weekly\"],\"ItemLayouts\":[{\"Id\":\"weekly\",\"X\":0.123,\"Y\":0.237,\"Width\":0.333,\"Height\":0.417}],\"HiddenItems\":[\"subscription\"]},{\"Id\":\"pc\",\"Visible\":true,\"Span\":1,\"Size\":1,\"Sections\":[\"cpu\",\"ram\"],\"ItemLayouts\":[{\"Id\":\"cpu\",\"X\":0.173,\"Y\":0.267,\"Width\":0.319,\"Height\":0.283}],\"HiddenItems\":[\"network\"]}]}}");
            string original = saved.ToJson();
            using (Fixture fixture = new Fixture())
            {
                foreach (bool compact in new[] { false, true })
                {
                    fixture.Layout(saved, compact); fixture.FontScale(2); fixture.Render(320, 480);
                    LayoutSettings accepted = null;
                    LayoutEditor editor = fixture.Editor(saved, compact, state => { }, state => accepted = state);
                    RenderEditor(editor, root => { editor.SetItemEditing(true); editor.SetPreviewZoom(4); root.UpdateLayout(); }, null);
                    Require(editor.TrySave(), "unchanged legacy layout could not be saved"); editor.Close();
                    Require(accepted.ToJson() == original && saved.ToJson() == original,
                        "opening/rendering/saving after upgrade changed legacy coordinates, order, dimensions or hidden items");
                }
                using (var renderer = new WindowsWidgetRenderer())
                    renderer.Render(fixture.Accounts, saved, new SystemSnapshot(), 2, DateTime.UtcNow);
            }
            Require(saved.Copy().ToJson() == original, "Windows widget rendering or reload changed the legacy layout");
            report("PASS upgrade preservation: LayoutV1 off-grid positions, sizes, order, sections and hidden items survive render, editor open/save, widget render and reload unchanged");
        }

        private static void VerifyItemVisibility(Action<string> report)
        {
            using (Fixture fixture = new Fixture())
            {
                foreach (bool compact in new[] { false, true })
                {
                    LayoutSettings settings = LayoutSettings.Defaults();
                    settings.Mode(compact).Card("account1").SetItemVisible("header", false);
                    settings.Mode(compact).Card("account1").SetItemVisible("weekly", false);
                    settings.Mode(compact).Card("pc").SetItemVisible("cpu", false);
                    settings = settings.Copy();
                    fixture.Layout(settings, compact); fixture.Render(900, 620);
                    DashboardLayoutView layout = (DashboardLayoutView)typeof(DashboardController).GetField("_layoutView", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(fixture.Controller);
                    LayoutTile tile = layout.Tiles(compact).Single(value => value.Settings.Id == "account1");
                    Require(tile.ContentItems().Where(item => item.Id == "header" || item.Id == "weekly").All(item => !item.Element.IsVisible),
                        "hidden header or gauge was still rendered");
                    Require(!layout.Tiles(compact).Single(value => value.Settings.Id == "pc").ContentItems().Single(item => item.Id == "cpu").Element.IsVisible,
                        "hidden PC metric was still rendered");
                    if (compact)
                        Require(tile.ContentItems().Single(item => item.Id == "countdown").Element.IsVisible, "hiding a ring also hid its countdown");
                    fixture.Page(1); fixture.Render(900, 620);
                    Require(tile.ContentItems().Single(item => item.Id == "header").Element.IsVisible, "hidden item leaked into another account");
                    settings.Mode(compact).Card("account1").HiddenItems.Clear();
                    fixture.Layout(settings, compact); fixture.Render(900, 620);
                    Require(tile.ContentItems().Single(item => item.Id == "weekly").Element.IsVisible, "restored item stayed hidden");
                    Require(settings.Mode(!compact).Card("pc").ShowsItem("cpu"), "hiding changed the other mode");
                }
                LayoutSettings original = LayoutSettings.Defaults(), saved = null, restored = null;
                LayoutEditor editor = fixture.Editor(original, true, state => { }, state => saved = state);
                RenderEditor(editor, root => {
                    editor.SetItemEditing(true); root.UpdateLayout();
                    CheckBox box = Descendants<CheckBox>(root).Single(value => Convert.ToString(value.Tag) == "item-visible:subscription");
                    box.IsChecked = false; box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); root.UpdateLayout();
                    Require(!editor.Draft.Widget.Card("account1").ShowsItem("subscription") &&
                        original.Widget.Card("account1").ShowsItem("subscription"), "checkbox changed the saved settings instead of the draft");
                    Require(Descendants<Button>(root).Any(button => Convert.ToString(button.Tag) == "item:subscription"),
                        "hidden item cannot be selected for restoration");
                }, null);
                Require(editor.TrySave(), "hidden item settings were not saved"); editor.Close();
                LayoutEditor cancel = fixture.Editor(saved, true, state => restored = state, state => { throw new InvalidOperationException("cancel saved"); });
                RenderEditor(cancel, root => { cancel.SetItemVisible("subscription", true); root.UpdateLayout(); }, null);
                cancel.Close();
                Require(!restored.Widget.Card("account1").ShowsItem("subscription"), "cancel did not restore hidden items");
            }
            report("PASS item visibility: header/gauge/subscription/PC hide and restore, saved draft isolation, cancel, per-account/mode independence");
        }

        private static void DragItem(Grid root, Thumb handle, double dx, double dy, Action during = null, bool cancelled = false)
        {
            Point start = handle.TransformToAncestor(root).Transform(new Point());
            handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            foreach (double part in new[] { 0.25, 0.5, 1.0, 1.0 })
            {
                Point pointer = root.TransformToDescendant(handle).Transform(new Point(start.X + dx * part, start.Y + dy * part));
                handle.RaiseEvent(new DragDeltaEventArgs(pointer.X, pointer.Y) { RoutedEvent = Thumb.DragDeltaEvent }); root.UpdateLayout();
            }
            if (during != null) during();
            handle.RaiseEvent(new DragCompletedEventArgs(dx, dy, cancelled) { RoutedEvent = Thumb.DragCompletedEvent });
        }

        internal static void VerifyHistoryCards(UsageHistoryStore store, string key, string evidenceRoot, Action<string> report)
        {
            using (Fixture fixture = new Fixture())
            {
                fixture.Set("_usageHistory", store);
                foreach (bool compact in new[] { false, true })
                {
                    fixture.Accounts[0].LastSnapshot.HistoryKey = key;
                    fixture.FontScale(1.5); fixture.Layout(LayoutSettings.Defaults(), compact);
                    int width = compact ? 680 : 1280, height = compact ? 650 : 820;
                    fixture.Render(width, height);
                    DashboardLayoutView layout = (DashboardLayoutView)typeof(DashboardController).GetField("_layoutView", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(fixture.Controller);
                    var tiles = layout.Tiles(compact).ToList();
                    Rect cardBounds = tiles[0].Bounds, neighborBounds = tiles[1].Bounds;
                    Button entry = (Button)fixture.Window.FindName((compact ? "Compact" : "") + "Account1HistoryButton");
                    entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); fixture.Render(width, height);
                    Require(tiles[0].History != null && tiles[0].Content.Visibility == Visibility.Collapsed &&
                        tiles[1].History == null && cardBounds == tiles[0].Bounds && neighborBounds == tiles[1].Bounds,
                        "history did not replace only the selected card while preserving card bounds");
                    UsageHistoryView history = tiles[0].History;
                    Require(history.ActualWidth > 280 && history.ActualHeight > 150 && history.Parent == tiles[0].Surface,
                        "history is not hosted inside the card: " + history.ActualWidth + " x " + history.ActualHeight + ", compact=" + compact + ", parent=" + history.Parent);
                    fixture.Call("BindAccountPage"); fixture.Render(width, height);
                    Require(tiles[0].History == history, "a routine refresh removed the card history");
                    fixture.Capture(Path.Combine(evidenceRoot, compact ? "dashboard-history-widget.png" : "dashboard-history-expanded.png"));
                    layout.ShowHistory(1, compact, new UsageHistoryView(store, key, delegate { layout.HideHistory(1, compact); }));
                    LayoutEditor editor = fixture.Editor(LayoutSettings.Defaults(), compact, state => { }, state => { });
                    RenderEditor(editor, root => {
                        editor.SetItemEditing(true); root.UpdateLayout();
                        Require(tiles[0].History == history && history.Visibility == Visibility.Collapsed && tiles[0].Content.IsVisible,
                            "item editing discarded the open history instead of temporarily hiding it");
                        Descendants<Button>(root).Single(button => Convert.ToString(button.Content) == "계정 2").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
                        Require(Descendants<Button>(root).Any(button => Convert.ToString(button.Tag) == "item:weekly"), "another card's history prevented item selection");
                    }, null);
                    editor.Close(); fixture.Render(width, height);
                    Require(tiles[0].History == history && history.IsVisible && tiles[0].Content.Visibility == Visibility.Collapsed,
                        "closing the editor did not restore the previous history view");
                    fixture.FontScale(2); fixture.Render(width, height);
                    Button back = (Button)typeof(UsageHistoryView).GetField("_backButton", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(history);
                    Point center = back.TranslatePoint(new Point(back.ActualWidth / 2, back.ActualHeight / 2), fixture.Root);
                    Require(fixture.Root.InputHitTest(center) != null && back.IsVisible, "card back button disappeared at large font size");
                    back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); fixture.Render(width, height);
                    Require(tiles[0].History == null && tiles[0].Content.Visibility == Visibility.Visible, "back did not restore the original card");
                    entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    fixture.Accounts[0].LastSnapshot.HistoryKey = new String('9', 64); fixture.Call("BindAccountPage");
                    Require(tiles[0].History == null, "another authenticated account inherited the previous account's history");
                    entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); fixture.Page(1);
                    Require(tiles[0].History == null, "account paging kept the old slot's history");
                }
            }
            report("PASS history: expanded and widget cards switch inline, preserve neighbors, return and discard stale account bindings");
        }

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
                    if (n <= 2) snapshot.Subscription = new AccountSubscriptionInfo { Date = DateTime.Today.AddDays(n == 1 ? 25 : 9), Kind = n == 1 ? "change" : "renewal", NextPlan = n == 1 ? "plus" : null, CheckedAt = DateTime.Now };
                    Accounts.Add(new AccountState { Number = n, Label = "계정 " + n, LastSnapshot = snapshot });
                }
                Set("_layouts", LayoutSettings.Defaults());
                Set("_layoutView", new DashboardLayoutView(Window));
                Root = (Grid)Window.Content;
                Root.Resources.MergedDictionaries.Add(Window.Resources);
                Root.SetValue(Control.FontFamilyProperty, Window.FontFamily); Root.SetValue(Control.ForegroundProperty, Window.Foreground);
                Call("CaptureFontTargets", Root);
                FontScale(1.5);
                UpdatePc(false);
                foreach (string prefix in new[] { "Cpu", "Gpu", "Memory", "Disk" })
                {
                    ((TextBlock)Window.FindName("Compact" + prefix + "Value")).Text = "28%";
                    Border bar = (Border)Window.FindName("Compact" + prefix + "Bar");
                    bar.Height = ((FrameworkElement)bar.Parent).Height * 0.28;
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
            internal void UpdatePc(bool extraGpu)
            {
                SystemSnapshot system = new SystemSnapshot { CpuPercent = 28, MemoryPercent = 63, MemoryUsedGb = 20, MemoryTotalGb = 32 };
                system.Gpus.Add(new GpuSnapshot { Key = "gpu:0", Name = "예시 GPU", Percent = 41 });
                if (extraGpu) system.Gpus.Add(new GpuSnapshot { Index = 1, Key = "gpu:1", Name = "추가 GPU", Percent = 12 });
                system.Disks.Add(new DiskSnapshot { Key = "disk:0", Name = "디스크 0", Percent = 12, Detail = "예시 SSD" });
                system.Networks.Add(new NetworkSnapshot { Key = "network:0", Name = "네트워크", Connected = true, Percent = 4 });
                object items = typeof(DashboardController).GetMethod("BuildPerformanceItems", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { system });
                Call("UpdatePerformanceCards", items);
            }
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
                if (Window.Content == Root) { Window.Content = null; _surface.RootVisual = Root; }
                _width = width; _height = height;
                Root.Measure(new Size(width, height)); Root.Arrange(new Rect(0, 0, width, height)); Root.UpdateLayout();
            }
            internal void Capture(string path) { LayoutRegressionTests.Capture(Root, _width, _height, path); }
            internal void Set(string name, object value) { typeof(DashboardController).GetField(name, Flags).SetValue(Controller, value); }
            internal object Call(string name, params object[] args) { return typeof(DashboardController).GetMethod(name, Flags).Invoke(Controller, args); }
            public void Dispose() { _surface.Dispose(); }
        }
    }
}
