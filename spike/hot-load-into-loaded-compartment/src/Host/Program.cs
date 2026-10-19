// Spike host: one scenario per process (so the default context and the native loader start clean).
// Usage: Host <out-dir> <native-dir> <scenario>
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

var outDir = Path.GetFullPath(args[0]);
var nativeDir = Path.GetFullPath(args[1]);
var scenario = args[2];
string P(string name) => Path.Combine(outDir, name, name + ".dll");
string D1 = Path.Combine(outDir, "D1", "D.dll"), D2 = Path.Combine(outDir, "D2", "D.dll");
string EPath = P("E"), FPath = P("F");

int fails = 0;
void Check(string label, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}"); if (!ok) fails++; }
void Say(string s) => Console.WriteLine("  " + s);

// Calls a static method by reflection; returns the result or a one-line description of the exception chain.
string Call(Assembly asm, string type, string method, params object[] a)
{
    try { return "OK: " + asm.GetType(type, true)!.GetMethod(method)!.Invoke(null, a); }
    catch (Exception ex) { return "EX: " + Describe(ex); }
}
string Describe(Exception ex)
{
    if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
    return $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
}
string Try(Func<object?> f) { try { return "OK: " + f(); } catch (Exception ex) { return "EX: " + Describe(ex); } }
void Dump(Compartment c) => Say($"compartment '{c.Name}' assemblies: [{string.Join(", ", c.Assemblies.Select(a => a.GetName().Name + " " + a.GetName().Version))}]");

Console.WriteLine($"=== {scenario} ===");
Say($"default context already has D? {AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == "D")} (host references D 1.0 => it is in the TPA list either way)");

switch (scenario)
{
// ---------------------------------------------------------------- Q1
case "q1-loadfrompath":
{
    var c = new Compartment("q1a");
    var d1 = c.LoadFromAssemblyPath(D1);
    Say($"loaded {d1.FullName}");
    var r = Try(() => c.LoadFromAssemblyPath(D2).FullName);
    Say($"LoadFromAssemblyPath(D 2.0) -> {r}");
    Check("loading D 2.0 into a compartment that holds D 1.0 throws FileLoadException", r.StartsWith("EX: FileLoadException"));
    var again = Try(() => c.LoadFromAssemblyPath(D1) == d1);
    Say($"LoadFromAssemblyPath(D 1.0 same path) again -> {again}");
    var byName = Try(() => c.LoadFromAssemblyName(new AssemblyName("D")).FullName);
    Say($"LoadFromAssemblyName(D) -> {byName}");
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    var use = Call(a, "PluginA.A", "UseD");
    Say($"after the failure, PluginA.UseD -> {use}");
    Check("compartment still usable after the failed load", use.Contains("D 1.0"));
    Dump(c);
    break;
}
case "q1-load-override":
{
    var c = new Compartment("q1b");
    c.Private["D"] = D1;
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD -> " + Call(a, "PluginA.A", "UseD"));
    // Hot install: plugin B needs D 2.0; the table is changed to point D at 2.0.
    c.Private["D"] = D2;
    var b = c.LoadFromAssemblyPath(P("PluginB"));
    var r = Call(b, "PluginB.B", "UseD");
    Say("PluginB.UseD (needs D 2.0, Load returns LoadFromAssemblyPath(D2)) -> " + r);
    Check("binding D 2.0 through Load when D 1.0 is already in the compartment fails", r.StartsWith("EX:"));
    var r2 = Call(b, "PluginB.B", "UseD");
    Say("retry PluginB.UseD -> " + r2);
    Say("PluginA.UseDLater (new method in A, still references D 1.0) -> " + Call(a, "PluginA.A", "UseDLater"));
    Dump(c);
    break;
}
case "q1-return-existing-lower":
{
    // Load returns the already-loaded D 1.0 for a request for D 2.0.
    var c = new Compartment("q1c");
    c.Private["D"] = D1;
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD -> " + Call(a, "PluginA.A", "UseD"));
    var b = c.LoadFromAssemblyPath(P("PluginB"));
    var r = Call(b, "PluginB.B", "UseD");
    Say("PluginB.UseD (requests D 2.0.0.0, Load hands back D 1.0) -> " + r);
    var r2 = Call(b, "PluginB.B", "UseNewIn2");
    Say("PluginB.UseNewIn2 -> " + r2);
    // Hypothesis was "the runtime rejects it". Observed: it does NOT. The check asserts the observed (negative) behaviour.
    Check("NEGATIVE: the runtime silently accepts a LOWER version returned by Load (failure surfaces later as MissingMethodException)", r.Contains("D 1.0") && r2.Contains("MissingMethodException"));
    Dump(c);
    break;
}
case "q1-return-existing-higher":
{
    // D 2.0 loaded first (plugin B), then plugin A that was compiled against D 1.0.
    var c = new Compartment("q1d");
    c.Private["D"] = D2;
    var b = c.LoadFromAssemblyPath(P("PluginB"));
    Say("PluginB.UseNewIn2 -> " + Call(b, "PluginB.B", "UseNewIn2"));
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    var r = Call(a, "PluginA.A", "UseD");
    Say("PluginA.UseD (requests D 1.0.0.0, gets D 2.0 already loaded) -> " + r);
    Check("a higher already-loaded version satisfies a lower request", r.Contains("D 2.0"));
    Dump(c);
    break;
}
case "q1-same-version-second-plugin":
{
    var c = new Compartment("q1e");
    c.Private["D"] = D1;
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD -> " + Call(a, "PluginA.A", "UseD"));
    var b1 = c.LoadFromAssemblyPath(P("PluginB1"));
    var r = Call(b1, "PluginB1.B1", "UseD");
    Say("PluginB1.UseD (also D 1.0) -> " + r);
    Check("second plugin needing the same D version shares it", r.Contains("D 1.0") && r.Contains("'q1e'"));
    Say("Load calls: " + string.Join(" | ", c.Log));
    break;
}
// ---------------------------------------------------------------- Q2
case "q2-default-then-private":
{
    var c = new Compartment("q2");
    // D is host-shared: Load returns null -> default context's D 1.0.
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    var r0 = Call(a, "PluginA.A", "UseD");
    Say("PluginA.UseD -> " + r0);
    Check("D resolved from the default context", r0.Contains("'Default'"));
    // Hot install: plugin B needs D 2.0 which the host doesn't have -> D becomes compartment-private.
    c.Private["D"] = D2;
    Say("--- table changed: D -> private D 2.0 ---");
    int before = c.Log.Count;
    var rA = Call(a, "PluginA.A", "UseDLater");
    Say("PluginA.UseDLater (not yet JIT-compiled, same assembly) -> " + rA + $"   [Load calls: {c.Log.Count - before}]");
    before = c.Log.Count;
    var b1 = c.LoadFromAssemblyPath(P("PluginB1"));
    var rB1 = Call(b1, "PluginB1.B1", "UseD");
    Say("PluginB1.UseD (new assembly, requests D 1.0.0.0) -> " + rB1 + $"   [Load calls: {c.Log.Count - before}]");
    before = c.Log.Count;
    var b = c.LoadFromAssemblyPath(P("PluginB"));
    var rB = Call(b, "PluginB.B", "UseNewIn2");
    Say("PluginB.UseNewIn2 (new assembly, requests D 2.0.0.0) -> " + rB + $"   [Load calls: {c.Log.Count - before}]");
    before = c.Log.Count;
    var byName = Try(() => { var x = c.LoadFromAssemblyName(new AssemblyName("D")); return x.GetName().Version + " in " + AssemblyLoadContext.GetLoadContext(x)!.Name; });
    Say("c.LoadFromAssemblyName(\"D\") -> " + byName + $"   [Load calls: {c.Log.Count - before}]");
    Check("A keeps the host's D (its references are already bound)", rA.Contains("'Default'"));
    Dump(c);
    Say("Load calls: " + string.Join(" | ", c.Log));
    // Type identity: is BaseThing seen by A the same type as BaseThing seen by B?
    var tA = a.GetType("PluginA.ADerived")!.BaseType!;
    var tB = Try(() => b.GetReferencedAssemblies().First(n => n.Name == "D"));
    Say($"A's BaseThing comes from ALC '{AssemblyLoadContext.GetLoadContext(tA.Assembly)!.Name}' v{tA.Assembly.GetName().Version}");
    break;
}
case "q2-default-then-private-unversioned-first":
{
    // Same as above, but the first request for D in the compartment is by the plugin needing 2.0.
    var c = new Compartment("q2b");
    var b1 = c.LoadFromAssemblyPath(P("PluginB1"));
    Say("PluginB1.UseD (D 1.0 -> default) -> " + Call(b1, "PluginB1.B1", "UseD"));
    c.Private["D"] = D2;
    var b = c.LoadFromAssemblyPath(P("PluginB"));
    Say("PluginB.UseNewIn2 (D 2.0 -> private) -> " + Call(b, "PluginB.B", "UseNewIn2"));
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD (D 1.0, new assembly, table now says private 2.0) -> " + Call(a, "PluginA.A", "UseD"));
    Dump(c);
    Say("Load calls: " + string.Join(" | ", c.Log));
    break;
}
case "q2-private-then-default":
{
    // Opposite direction: the compartment had D private (1.0); a new plugin wants D from the host.
    var c = new Compartment("q2c");
    c.Private["D"] = D1;
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD -> " + Call(a, "PluginA.A", "UseD"));
    c.Private.Remove("D");
    var b1 = c.LoadFromAssemblyPath(P("PluginB1"));
    var r = Call(b1, "PluginB1.B1", "UseD");
    Say("PluginB1.UseD after removing D from the private table -> " + r);
    Check("once loaded in the compartment, the compartment's own copy wins (Load not consulted)", r.Contains("'q2c'"));
    Say("Load calls: " + string.Join(" | ", c.Log));
    break;
}
// ---------------------------------------------------------------- Q3
case "q3-failure-then-available":
{
    var c = new Compartment("q3");
    var cAsm = c.LoadFromAssemblyPath(P("PluginC"));
    var r1 = Call(cAsm, "PluginC.C", "UseE");
    Say("PluginC.UseE with E unknown -> " + r1);
    Check("unknown dependency -> FileNotFoundException", r1.Contains("FileNotFoundException"));
    var r1b = Call(cAsm, "PluginC.C", "UseE");
    Say("retry PluginC.UseE while still unknown -> " + r1b + $"   [Load calls so far: {c.Log.Count}]");
    c.Private["E"] = EPath;
    Say("--- table changed: E -> private ---");
    int before = c.Log.Count;
    var r2 = Call(cAsm, "PluginC.C", "UseE");
    Say("retry PluginC.UseE (same method) -> " + r2 + $"   [Load calls: {c.Log.Count - before}]");
    before = c.Log.Count;
    var r3 = Call(cAsm, "PluginC.C", "UseEOther");
    Say("PluginC.UseEOther (other method, same assembly) -> " + r3 + $"   [Load calls: {c.Log.Count - before}]");
    before = c.Log.Count;
    var c2 = c.LoadFromAssemblyPath(P("PluginC2"));
    var r4 = Call(c2, "PluginC2.C2", "UseE");
    Say("PluginC2.UseE (new assembly) -> " + r4 + $"   [Load calls: {c.Log.Count - before}]");
    Check("compartment recovers once E becomes resolvable", r2.StartsWith("OK") && r4.StartsWith("OK"));
    Dump(c);
    Say("Load calls: " + string.Join(" | ", c.Log));
    break;
}
case "q3-failure-loadfromname":
{
    var c = new Compartment("q3b");
    var r1 = Try(() => c.LoadFromAssemblyName(new AssemblyName("E")).FullName);
    Say("c.LoadFromAssemblyName(E) unknown -> " + r1);
    c.Private["E"] = EPath;
    var r2 = Try(() => c.LoadFromAssemblyName(new AssemblyName("E")).FullName);
    Say("c.LoadFromAssemblyName(E) after table change -> " + r2);
    Check("failure not cached for LoadFromAssemblyName", r2.StartsWith("OK"));
    Say("Load calls: " + string.Join(" | ", c.Log));
    break;
}
case "q3-load-throws":
{
    // Load throws (instead of returning null) the first time.
    var c = new Compartment("q3c") { ThrowFor = "E" };
    var cAsm = c.LoadFromAssemblyPath(P("PluginC"));
    Say("PluginC.UseE while Load throws -> " + Call(cAsm, "PluginC.C", "UseE"));
    c.ThrowFor = null; c.Private["E"] = EPath;
    var r = Call(cAsm, "PluginC.C", "UseE");
    Say("retry after fixing -> " + r);
    Check("recovers after Load threw", r.StartsWith("OK"));
    Say("Load calls: " + string.Join(" | ", c.Log));
    break;
}
case "q3-resolving-event":
{
    // The Resolving event is the last resort after Load + default both fail; failures there aren't cached either?
    var c = new Compartment("q3d");
    var cAsm = c.LoadFromAssemblyPath(P("PluginC"));
    int resolvingCalls = 0;
    c.Resolving += (ctx, n) => { resolvingCalls++; return null; };
    Say("PluginC.UseE -> " + Call(cAsm, "PluginC.C", "UseE") + $" [Resolving calls {resolvingCalls}]");
    Say("PluginC.UseE again -> " + Call(cAsm, "PluginC.C", "UseE") + $" [Resolving calls {resolvingCalls}]");
    break;
}
// ---------------------------------------------------------------- Q4
case "q4-new-assemblies":
{
    var c = new Compartment("q4");
    c.Private["D"] = D1;
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD -> " + Call(a, "PluginA.A", "UseD"));
    c.Private["F"] = FPath;
    var g = c.LoadFromAssemblyPath(P("PluginG"));
    var r = Call(g, "PluginG.G", "UseF");
    Say("hot-added PluginG (+ new private F) -> " + r);
    Check("brand-new names can be added to a loaded compartment", r.StartsWith("OK"));
    // A private copy of a name the *default* context has loaded, never requested by this compartment.
    var c2 = new Compartment("q4b");
    var hostD = Try(() => typeof(DLib.Info).Assembly.GetName().Version);  // force D 1.0 into the default context
    Say("host forced D into default context: " + hostD);
    c2.Private["D"] = D2;
    var b = c2.LoadFromAssemblyPath(P("PluginB"));
    var r2 = Call(b, "PluginB.B", "UseNewIn2");
    Say("compartment-private D 2.0 while default has D 1.0 -> " + r2);
    Check("private copy alongside a different version in the default context works", r2.Contains("D 2.0") && r2.Contains("'q4b'"));
    // Same plugin assembly loaded twice from a different path (e.g. 'reinstall' of A under another folder).
    var copyDir = Path.Combine(Path.GetTempPath(), "spike-q4-copy"); Directory.CreateDirectory(copyDir);
    File.Copy(P("PluginA"), Path.Combine(copyDir, "PluginA.dll"), true);
    var r3 = Try(() => c.LoadFromAssemblyPath(Path.Combine(copyDir, "PluginA.dll")) == a);
    Say("loading PluginA again from another path into the same compartment -> " + r3);
    var r4 = Try(() => c.LoadFromAssemblyPath(P("PluginA")) == a);
    Say("loading PluginA again from the same path -> " + r4);
    break;
}
// ---------------------------------------------------------------- Q5
case "q5-what-triggers-resolution":
{
    (string what, Action<Assembly> act)[] probes =
    {
        ("load assembly only", _ => { }),
        ("call A.Self()", a => Call(a, "PluginA.A", "Self")),
        ("GetReferencedAssemblies()", a => a.GetReferencedAssemblies()),
        ("GetType(AWithParam) + GetMethods()", a => a.GetType("PluginA.AWithParam")!.GetMethods()),
        ("GetMethod(M).GetParameters()[0].ParameterType", a => _ = a.GetType("PluginA.AWithParam")!.GetMethod("M")!.GetParameters()[0].ParameterType),
        ("new AWithParam().Plain()", a => { var t = a.GetType("PluginA.AWithParam")!; t.GetMethod("Plain")!.Invoke(Activator.CreateInstance(t), null); }),
        ("GetType(\"PluginA.ADerived\") (base type in D)", a => a.GetType("PluginA.ADerived")),
        ("GetTypes() (what [Extension] scanning does)", a => a.GetTypes()),
        ("GetCustomAttributes on assembly", a => a.GetCustomAttributes(false)),
        ("call A.MaybeD(false) (D only in untaken branch)", a => Call(a, "PluginA.A", "MaybeD", false)),
        ("call A.UseD()", a => Call(a, "PluginA.A", "UseD")),
    };
    foreach (var (what, act) in probes)
    {
        var c = new Compartment("q5", quiet: true);
        var a = c.LoadFromAssemblyPath(P("PluginA"));
        act(a);
        var requested = c.Log.Any(l => l.StartsWith("Load(D,"));
        Say($"{what,-55} -> Load(D) requested: {requested}");
    }
    // Consequence: if D was never requested, the compartment can still pick a different D.
    var cc = new Compartment("q5-late");
    var aa = cc.LoadFromAssemblyPath(P("PluginA"));
    Say("A loaded, A.Self() -> " + Call(aa, "PluginA.A", "Self"));
    cc.Private["D"] = D2;  // hot install of B makes D private 2.0 before anyone asked for D
    var bb = cc.LoadFromAssemblyPath(P("PluginB"));
    Say("PluginB.UseNewIn2 -> " + Call(bb, "PluginB.B", "UseNewIn2"));
    var r = Call(aa, "PluginA.A", "UseD");
    Say("PluginA.UseD (compiled against 1.0) -> " + r);
    Check("an unrequested name can still be decided late (A gets 2.0, no clash)", r.Contains("D 2.0"));
    break;
}
// ---------------------------------------------------------------- Q6
case "q6-native-remap":
{
    var c = new Compartment("q6a");
    c.Native["nat"] = Path.Combine(nativeDir, "v1", "libnat.so");
    var n1 = c.LoadFromAssemblyPath(P("PluginN1"));
    Say("N1.Version -> " + Call(n1, "PluginN1.N1", "Version"));
    c.Native["nat"] = Path.Combine(nativeDir, "v2", "libnat.so");
    Say("--- native table changed: nat -> v2 ---");
    int before = c.Log.Count;
    Say("N1.Version again (already bound P/Invoke) -> " + Call(n1, "PluginN1.N1", "Version") + $"   [LoadUnmanagedDll calls: {c.Log.Count - before}]");
    before = c.Log.Count;
    var n2 = c.LoadFromAssemblyPath(P("PluginN2"));
    var r = Call(n2, "PluginN2.N2", "Version");
    Say("N2.Version (new assembly, same compartment) -> " + r + $"   [LoadUnmanagedDll calls: {c.Log.Count - before}]");
    Say("Native calls: " + string.Join(" | ", c.Log));
    break;
}
case "q6-native-already-in-process":
{
    var h1 = NativeLibrary.Load(Path.Combine(nativeDir, "v1", "libnat.so"));
    Say($"default/host loaded v1 libnat.so handle=0x{h1:x}");
    var c = new Compartment("q6b");
    c.Native["nat"] = Path.Combine(nativeDir, "v2", "libnat.so");
    var n1 = c.LoadFromAssemblyPath(P("PluginN1"));
    var r = Call(n1, "PluginN1.N1", "Version");
    Say("compartment maps nat -> v2 by full path; N1.Version -> " + r);
    Check("a second copy (different file, same soname) loads side by side when loaded by full path", r == "OK: 2");
    // Now the soname trap: natuser (v2 dir) has DT_NEEDED libnat.so with RUNPATH $ORIGIN.
    var c2 = new Compartment("q6c");
    c2.Native["natuser"] = Path.Combine(nativeDir, "v2", "libnatuser.so");
    var n3 = c2.LoadFromAssemblyPath(P("PluginN3"));
    var r2 = Call(n3, "PluginN3.N3", "Version");
    Say("libnatuser.so (v2 dir, DT_NEEDED libnat.so) sees nat_version -> " + r2);
    Check("DT_NEEDED dependency resolves to the copy already loaded with the same soname (v1), not the one next to it", r2 == "OK: 1");
    break;
}
case "q6-native-soname-control":
{
    // Control for the above: nothing loaded beforehand.
    var c2 = new Compartment("q6d");
    c2.Native["natuser"] = Path.Combine(nativeDir, "v2", "libnatuser.so");
    var n3 = c2.LoadFromAssemblyPath(P("PluginN3"));
    var r2 = Call(n3, "PluginN3.N3", "Version");
    Say("libnatuser.so (v2 dir) with nothing loaded before -> " + r2);
    Check("control: picks the libnat.so next to it (v2)", r2 == "OK: 2");
    break;
}
case "q6-native-failure-then-available":
{
    var c = new Compartment("q6e");
    var n1 = c.LoadFromAssemblyPath(P("PluginN1"));
    var r1 = Call(n1, "PluginN1.N1", "Version");
    Say("N1.Version with nat unknown -> " + r1);
    c.Native["nat"] = Path.Combine(nativeDir, "v1", "libnat.so");
    int before = c.Log.Count;
    var r2 = Call(n1, "PluginN1.N1", "Version");
    Say("retry N1.Version after mapping nat -> " + r2 + $"   [LoadUnmanagedDll calls: {c.Log.Count - before}]");
    before = c.Log.Count;
    var n2 = c.LoadFromAssemblyPath(P("PluginN2"));
    var r3 = Call(n2, "PluginN2.N2", "Version");
    Say("N2.Version (new assembly) -> " + r3 + $"   [LoadUnmanagedDll calls: {c.Log.Count - before}]");
    Check("native failures are not cached", r2 == "OK: 1" && r3 == "OK: 1");
    break;
}
// ---------------------------------------------------------------- extra controls
case "q2-null-answer-not-cached":
{
    // Control: table unchanged. Does the runtime ask again for D when a *new* assembly in the compartment needs it?
    var c = new Compartment("q2n");
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD -> " + Call(a, "PluginA.A", "UseD"));
    int before = c.Log.Count(l => l.StartsWith("Load(D,"));
    var b1 = c.LoadFromAssemblyPath(P("PluginB1"));
    Say("PluginB1.UseD -> " + Call(b1, "PluginB1.B1", "UseD"));
    var after = c.Log.Count(l => l.StartsWith("Load(D,"));
    Check("a null (default) answer is NOT remembered per compartment: Load(D) is asked again for each new referencing assembly", after == before + 1);
    break;
}
case "q2-split-identity":
{
    var c = new Compartment("q2s");
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    var baseSeenByA = a.GetType("PluginA.ADerived")!.BaseType!;          // binds A's reference to D -> default
    c.Private["D"] = D2;
    var b = c.LoadFromAssemblyPath(P("PluginB"));
    Say("PluginB.UseD -> " + Call(b, "PluginB.B", "UseD"));
    var dInCompartment = c.Assemblies.First(x => x.GetName().Name == "D");
    var baseSeenByB = dInCompartment.GetType("DLib.BaseThing")!;
    Say($"A sees DLib.BaseThing from '{AssemblyLoadContext.GetLoadContext(baseSeenByA.Assembly)!.Name}' {baseSeenByA.Assembly.GetName().Version}; B sees it from '{AssemblyLoadContext.GetLoadContext(baseSeenByB.Assembly)!.Name}' {baseSeenByB.Assembly.GetName().Version}");
    Check("two plugins in ONE compartment now disagree on DLib.BaseThing (type identity split, no error raised)", baseSeenByA != baseSeenByB);
    var obj = Activator.CreateInstance(a.GetType("PluginA.ADerived")!)!;
    Say($"an A object is-a B's BaseThing? {baseSeenByB.IsInstanceOfType(obj)}");
    break;
}
case "q4-same-version-different-file":
{
    // D1b: assembly version 1.0.0.0 but different contents (has NewIn2, reports "2.0") - e.g. two package versions sharing an AssemblyVersion.
    var c = new Compartment("q4s");
    var first = c.LoadFromAssemblyPath(D1);
    var r = Try(() => { var x = c.LoadFromAssemblyPath(Path.Combine(outDir, "D1b", "D.dll")); return $"same instance as first: {x == first}, location {x.Location.Split('/')[^2]}"; });
    Say("LoadFromAssemblyPath(D1b: same name+version, different file) -> " + r);
    // Contrast with q4-new-assemblies: a byte-identical copy from another path returns the existing instance.
    Check("same name+version but different contents (different MVID) -> FileLoadException, like a version clash", r.StartsWith("EX: FileLoadException"));
    break;
}
case "q4-lower-path-after-higher":
{
    var c = new Compartment("q4l");
    c.LoadFromAssemblyPath(D2);
    var r = Try(() => c.LoadFromAssemblyPath(D1).GetName().Version);
    Say("LoadFromAssemblyPath(D 1.0) when D 2.0 is loaded -> " + r);
    break;
}
case "rule-demo":
{
    // Simulates install-time evaluation against a compartment with plugin A loaded.
    var c = new RecordingCompartment("rule");
    c.Commit(new Dictionary<string, Answer> { ["PluginA"] = new(Source.Private, new(1, 0, 0, 0), P("PluginA")), ["D"] = new(Source.Default) });
    var a = c.LoadFromAssemblyPath(P("PluginA"));
    Say("PluginA.UseD -> " + Call(a, "PluginA.A", "UseD"));
    var cAsm = c.LoadFromAssemblyPath(P("PluginC"));      // C is loaded, E not planned -> fails, recorded as Failed
    c.Commit(new Dictionary<string, Answer> { ["PluginC"] = new(Source.Private, new(1, 0, 0, 0), P("PluginC")) });
    Say("PluginC.UseE (E unknown) -> " + Call(cAsm, "PluginC.C", "UseE"));
    Say("committed: " + string.Join(", ", c.Committed.Where(kv => !kv.Key.StartsWith("System")).Select(kv => $"{kv.Key}={kv.Value}")));
    var cases = new (string label, Dictionary<string, Answer> wanted, bool expectHot)[]
    {
        ("B1: needs D 1.0 from host (same answer)", new() { ["PluginB1"] = new(Source.Private, new(1,0,0,0), P("PluginB1")), ["D"] = new(Source.Default) }, true),
        ("B: needs D 2.0 private (host has 1.0)", new() { ["PluginB"] = new(Source.Private, new(1,0,0,0), P("PluginB")), ["D"] = new(Source.Private, new(2,0,0,0), D2) }, false),
        ("G: needs brand-new F", new() { ["PluginG"] = new(Source.Private, new(1,0,0,0), P("PluginG")), ["F"] = new(Source.Private, new(1,0,0,0), FPath) }, true),
        ("C2: needs E (earlier request for E failed)", new() { ["PluginC2"] = new(Source.Private, new(1,0,0,0), P("PluginC2")), ["E"] = new(Source.Private, new(1,0,0,0), EPath) }, true),
    };
    foreach (var (label, wanted, expectHot) in cases)
    {
        var v = HotLoadRule.Check(c.Committed, wanted);
        Say($"{label,-45} -> {v}");
        Check($"rule verdict for {label.Split(':')[0]}", v.HotLoad == expectHot);
    }
    // Apply the two hot-loadable installs and prove they actually work.
    var wantG = cases[2].wanted; c.Commit(wantG);
    var g = c.LoadFromAssemblyPath(P("PluginG"));
    Say("hot-loaded G -> " + Call(g, "PluginG.G", "UseF"));
    c.Commit(cases[3].wanted);
    var c2 = c.LoadFromAssemblyPath(P("PluginC2"));
    var r = Call(c2, "PluginC2.C2", "UseE");
    Say("hot-loaded C2 -> " + r + "; and the earlier-failing C.UseE now -> " + Call(cAsm, "PluginC.C", "UseE"));
    Check("hot loads allowed by the rule work", r.StartsWith("OK"));
    // Safety net: someone bypasses the rule and commits B's plan anyway.
    c.Commit(cases[1].wanted);
    var b = c.LoadFromAssemblyPath(P("PluginB"));
    var rb = Call(b, "PluginB.B", "UseNewIn2");
    Say("bypassing the rule: B.UseNewIn2 -> " + rb);
    Check("frozen answers prevent a silent split (B fails loudly instead of getting a second D)", rb.StartsWith("EX:") && !c.Assemblies.Any(x => x.GetName().Name == "D"));
    Say("committed D after the bypass attempt: " + c.Committed["D"]);
    Check("D stays frozen at 'default' after the failed bypass", c.Committed["D"].Source == Source.Default);
    foreach (var l in c.Log.Where(l => !l.Contains("System."))) Say("log: " + l);
    break;
}
default:
    Console.WriteLine("unknown scenario"); return 2;
}
Console.WriteLine(fails == 0 ? "  RESULT: all checks PASS" : $"  RESULT: {fails} check(s) FAIL");
return 0;

/// <summary>A compartment whose resolution table can change at runtime (simulating a hot install).</summary>
class Compartment : AssemblyLoadContext
{
    public Dictionary<string, string> Private { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Native { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = new();
    public string? ThrowFor { get; set; }
    private readonly bool _quiet;
    public Compartment(string name, bool quiet = false) : base(name, isCollectible: false) => _quiet = quiet;

    protected override Assembly? Load(AssemblyName name)
    {
        string answer;
        Assembly? result = null;
        try
        {
            if (ThrowFor != null && name.Name == ThrowFor) { answer = "throw InvalidOperationException"; throw new InvalidOperationException("Load refused " + name.Name); }
            if (Private.TryGetValue(name.Name!, out var path))
            {
                result = LoadFromAssemblyPath(path);
                answer = $"private {result.GetName().Version} ({path.Split('/')[^2]})";
            }
            else answer = "null -> default context";
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            answer = $"threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
            Record(name, answer);
            throw;
        }
        catch (InvalidOperationException) { Record(name, "threw InvalidOperationException"); throw; }
        Record(name, answer);
        return result;
    }

    private void Record(AssemblyName name, string answer)
    {
        var line = $"Load({name.Name}, {name.Version}) -> {answer}";
        Log.Add(line);
        if (!_quiet) Console.WriteLine("    [" + Name + "] " + line);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string line;
        IntPtr h = IntPtr.Zero;
        if (Native.TryGetValue(unmanagedDllName, out var path)) { h = NativeLibrary.Load(path); line = $"LoadUnmanagedDll({unmanagedDllName}) -> {path.Split('/')[^2]}/{Path.GetFileName(path)} handle=0x{h:x}"; }
        else line = $"LoadUnmanagedDll({unmanagedDllName}) -> IntPtr.Zero (unknown)";
        Log.Add(line);
        if (!_quiet) Console.WriteLine("    [" + Name + "] " + line);
        return h;
    }
}
