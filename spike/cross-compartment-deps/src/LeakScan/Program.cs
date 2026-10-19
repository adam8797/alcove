// Static "exposed dependency" scanner using only System.Reflection.Metadata (in-box, no loading).
using System.Reflection.Metadata.Ecma335;
//   LeakScan surface <provider.dll> [more.dll ...]          -> which referenced assemblies appear in the PUBLIC surface, and where
//   LeakScan consumer <consumer.dll> <provider.dll>          -> which of the provider's members the consumer calls whose signatures mention which assemblies
//   LeakScan bench <dll> ...                                 -> timing only
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var mode = args[0];
if (mode == "surface" || mode == "bench")
{
    foreach (var path in args.Skip(1))
    {
        var sw = Stopwatch.StartNew();
        var (exposed, stats) = SurfaceScanner.Scan(path);
        sw.Stop();
        Console.WriteLine($"## {Path.GetFileName(path)}  ({new FileInfo(path).Length / 1024} KiB): {stats}, {exposed.Count} exposed assemblies, {sw.Elapsed.TotalMilliseconds:F1} ms");
        if (mode == "bench") { Console.WriteLine("   exposed: " + string.Join(", ", exposed.Keys.Order())); continue; }
        foreach (var (asm, where) in exposed.OrderBy(k => k.Key))
        {
            Console.WriteLine($"   {asm}: {where.Count} site(s)");
            foreach (var w in where.Order()) Console.WriteLine($"      {w}");
        }
    }
}
else if (mode == "consumer")
{
    var sw = Stopwatch.StartNew();
    var hits = ConsumerScanner.Scan(args[1], Path.GetFileNameWithoutExtension(args[2]));
    sw.Stop();
    Console.WriteLine($"## {Path.GetFileName(args[1])} using {Path.GetFileName(args[2])}: {hits.Count} member/type references mention other assemblies ({sw.Elapsed.TotalMilliseconds:F1} ms)");
    foreach (var h in hits.Order()) Console.WriteLine("   " + h);
}

/// <summary>Collects the assembly names of every TypeReference reached while decoding a signature.</summary>
sealed class ScopeCollector(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    public HashSet<string> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ScopeOf(TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        var scope = tr.ResolutionScope;
        return scope.Kind switch
        {
            HandleKind.AssemblyReference => md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
            HandleKind.TypeReference => ScopeOf((TypeReferenceHandle)scope),
            _ => "", // this module
        };
    }
    public string Name(TypeReferenceHandle h) { var tr = md.GetTypeReference(h); return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name); }

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { var s = ScopeOf(h); if (s != "") Assemblies.Add(s); return $"[{s}]{Name(h)}"; }
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => md.GetString(md.GetTypeDefinition(h).Name);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetPinnedType(string e) => e;
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool req) => u;

    /// <summary>Visit a TypeDef/TypeRef/TypeSpec handle (base types, interfaces, constraints, event types).</summary>
    public void Visit(EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeReference: GetTypeFromReference(md, (TypeReferenceHandle)h, 0); break;
            case HandleKind.TypeSpecification: md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null); break;
        }
    }
}

static class SurfaceScanner
{
    public static (Dictionary<string, List<string>> exposed, string stats) Scan(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var exposed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int types = 0, members = 0;

        void Record(ScopeCollector c, string site)
        {
            foreach (var a in c.Assemblies) { if (!exposed.TryGetValue(a, out var l)) exposed[a] = l = []; l.Add(site); }
        }
        ScopeCollector New() => new(md);

        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (!IsVisible(md, td)) continue;
            types++;
            var tn = md.GetString(td.Namespace) + "." + md.GetString(td.Name);
            var c = New();
            if (!td.BaseType.IsNil) { c.Visit(td.BaseType); Record(c, $"{tn} : base type"); }
            foreach (var ih in td.GetInterfaceImplementations()) { c = New(); c.Visit(md.GetInterfaceImplementation(ih).Interface); Record(c, $"{tn} : interface"); }
            foreach (var gp in td.GetGenericParameters()) foreach (var ch in md.GetGenericParameter(gp).GetConstraints()) { c = New(); c.Visit(md.GetGenericParameterConstraint(ch).Type); Record(c, $"{tn} generic constraint"); }
            foreach (var ca in td.GetCustomAttributes()) { c = New(); VisitAttribute(md, c, ca); Record(c, $"{tn} [attribute]"); }

            bool sealedType = (td.Attributes & TypeAttributes.Sealed) != 0;
            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                var acc = f.Attributes & FieldAttributes.FieldAccessMask;
                if (!(acc == FieldAttributes.Public || (!sealedType && (acc == FieldAttributes.Family || acc == FieldAttributes.FamORAssem)))) continue;
                members++; c = New(); f.DecodeSignature(c, null); Record(c, $"{tn}.{md.GetString(f.Name)} (field)");
            }
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var acc = m.Attributes & MethodAttributes.MemberAccessMask;
                if (!(acc == MethodAttributes.Public || (!sealedType && (acc == MethodAttributes.Family || acc == MethodAttributes.FamORAssem)))) continue;
                members++; c = New(); m.DecodeSignature(c, null);
                foreach (var gp in m.GetGenericParameters()) foreach (var ch in md.GetGenericParameter(gp).GetConstraints()) c.Visit(md.GetGenericParameterConstraint(ch).Type);
                Record(c, $"{tn}.{md.GetString(m.Name)}() (method)");
            }
            foreach (var ph in td.GetProperties())
            {   // accessors are already covered as methods; properties checked anyway for clearer site names
                var p = md.GetPropertyDefinition(ph); var acc = p.GetAccessors();
                var getter = acc.Getter.IsNil ? default(MethodAttributes?) : md.GetMethodDefinition(acc.Getter).Attributes & MethodAttributes.MemberAccessMask;
                if (getter != MethodAttributes.Public) continue;
                c = New(); p.DecodeSignature(c, null); Record(c, $"{tn}.{md.GetString(p.Name)} (property)");
            }
            foreach (var eh in td.GetEvents())
            {
                var e = md.GetEventDefinition(eh); c = New(); c.Visit(e.Type); Record(c, $"{tn}.{md.GetString(e.Name)} (event)");
            }
        }
        // Type forwarders are part of the surface too.
        foreach (var eth in md.ExportedTypes)
        {
            var et = md.GetExportedType(eth);
            if (et.Implementation.Kind == HandleKind.AssemblyReference)
            {
                var a = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)et.Implementation).Name);
                if (!exposed.TryGetValue(a, out var l)) exposed[a] = l = [];
                l.Add($"{md.GetString(et.Namespace)}.{md.GetString(et.Name)} (type forwarder)");
            }
        }
        return (exposed, $"{types} visible types, {members} visible members");
    }

    static bool IsVisible(MetadataReader md, TypeDefinition td)
    {
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        if (vis == TypeAttributes.Public) return true;
        if (vis is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem)
            return IsVisible(md, md.GetTypeDefinition(td.GetDeclaringType()));
        return false;
    }

    public static void VisitAttribute(MetadataReader md, ScopeCollector c, CustomAttributeHandle h)
    {
        var ca = md.GetCustomAttribute(h);
        if (ca.Constructor.Kind == HandleKind.MemberReference) c.Visit(md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent);
    }
}

static class ConsumerScanner
{
    /// <summary>Lists the consumer's MemberReferences / TypeSpecs that target the provider and whose signatures mention other non-framework assemblies.</summary>
    public static List<string> Scan(string consumerPath, string provider)
    {
        using var fs = File.OpenRead(consumerPath);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var hits = new List<string>();
        foreach (var mrh in md.MemberReferences)
        {
            var mr = md.GetMemberReference(mrh);
            var c = new ScopeCollector(md);
            c.Visit(mr.Parent);
            if (!c.Assemblies.Contains(provider)) continue;
            var sig = mr.GetKind() == MemberReferenceKind.Method ? mr.DecodeMethodSignature(c, null).ReturnType : mr.DecodeFieldSignature(c, null);
            var others = c.Assemblies.Where(a => !a.Equals(provider, StringComparison.OrdinalIgnoreCase) && !IsFramework(a)).ToList();
            if (others.Count > 0) hits.Add($"{Describe(md, mr.Parent)}::{md.GetString(mr.Name)} mentions {string.Join(",", others)}");
        }
        foreach (var tsh in Enumerable.Range(1, md.GetTableRowCount(TableIndex.TypeSpec)).Select(i => MetadataTokens.TypeSpecificationHandle(i)))
        {
            var c = new ScopeCollector(md);
            var s = md.GetTypeSpecification(tsh).DecodeSignature(c, null);
            var others = c.Assemblies.Where(a => !a.Equals(provider, StringComparison.OrdinalIgnoreCase) && !IsFramework(a)).ToList();
            if (c.Assemblies.Contains(provider) && others.Count > 0) hits.Add($"typespec {s} mentions {string.Join(",", others)}");
        }
        return hits;
    }
    static bool IsFramework(string a) => a is "System.Runtime" or "netstandard" or "mscorlib" || a.StartsWith("System.") || a.StartsWith("Microsoft.");
    static string Describe(MetadataReader md, EntityHandle h)
    {
        var c = new ScopeCollector(md);
        return h.Kind switch
        {
            HandleKind.TypeReference => c.GetTypeFromReference(md, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(c, null),
            _ => h.Kind.ToString(),
        };
    }
}
