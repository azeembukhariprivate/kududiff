using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kududiff.Controls
{
    /// <summary>The KUDUDIFF mark: two pages side by side whose lines differ. Uses the text colour.</summary>
    public class LogoMark : Viewbox
    {
        public LogoMark(double size = 40)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;

            var canvas = new Canvas { Width = 64, Height = 64 };
            foreach (var x in new[] { 9.0, 35.0 })
            {
                var page = new Rectangle { Width = 20, Height = 40, RadiusX = 3.5, RadiusY = 3.5, StrokeThickness = 3.5 };
                Canvas.SetLeft(page, x);
                Canvas.SetTop(page, 12);
                Bind(page, Shape.StrokeProperty);
                canvas.Children.Add(page);
            }
            // Lines on each page; the second and third differ between the two.
            foreach (var (x, y, w) in new[]
                     {
                         (14.0, 20.0, 10.0), (14.0, 27.0, 7.0), (14.0, 34.0, 10.0), (14.0, 41.0, 8.0),
                         (40.0, 20.0, 10.0), (40.0, 27.0, 10.0), (40.0, 41.0, 8.0)
                     })
            {
                var line = new Rectangle { Width = w, Height = 3.2, RadiusX = 1.6, RadiusY = 1.6 };
                Canvas.SetLeft(line, x);
                Canvas.SetTop(line, y);
                Bind(line, Shape.FillProperty);
                canvas.Children.Add(line);
            }
            Child = canvas;
        }

        private void Bind(Shape shape, DependencyProperty property) =>
            shape.SetBinding(property, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
    }

    /// <summary>The app tile: the mark in white on a purple rounded square.</summary>
    public static class LogoTile
    {
        public static Border Create(double size)
        {
            var mark = new LogoMark(size);
            mark.SetValue(TextElement.ForegroundProperty, Brushes.White);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.234),
                Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
                Child = mark
            };
        }
    }
}
