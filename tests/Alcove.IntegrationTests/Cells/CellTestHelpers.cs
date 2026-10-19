using System.Reflection;
using Alcove.Internal.Cells;
using Alcove.Internal.Hosting;
using Alcove.IntegrationTests.Infrastructure;

namespace Alcove.IntegrationTests.Cells;

internal static class CellTestHelpers
{
    private static readonly Lazy<HostInventory> s_inventory = new(() => HostInventory.FromCurrentProcess([]));

    /// <summary>The test process's own host inventory.</summary>
    public static HostInventory Inventory => s_inventory.Value;

    /// <summary>A fresh cell environment over the test process's inventory, with captured logs.</summary>
    public static (CellEnvironment Environment, ListLoggerFactory Logs) CreateEnvironment()
    {
        var logs = new ListLoggerFactory();
        return (new CellEnvironment(Inventory, logs), logs);
    }

    /// <summary>Calls a public static method or property getter by reflection: "OK: result" or "EX: Type: message".</summary>
    public static string Call(Assembly assembly, string type, string member)
    {
        try
        {
            var t = assembly.GetType(type, throwOnError: true)!;
            var result = t.GetMethod(member, Type.EmptyTypes) is { } method
                ? method.Invoke(null, null)
                : t.GetProperty(member)!.GetValue(null);
            return "OK: " + result;
        }
        catch (Exception ex)
        {
            return "EX: " + Describe(ex);
        }
    }

    public static string Describe(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
        {
            ex = inner;
        }

        var text = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += $" ---> {inner.GetType().Name}: {inner.Message.Split('\n')[0]}";
        }

        return text;
    }
}
