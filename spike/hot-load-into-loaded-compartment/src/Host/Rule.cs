// Sketch of the proposed hot-load rule and of a compartment that records (and freezes) every answer it gives.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

enum Source { Default, Private, Failed }

/// <summary>One answer for a simple name: where it comes from and which exact file/version.</summary>
record Answer(Source Source, Version? Version = null, string? Path = null)
{
    public bool SameAs(Answer o) => Source == o.Source && (Source != Source.Private || (Version == o.Version && Path == o.Path));
    public override string ToString() => Source switch
    {
        Source.Private => $"private {Version} ({System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))})",
        _ => Source.ToString().ToLowerInvariant(),
    };
}

record Verdict(bool HotLoad, string Reason)
{
    public override string ToString() => HotLoad ? "HOT LOAD" : "PendingRestart: " + Reason;
}

static class HotLoadRule
{
    /// <summary>
    /// committed = the compartment's resolution table (planned answers for every name in the resolved sets of the
    ///             plugins already loaded) merged with every answer actually handed out by Load/LoadUnmanagedDll.
    /// wanted    = the new plugin's resolved set *after* re-resolving the compartment jointly with it.
    /// </summary>
    public static Verdict Check(IReadOnlyDictionary<string, Answer> committed, IReadOnlyDictionary<string, Answer> wanted)
    {
        var clashes = new List<string>();
        foreach (var (name, want) in wanted)
        {
            if (!committed.TryGetValue(name, out var have)) continue;      // new name: always fine
            if (have.Source == Source.Failed) continue;                     // failures aren't cached by the runtime
            if (!have.SameAs(want)) clashes.Add($"{name}: compartment already uses {have}, plugin needs {want}");
        }
        return clashes.Count == 0 ? new(true, "") : new(false, string.Join("; ", clashes));
    }
}

/// <summary>
/// A compartment that records the first answer for each simple name and never changes it during the run
/// (the safety net behind the rule): later table changes for an answered name are refused, not applied.
/// </summary>
class RecordingCompartment : AssemblyLoadContext
{
    private readonly object _gate = new();
    public Dictionary<string, Answer> Planned { get; } = new(StringComparer.OrdinalIgnoreCase);   // from resolution
    public Dictionary<string, Answer> Given { get; } = new(StringComparer.OrdinalIgnoreCase);     // observed at runtime
    public List<string> Log { get; } = new();

    public RecordingCompartment(string name) : base(name, isCollectible: false)
    {
        // Resolving runs only after Load returned null AND the default context failed: that's how we learn
        // that a "default" answer actually failed (failures aren't cached, so they don't block a hot load).
        Resolving += (_, n) =>
        {
            lock (_gate)
            {
                // Only an answer that never succeeded counts as Failed; a name the default context already supplied
                // (e.g. D 1.0) stays frozen even if a later request for a higher version fails.
                if (!AssemblyLoadContext.Default.Assemblies.Any(x => x.GetName().Name == n.Name)) Given[n.Name!] = new Answer(Source.Failed);
                Log.Add($"Resolving({n.Name}, {n.Version}) -> failed");
            }
            return null;
        };
    }

    /// <summary>What the rule compares against.</summary>
    public IReadOnlyDictionary<string, Answer> Committed
    {
        get
        {
            lock (_gate)
            {
                var d = new Dictionary<string, Answer>(Planned, StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in Given) if (v.Source != Source.Failed || !d.ContainsKey(k)) d[k] = v;
                return d;
            }
        }
    }

    /// <summary>Hot install: merges the new plugin's set into the plan. Only call after the rule said HOT LOAD.</summary>
    public void Commit(IReadOnlyDictionary<string, Answer> wanted) { lock (_gate) foreach (var (k, v) in wanted) Planned[k] = v; }

    protected override Assembly? Load(AssemblyName name)
    {
        lock (_gate)
        {
            Answer answer;
            if (Given.TryGetValue(name.Name!, out var prior) && prior.Source != Source.Failed)
            {
                answer = prior;                                              // frozen
                if (Planned.TryGetValue(name.Name!, out var p) && !p.SameAs(prior))
                    Log.Add($"WARNING {name.Name}: plan now says {p} but {prior} was already given; keeping {prior}");
            }
            else answer = Planned.TryGetValue(name.Name!, out var p) ? p : new Answer(Source.Default);

            Assembly? result = null;
            if (answer.Source == Source.Private)
            {
                result = LoadFromAssemblyPath(answer.Path!);
                // The runtime does NOT check this when Load hands back an assembly (see q1-return-existing-lower).
                if (name.Version != null && result.GetName().Version < name.Version)
                    throw new FileLoadException($"{name.Name} {name.Version} requested but compartment '{Name}' already holds {result.GetName().Version}");
            }
            Given[name.Name!] = answer;
            Log.Add($"Load({name.Name}, {name.Version}) -> {answer}");
            return result;
        }
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) => IntPtr.Zero;
}
