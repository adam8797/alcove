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

    /// <summary>A <c>[LoggerMessage]</c> method: its class, name, event ID and level.</summary>
    private sealed record Message(Type Class, string Method, int EventId, LogLevel Level);

    // Read as attribute data, so Alcove.Wpf (metadata only: it can't load on Linux) is checked too.
    private static IReadOnlyList<Message> Messages() =>
    [
        .. Shipping.Append(TestSupport.WpfAddOnMetadata.Assembly)
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.CustomAttributes
                .Where(a => a.AttributeType.FullName == typeof(LoggerMessageAttribute).FullName)
                .Select(a => new Message(
                    m.DeclaringType!,
                    m.Name,
                    Named(a, nameof(LoggerMessageAttribute.EventId)) is int id ? id : -1,
                    Named(a, nameof(LoggerMessageAttribute.Level)) is int level ? (LogLevel)level : LogLevel.None))),
    ];

    private static object? Named(CustomAttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(n => n.MemberName == name).TypedValue.Value;

    [Fact]
    public void LoggerMessages_AreFound()
        => Assert.True(Messages().Count > 40, "The LoggerMessage scan found too few messages; is the attribute still in metadata?");

    [Fact]
    public void EventIds_AreUnique()
    {
        var duplicates = Messages()
            .GroupBy(m => m.EventId)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(m => $"{m.Class.Name}.{m.Method}"))}");

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EventIds_AreSetAndNamesAreUnique()
    {
        var messages = Messages();

        Assert.All(messages, m => Assert.True(m.EventId > 0, $"{m.Class.Name}.{m.Method} has no event ID"));
        Assert.Empty(messages.GroupBy(m => $"{m.Class.FullName}.{m.Method}").Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void EachLogClass_OwnsOneRangeOfAHundred()
    {
        var ranges = Messages().GroupBy(m => m.Class).ToList();

        var spanning = ranges.Where(g => g.Select(m => m.EventId / 100).Distinct().Count() > 1).Select(g => g.Key.Name);
        Assert.Empty(spanning);

        var shared = ranges.GroupBy(g => g.First().EventId / 100).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key * 100}: {string.Join(", ", g.Select(c => c.Key.Name))}");
        Assert.Empty(shared);
    }

    [Fact]
    public void Levels_AreNeverCriticalOrNone()
        => Assert.DoesNotContain(Messages(), m => m.Level is LogLevel.Critical or LogLevel.None);
}
