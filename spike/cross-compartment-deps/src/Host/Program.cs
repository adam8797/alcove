using System.Reflection;
using System.Runtime.Loader;
using Contracts;
using Host;

var layout = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
string Pkg(string id, string ver = "1.0.0") => Path.Combine(layout, "packages", id, ver, id + ".dll");
string DepV(int v) => Path.Combine(layout, "packages", "Dep", $"{v}.0.0", "Dep.dll");
var only = args.Length > 0 ? args[0] : null;
int n = 0;

// Host itself uses Dep v1 (host-shared).
Console.WriteLine($"Host: Dep in default context = {new Dep.Widget().Describe()}");

Compartment NewX(string name = "X")
{
    // Compartment X holds plugin A, with Dep v2 compartment-private (host has v1).
    var x = new Compartment(name);
    x.HostOwned.Add("Contracts");
    x.Private["A"] = Pkg("A");
    x.Private["Dep"] = DepV(2);
    return x;
}
Compartment NewY(Compartment x, string plugin = "B", string name = "Y")
{
    var y = new Compartment(name);
    y.HostOwned.Add("Contracts");
    y.ProvidedBy["A"] = x;
    y.Private[plugin] = Pkg(plugin);
    return y;
}

void Scenario(string id, string title, string expect, Func<IEnumerable<ScenarioResult>> body, params Compartment[] cs)
{
    if (only is not null && !id.StartsWith(only, StringComparison.OrdinalIgnoreCase)) return;
    n++;
    Console.WriteLine();
    Console.WriteLine($"=== {id}: {title}");
    Console.WriteLine($"    expectation: {expect}");
    List<ScenarioResult> results;
    try { results = body().ToList(); }
    catch (Exception e) { results = [new("scenario body", false, $"{e.GetType().FullName}: {e.Message}")]; }
    foreach (var r in results) Console.WriteLine($"  [{(r.Ok ? "OK  " : "FAIL")}] {r.Name}: {r.Detail}");
    foreach (var c in cs) foreach (var t in c.Trace.Distinct()) Console.WriteLine($"    trace {t}");
}

IEnumerable<ScenarioResult> RunPlugin(Compartment c, string asmName)
{
    var asm = c.LoadFromAssemblyName(new AssemblyName(asmName));
    var src = (IScenarioSource)Activator.CreateInstance(asm.GetType(asmName + ".Scenarios", throwOnError: true)!)!;
    return src.Run().ToList();
}

ScenarioResult Check(string name, Func<string> f) => Step.Run(name, f);

// ======================= Q1 =======================
{
    var x = NewX(); var y = NewY(x);
    Scenario("Q1.1", "B(Y) uses A(X); A loaded into X first (eager)", "all OK", () =>
    {
        var aInX = x.LoadFromAssemblyPath(Pkg("A"));
        var r = RunPlugin(y, "B").ToList();
        var aViaY = y.LoadFromAssemblyName(new AssemblyName("A"));
        r.Add(Check("host: Y.LoadFromAssemblyName(A) is X's instance", () => $"{ReferenceEquals(aViaY, aInX)}; GetLoadContext={Where.Ctx(aViaY)}"));
        r.Add(Check("host: Y.Assemblies", () => string.Join(", ", y.Assemblies.Select(a => a.GetName().Name))));
        r.Add(Check("host: X.Assemblies", () => string.Join(", ", x.Assemblies.Select(a => a.GetName().Name))));
        r.Add(Check("host: A's AGreeter as host-owned IGreeter", () => ((IGreeter)Activator.CreateInstance(aInX.GetType("A.AGreeter")!)!).Greet()));
        r.Add(Check("host: B's BGreeter as host-owned IGreeter", () => ((IGreeter)Activator.CreateInstance(y.LoadFromAssemblyName(new("B")).GetType("B.BGreeter")!)!).Greet()));
        return r;
    }, x, y);
}
{
    var x = NewX(); var y = NewY(x);
    Scenario("Q1.2", "B(Y) loaded before anything loaded in X (lazy, AskProvider)", "all OK; A gets loaded into X on demand", () =>
    {
        var before = string.Join(",", x.Assemblies.Select(a => a.GetName().Name));
        var r = RunPlugin(y, "B").Take(3).ToList();
        r.Add(Check("host: X.Assemblies before/after", () => $"before=[{before}] after=[{string.Join(",", x.Assemblies.Select(a => a.GetName().Name))}]"));
        return r;
    }, x, y);
}
{
    var x = NewX(); var y = NewY(x); y.Strategy = DelegateStrategy.AlreadyLoadedOnly;
    Scenario("Q1.3", "Same as Q1.2 but Y only returns assemblies X has ALREADY loaded", "FAIL: A not found (order-dependent)", () => RunPlugin(y, "B").Take(2), x, y);
}

// ======================= Q2 =======================
// Q2a is covered inside B's scenarios (Q1.1 output, lines 'Q2a ...').
{
    var x = NewX(); var y = NewY(x, "BD2"); y.Private["Dep"] = DepV(2);
    Scenario("Q2b.1", "BD2 (refs Dep 2.0) in Y; Y loads its OWN copy of the very same Dep 2.0 file", "leaky calls FAIL (two Dep instances)", () => RunPlugin(y, "BD2"), x, y);
}
{
    var x = NewX(); var y = NewY(x, "BD2"); y.HostShared.Add("Dep");
    Scenario("Q2b.2", "BD2 (refs Dep 2.0) in Y; Y treats Dep as host-shared (default ctx has Dep 1.0)", "FAIL: default can't satisfy 2.0", () => RunPlugin(y, "BD2"), x, y);
}
{
    var x = NewX(); var y = NewY(x, "BD3"); y.Private["Dep"] = DepV(3);
    Scenario("Q2b.3", "BD3 (refs Dep 3.0) in Y with private Dep 3.0; A(X) has Dep 2.0", "leaky calls FAIL; own v3 use OK", () => RunPlugin(y, "BD3"), x, y);
}
{
    var x = NewX(); var y = NewY(x, "BD2"); y.ProvidedBy["Dep"] = x;
    Scenario("Q2c.1", "BD2 in Y; Y delegates Dep to X (B uses A's copy)", "all OK", () => RunPlugin(y, "BD2"), x, y);
}
{
    var x = NewX(); var y = NewY(x, "BD3"); y.ProvidedBy["Dep"] = x;
    Scenario("Q2c.2", "BD3 (needs Dep 3.0) in Y; Y delegates Dep to X which has 2.0", "FAIL: lower version than referenced", () => RunPlugin(y, "BD3"), x, y);
}
{
    var x = NewX(); var y = NewY(x, "BOwnD1"); y.Private["Dep"] = DepV(1);
    Scenario("Q2c.3", "BOwnD1 (own use of Dep 1.0, no leaky A calls) in Y with private Dep 1.0", "OK; object exchange shows 'NOT my Widget'", () => RunPlugin(y, "BOwnD1"), x, y);
}
{
    var x = NewX(); var y = NewY(x, "BOwnD1"); y.HostShared.Add("Dep");
    Scenario("Q2c.4", "BOwnD1 in Y with Dep host-shared (default 1.0)", "OK", () => RunPlugin(y, "BOwnD1"), x, y);
}
{
    var x = NewX(); var y = NewY(x, "BOwnD1"); y.ProvidedBy["Dep"] = x;
    Scenario("Q2c.5", "BOwnD1 in Y; Y delegates Dep to X (2.0, which removed OldApi)", "OldApi FAILS (MissingMethod)", () => RunPlugin(y, "BOwnD1"), x, y);
}

{
    // Exposed dependency that is NOT private in X: delegation must still yield X's answer (here: default context).
    var x = NewX(); var y = new Compartment("Y"); y.ProvidedBy["A"] = x; y.ProvidedBy["Contracts"] = x; y.Private["B"] = Pkg("B");
    Scenario("Q2c.6", "Y delegates Contracts to X, where X answers 'host-owned -> null' (default context)", "OK: delegation returns the DEFAULT context's Contracts", () =>
    {
        var r = RunPlugin(y, "B").Take(2).ToList();
        r.Add(Check("host: Y.LoadFromAssemblyName(Contracts) == typeof(IGreeter).Assembly", () => $"{y.LoadFromAssemblyName(new("Contracts")) == typeof(IGreeter).Assembly}; ctx={Where.Ctx(y.LoadFromAssemblyName(new("Contracts")))}"));
        return r;
    }, x, y);
}

// ======================= Q4 =======================
Compartment NewZ(string plugin, Compartment? x, Compartment? y, string name = "Z")
{
    var z = new Compartment(name);
    z.HostOwned.Add("Contracts");
    if (x is not null) z.ProvidedBy["A"] = x;
    if (y is not null) z.ProvidedBy["B"] = y;
    z.Private[plugin] = Pkg(plugin);
    return z;
}
{
    var x = NewX(); var y = NewY(x); var z = NewZ("C", x, y);
    Scenario("Q4.1", "Diamond C(Z)->A(X) and C(Z)->B(Y)->A(X); C loaded first, nothing else loaded yet", "all OK", () => RunPlugin(z, "C"), x, y, z);
}
{
    var x = NewX(); var y = NewY(x); var z = NewZ("CChain", null, y);
    Scenario("Q4.2", "Chain CChain(Z)->B(Y)->A(X); Z maps only B (C has no metadata ref to A)", "OK", () => RunPlugin(z, "CChain"), x, y, z);
}
{
    var x = NewX(); var y = NewY(x); var z = NewZ("C", null, y);
    Scenario("Q4.3", "C(Z) references A, but Z maps only its DIRECT dep B (A not in table)", "FAIL: A unresolvable from Z", () => RunPlugin(z, "C"), x, y, z);
}
{
    var x = NewX(); var y = NewY(x); var z = NewZ("C", null, y); z.Private["A"] = Pkg("A"); z.Private["Dep"] = DepV(2);
    Scenario("Q4.4", "C(Z) loads its OWN copy of A while B(Y) uses X's A", "FAIL: type identity split", () => RunPlugin(z, "C"), x, y, z);
}
{
    // Diamond through two compartments to the same provider, plus leaked Dep delegated from both.
    var x = NewX(); var y1 = NewY(x, "BD2", "Y1"); y1.ProvidedBy["Dep"] = x;
    var y2 = NewY(x, "B", "Y2");
    Scenario("Q4.5", "Two siblings BD2(Y1)->A(X) and B(Y2)->A(X), Y1 delegates Dep to X", "all OK; one A, one Dep v2", () =>
    {
        var r = RunPlugin(y1, "BD2").Take(4).Concat(RunPlugin(y2, "B").Take(1)).ToList();
        r.Add(Check("host: A instances across X/Y1/Y2", () => $"{ReferenceEquals(y1.LoadFromAssemblyName(new("A")), y2.LoadFromAssemblyName(new("A")))}"));
        return r;
    }, x, y1, y2);
}

// ======================= Q5 (host side) =======================
{
    var x = NewX(); var y = NewY(x);
    Scenario("Q5.1", "Reflection APIs called from the HOST (default context)", "AQN lookups fail unless contextual reflection is entered", () =>
    {
        var aInX = x.LoadFromAssemblyPath(Pkg("A"));
        y.LoadFromAssemblyPath(Pkg("B"));
        var r = new List<ScenarioResult>
        {
            Check("Type.GetType(\"A.AService, A\") from host", () => Type.GetType("A.AService, A")?.ToString() ?? "null"),
            Check("Type.GetType(\"A.AService, A\", throwOnError) from host", () => Type.GetType("A.AService, A", throwOnError: true)!.ToString()),
            Check("Y.EnterContextualReflection + Type.GetType(\"A.AService, A\")", () => { using var _ = y.EnterContextualReflection(); var t = Type.GetType("A.AService, A"); return t is null ? "null" : $"{Where.Of(t)}; same as X's: {t.Assembly == aInX}"; }),
            Check("Y contextual + Type.GetType(\"A.Box`1[[B.BThing, B]], A\")", () => { using var _ = y.EnterContextualReflection(); var t = Type.GetType("A.Box`1[[B.BThing, B]], A"); return t is null ? "null" : $"{t} args@{Where.Ctx(t.GetGenericArguments()[0].Assembly)}"; }),
            Check("aInX.GetType(\"A.Box`1[[B.BThing, B]]\") (no contextual)", () => aInX.GetType("A.Box`1[[B.BThing, B]]")?.ToString() ?? "null"),
            Check("Y contextual + aInX.GetType(\"A.Box`1[[B.BThing, B]]\")", () => { using var _ = y.EnterContextualReflection(); return aInX.GetType("A.Box`1[[B.BThing, B]]")?.ToString() ?? "null"; }),
            Check("Y.LoadFromAssemblyName(A) == X's A", () => $"{y.LoadFromAssemblyName(new("A")) == aInX}"),
            Check("Activator.CreateInstance(\"A\", \"A.AService\") from host", () => Activator.CreateInstance("A", "A.AService")?.Unwrap()?.ToString() ?? "null"),
            Check("Y contextual + Activator.CreateInstance(\"A\", \"A.AService\")", () => { using var _ = y.EnterContextualReflection(); var o = Activator.CreateInstance("A", "A.AService")!.Unwrap()!; return $"{o} from {Where.Of(o.GetType())}"; }),
            Check("Activator.CreateInstance(aInX.GetType(\"A.AService\"))", () => { var o = Activator.CreateInstance(aInX.GetType("A.AService")!)!; return $"{o} from {Where.Of(o.GetType())}"; }),
            Check("AssemblyLoadContext.All names", () => string.Join(",", AssemblyLoadContext.All.Select(c => c.Name).Distinct())),
        };
        return r;
    }, x, y);
}
{
    var x = NewX(); var y = NewY(x);
    Scenario("Q5.2", "Reflection APIs from inside B (in Y), with A not yet loaded in X", "Type.GetType/Assembly.Load route through Y.Load -> X", () => RunPlugin(y, "B").Where(r => r.Name.StartsWith("Q5")), x, y);
}

Console.WriteLine();
Console.WriteLine($"Ran {n} scenarios.");
