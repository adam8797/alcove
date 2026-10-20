using System.Reflection;
using System.Windows;

namespace Alcove.Wpf.Views;

/// <summary>
/// Links view-models to views with implicit <see cref="DataTemplate"/>s, by convention: <c>FooViewModel</c> →
/// <c>FooView</c>, in the same namespace or in the namespace with <c>ViewModels</c> replaced by <c>Views</c>, in the same
/// assembly. A <c>ContentControl</c> (or <c>ItemsControl</c> item) whose content is the view-model then shows the view,
/// with the view-model as its <see cref="FrameworkElement.DataContext"/>.
/// </summary>
/// <remarks>
/// <see cref="WpfPluginOptions.MapViewModelsToViews"/> does this for every plugin as it loads. Create and call these
/// on the UI thread.
/// </remarks>
public static class ViewTemplates
{
    /// <summary>An implicit template (keyed by <see cref="DataTemplate.DataTemplateKey"/>) showing <paramref name="viewType"/> for <paramref name="viewModelType"/>.</summary>
    /// <param name="viewModelType">The view-model type.</param>
    /// <param name="viewType">The view: a <see cref="FrameworkElement"/> with a public parameterless constructor.</param>
    /// <returns>The sealed template.</returns>
    /// <exception cref="ArgumentException"><paramref name="viewType"/> isn't such a view.</exception>
    public static DataTemplate Create(Type viewModelType, Type viewType)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        ArgumentNullException.ThrowIfNull(viewType);
        if (!IsView(viewType))
        {
            throw new ArgumentException($"{viewType} isn't a FrameworkElement with a public parameterless constructor.", nameof(viewType));
        }

        var template = new DataTemplate(viewModelType) { VisualTree = new FrameworkElementFactory(viewType) };
        template.Seal();
        return template;
    }

    /// <summary>The convention's templates for an assembly's public view-models that have a matching view.</summary>
    /// <param name="assembly">A plugin (or app) assembly.</param>
    /// <returns>A dictionary holding one implicit template per view-model; empty if none matched.</returns>
    public static ResourceDictionary CreateForAssembly(Assembly assembly)
    {
        var dictionary = new ResourceDictionary();
        foreach (var (viewModel, view) in FindPairs(assembly))
        {
            var template = Create(viewModel, view);
            dictionary.Add(template.DataTemplateKey!, template);
        }

        return dictionary;
    }

    /// <summary>The view-model and view pairs the convention finds in an assembly's public types.</summary>
    internal static IReadOnlyList<(Type ViewModel, Type View)> FindPairs(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return ViewConventions.FindPairs(assembly.GetExportedTypes(), IsView);
    }

    private static bool IsView(Type type) =>
        typeof(FrameworkElement).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is { IsPublic: true };
}
