using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Settings;

/// <summary>
/// 极简 Markdown 渲染器：给插件市场的详情页展示 README 用。
/// 支持标题、段落、无序/有序列表、表格、围栏代码块、引用、分隔线、行内粗体/斜体/行内代码/链接。
/// 不渲染图片（相对路径在客户端无从解析），会退化成 alt 文本。
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly Regex InlinePattern = new(
        @"(?<bold>\*\*[^*]+\*\*|__[^_]+__)" +
        @"|(?<strike>~~[^~]+~~)" +
        @"|(?<code>`[^`]+`)" +
        @"|(?<italic>\*[^*\n]+\*|_[^_\n]+_)" +
        @"|(?<link>\[[^\]]*\]\([^)\s]+\))" +
        @"|(?<image>!\[[^\]]*\]\([^)\s]+\))",
        RegexOptions.Compiled);

    private static readonly Regex OrderedItemPattern = new(@"^\s*(?<indent>\s*)(?<number>\d+)[.)]\s+(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex UnorderedItemPattern = new(@"^(?<indent>\s*)[-*+]\s+(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex HeadingPattern = new(@"^(?<level>#{1,6})\s+(?<text>.*?)\s*#*$", RegexOptions.Compiled);

    private static readonly Regex RulePattern = new(@"^\s*([-*_])\1{2,}\s*$", RegexOptions.Compiled);

    private static readonly Regex TableAlignPattern = new(@"^\s*\|?[\s:|-]+\|?\s*$", RegexOptions.Compiled);

    /// <param name="markdown">Markdown 原文。</param>
    /// <param name="linkBase">相对链接的解析前缀（例如仓库的 blob 地址）；为空时相对链接按普通文本处理。</param>
    public static UIElement Render(string markdown, Uri? linkBase = null)
    {
        var host = new StackPanel { Spacing = 8 };
        foreach (var block in ParseBlocks(markdown))
        {
            host.Children.Add(RenderBlock(block, linkBase));
        }

        if (host.Children.Count == 0)
        {
            host.Children.Add(Secondary("（暂无内容）"));
        }

        return host;
    }

    private static List<Block> ParseBlocks(string markdown)
    {
        var blocks = new List<Block>();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new ParagraphBlock(JoinParagraph(paragraph)));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var language = line.Trim().TrimStart('`').Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                blocks.Add(new CodeBlock(string.Join("\n", code), language.Length > 0 ? language : null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (HeadingPattern.Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(heading.Groups["level"].Value.Length, heading.Groups["text"].Value));
                continue;
            }

            if (RulePattern.IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new RuleBlock());
                continue;
            }

            // 表格：当前行含 |，且下一行是分隔行
            if (line.Contains('|') && i + 1 < lines.Length && TableAlignPattern.IsMatch(lines[i + 1]) && lines[i + 1].Contains('-'))
            {
                FlushParagraph();
                var header = SplitRow(line);
                var rows = new List<List<string>>();
                i += 2;
                while (i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    rows.Add(SplitRow(lines[i]));
                    i++;
                }

                i--;
                blocks.Add(new TableBlock(header, rows));
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    quote.Add(lines[i].TrimStart().TrimStart('>').TrimStart());
                    i++;
                }

                i--;
                blocks.Add(new QuoteBlock(quote));
                continue;
            }

            if (OrderedItemPattern.IsMatch(line) || UnorderedItemPattern.IsMatch(line))
            {
                FlushParagraph();
                var items = new List<ListItem>();
                var ordered = OrderedItemPattern.IsMatch(line);
                while (i < lines.Length)
                {
                    var orderedMatch = OrderedItemPattern.Match(lines[i]);
                    var unorderedMatch = UnorderedItemPattern.Match(lines[i]);
                    if (ordered && orderedMatch.Success)
                    {
                        items.Add(new ListItem(Indent(orderedMatch.Groups["indent"].Value), orderedMatch.Groups["text"].Value));
                    }
                    else if (!ordered && unorderedMatch.Success)
                    {
                        items.Add(new ListItem(Indent(unorderedMatch.Groups["indent"].Value), unorderedMatch.Groups["text"].Value));
                    }
                    else
                    {
                        break;
                    }

                    i++;
                }

                i--;
                blocks.Add(new ListBlock(items, ordered));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        return blocks;
    }

    private static int Indent(string whitespace) => whitespace.Replace("\t", "    ").Length / 2;

    private static List<string> SplitRow(string line)
        => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();

    /// <summary>段落内的软换行：中文之间不加空格，其余按英文习惯补空格。</summary>
    private static string JoinParagraph(List<string> lines)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0 && !IsWide(builder[^1]) && !IsWide(line[0]))
            {
                builder.Append(' ');
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private static bool IsWide(char c) => c >= 0x2E80;

    private static UIElement RenderBlock(Block block, Uri? linkBase) => block switch
    {
        HeadingBlock heading => RenderHeading(heading, linkBase),
        ParagraphBlock paragraph => RenderParagraph(paragraph.Text, linkBase),
        CodeBlock code => RenderCode(code),
        QuoteBlock quote => RenderQuote(quote, linkBase),
        ListBlock list => RenderList(list, linkBase),
        TableBlock table => RenderTable(table, linkBase),
        _ => RenderRule(),
    };

    private static UIElement RenderHeading(HeadingBlock heading, Uri? linkBase)
    {
        var text = new TextBlock
        {
            FontSize = heading.Level switch
            {
                1 => 20,
                2 => 17,
                3 => 15,
                _ => 13.5,
            },
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, heading.Level <= 2 ? 6 : 2, 0, 0),
        };
        AppendInlines(text, heading.Text, linkBase);
        return text;
    }

    private static UIElement RenderParagraph(string text, Uri? linkBase)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        AppendInlines(block, text, linkBase);
        return block;
    }

    private static UIElement RenderCode(CodeBlock code)
    {
        var text = new TextBlock
        {
            Text = code.Code,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };

        return new Border
        {
            Background = ThemeBrush("ControlFillColorSecondaryBrush", 0x14, 0x14, 0x14),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", 0x33, 0x33, 0x33),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = text,
            },
        };
    }

    private static UIElement RenderQuote(QuoteBlock quote, Uri? linkBase)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var line in quote.Lines)
        {
            panel.Children.Add(RenderParagraph(line, linkBase));
        }

        return new Border
        {
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush", 0x00, 0x78, 0xD4),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Child = panel,
        };
    }

    private static UIElement RenderList(ListBlock list, Uri? linkBase)
    {
        var panel = new StackPanel { Spacing = 4 };
        var index = 1;

        foreach (var item in list.Items)
        {
            var row = new Grid
            {
                ColumnSpacing = 8,
                Margin = new Thickness(item.Indent * 16, 0, 0, 0),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = list.Ordered ? $"{index}." : "•",
                MinWidth = list.Ordered ? 18 : 10,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
            };
            row.Children.Add(marker);

            var content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            AppendInlines(content, item.Text, linkBase);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            panel.Children.Add(row);
            index++;
        }

        return panel;
    }

    private static UIElement RenderTable(TableBlock table, Uri? linkBase)
    {
        var columns = Math.Max(table.Header.Count, table.Rows.Count > 0 ? table.Rows.Max(r => r.Count) : 0);
        if (columns == 0)
        {
            return new StackPanel();
        }

        var grid = new Grid { RowSpacing = 0, ColumnSpacing = 12 };
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rows = new List<IReadOnlyList<string>> { table.Header };
        rows.AddRange(table.Rows);
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var stroke = ThemeBrush("CardStrokeColorDefaultBrush", 0x33, 0x33, 0x33);
        for (var r = 0; r < rows.Count; r++)
        {
            var isHeader = r == 0;
            for (var c = 0; c < columns; c++)
            {
                var cell = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    IsTextSelectionEnabled = true,
                };
                AppendInlines(cell, c < rows[r].Count ? rows[r][c] : "", linkBase);

                var border = new Border
                {
                    Padding = new Thickness(0, 6, 0, 6),
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = cell,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return grid;
    }

    private static UIElement RenderRule() => new Border
    {
        Height = 1,
        Margin = new Thickness(0, 4, 0, 4),
        Background = ThemeBrush("DividerStrokeColorDefaultBrush", 0x33, 0x33, 0x33),
    };

    private static void AppendInlines(TextBlock target, string text, Uri? linkBase)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
            {
                target.Inlines.Add(new Run { Text = text[position..match.Index] });
            }

            var value = match.Value;
            if (match.Groups["bold"].Success)
            {
                target.Inlines.Add(new Run { Text = Trim(value, 2), FontWeight = FontWeights.SemiBold });
            }
            else if (match.Groups["strike"].Success)
            {
                target.Inlines.Add(new Run { Text = Trim(value, 2), TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough });
            }
            else if (match.Groups["code"].Success)
            {
                target.Inlines.Add(new Run
                {
                    Text = Trim(value, 1),
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
                });
            }
            else if (match.Groups["italic"].Success)
            {
                target.Inlines.Add(new Run { Text = Trim(value, 1), FontStyle = Windows.UI.Text.FontStyle.Italic });
            }
            else if (match.Groups["image"].Success)
            {
                var alt = value[2..value.IndexOf(']')];
                target.Inlines.Add(new Run
                {
                    Text = string.IsNullOrEmpty(alt) ? "[图片]" : $"[图片: {alt}]",
                    Foreground = ThemeBrush("TextFillColorTertiaryBrush", 0x80, 0x80, 0x85),
                });
            }
            else if (match.Groups["link"].Success)
            {
                var label = value[1..value.IndexOf(']')];
                var url = value[(value.IndexOf('(') + 1)..^1];
                if (TryResolve(url, linkBase, out var uri))
                {
                    var hyperlink = new Hyperlink { NavigateUri = uri, UnderlineStyle = UnderlineStyle.None };
                    hyperlink.Inlines.Add(new Run { Text = label });
                    ToolTipService.SetToolTip(hyperlink, uri.ToString());
                    target.Inlines.Add(hyperlink);
                }
                else
                {
                    target.Inlines.Add(new Run { Text = label });
                }
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            target.Inlines.Add(new Run { Text = text[position..] });
        }
    }

    private static bool TryResolve(string url, Uri? linkBase, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            uri = absolute;
            return true;
        }

        if (linkBase != null && Uri.TryCreate(linkBase, url.TrimStart('.', '/'), out var relative))
        {
            uri = relative;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string Trim(string value, int count) => value[count..^count];

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrush("TextFillColorSecondaryBrush", 0x90, 0x90, 0x95),
    };

    private static Brush ThemeBrush(string key, byte r, byte g, byte b)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));

    private abstract record Block;

    private sealed record HeadingBlock(int Level, string Text) : Block;

    private sealed record ParagraphBlock(string Text) : Block;

    private sealed record CodeBlock(string Code, string? Language) : Block;

    private sealed record QuoteBlock(List<string> Lines) : Block;

    private sealed record ListBlock(List<ListItem> Items, bool Ordered) : Block;

    private sealed record ListItem(int Indent, string Text);

    private sealed record TableBlock(List<string> Header, List<List<string>> Rows) : Block;

    private sealed record RuleBlock : Block;
}
