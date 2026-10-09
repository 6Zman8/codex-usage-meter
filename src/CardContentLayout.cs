using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal sealed class LayoutContentItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
        internal FrameworkElement Element;
    }

    internal static class CardContentLayout
    {
        internal static void RestoreVisibility(LayoutTile tile)
        {
            foreach (var entry in tile.SuppressedItems) entry.Key.Visibility = entry.Value;
            tile.SuppressedItems.Clear();
        }

        internal static void ApplyVisibility(LayoutTile tile)
        {
            foreach (LayoutContentItem item in tile.ContentItems())
                if (!tile.Settings.ShowsItem(item.Id))
                {
                    tile.SuppressedItems[item.Element] = item.Element.Visibility;
                    item.Element.Visibility = Visibility.Collapsed;
                }
        }

        internal static List<LayoutContentItem> VisibleItems(LayoutTile tile)
        {
            return tile.ContentItems().Where(item => item.Element.IsVisible && item.Element.ActualWidth > 1 && item.Element.ActualHeight > 1).ToList();
        }

        internal static Rect InnerBounds(LayoutTile tile)
        {
            Thickness padding = tile.Card.Padding, border = tile.Card.BorderThickness;
            double x = padding.Left + border.Left, y = padding.Top + border.Top;
            return new Rect(x, y, Math.Max(1, tile.Card.ActualWidth - x - padding.Right - border.Right),
                Math.Max(1, tile.Card.ActualHeight - y - padding.Bottom - border.Bottom));
        }

        internal static LayoutItemSettings Position(LayoutTile tile, LayoutContentItem item)
        {
            Rect inner = InnerBounds(tile);
            Rect bounds = item.Element.TransformToAncestor(tile.Card).TransformBounds(new Rect(item.Element.RenderSize));
            var position = new LayoutItemSettings { Id = item.Id, X = (bounds.X - inner.X) / inner.Width, Y = (bounds.Y - inner.Y) / inner.Height,
                Width = bounds.Width / inner.Width, Height = bounds.Height / inner.Height };
            position.Normalize(); return position;
        }

        internal static List<LayoutItemSettings> Capture(LayoutTile tile)
        {
            return VisibleItems(tile).Select(item => Position(tile, item)).ToList();
        }

        internal static void Apply(LayoutTile tile)
        {
            if (tile.ContentItems == null || tile.ShowingHistory || tile.Content.ActualWidth <= 0 || tile.Content.ActualHeight <= 0) return;
            bool custom = tile.Settings != null && tile.Settings.ItemLayouts.Count > 0;
            foreach (LayoutContentItem item in tile.ContentItems())
            {
                FrameworkElement element = item.Element;
                if (!custom)
                {
                    if (!element.RenderTransform.Value.IsIdentity) element.RenderTransform = Transform.Identity;
                    continue;
                }
                if (!element.IsVisible || element.ActualWidth <= 1 || element.ActualHeight <= 1) continue;
                GeneralTransform transform = element.TransformToAncestor(tile.Surface);
                Point origin = transform.Transform(new Point()), xAxis = transform.Transform(new Point(1, 0)), yAxis = transform.Transform(new Point(0, 1));
                Matrix basis = new Matrix(xAxis.X - origin.X, xAxis.Y - origin.Y, yAxis.X - origin.X, yAxis.Y - origin.Y, origin.X, origin.Y);
                Matrix inverse = element.RenderTransform.Value;
                if (!inverse.HasInverse) continue;
                inverse.Invert(); inverse.Append(basis); basis = inverse;
                if (basis.M11 <= 0 || basis.M22 <= 0) continue;
                Rect natural = new Rect(basis.OffsetX, basis.OffsetY, element.ActualWidth * basis.M11, element.ActualHeight * basis.M22);
                LayoutItemSettings saved = tile.Settings.ItemLayouts.FirstOrDefault(value => value.Id == item.Id);
                Rect target;
                if (saved != null)
                    target = new Rect(saved.X * tile.Surface.ActualWidth, saved.Y * tile.Surface.ActualHeight,
                        saved.Width * tile.Surface.ActualWidth, saved.Height * tile.Surface.ActualHeight);
                else
                {
                    // New or restored items keep the original automatic layout inside the card.
                    double fit = Math.Min(1, Math.Min(tile.Surface.ActualWidth / tile.Content.ActualWidth, tile.Surface.ActualHeight / tile.Content.ActualHeight));
                    target = new Rect((tile.Surface.ActualWidth - tile.Content.ActualWidth * fit) / 2 + natural.X * fit,
                        (tile.Surface.ActualHeight - tile.Content.ActualHeight * fit) / 2 + natural.Y * fit, natural.Width * fit, natural.Height * fit);
                }
                double scale = Math.Min(target.Width / natural.Width, target.Height / natural.Height);
                double left = target.X + (target.Width - natural.Width * scale) / 2, top = target.Y + (target.Height - natural.Height * scale) / 2;
                Matrix next = new Matrix(scale, 0, 0, scale, (left - natural.X) / basis.M11, (top - natural.Y) / basis.M22);
                Matrix current = element.RenderTransform.Value;
                if (Math.Abs(next.M11 - current.M11) > 0.00001 || Math.Abs(next.OffsetX - current.OffsetX) > 0.0001 || Math.Abs(next.OffsetY - current.OffsetY) > 0.0001)
                    element.RenderTransform = new MatrixTransform(next);
            }
        }
    }

    internal sealed class LayoutContentAdorner : Adorner
    {
        private readonly LayoutTile _tile;
        private readonly Visual _dragSurface;
        private readonly Canvas _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        private readonly Dictionary<string, Grid> _handles = new Dictionary<string, Grid>();
        private readonly Action<string> _select;
        private readonly Action<string, double, double, double, double> _change;
        private string _signature;
        private Rect _lastBounds = Rect.Empty;

        internal LayoutContentAdorner(LayoutTile tile, Visual dragSurface, Action<string> select,
            Action<string, double, double, double, double> change) : base(tile.Card)
        {
            _tile = tile; _dragSurface = dragSurface; _select = select; _change = change;
            AddVisualChild(_canvas);
        }

        internal void Refresh(string selected)
        {
            var items = CardContentLayout.VisibleItems(_tile);
            string signature = String.Join("|", items.Select(item => item.Id));
            if (_signature != signature)
            {
                _signature = signature; _canvas.Children.Clear(); _handles.Clear();
                foreach (LayoutContentItem item in items)
                {
                    Grid group = new Grid { Tag = item.Id };
                    Thumb move = Handle(item, false), resize = Handle(item, true);
                    group.Children.Add(move); group.Children.Add(resize); _canvas.Children.Add(group); _handles.Add(item.Id, group);
                }
            }
            foreach (LayoutContentItem item in items)
            {
                Grid group = _handles[item.Id];
                Rect bounds = item.Element.TransformToAncestor(_tile.Card).TransformBounds(new Rect(item.Element.RenderSize));
                Canvas.SetLeft(group, bounds.X); Canvas.SetTop(group, bounds.Y); group.Width = bounds.Width; group.Height = bounds.Height;
                bool active = item.Id == selected; Panel.SetZIndex(group, active ? 10 : 0);
                ((Thumb)group.Children[0]).BorderBrush = LayoutEditor.Brush(active ? "#72D4B5" : "#717A88");
                group.Children[1].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
            AdornerLayer layer = VisualTreeHelper.GetParent(this) as AdornerLayer;
            Visual parent = layer == null ? null : VisualTreeHelper.GetParent(layer) as Visual;
            if (parent != null)
            {
                Rect bounds = _tile.Card.TransformToAncestor(parent).TransformBounds(new Rect(_tile.Card.RenderSize));
                if (bounds != _lastBounds) { _lastBounds = bounds; layer.Update(_tile.Card); }
            }
        }

        private Thumb Handle(LayoutContentItem item, bool resize)
        {
            Thumb thumb = new Thumb { Tag = (resize ? "resize:" : "move:") + item.Id, Cursor = resize ? Cursors.SizeNWSE : Cursors.SizeAll,
                ToolTip = item.Label + (resize ? " · 끌어서 크기 조절" : " · 끌어서 위치 이동"), BorderBrush = LayoutEditor.Brush("#717A88") };
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, resize ? LayoutEditor.Brush("#315D4E") : Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(resize ? 0 : 1));
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            FrameworkElementFactory label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(TextBlock.TextProperty, resize ? "◢" : item.Label); label.SetValue(TextBlock.FontSizeProperty, resize ? 16.0 : 10.0);
            label.SetValue(TextBlock.ForegroundProperty, Brushes.White); label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetValue(TextBlock.BackgroundProperty, LayoutEditor.Brush("#315D4E"));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            label.SetValue(FrameworkElement.HorizontalAlignmentProperty, resize ? HorizontalAlignment.Center : HorizontalAlignment.Left);
            border.AppendChild(label); thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            if (resize) { thumb.Width = 22; thumb.Height = 22; thumb.HorizontalAlignment = HorizontalAlignment.Right; thumb.VerticalAlignment = VerticalAlignment.Bottom; }
            LayoutItemSettings initial = null; Point anchor = new Point(), start = new Point(); double scaleX = 1, scaleY = 1; Rect inner = Rect.Empty;
            thumb.PreviewMouseLeftButtonDown += delegate { _select(item.Id); };
            thumb.DragStarted += delegate(object sender, DragStartedEventArgs e) {
                _select(item.Id); initial = null;
                LayoutContentItem current = CardContentLayout.VisibleItems(_tile).FirstOrDefault(value => value.Id == item.Id);
                if (current == null) return;
                initial = CardContentLayout.Position(_tile, current); inner = CardContentLayout.InnerBounds(_tile);
                anchor = new Point(e.HorizontalOffset, e.VerticalOffset); start = thumb.TransformToAncestor(_dragSurface).Transform(anchor);
                GeneralTransform transform = _tile.Card.TransformToAncestor(_dragSurface);
                Point origin = transform.Transform(new Point());
                scaleX = Math.Max(0.001, transform.Transform(new Point(1, 0)).X - origin.X);
                scaleY = Math.Max(0.001, transform.Transform(new Point(0, 1)).Y - origin.Y);
            };
            thumb.DragDelta += delegate(object sender, DragDeltaEventArgs e) {
                if (initial == null) return;
                Point pointer = thumb.TransformToAncestor(_dragSurface).Transform(new Point(anchor.X + e.HorizontalChange, anchor.Y + e.VerticalChange));
                double dx = (pointer.X - start.X) / scaleX / inner.Width, dy = (pointer.Y - start.Y) / scaleY / inner.Height;
                double x = initial.X, y = initial.Y, width = initial.Width, height = initial.Height;
                if (resize)
                {
                    double horizontal = dx / width, vertical = dy / height;
                    double factor = 1 + (Math.Abs(horizontal) >= Math.Abs(vertical) ? horizontal : vertical);
                    factor = Math.Max(Math.Max(0.025 / width, 0.02 / height), Math.Min(factor, Math.Min((1 - x) / width, (1 - y) / height)));
                    width *= factor; height *= factor;
                }
                else { x = Math.Max(0, Math.Min(1 - width, x + dx)); y = Math.Max(0, Math.Min(1 - height, y + dy)); }
                _change(item.Id, x, y, width, height);
                Refresh(item.Id);
            };
            return thumb;
        }

        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index) { return _canvas; }
        protected override Size MeasureOverride(Size constraint) { _canvas.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _canvas.Arrange(new Rect(finalSize)); return finalSize; }
    }
}
