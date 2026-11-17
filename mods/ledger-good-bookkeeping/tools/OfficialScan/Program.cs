using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

// Build-time host for the installed game's scanner; excluded from mod packages.
if (args.Length < 2) throw new ArgumentException("Usage: OfficialScan <Managed directory> <ledger package> [report.json]");
var managed = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
return Scan(managed, Path.GetFullPath(args[1]), args.Length > 2 ? args[2] : null);

[MethodImpl(MethodImplOptions.NoInlining)]
static int Scan(string managed, string folder, string? output)
{
    var manifest = XDocument.Load(Path.Combine(folder, "manifest.xml")).Root!;
    if (manifest.Element("ID")?.Value != "DeepSpacePawnshopLedger") throw new ArgumentException("Not a ledger package");
    var dll = Path.Combine(folder, "DeepSpacePawnshopLedger.dll");
    var declared = manifest.Element("Capabilities")!.Elements("Capability").Select(e => e.Value).ToArray();
    var allowed = System.IO.Directory.GetFiles(managed, "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
    var result = ModScanner.Scan(new[] { dll }, allowed, folder, false);
    var json = JsonSerializer.Serialize(new {
        ScannerVersion = ModScanner.VERSION, result.MethodsScanned, result.Error,
        ObservedUtc = DateTime.UtcNow.ToString("O"),
        CandidateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))),
        GameAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(managed, "Assembly-CSharp.dll")))),
        DeclaredCapabilities = declared,
        Capabilities = result.Capabilities.ToArray(),
        Findings = result.Findings.Select(f => f.ToString()).ToArray(),
        HardFlags = result.HardFlags.Select(f => f.ToString()).ToArray(),
        Pending = result.Pending.Select(f => f.ToString()).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (output is not null) File.WriteAllText(output, json);
    // This installed scanner reports the existing SaveFiles patch as a pending
    // files rule. Accept only that already-declared capability; retain it in
    // the report rather than hiding future enforcement findings.
    return result.Error == null && result.HardFlags.Count == 0 &&
        declared.SequenceEqual(new[] { "files" }) && result.Capabilities.All(c => c == "files") &&
        result.Pending.All(f => f.ToString().StartsWith("files:", StringComparison.Ordinal)) ? 0 : 1;
}
