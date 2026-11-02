using System.Diagnostics;
using Steamworks;

if (args.Length == 2 && args[0] == "--verify")
    return VerifyPreviews(args[1]);

if (args.Length < 4)
{
    Console.Error.WriteLine(
        "Usage: WorkshopPreviewUpdater <publishedFileId> <cover512.png> <gallery-image-1> <gallery-image-2> [changeNote]");
    return 2;
}

if (!ulong.TryParse(args[0], out var publishedFileId))
{
    Console.Error.WriteLine("Invalid published file ID.");
    return 2;
}

var cover = Path.GetFullPath(args[1]);
var previews = args.Skip(2).Take(2).Select(Path.GetFullPath).ToArray();
var changeNote = args.Length > 4 ? args[4] : "修正工坊封面并补充账本界面预览图。";
foreach (var path in new[] { cover }.Concat(previews))
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Image not found: {path}");
        return 2;
    }
}

if (!SteamAPI.Init())
{
    Console.Error.WriteLine("Steam API initialization failed. Keep Steam running and signed in.");
    return 1;
}

try
{
    var handle = SteamUGC.StartItemUpdate((AppId_t)4348910, (PublishedFileId_t)publishedFileId);
    if (handle == UGCUpdateHandle_t.Invalid)
    {
        Console.Error.WriteLine("Could not start the Workshop item update.");
        return 1;
    }

    if (!SteamUGC.SetItemPreview(handle, cover))
    {
        Console.Error.WriteLine("Steam rejected the cover image.");
        return 1;
    }

    foreach (var preview in previews)
    {
        if (!SteamUGC.AddItemPreviewFile(handle, preview, EItemPreviewType.k_EItemPreviewType_Image))
        {
            Console.Error.WriteLine($"Steam rejected preview image: {preview}");
            return 1;
        }
    }

    var completed = false;
    var ioFailure = false;
    var result = EResult.k_EResultNone;
    var needsTermsAcceptance = false;
    var callResult = CallResult<SubmitItemUpdateResult_t>.Create((data, failed) =>
    {
        result = data.m_eResult;
        needsTermsAcceptance = data.m_bUserNeedsToAcceptWorkshopLegalAgreement;
        ioFailure = failed;
        completed = true;
    });
    callResult.Set(SteamUGC.SubmitItemUpdate(handle, changeNote));

    var timer = Stopwatch.StartNew();
    while (!completed && timer.Elapsed < TimeSpan.FromMinutes(5))
    {
        SteamAPI.RunCallbacks();
        Thread.Sleep(50);
    }

    if (!completed)
    {
        Console.Error.WriteLine("Timed out while submitting Workshop preview images.");
        return 1;
    }

    if (ioFailure || result != EResult.k_EResultOK)
    {
        Console.Error.WriteLine($"Workshop update failed: {result}; IO failure: {ioFailure}.");
        if (needsTermsAcceptance)
            Console.Error.WriteLine("Accept the Steam Workshop legal agreement, then retry.");
        return 1;
    }

    Console.WriteLine($"Updated cover and added {previews.Length} preview images to {publishedFileId}.");
    Console.WriteLine($"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}");
    return 0;
}
finally
{
    SteamAPI.Shutdown();
}

static int VerifyPreviews(string idText)
{
    if (!ulong.TryParse(idText, out var id))
    {
        Console.Error.WriteLine("Invalid published file ID.");
        return 2;
    }
    if (!SteamAPI.Init())
    {
        Console.Error.WriteLine("Steam API initialization failed.");
        return 1;
    }

    try
    {
        var query = SteamUGC.CreateQueryUGCDetailsRequest(
            new[] { (PublishedFileId_t)id }, 1);
        SteamUGC.SetReturnAdditionalPreviews(query, true);
        var completed = false;
        var ioFailure = false;
        var result = EResult.k_EResultNone;
        var returned = 0u;
        var callback = CallResult<SteamUGCQueryCompleted_t>.Create((data, failed) =>
        {
            result = data.m_eResult;
            returned = data.m_unNumResultsReturned;
            ioFailure = failed;
            completed = true;
        });
        callback.Set(SteamUGC.SendQueryUGCRequest(query));

        var timer = Stopwatch.StartNew();
        while (!completed && timer.Elapsed < TimeSpan.FromMinutes(2))
        {
            SteamAPI.RunCallbacks();
            Thread.Sleep(50);
        }
        if (!completed || ioFailure || result != EResult.k_EResultOK || returned == 0)
        {
            Console.Error.WriteLine($"Workshop query failed: {result}; IO failure: {ioFailure}.");
            SteamUGC.ReleaseQueryUGCRequest(query);
            return 1;
        }

        var count = SteamUGC.GetQueryUGCNumAdditionalPreviews(query, 0);
        Console.WriteLine($"Workshop item {id}: {count} additional preview image(s).");
        for (uint i = 0; i < count; i++)
        {
            string url;
            string originalFileName;
            EItemPreviewType type;
            if (SteamUGC.GetQueryUGCAdditionalPreview(
                    query, 0, i, out url, 4096, out originalFileName, 4096, out type))
                Console.WriteLine($"{i + 1}. {originalFileName} [{type}] {url}");
        }
        SteamUGC.ReleaseQueryUGCRequest(query);
        return count == 2 ? 0 : 1;
    }
    finally
    {
        SteamAPI.Shutdown();
    }
}
