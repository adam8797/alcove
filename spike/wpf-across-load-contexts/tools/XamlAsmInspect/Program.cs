using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length == 0)
{
    Console.WriteLine("usage: XamlAsmInspect <assembly.dll>...");
    return 1;
}

var fullName = new Regex(@"[A-Za-z_][A-Za-z0-9_.]*, Version=\d+\.\d+\.\d+\.\d+, Culture=[A-Za-z\-]+, PublicKeyToken=[0-9a-fnul]+", RegexOptions.Compiled);

foreach (var path in args)
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader();
    var def = md.GetAssemblyDefinition();
    string name = md.GetString(def.Name);
    Console.WriteLine($"== {path}");
    Console.WriteLine($"   identity: {name}, Version={def.Version}");

    string gResName = name + ".g.resources";
    ManifestResource? gRes = null;
    foreach (var h in md.ManifestResources)
    {
        var r = md.GetManifestResource(h);
        if (md.GetString(r.Name) == gResName) gRes = r;
    }
    Console.WriteLine($"   has WPF resources ({gResName}): {(gRes is null ? "no" : "YES")}");
    if (gRes is not { } res) continue;

    // Embedded manifest resource: 4-byte length + data, at Resources directory + offset.
    var dir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
    pe.PEHeaders.TryGetDirectoryOffset(dir, out int dirOffset);
    var image = pe.GetEntireImage();
    var reader = image.GetReader(dirOffset + (int)res.Offset, 4);
    int len = reader.ReadInt32();
    var bytes = image.GetContent(dirOffset + (int)res.Offset + 4, len).ToArray();

    using var rr = new ResourceReader(new MemoryStream(bytes));
    var e = rr.GetEnumerator();
    var parts = new List<string>();
    while (e.MoveNext()) parts.Add((string)e.Key);
    foreach (var part in parts.Order())
    {
        rr.GetResourceData(part, out string type, out byte[] data);
        var text = Encoding.Latin1.GetString(data);
        // BAML strings are length-prefixed; the regex may swallow a length byte that happens to be a letter.
        var refs = fullName.Matches(text).Select(m => m.Value)
            .Select(v => v[0] == (char)(v.Length - 1) ? v[1..] : v).Distinct().ToList();
        Console.WriteLine($"   part {part} ({type}, {data.Length} bytes)");
        foreach (var r in refs) Console.WriteLine($"      BAML assembly record: {r}");
        if (part.EndsWith(".baml") && refs.Count == 0) Console.WriteLine("      (no full assembly names found)");
        // Show pack URIs embedded in the BAML (e.g. ResourceDictionary.Source values)
        foreach (Match m in Regex.Matches(text, @"/[A-Za-z0-9_.]+;(v[0-9.]+;)?component/[A-Za-z0-9_./]+", RegexOptions.IgnoreCase))
            Console.WriteLine($"      pack URI string in BAML: {m.Value}");
    }
}
return 0;
