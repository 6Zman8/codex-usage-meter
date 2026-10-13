using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexUsageMeter
{
    // The regression runner supplies its existing isolated Fixture and RenderEditor.
    internal static class LayoutPreviewParityTests
    {
        internal static void Run(Action<string> report, Window window, Grid root,
            DashboardLayoutView layout, Action<LayoutSettings, bool> apply,
            Action<int, int> render, Action<double> fontScale,
            Action<LayoutEditor, Action<Grid>> renderEditor,
            Func<LayoutSettings, LayoutEditor> openEditor)
        {
            object originalFontSize = root.ReadLocalValue(Control.FontSizeProperty);
            object originalFormatting = root.ReadLocalValue(TextOptions.TextFormattingModeProperty);
            try
            {
            foreach (int width in new[] { 900, 460, 320 })
            foreach (int height in new[] { 780, 480, 320 })
            foreach (double font in new[] { 1.0, 2.0 })
            foreach (int placement in new[] { 0, 1, 2 })
            {
                LayoutSettings settings = LayoutSettings.Defaults();
                settings.Widget.UseSingleAccount(1, true);
                // Fixture.Render detaches the dashboard from its Window. Supply
                // the actual window inheritance only while rendering that fixture.
                root.SetValue(Control.FontSizeProperty, window.FontSize);
                root.SetValue(TextOptions.TextFormattingModeProperty, TextOptions.GetTextFormattingMode(window));
                fontScale(font); apply(settings, true); render(width, height);
                LayoutTile tile = layout.Tiles(true).First(t => t.Settings.Id == "account1");
                LayoutTile pc = layout.Tiles(true).First(t => t.Settings.Id == "pc");
                if (placement != 0)
                {
                    settings.Widget.Card("account1").ItemLayouts = CardContentLayout.Capture(tile);
                    settings.Widget.Card("pc").ItemLayouts = CardContentLayout.Capture(pc);
                    if (placement == 2)
                    {
                        settings.Widget.Card("account1").ItemLayouts.RemoveAll(i => i.Id == "header");
                        settings.Widget.Card("pc").ItemLayouts.RemoveAll(i => i.Id == "header");
                    }
                    apply(settings, true); render(width, height);
                }
                Dictionary<string, Rect> before = Bounds(tile, root);
                Dictionary<string, Rect> beforePc = Bounds(pc, root);
                string originalSettings = settings.ToJson();
                root.ClearValue(Control.FontSizeProperty);
                root.ClearValue(TextOptions.TextFormattingModeProperty);
                LayoutSettings saved = null;
                LayoutEditor editor = openEditor(settings);
                try
                {
                    editor.SetPreviewZoom(1);
                    renderEditor(editor, editorRoot => {
                        if ((double)root.GetValue(Control.FontSizeProperty) != window.FontSize ||
                            TextOptions.GetTextFormattingMode(root) != TextOptions.GetTextFormattingMode(window))
                            throw new InvalidOperationException("Preview replaced inherited dashboard typography.");
                        Rect viewport = root.TransformToAncestor(editorRoot).TransformBounds(new Rect(root.RenderSize));
                        if (Math.Abs(viewport.Width - width) > .05 || Math.Abs(viewport.Height - height) > .05)
                            throw new InvalidOperationException("Original-mode 100% preview changed the effective viewport.");
                        Same(before, Bounds(tile, root), "preview " + width + "/" + height + "/" + font + "/" + placement);
                        Same(beforePc, Bounds(pc, root), "PC preview " + width + "/" + height + "/" + font + "/" + placement);
                        if (!editor.TrySave()) throw new InvalidOperationException("Parity fixture save failed.");
                        saved = LayoutSettings.Parse(editor.Draft.ToJson());
                        if (saved.ToJson() != originalSettings)
                            throw new InvalidOperationException("Opening/saving preview changed the saved layout.");
                    });
                }
                finally { editor.RestoreDashboard(); }
                if (root.ReadLocalValue(Control.FontSizeProperty) != DependencyProperty.UnsetValue ||
                    root.ReadLocalValue(TextOptions.TextFormattingModeProperty) != DependencyProperty.UnsetValue)
                    throw new InvalidOperationException("Restoring dashboard left preview typography overrides.");
                root.SetValue(Control.FontSizeProperty, window.FontSize);
                root.SetValue(TextOptions.TextFormattingModeProperty, TextOptions.GetTextFormattingMode(window));
                apply(saved, true); render(width, height);
                Same(before, Bounds(tile, root), "restored " + width + "/" + height + "/" + font + "/" + placement);
                Same(beforePc, Bounds(pc, root), "PC restored " + width + "/" + height + "/" + font + "/" + placement);
            }
            report("PASS: compact actual-size card/header parity including emergency outer scroll before, during 100% preview, and after save/restore (320/460/900px, 320/480/780 height, 100/200% font, automatic/full/partial placement)");
            }
            finally
            {
                RestoreLocal(root, Control.FontSizeProperty, originalFontSize);
                RestoreLocal(root, TextOptions.TextFormattingModeProperty, originalFormatting);
            }
        }

        private static void RestoreLocal(DependencyObject target, DependencyProperty property, object value)
        {
            if (value == DependencyProperty.UnsetValue) target.ClearValue(property);
            else target.SetValue(property, value);
        }

        private static Dictionary<string, Rect> Bounds(LayoutTile tile, Grid root)
        {
            var result = new Dictionary<string, Rect>();
            Rect card = tile.Card.TransformToAncestor(root).TransformBounds(new Rect(tile.Card.RenderSize));
            result.Add("card", new Rect(0, 0, card.Width, card.Height));
            foreach (LayoutContentItem item in tile.ContentItems())
            {
                if (!item.Element.IsVisible) continue;
                Rect bounds = item.Element.TransformToAncestor(root).TransformBounds(new Rect(item.Element.RenderSize));
                bounds.Offset(-card.X, -card.Y);
                result.Add(item.Id, bounds);
            }
            foreach (string name in new[] { "CompactAccount1BadgeText", "CompactAccount1TitleText",
                "CompactAccount1Identity", "CompactAccount1HistoryButton", "CompactAccount1CodexLoginButton", "CompactPcHistoryButton" })
            {
                FrameworkElement element = (FrameworkElement)root.FindName(name);
                if (element == null || !element.IsVisible || !tile.Card.IsAncestorOf(element)) continue;
                Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                bounds.Offset(-card.X, -card.Y); result.Add(name, bounds);
            }
            return result;
        }

        private static void Same(Dictionary<string, Rect> expected, Dictionary<string, Rect> actual, string stage)
        {
            if (!expected.Keys.OrderBy(k => k).SequenceEqual(actual.Keys.OrderBy(k => k)))
                throw new InvalidOperationException(stage + ": visible content changed.");
            foreach (string key in expected.Keys)
            {
                Rect a = expected[key], b = actual[key];
                if (Math.Abs(a.X - b.X) > .05 || Math.Abs(a.Y - b.Y) > .05 ||
                    Math.Abs(a.Width - b.Width) > .05 || Math.Abs(a.Height - b.Height) > .05)
                    throw new InvalidOperationException(stage + ": " + key + " bounds differ: " + a + " / " + b);
            }
        }
    }
}
