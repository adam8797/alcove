namespace Alcove.Wpf.Internal;

/// <summary>
/// The naming convention linking view-models to views: <c>FooViewModel</c> → <c>FooView</c>, in the same namespace or
/// in the namespace with its <c>ViewModels</c> segment replaced by <c>Views</c> (<c>App.ViewModels.FooViewModel</c> →
/// <c>App.Views.FooView</c>). Both types must be in the same assembly.
/// </summary>
internal static class ViewConventions
{
    private const string ViewModelSuffix = "ViewModel";

    /// <summary>The full names a view of <paramref name="viewModelFullName"/> may have, in the order they're tried.</summary>
    public static IReadOnlyList<string> CandidateViewNames(string viewModelFullName)
    {
        ArgumentException.ThrowIfNullOrEmpty(viewModelFullName);
        var dot = viewModelFullName.LastIndexOf('.');
        var ns = dot < 0 ? "" : viewModelFullName[..dot];
        var name = viewModelFullName[(dot + 1)..];
        if (name.Length <= ViewModelSuffix.Length || !name.EndsWith(ViewModelSuffix, StringComparison.Ordinal))
        {
            return [];
        }

        var view = name[..^ViewModelSuffix.Length] + "View";
        var names = new List<string> { Qualify(ns, view) };
        var segments = ns.Split('.');
        var index = Array.LastIndexOf(segments, "ViewModels");
        if (index >= 0)
        {
            segments[index] = "Views";
            names.Add(Qualify(string.Join('.', segments), view));
        }

        return names;

        static string Qualify(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// Pairs each view-model in <paramref name="types"/> (public, non-abstract, non-generic classes named <c>*ViewModel</c>)
    /// with the first candidate view in <paramref name="types"/> that <paramref name="isView"/> accepts.
    /// </summary>
    /// <returns>The pairs, sorted by view-model name.</returns>
    public static IReadOnlyList<(Type ViewModel, Type View)> FindPairs(IEnumerable<Type> types, Func<Type, bool> isView)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(isView);
        var all = types.Where(t => t.FullName is not null).ToList();
        var byName = all.GroupBy(t => t.FullName!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var pairs = new List<(Type, Type)>();
        foreach (var viewModel in all.Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && (t.IsPublic || t.IsNestedPublic))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var name in CandidateViewNames(viewModel.FullName!))
            {
                if (byName.TryGetValue(name, out var view) && view != viewModel && isView(view))
                {
                    pairs.Add((viewModel, view));
                    break;
                }
            }
        }

        return pairs;
    }
}
