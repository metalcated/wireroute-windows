using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private ModalRequest? activeModal;
    private Action<WireRouteModalResult>? dismissActiveModal;

    private async Task<WireRouteModalResult> ShowModalAsync(ModalRequest request)
    {
        if (activeModal is not null)
        {
            throw new InvalidOperationException("A WireRoute modal is already open.");
        }

        activeModal = request;
        var completion = new TaskCompletionSource<WireRouteModalResult>();
        var header = new StackPanel { Spacing = 6 };
        var titleRow = new Grid { ColumnSpacing = 14 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!string.IsNullOrWhiteSpace(request.IconGlyph))
        {
            var icon = new Border
            {
                Width = 50,
                Height = 50,
                Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
                CornerRadius = new CornerRadius(10),
                Child = new FontIcon
                {
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 23,
                    Foreground = (Brush)Application.Current.Resources["NordicAccentBrush"],
                    Glyph = request.IconGlyph,
                },
            };
            titleRow.Children.Add(icon);
        }

        var title = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Text = request.Title,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        titleRow.Children.Add(title);
        header.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(request.Subtitle))
        {
            var subtitle = new TextBlock
            {
                Margin = string.IsNullOrWhiteSpace(request.IconGlyph) ? new Thickness(0) : new Thickness(64, 0, 0, 0),
                Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
                FontSize = 14,
                Text = request.Subtitle,
                TextWrapping = TextWrapping.Wrap,
            };
            header.Children.Add(subtitle);
        }

        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button? cancelButton = null;
        Button? secondaryButton = null;
        Button? primaryButton = null;
        if (!string.IsNullOrWhiteSpace(request.CancelText))
        {
            cancelButton = CreateModalButton(request.CancelText!, isPrimary: false);
            Grid.SetColumn(cancelButton, 1);
            footer.Children.Add(cancelButton);
        }

        if (!string.IsNullOrWhiteSpace(request.SecondaryText))
        {
            secondaryButton = CreateModalButton(request.SecondaryText!, isPrimary: false);
            Grid.SetColumn(secondaryButton, 2);
            footer.Children.Add(secondaryButton);
        }

        if (!string.IsNullOrWhiteSpace(request.PrimaryText))
        {
            primaryButton = CreateModalButton(request.PrimaryText!, isPrimary: true);
            primaryButton.IsEnabled = request.IsPrimaryEnabled;
            Grid.SetColumn(primaryButton, 3);
            footer.Children.Add(primaryButton);
        }

        request.AttachButtons(primaryButton, secondaryButton, cancelButton);
        ModalHeaderPresenter.Content = header;
        ModalContentPresenter.Content = request.Content;
        ModalFooterPresenter.Content = footer;
        var availableWidth = Root.ActualWidth > 0 ? Root.ActualWidth : 1180;
        var availableHeight = Root.ActualHeight > 0 ? Root.ActualHeight : 760;
        ModalFrame.Width = Math.Max(320, Math.Min(request.MaxWidth, availableWidth - 48));
        ModalFrame.MaxHeight = Math.Max(320, availableHeight - 48);
        ModalOverlay.Visibility = Visibility.Visible;

        void Finish(WireRouteModalResult result)
        {
            if (!completion.TrySetResult(result))
            {
                return;
            }

            ModalOverlay.Visibility = Visibility.Collapsed;
            ModalHeaderPresenter.Content = null;
            ModalContentPresenter.Content = null;
            ModalFooterPresenter.Content = null;
            request.DetachButtons();
            activeModal = null;
            dismissActiveModal = null;
        }

        async Task InvokeAsync(
            WireRouteModalResult result,
            Func<Task<bool>>? action)
        {
            if (request.IsBusy)
            {
                return;
            }

            request.SetBusy(true);
            var shouldClose = true;
            try
            {
                if (action is not null)
                {
                    shouldClose = await action();
                }
            }
            finally
            {
                if (!shouldClose)
                {
                    request.SetBusy(false);
                }
            }

            if (shouldClose)
            {
                Finish(result);
            }
        }

        if (cancelButton is not null)
        {
            cancelButton.Click += async (_, _) =>
                await InvokeAsync(WireRouteModalResult.Cancel, request.OnCancel);
        }

        if (secondaryButton is not null)
        {
            secondaryButton.Click += async (_, _) =>
                await InvokeAsync(WireRouteModalResult.Secondary, request.OnSecondary);
        }

        if (primaryButton is not null)
        {
            primaryButton.Click += async (_, _) =>
                await InvokeAsync(WireRouteModalResult.Primary, request.OnPrimary);
        }

        dismissActiveModal = Finish;
        DispatcherQueue.TryEnqueue(() =>
        {
            (primaryButton ?? cancelButton ?? secondaryButton)?.Focus(FocusState.Programmatic);
        });
        return await completion.Task;
    }

    private static Button CreateModalButton(string text, bool isPrimary)
    {
        var button = new Button
        {
            Content = text,
            CornerRadius = new CornerRadius(6),
            MinHeight = 40,
            Padding = new Thickness(16, 8, 16, 8),
        };
        if (isPrimary)
        {
            button.Style = (Style)Application.Current.Resources["NordicAccentButtonStyle"];
        }

        return button;
    }

    private void ModalOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || activeModal?.IsBusy == true)
        {
            return;
        }

        dismissActiveModal?.Invoke(WireRouteModalResult.Cancel);
        e.Handled = true;
    }

    private enum WireRouteModalResult
    {
        Cancel,
        Secondary,
        Primary,
    }

    private sealed class ModalRequest
    {
        private Button? primaryButton;
        private Button? secondaryButton;
        private Button? cancelButton;

        public required string Title { get; init; }

        public string? Subtitle { get; init; }

        public string? IconGlyph { get; init; }

        public required FrameworkElement Content { get; init; }

        public string? PrimaryText { get; init; }

        public string? SecondaryText { get; init; }

        public string? CancelText { get; init; } = "Cancel";

        public double MaxWidth { get; init; } = 900;

        public Func<Task<bool>>? OnPrimary { get; init; }

        public Func<Task<bool>>? OnSecondary { get; init; }

        public Func<Task<bool>>? OnCancel { get; init; }

        public bool IsPrimaryEnabled { get; private set; } = true;

        public bool IsBusy { get; private set; }

        public void SetPrimaryEnabled(bool isEnabled)
        {
            IsPrimaryEnabled = isEnabled;
            if (primaryButton is not null)
            {
                primaryButton.IsEnabled = isEnabled && !IsBusy;
            }
        }

        public void SetCancelEnabled(bool isEnabled)
        {
            if (cancelButton is not null)
            {
                cancelButton.IsEnabled = isEnabled && !IsBusy;
            }
        }

        public void SetBusy(bool isBusy)
        {
            IsBusy = isBusy;
            if (primaryButton is not null)
            {
                primaryButton.IsEnabled = IsPrimaryEnabled && !isBusy;
            }

            if (secondaryButton is not null)
            {
                secondaryButton.IsEnabled = !isBusy;
            }

            if (cancelButton is not null)
            {
                cancelButton.IsEnabled = !isBusy;
            }
        }

        public void AttachButtons(Button? primary, Button? secondary, Button? cancel)
        {
            primaryButton = primary;
            secondaryButton = secondary;
            cancelButton = cancel;
        }

        public void DetachButtons()
        {
            primaryButton = null;
            secondaryButton = null;
            cancelButton = null;
        }
    }
}
