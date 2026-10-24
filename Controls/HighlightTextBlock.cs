using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClipAssist.Controls;

/// <summary>
/// 支持搜索关键词高亮的 TextBlock。
/// 不直接修改 Text，而是重建 Inlines；TextTrimming、ToolTip 等原有行为保持不变。
/// </summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure,
            OnHighlightInputChanged));

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText),
        typeof(string),
        typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure,
            OnHighlightInputChanged));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(
            Brushes.Gold,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnHighlightInputChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private static void OnHighlightInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HighlightTextBlock)d).RebuildInlines();
    }

    private void RebuildInlines()
    {
        Inlines.Clear();

        foreach (var segment in TextHighlighter.Split(SourceText, SearchText))
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.Background = HighlightBrush;
            }

            Inlines.Add(run);
        }
    }
}