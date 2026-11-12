using System.Reflection;
using System.Runtime.Loader;
using System.Collections;
using System.Xml.Linq;
using System.Runtime.Serialization;
using HarmonyLib;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: FormalPatchHarness <mod dll> <game directory> [manifest.xml]");
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

if (args.Length == 3)
{
    var gameAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
        Path.Combine(managedDir, "Assembly-CSharp.dll"));
    CheckNativeLoader(gameAssembly, modPath, managedDir, args[2], failures);
    CheckStoryContext(modAssembly, gameAssembly, failures);
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine($"Formal patch metadata passed: {patchTypes.Count} patch classes, {methods.Count} game methods.");
return patchTypes.Count >= 15 ? 0 : 1;

static void CheckStoryContext(Assembly mod, Assembly game, ICollection<string> failures)
{
    const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
    object Empty(string type) => FormatterServices.GetUninitializedObject(game.GetType(type, true)!);
    void Set(object target, string name, object value) => target.GetType().GetField(name)!.SetValue(target, value);
    object Context() => Activator.CreateInstance(mod.GetType("AIWorldPawnshop.Models.GameConversationContext", true)!)!;
    IList Lines(object context, string name) => (IList)context.GetType().GetProperty(name)!.GetValue(context)!;
    var builder = mod.GetType("AIWorldPawnshop.Runtime.NativeConversationContextBuilder", true)!;
    var populateFacts = builder.GetMethod("PopulateOriginalStoryFacts", flags)!;
    var populateDialogue = builder.GetMethod("PopulateDialogueContext", flags)!;
    var store = Empty("PlayerStore");
    var manager = Empty("StoreClientManager");
    var arrivals = new List<string>();
    Set(manager, "arrestedClients", arrivals);
    Set(store, "storeClientManager", manager);

    foreach (var identifier in new[] { "wanted7", "wanted8_weekly" })
    {
        var client = Empty("StoreClient");
        Set(client, "identifier", identifier);
        Set(client, "isWanted", true);
        // Use the actual native arrival method: it writes arrestedClients even
        // though no report action has happened.
        game.GetType("PlayerStore", true)!.GetMethod("OnCriminalVisit")!.Invoke(store, new[] { client });
        if (!arrivals.Contains(identifier))
            failures.Add("Fixture failed: native criminal arrival did not register " + identifier);
        var context = Context();
        populateFacts.Invoke(null, new[] { context, store, client, identifier, "Test visitor" });
        if (Lines(context, "OriginalStoryFacts").Cast<string>().Any(line => line.Contains("曾被玩家交给治安部门")))
            failures.Add("Arrival was falsely described as a player arrest: " + identifier);
        var facts = Lines(context, "OriginalStoryFacts").Cast<string>().ToArray();
        if (!facts.Any(line => line.Contains("本次来访举报状态：False")))
            failures.Add("Current unreported state was lost: " + identifier);
        Set(client, "reported", true);
        var reported = Context();
        populateFacts.Invoke(null, new[] { reported, store, client, identifier, "Test visitor" });
        if (!Lines(reported, "OriginalStoryFacts").Cast<string>().Any(line => line.Contains("本次来访举报状态：True")))
            failures.Add("An actual current report was incorrectly suppressed: " + identifier);
    }

    object Dialogue(string text)
    {
        var dialogue = Empty("Dialogue");
        Set(dialogue, "plainText", text);
        return dialogue;
    }
    var visitor = Empty("StoreClient");
    var current = Dialogue("CURRENT_LINE");
    Set(current, "nextDialogue", Dialogue("FUTURE_BRANCH_NOT_OCCURRED"));
    Set(visitor, "mainDialogue", Dialogue("UNPLAYED_OPENING"));
    Set(visitor, "customOnArrestDialogue", Dialogue("UNTRIGGERED_ARREST"));
    Set(visitor, "interogationDialogue", Dialogue("UNTRIGGERED_INTERROGATION"));
    Set(visitor, "acceptDealDialogue", Dialogue("UNTRIGGERED_DEAL"));
    var active = Context();
    populateDialogue.Invoke(null, new[] { active, visitor, current });
    var activeLines = string.Join("\n", Lines(active, "OriginalDialogueContext").Cast<string>());
    if (!activeLines.Contains("CURRENT_LINE") || activeLines.Contains("NOT_OCCURRED") || activeLines.Contains("UNTRIGGERED"))
        failures.Add("Dialogue context includes an unplayed story branch or loses the current line.");
    var idle = Context();
    populateDialogue.Invoke(null, new[] { idle, visitor, null });
    if (Lines(idle, "OriginalDialogueContext").Count != 0)
        failures.Add("Idle dialogue context includes unplayed lines as personal history.");
    var arrested = Context();
    populateDialogue.Invoke(null, new[] { arrested, visitor, Dialogue("ACTUAL_ARREST_LINE") });
    if (!Lines(arrested, "OriginalDialogueContext").Cast<string>().Any(line => line.Contains("ACTUAL_ARREST_LINE")))
        failures.Add("A genuinely active arrest dialogue was incorrectly removed.");
    var lunFacts = new List<string>();
    var story = Empty("StoreClientData");
    Set(story, "officerLunArrested", true);
    builder.GetMethod("AddCharacterSpecificStoryFacts", flags)!.Invoke(null,
        new object[] { lunFacts, "officerlun", story, store });
    if (!lunFacts.Any(line => line.Contains("是否曾被逮捕 True")))
        failures.Add("A confirmed native character story fact was incorrectly removed.");
    Console.WriteLine("Native story provenance checked: criminal arrivals and unplayed dialogue branches.");
}

static void CheckNativeLoader(Assembly game, string modPath, string managedDir,
    string manifestPath, ICollection<string> failures)
{
    var manifest = XDocument.Load(manifestPath).Root!;
    var declared = manifest.Element("Capabilities")!.Elements("Capability")
        .Select(element => element.Value).ToHashSet(StringComparer.Ordinal);
    var retiring = new[] { "dynamic-code", "reflection-external", "native", "threads" };
    foreach (var retired in retiring)
        if (declared.Contains(retired))
            failures.Add($"Manifest still requests retiring capability {retired}.");

    // Pass the same game references the loader collects in Unity. Do not run
    // the loader lifecycle, modify its trust state, or enable the mod here.
    var allowed = Directory.EnumerateFiles(managedDir, "*.dll", SearchOption.AllDirectories)
        .Select(Path.GetFileNameWithoutExtension).ToArray();
    var scanner = game.GetType("ModScanner", throwOnError: true)!;
    var scan = scanner.GetMethod("Scan")!.Invoke(null,
        new object?[] { new[] { modPath }, allowed, null, false })!;
    var scanType = scan.GetType();
    var error = scanType.GetField("Error")!.GetValue(scan) as string;
    if (error is not null)
        failures.Add("Native scanner error: " + error);
    foreach (var finding in (IEnumerable)scanType.GetField("HardFlags")!.GetValue(scan)!)
        failures.Add("Native scanner refused: " + finding);
    var detected = ((IEnumerable<string>)scanType.GetField("Capabilities")!.GetValue(scan)!).ToArray();
    foreach (var missing in detected.Except(declared))
        failures.Add("Native scanner found undeclared capability: " + missing);
    foreach (var retired in detected.Intersect(retiring))
        failures.Add("Native scanner found retiring capability: " + retired);
    Console.WriteLine("Native loader scan: " + string.Join(", ", detected));
    foreach (var finding in (IEnumerable)scanType.GetField("Pending")!.GetValue(scan)!)
        failures.Add("Native scanner pending rule: " + finding);

    var members = new (string Type, string Field, string ValueType)[]
    {
        ("StoreClientInstance", "storeClient", "StoreClient"),
        ("StoreClientInstance", "gridInv", "GameGridInventory"),
        ("DialogChoiceBubble", "button", "UnityEngine.UI.Button"),
        ("NegociationUIManager", "frame", "NegotiationTabletFrame"),
        ("PhoneUIManager", "currentNumber", "System.Int64"),
        ("BargainUIManager", "trackedClient", "StoreClient"),
        ("BargainUIManager", "markupApplied", "System.Boolean"),
        ("BargainUIManager", "buyingDiscountApplied", "System.Boolean")
    };
    foreach (var member in members)
    {
        var field = game.GetType(member.Type, true)!.GetField(member.Field,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.FieldType.FullName != member.ValueType)
            failures.Add($"Native field changed: {member.Type}.{member.Field} ({member.ValueType}).");
    }
    if (game.GetType("LootRegistry", true)!.GetMethod("RebuildIfDirty",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: Type.EmptyTypes, modifiers: null) is null)
        failures.Add("Native method changed: LootRegistry.RebuildIfDirty().");
    Console.WriteLine($"Native backing fields checked: {members.Length}.");
}
