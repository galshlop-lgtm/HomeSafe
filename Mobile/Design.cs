using Microsoft.Maui.Controls.Shapes;

namespace HomeSafe.Mobile;

internal static class Design
{
    public static readonly Color Paper = Color.FromArgb("#FFFFFF");
    public static readonly Color Canvas = Color.FromArgb("#F3F6FC");
    public static readonly Color Ink = Color.FromArgb("#142142");
    public static readonly Color Muted = Color.FromArgb("#52627E");
    public static readonly Color Line = Color.FromArgb("#E1E8F4");
    public static readonly Color Blue = Color.FromArgb("#2258E7");
    public static readonly Color SoftBlue = Color.FromArgb("#E8EFFF");
    public static readonly Color Green = Color.FromArgb("#10684B");
    public static readonly Color SoftGreen = Color.FromArgb("#E5F7EF");
    public static readonly Color Red = Color.FromArgb("#A82237");
    public static readonly Color SoftRed = Color.FromArgb("#FFE9EC");
    public static readonly Color Amber = Color.FromArgb("#80510C");
    public static readonly Color SoftAmber = Color.FromArgb("#FFF2D8");

    public static Label Text(string value, double size = 15, Color? color = null, bool bold = false) => new()
    {
        Text = value, FontSize = size, TextColor = color ?? Ink,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        HorizontalTextAlignment = TextAlignment.Start, LineBreakMode = LineBreakMode.WordWrap
    };

    public static VerticalStackLayout Stack(double spacing, params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = spacing };
        foreach (var child in children) stack.Add(child);
        return stack;
    }

    public static Grid Columns(View start, View end, bool startExpands = true)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new(startExpands ? GridLength.Star : GridLength.Auto));
        grid.ColumnDefinitions.Add(new(startExpands ? GridLength.Auto : GridLength.Star));
        grid.Add(start, 0, 0);
        grid.Add(end, 1, 0);
        return grid;
    }

    public static Border Box(View child, Color? background = null, Color? stroke = null, int radius = 20,
        double padding = 16) => new()
    {
        Content = child, BackgroundColor = background ?? Paper, Stroke = stroke ?? Line,
        StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) },
        Padding = new Thickness(padding)
    };

    public static Border Pill(string label, Color color, Color background)
    {
        var text = Text(label, 12, color, true);
        return new Border
        {
            Content = text, BackgroundColor = background, StrokeThickness = 0, StrokeShape = new RoundRectangle
            {
                CornerRadius = new CornerRadius(14)
            },
            Padding = new Thickness(10, 5), HorizontalOptions = LayoutOptions.Start
        };
    }

    public static Button Button(string label, Func<Task> action, bool primary = true, Color? color = null)
    {
        var button = new Button
        {
            Text = label, FontSize = 15, FontAttributes = FontAttributes.Bold, CornerRadius = 14,
            MinimumHeightRequest = 50, Padding = new Thickness(14, 8),
            BackgroundColor = color ?? (primary ? Blue : SoftBlue),
            TextColor = primary ? Paper : Ink
        };
        button.Clicked += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    public static Entry Entry(string placeholder, string? value = null, Keyboard? keyboard = null) => new()
    {
        Placeholder = placeholder, Text = value, Keyboard = keyboard ?? Keyboard.Default,
        TextColor = Ink, PlaceholderColor = Muted, FontSize = 16, MinimumHeightRequest = 48
    };

    public static View Field(string label, View input)
    {
        SemanticProperties.SetDescription(input, label);
        return Stack(5, Text(label, 14, Ink, true), Box(input, Canvas, Line, 13, 6));
    }

    public static View Shield(double size, bool hero = false) => new GraphicsView
    {
        Drawable = new ShieldArt(hero), WidthRequest = size, HeightRequest = size, InputTransparent = true
    };

    private sealed class ShieldArt(bool hero) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirty)
        {
            canvas.SaveState();
            canvas.Scale(dirty.Width / 100, dirty.Height / 100);
            canvas.FillColor = hero ? Color.FromArgb("#29468D") : Blue;
            canvas.FillRoundedRectangle(0, 0, 100, 100, 25);
            if (hero)
            {
                canvas.StrokeColor = Color.FromArgb("#4F71C0");
                canvas.StrokeSize = 1;
                canvas.DrawCircle(50, 50, 37);
                canvas.DrawCircle(50, 50, 44);
                canvas.FillColor = Color.FromArgb("#6D96FF");
                canvas.FillCircle(78, 19, 4);
            }
            var shield = new PathF();
            shield.MoveTo(50, 21);
            shield.LineTo(73, 31);
            shield.LineTo(73, 51);
            shield.QuadTo(72, 68, 50, 80);
            shield.QuadTo(28, 68, 27, 51);
            shield.LineTo(27, 31);
            shield.Close();
            canvas.FillColor = hero ? Color.FromArgb("#3357AA") : Color.FromArgb("#4675F1");
            canvas.FillPath(shield);
            canvas.StrokeColor = Paper;
            canvas.StrokeSize = 3;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(shield);
            var roof = new PathF();
            roof.MoveTo(37, 52);
            roof.LineTo(50, 41);
            roof.LineTo(63, 52);
            roof.MoveTo(41, 50);
            roof.LineTo(41, 64);
            roof.LineTo(59, 64);
            roof.LineTo(59, 50);
            canvas.DrawPath(roof);
            canvas.RestoreState();
        }
    }
}
