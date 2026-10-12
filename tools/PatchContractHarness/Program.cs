using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: PatchContractHarness <AIWorldPawnshop.dll> <Probably Stolen game directory>");
    return 2;
}

var modPath = Path.GetFullPath(args[0]);
var gameDirectory = Path.GetFullPath(args[1]);
var probeDirectories = new[]
{
    Path.GetDirectoryName(modPath)!,
    Path.Combine(gameDirectory, "MelonLoader", "Il2CppAssemblies"),
    Path.Combine(gameDirectory, "MelonLoader", "net6"),
    Path.Combine(gameDirectory, "MelonLoader")
};

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in probeDirectories)
    {
        var candidate = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(candidate))
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
    }

    return null;
};

var failures = new List<string>();
var modAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
var gameAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
    Path.Combine(gameDirectory, "MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll"));

CheckNewsProjectionResultContract(modAssembly, gameAssembly, failures);
CheckNewsProjectionScopeCleanupContract(modAssembly, failures);
CheckWorldEconomyProjectionContract(modAssembly, failures);
CheckInitializationDoesNotPatchTwice(modAssembly, failures);
CheckPhoneChoiceIsolationContract(modAssembly, failures);
CheckDeferredBarterSettlementContract(modAssembly, failures);
    CheckBarterPresentationContract(modAssembly, failures);

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine("Harmony patch contract regression harness passed.");
return 0;

static void CheckNewsProjectionResultContract(
    Assembly modAssembly,
    Assembly gameAssembly,
    ICollection<string> failures)
{
    var target = gameAssembly
        .GetType("Il2Cpp.StoreEventManager", throwOnError: true)!
        .GetMethod("GetActiveEvents", BindingFlags.Public | BindingFlags.Instance)!;
    var postfix = modAssembly
        .GetType("AIWorldPawnshop.StoreEventManagerNewsProjectionPatch", throwOnError: true)!
        .GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!;
    var resultParameter = postfix.GetParameters()
        .Single(parameter => parameter.Name == "__result");
    var expected = target.ReturnType.MakeByRefType();

    if (resultParameter.ParameterType != expected)
    {
        failures.Add(
            $"GetActiveEvents returns {expected}, but the news postfix declares " +
            $"{resultParameter.ParameterType}.");
    }
}

static void CheckNewsProjectionScopeCleanupContract(
    Assembly modAssembly,
    ICollection<string> failures)
{
    var patch = modAssembly
        .GetType("AIWorldPawnshop.NewsUiManagerPopulatePatch", throwOnError: true)!;
    var finalizer = patch.GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static);
    if (finalizer is null)
    {
        failures.Add("NewsUIManager.PopulateUI patch lacks an exception finalizer for scope cleanup.");
        return;
    }

    var state = finalizer.GetParameters().SingleOrDefault(parameter => parameter.Name == "__state");
    var exception = finalizer.GetParameters().SingleOrDefault(parameter => parameter.Name == "__exception");
    if (state?.ParameterType != typeof(bool).MakeByRefType() ||
        exception?.ParameterType != typeof(Exception) ||
        finalizer.ReturnType != typeof(Exception))
    {
        failures.Add("News projection finalizer does not preserve Harmony exception/state contract.");
    }
}

static void CheckWorldEconomyProjectionContract(
    Assembly modAssembly,
    ICollection<string> failures)
{
    var adapter = modAssembly
        .GetType("AIWorldPawnshop.Runtime.NativeWorldEconomyAdapter", throwOnError: true)!;
    foreach (var methodName in new[] { "ApplyCurrentClient", "RefreshCurrentClient", "RestoreClient" })
    {
        if (adapter.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static) is null)
            failures.Add($"Native world economy adapter lacks {methodName}.");
    }

    var dismissPatch = modAssembly
        .GetType("AIWorldPawnshop.PlayerStoreDismissClientPatch", throwOnError: true)!;
    var postfix = dismissPatch.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!;
    if (!EnumerateCalledMethods(postfix).Any(method => method.Name == "OnNativeClientDismissed"))
        failures.Add("DismissCurrentClient does not route through native economy restoration.");
}

static void CheckInitializationDoesNotPatchTwice(
    Assembly modAssembly,
    ICollection<string> failures)
{
    var initialize = modAssembly
        .GetType("AIWorldPawnshop.AIWorldPawnshopMod", throwOnError: true)!
        .GetMethod("OnInitializeMelon", BindingFlags.Public | BindingFlags.Instance)!;

    if (EnumerateCalledMethods(initialize).Any(method =>
            method.DeclaringType?.FullName == "HarmonyLib.Harmony" &&
            method.Name == "PatchAll"))
    {
        failures.Add(
            "OnInitializeMelon calls Harmony.PatchAll even though MelonLoader already applies " +
            "attributed patches before invoking the mod initializer.");
    }
}

static void CheckPhoneChoiceIsolationContract(
    Assembly modAssembly,
    ICollection<string> failures)
{
    var entryType = modAssembly
        .GetType("AIWorldPawnshop.UI.NativeDialogEntry", throwOnError: true)!;
    var attachPhone = entryType.GetMethod(
        "AttachPhoneChoices",
        BindingFlags.Public | BindingFlags.Static)!;
    if (EnumerateCalledMethods(attachPhone).Any(method =>
            method.DeclaringType == entryType && method.Name == "Attach"))
    {
        failures.Add(
            "AttachPhoneChoices calls the shared native choice layout; phone free chat " +
            "must use an isolated entry to avoid overlap with the end-call button.");
    }

    var requiredPatches = new[]
    {
        "AIWorldPawnshop.UI.NativePhoneEndConversationPatch",
        "AIWorldPawnshop.UI.NativePhoneInterruptPatch",
        "AIWorldPawnshop.UI.NativePhoneStopCallPatch",
        "AIWorldPawnshop.UI.NativePhoneClosePatch",
        "AIWorldPawnshop.UI.NativePhoneDialogEndPatch"
    };
    foreach (var patchName in requiredPatches)
    {
        if (modAssembly.GetType(patchName, throwOnError: false) is null)
            failures.Add($"Missing phone lifecycle hide patch: {patchName}.");
    }
}

static void CheckDeferredBarterSettlementContract(
    Assembly modAssembly,
    ICollection<string> failures)
{
    var walletApplication = modAssembly
        .GetType("AIWorldPawnshop.Runtime.WalletTransferApplication", throwOnError: true)!;
    if (walletApplication.GetProperty("Deferred", BindingFlags.Public | BindingFlags.Instance) is null)
        failures.Add("WalletTransferApplication is missing the deferred settlement marker.");

    var walletAdapter = modAssembly
        .GetType("AIWorldPawnshop.Runtime.NativeWalletAdapter", throwOnError: true)!;
    if (walletAdapter.GetMethod("CommitPending", BindingFlags.Public | BindingFlags.Static) is null)
        failures.Add("NativeWalletAdapter is missing CommitPending for accepted barter settlement.");

    var barterAdapter = modAssembly
        .GetType("AIWorldPawnshop.Runtime.NativeBarterAdapter", throwOnError: true)!;
    var complete = barterAdapter.GetMethod(
        "CompleteAcceptedTransaction",
        BindingFlags.Public | BindingFlags.Static);
    if (complete is null)
    {
        failures.Add("NativeBarterAdapter is missing the post-accept settlement hook.");
        return;
    }

    var acceptPatch = modAssembly
        .GetType("AIWorldPawnshop.PlayerStoreAcceptBarterPatch", throwOnError: true)!;
    var postfix = acceptPatch.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!;
    if (!EnumerateCalledMethods(postfix).Any(method =>
            method.DeclaringType == barterAdapter &&
            method.Name == complete.Name))
    {
        failures.Add("AcceptBarter Postfix does not commit deferred barter settlement.");
    }
}

static void CheckBarterPresentationContract(
    Assembly modAssembly,
    ICollection<string> failures)
{
    var barterAdapter = modAssembly
        .GetType("AIWorldPawnshop.Runtime.NativeBarterAdapter", throwOnError: true)!;
    if (barterAdapter.GetMethod("Update", BindingFlags.Public | BindingFlags.Static) is null)
        failures.Add("NativeBarterAdapter is missing deferred native UI refresh support.");
    if (barterAdapter.GetMethod("TryOverridePlacedItemResponse", BindingFlags.Public | BindingFlags.Static) is null)
        failures.Add("NativeBarterAdapter is missing the exact wrong-item response override.");

    var responsePatch = modAssembly
        .GetType("AIWorldPawnshop.BarterOfferGetItemPlacedResponsePatch", throwOnError: true)!;
    if (responsePatch.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static) is null)
        failures.Add("BarterOffer placed-item response patch is missing its Postfix.");
}

static IEnumerable<MethodBase> EnumerateCalledMethods(MethodInfo method)
{
    var body = method.GetMethodBody();
    var bytes = body?.GetILAsByteArray();
    if (bytes is null)
        yield break;

    var oneByte = new OpCode[0x100];
    var twoByte = new OpCode[0x100];
    foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
    {
        if (field.GetValue(null) is not OpCode opcode)
            continue;

        var value = unchecked((ushort)opcode.Value);
        if (value < 0x100)
            oneByte[value] = opcode;
        else if ((value & 0xff00) == 0xfe00)
            twoByte[value & 0xff] = opcode;
    }

    var position = 0;
    while (position < bytes.Length)
    {
        var first = bytes[position++];
        var opcode = first == 0xfe ? twoByte[bytes[position++]] : oneByte[first];
        var operandStart = position;
        var operandSize = GetOperandSize(opcode.OperandType, bytes, operandStart);

        if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && operandSize == 4)
        {
            var token = BitConverter.ToInt32(bytes, operandStart);
            MethodBase? called = null;
            try
            {
                called = method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method.GetGenericArguments());
            }
            catch
            {
                // An unresolved unrelated call cannot turn a failing contract green.
            }

            if (called is not null)
                yield return called;
        }

        position += operandSize;
    }
}

static int GetOperandSize(OperandType operandType, byte[] bytes, int operandStart) =>
    operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or
        OperandType.ShortInlineI or
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or
        OperandType.InlineField or
        OperandType.InlineI or
        OperandType.InlineMethod or
        OperandType.InlineSig or
        OperandType.InlineString or
        OperandType.InlineSwitch or
        OperandType.InlineTok or
        OperandType.InlineType or
        OperandType.ShortInlineR => operandType == OperandType.InlineSwitch
            ? 4 + BitConverter.ToInt32(bytes, operandStart) * 4
            : 4,
        OperandType.InlineI8 or
        OperandType.InlineR => 8,
        _ => throw new InvalidOperationException($"Unsupported IL operand type: {operandType}")
    };
