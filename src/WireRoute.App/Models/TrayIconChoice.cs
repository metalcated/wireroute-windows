using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WireRoute.App.Models;

public sealed record TrayIconChoice(
    string Name,
    Brush PrimaryBrush,
    Brush AccentBrush,
    Brush ShieldBrush,
    Visibility LegacyRingVisibility);
