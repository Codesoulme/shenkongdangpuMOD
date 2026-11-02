using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: FormalPatchHarness <mod dll> <game directory>");
    return 2;
}

var modPath = Path.GetFullPath(args[0]);
var managedDir = Path.Combine(Path.GetFullPath(args[1]), "Probably Stolen_Data", "Managed");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in new[] { Path.GetDirectoryName(modPath)!, managedDir })
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path))
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};

var modAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
var patchTypes = modAssembly.GetTypes()
    .Where(type => type.GetCustomAttributes<HarmonyPatch>(false).Any())
    .ToList();
var failures = new List<string>();
var methods = new HashSet<MethodBase>();

foreach (var patchType in patchTypes)
{
    var info = HarmonyMethodExtensions.GetMergedFromType(patchType);
    if (info.declaringType is null || string.IsNullOrWhiteSpace(info.methodName))
    {
        failures.Add($"{patchType.FullName}: target type or name is missing");
        continue;
    }

    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    var candidates = info.declaringType.GetMethods(flags)
        .Where(method => method.Name == info.methodName)
        .Where(method => info.argumentTypes is null ||
            method.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(info.argumentTypes))
        .ToList();
    if (candidates.Count != 1)
    {
        failures.Add($"{patchType.FullName}: expected one target, found {candidates.Count}");
        continue;
    }

    var target = candidates[0];
    methods.Add(target);
    foreach (var patchMethod in patchType.GetMethods(flags)
                 .Where(method => method.Name is "Prefix" or "Postfix" or "Finalizer"))
    {
        foreach (var parameter in patchMethod.GetParameters())
        {
            if (parameter.Name == "__instance" && target.IsStatic)
                failures.Add($"{patchType.FullName}.{patchMethod.Name}: static target has no instance");
            if (parameter.Name == "__result" && target.ReturnType == typeof(void))
                failures.Add($"{patchType.FullName}.{patchMethod.Name}: void target has no result");
            if (parameter.Name?.StartsWith("__", StringComparison.Ordinal) == true &&
                int.TryParse(parameter.Name.AsSpan(2), out var index) &&
                index >= target.GetParameters().Length)
                failures.Add($"{patchType.FullName}.{patchMethod.Name}: argument {parameter.Name} is out of range");
        }
    }
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine($"Formal patch metadata passed: {patchTypes.Count} patch classes, {methods.Count} game methods.");
return patchTypes.Count >= 15 ? 0 : 1;
