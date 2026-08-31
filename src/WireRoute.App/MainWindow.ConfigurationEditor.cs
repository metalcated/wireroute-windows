using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private static RichEditBox CreateWireGuardConfigurationEditor(string configuration)
    {
        var editor = new RichEditBox
        {
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            MinHeight = 260,
            TextWrapping = TextWrapping.NoWrap,
        };
        SetWireGuardConfigurationText(editor, configuration);
        return editor;
    }

    private static string GetWireGuardConfigurationText(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out var configuration);
        return configuration.EndsWith('\r') ? configuration[..^1] : configuration;
    }

    private static void SetWireGuardConfigurationText(RichEditBox editor, string configuration)
    {
        editor.Document.SetText(TextSetOptions.None, configuration);
        ApplyWireGuardSyntaxColors(editor);
    }

    private static void ApplyWireGuardSyntaxColors(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out var configuration);
        var selectionStart = editor.Document.Selection.StartPosition;
        var selectionEnd = editor.Document.Selection.EndPosition;
        var defaultColor = ConfigurationSyntaxColor("NordicPrimaryTextBrush");
        var sectionColor = ConfigurationSyntaxColor("WireGuardSyntaxSectionBrush");
        var directiveColor = ConfigurationSyntaxColor("WireGuardSyntaxDirectiveBrush");
        var keyMaterialColor = ConfigurationSyntaxColor("WireGuardSyntaxKeyMaterialBrush");
        var valueColor = ConfigurationSyntaxColor("WireGuardSyntaxValueBrush");
        var numberColor = ConfigurationSyntaxColor("WireGuardSyntaxNumberBrush");
        var commentColor = ConfigurationSyntaxColor("NordicTertiaryTextBrush");

        editor.Document.BatchDisplayUpdates();
        try
        {
            SetConfigurationRangeColor(editor, 0, configuration.Length, defaultColor);
            var lineStart = 0;
            while (lineStart < configuration.Length)
            {
                var lineEnd = lineStart;
                while (lineEnd < configuration.Length
                    && configuration[lineEnd] is not '\r' and not '\n')
                {
                    lineEnd++;
                }

                var contentStart = lineStart;
                while (contentStart < lineEnd && char.IsWhiteSpace(configuration[contentStart]))
                {
                    contentStart++;
                }

                if (contentStart < lineEnd
                    && configuration[contentStart] is '#' or ';')
                {
                    SetConfigurationRangeColor(
                        editor,
                        contentStart,
                        lineEnd - contentStart,
                        commentColor);
                }
                else if (contentStart < lineEnd && configuration[contentStart] == '[')
                {
                    var closingBracket = configuration.IndexOf(']', contentStart, lineEnd - contentStart);
                    if (closingBracket >= 0)
                    {
                        SetConfigurationRangeColor(
                            editor,
                            contentStart,
                            closingBracket - contentStart + 1,
                            sectionColor);
                    }
                }
                else
                {
                    var equalsIndex = configuration.IndexOf('=', contentStart, lineEnd - contentStart);
                    if (equalsIndex >= 0)
                    {
                        var directiveEnd = equalsIndex;
                        while (directiveEnd > contentStart
                            && char.IsWhiteSpace(configuration[directiveEnd - 1]))
                        {
                            directiveEnd--;
                        }

                        SetConfigurationRangeColor(
                            editor,
                            contentStart,
                            directiveEnd - contentStart,
                            directiveColor);

                        var valueStart = equalsIndex + 1;
                        while (valueStart < lineEnd && char.IsWhiteSpace(configuration[valueStart]))
                        {
                            valueStart++;
                        }

                        var valueEnd = lineEnd;
                        while (valueEnd > valueStart && char.IsWhiteSpace(configuration[valueEnd - 1]))
                        {
                            valueEnd--;
                        }

                        var directive = configuration[contentStart..directiveEnd];
                        var color = directive.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)
                                || directive.Equals("PublicKey", StringComparison.OrdinalIgnoreCase)
                                || directive.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase)
                            ? keyMaterialColor
                            : directive.Equals("PersistentKeepalive", StringComparison.OrdinalIgnoreCase)
                                || directive.Equals("ListenPort", StringComparison.OrdinalIgnoreCase)
                                || directive.Equals("MTU", StringComparison.OrdinalIgnoreCase)
                                ? numberColor
                                : valueColor;
                        SetConfigurationRangeColor(
                            editor,
                            valueStart,
                            valueEnd - valueStart,
                            color);
                    }
                }

                lineStart = lineEnd;
                while (lineStart < configuration.Length
                    && configuration[lineStart] is '\r' or '\n')
                {
                    lineStart++;
                }
            }
        }
        finally
        {
            editor.Document.ApplyDisplayUpdates();
            editor.Document.Selection.SetRange(selectionStart, selectionEnd);
        }
    }

    private static Windows.UI.Color ConfigurationSyntaxColor(string resourceKey) =>
        ((SolidColorBrush)Application.Current.Resources[resourceKey]).Color;

    private static void SetConfigurationRangeColor(
        RichEditBox editor,
        int start,
        int length,
        Windows.UI.Color color)
    {
        if (length <= 0)
        {
            return;
        }

        editor.Document.GetRange(start, start + length).CharacterFormat.ForegroundColor = color;
    }
}
