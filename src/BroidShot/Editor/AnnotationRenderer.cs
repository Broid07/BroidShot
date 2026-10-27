using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BroidShot.Editor;

public static class AnnotationRenderer
{
    public static void Draw(DrawingContext context, IEnumerable<Annotation> annotations)
    {
        foreach (var item in annotations)
        {
            var brush = new SolidColorBrush(item.Color);
            var pen = new Pen(brush, item.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            switch (item.Tool)
            {
                case AnnotationTool.Rectangle: context.DrawRectangle(null, pen, item.Bounds); break;
                case AnnotationTool.Ellipse: context.DrawEllipse(null, pen, new Point(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2), item.Bounds.Width / 2, item.Bounds.Height / 2); break;
                case AnnotationTool.Redact: context.DrawRectangle(Brushes.Black, null, item.Bounds); break;
                case AnnotationTool.Highlight:
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(85, item.Color.R, item.Color.G, item.Color.B)), null, item.Bounds);
                    break;
                case AnnotationTool.Arrow:
                    context.DrawLine(pen, item.Start, item.End);
                    var direction = item.Start - item.End;
                    if (direction.Length > 0.1)
                    {
                        direction.Normalize();
                        var perpendicular = new Vector(-direction.Y, direction.X);
                        double size = Math.Max(14, item.Thickness * 4);
                        var head = new StreamGeometry();
                        using (var dc = head.Open())
                        {
                            dc.BeginFigure(item.End, true, true);
                            dc.LineTo(item.End + direction * size + perpendicular * size * .45, true, false);
                            dc.LineTo(item.End + direction * size - perpendicular * size * .45, true, false);
                        }
                        context.DrawGeometry(brush, null, head);
                    }
                    break;
                case AnnotationTool.Pen:
                    if (item.Points.Count > 1)
                    {
                        var path = new StreamGeometry();
                        using (var dc = path.Open())
                        {
                            dc.BeginFigure(item.Points[0], false, false);
                            dc.PolyLineTo(item.Points.GetRange(1, item.Points.Count - 1), true, true);
                        }
                        context.DrawGeometry(null, pen, path);
                    }
                    break;
                case AnnotationTool.Text:
                    context.DrawText(CreateText(item.Text, Math.Max(16, item.Thickness * 5), brush), item.Start);
                    break;
                case AnnotationTool.Number:
                    double radius = Math.Max(16, item.Thickness * 5);
                    context.DrawEllipse(brush, new Pen(Brushes.White, 2), item.Start, radius, radius);
                    var number = CreateText(item.Text, radius * 1.15, Brushes.White);
                    context.DrawText(number, new Point(item.Start.X - number.Width / 2, item.Start.Y - number.Height / 2));
                    break;
            }
        }
    }

    public static FormattedText CreateText(string text, double size, Brush brush) => new(text, CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1);

    public static BitmapSource Export(BitmapSource source, AnnotationDocument document)
    {
        var selection = PixelSelection(document.Selection, source.PixelWidth, source.PixelHeight);
        if (selection.Width < 1 || selection.Height < 1) throw new InvalidOperationException("Önce yakalamak istediğiniz alanı seçin.");
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.PushClip(new RectangleGeometry(selection));
            Draw(context, document.Items);
            context.Pop();
        }
        // Keep the physical-pixel raster origin identical to the preview. Translating vector
        // paths to the crop origin can alter antialiased edge channels by one on WPF.
        var rendered = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        int width = (int)selection.Width;
        int height = (int)selection.Height;
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        rendered.CopyPixels(new Int32Rect((int)selection.X, (int)selection.Y, width, height), pixels, stride, 0);
        // Detach the crop from the full desktop bitmap before handing it to history/export.
        var output = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        output.Freeze();
        return output;
    }

    public static Rect PixelSelection(Rect selection, int width, int height)
    {
        if (selection.IsEmpty) return Rect.Empty;
        double x = Math.Clamp(Math.Floor(selection.Left), 0, width);
        double y = Math.Clamp(Math.Floor(selection.Top), 0, height);
        double right = Math.Clamp(Math.Ceiling(selection.Right), x, width);
        double bottom = Math.Clamp(Math.Ceiling(selection.Bottom), y, height);
        return new Rect(x, y, right - x, bottom - y);
    }
}
