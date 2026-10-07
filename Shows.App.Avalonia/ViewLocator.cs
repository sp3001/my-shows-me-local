using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Shows.App.Avalonia;

/// <summary>
/// Given a view model, returns the corresponding view. Maps a view model's type name to a view in the
/// <c>Shows.App.Avalonia.Views</c> namespace by swapping the <c>ViewModel</c> suffix for <c>View</c>
/// (<c>ShowCardViewModel</c> → <c>Views.ShowCardView</c>, <c>TrackingViewModel</c> →
/// <c>Views.TrackingView</c>). Registered as the app's single <see cref="IDataTemplate"/> so every
/// <see cref="ContentControl"/> and <see cref="ItemsControl"/> resolves its item to the right view
/// without an explicit DataTemplate per page — the Avalonia analogue of the MAUI Shell's DataTemplates.
/// </summary>
/// <remarks>
/// <see cref="Match"/> returns true only when a matching view type actually exists, so non-view-model
/// bound objects (strings, the <c>FeedOption</c>/<c>CategoryTab</c> records) fall through to the default
/// text rendering rather than hitting the "Not Found" placeholder. This is why the card VMs — which
/// derive from <c>ObservableObject</c>, not <c>ViewModelBase</c> — still resolve: the match is by
/// convention, not by a base type.
/// </remarks>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public sealed class ViewLocator : IDataTemplate
{
    private const string ViewNamespace = "Shows.App.Avalonia.Views.";

    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var type = ResolveViewType(param.GetType());
        if (type is not null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + param.GetType().FullName };
    }

    public bool Match(object? data) => data is not null && ResolveViewType(data.GetType()) is not null;

    private static Type? ResolveViewType(Type viewModelType)
    {
        var name = viewModelType.Name;
        var viewName = name.EndsWith("ViewModel", StringComparison.Ordinal)
            ? string.Concat(name.AsSpan(0, name.Length - "ViewModel".Length), "View")
            : name + "View";

        // Same assembly as this ViewLocator, so a plain (non-assembly-qualified) name resolves.
        return Type.GetType(ViewNamespace + viewName, throwOnError: false);
    }
}
