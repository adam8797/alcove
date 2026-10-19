namespace Alcove.Wpf.TestHost;

/// <summary>The command line.</summary>
internal sealed class Arguments
{
    public string Command { get; private init; } = "";

    public string Scenario { get; private init; } = "";

    public string Root { get; private init; } = "";

    public string? Feed { get; private init; }

    public IReadOnlyList<string> Install { get; private init; } = [];

    public bool NoPreload { get; private init; }

    public bool Forward { get; private init; }

    public static Arguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("No command.");
        }

        string? root = null, feed = null, scenario = null;
        var install = new List<string>();
        bool noPreload = false, forward = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root": root = Value(args, ref i); break;
                case "--feed": feed = Value(args, ref i); break;
                case "--install": install.AddRange(Value(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--no-preload": noPreload = true; break;
                case "--forward": forward = true; break;
                case var other when !other.StartsWith("--", StringComparison.Ordinal) && scenario is null: scenario = other; break;
                default: throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }
        }

        return new Arguments
        {
            Command = args[0],
            Scenario = scenario ?? "",
            Root = root ?? throw new ArgumentException("--root is required."),
            Feed = feed,
            Install = install,
            NoPreload = noPreload,
            Forward = forward,
        };
    }

    private static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
}
