using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

// Build-time host only; never included in the playable package.
if (args.Length < 2) throw new ArgumentException("Usage: OfficialScan <Managed directory> <package directory>");
string managed = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
return Scan(managed, Path.GetFullPath(args[1]), args.Length > 2 ? args[2] : null);

[MethodImpl(MethodImplOptions.NoInlining)]
static int Scan(string managed, string folder, string? outputPath)
{
    var dll = Path.Combine(folder, "ScavengeExtraction.dll");
    var allowed = System.IO.Directory.GetFiles(managed, "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
    var result = ModScanner.Scan(new[] { dll }, allowed, folder, false);
    var data = new
    {
        ScannerVersion = ModScanner.VERSION, result.MethodsScanned, result.Error,
        ObservedUtc = DateTime.UtcNow.ToString("O"),
        CandidateSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dll))),
        GameAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(managed, "Assembly-CSharp.dll")))),
        Capabilities = result.Capabilities.ToArray(),
        Findings = result.Findings.Select(f => f.ToString()).ToArray(),
        HardFlags = result.HardFlags.Select(f => f.ToString()).ToArray(),
        Pending = result.Pending.Select(f => f.ToString()).ToArray()
    };
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (outputPath != null) File.WriteAllText(outputPath, json);
    return result.Error == null && result.HardFlags.Count == 0 && result.Pending.Count == 0 && result.Capabilities.Count == 0 ? 0 : 1;
}
