using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Alcove.Tests.Logging;

/// <summary>
/// Event IDs are part of what an app developer relies on (log filters, alerts), so they're stable and unique. Each
/// log class owns one range of 100 IDs, and no two classes share a range.
/// </summary>
public sealed class EventIdTests
{
    private static readonly Assembly[] Shipping =
    [
        typeof(IPlugin).Assembly,
        typeof(PluginHost).Assembly,
        typeof(DependencyInjection.PluginServiceCollectionExtensions).Assembly,
        typeof(Autofac.PluginContainerBuilderExtensions).Assembly,
    ];

    private static IReadOnlyList<(MethodInfo Method, LoggerMessageAttribute Attribute)> Messages() =>
    [
        .. Shipping
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()!))
            .Where(m => m.Attribute is not null),
    ];

    [Fact]
    public void LoggerMessages_AreFound()
        => Assert.True(Messages().Count > 40, "The LoggerMessage scan found too few messages; is the attribute still in metadata?");

    [Fact]
    public void EventIds_AreUnique()
    {
        var duplicates = Messages()
            .GroupBy(m => m.Attribute.EventId)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(m => $"{m.Method.DeclaringType!.Name}.{m.Method.Name}"))}");

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EventIds_AreSetAndNamesAreUnique()
    {
        var messages = Messages();

        Assert.All(messages, m => Assert.True(m.Attribute.EventId > 0, $"{m.Method.DeclaringType!.Name}.{m.Method.Name} has no event ID"));
        Assert.Empty(messages.GroupBy(m => $"{m.Method.DeclaringType!.FullName}.{m.Method.Name}").Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void EachLogClass_OwnsOneRangeOfAHundred()
    {
        var ranges = Messages().GroupBy(m => m.Method.DeclaringType!).ToList();

        var spanning = ranges.Where(g => g.Select(m => m.Attribute.EventId / 100).Distinct().Count() > 1).Select(g => g.Key.Name);
        Assert.Empty(spanning);

        var shared = ranges.GroupBy(g => g.First().Attribute.EventId / 100).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key * 100}: {string.Join(", ", g.Select(c => c.Key.Name))}");
        Assert.Empty(shared);
    }

    [Fact]
    public void Levels_AreNeverCriticalOrNone()
        => Assert.DoesNotContain(Messages(), m => m.Attribute.Level is LogLevel.Critical or LogLevel.None);
}
