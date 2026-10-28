using System.Security.Cryptography;
using System.Text.Json;
using AIWorldPawnshop.Models;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

public sealed class GameSourceFile
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public long WriteTicks { get; set; }
    public string Hash { get; set; } = string.Empty;
}

public sealed class GameKnowledgeItem
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long BaseValue { get; set; }
    public List<string> Types { get; set; } = new();
    public bool ObservedByPlayer { get; set; }
}

public sealed class GameKnowledgePerson
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CustomerGroup { get; set; } = string.Empty;
    public string PublicRole { get; set; } = string.Empty;
}

public sealed class GameKnowledgeEvent
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ObservedDay { get; set; }
}

public sealed class GameKnowledgeSnapshot
{
    public int Schema { get; set; } = GameKnowledgeCatalog.Schema;
    public string Fingerprint { get; set; } = string.Empty;
    public List<GameSourceFile> Files { get; set; } = new();
    public List<GameKnowledgeItem> Items { get; set; } = new();
    public List<GameKnowledgePerson> People { get; set; } = new();
    public List<GameKnowledgeEvent> Events { get; set; } = new();
}

public static class GameSourceFingerprint
{
    public static GameKnowledgeSnapshot Capture(string gameDirectory, GameKnowledgeSnapshot? previous = null)
    {
        var result = new GameKnowledgeSnapshot();
        var data = Path.Combine(gameDirectory, "Probably Stolen_Data");
        var files = new List<string>();
        AddIfExists(files, Path.Combine(gameDirectory, "GameAssembly.dll"));
        AddIfExists(files, Path.Combine(data, "il2cpp_data", "Metadata", "global-metadata.dat"));
        AddIfExists(files, Path.Combine(data, "resources.assets"));
        AddIfExists(files, Path.Combine(data, "globalgamemanagers.assets"));
        var addressables = Path.Combine(data, "StreamingAssets", "aa");
        AddIfExists(files, Path.Combine(addressables, "catalog.json"));
        if (System.IO.Directory.Exists(addressables))
            files.AddRange(System.IO.Directory.EnumerateFiles(addressables, "*.bundle", SearchOption.AllDirectories));
        if (System.IO.Directory.Exists(data))
        {
            files.AddRange(System.IO.Directory.EnumerateFiles(data, "sharedassets*.assets"));
            files.AddRange(System.IO.Directory.EnumerateFiles(data, "level*"));
        }
        if (files.Count == 0)
            throw new FileNotFoundException("没有找到原版资源文件", gameDirectory);

        var oldFiles = (previous?.Files ?? new List<GameSourceFile>())
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Path))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(gameDirectory, path).Replace('\\', '/');
            var hash = oldFiles.TryGetValue(relative, out var old) &&
                       old.Length == info.Length && old.WriteTicks == info.LastWriteTimeUtc.Ticks &&
                       !string.IsNullOrWhiteSpace(old.Hash)
                ? old.Hash
                : HashFile(path);
            result.Files.Add(new GameSourceFile
            {
                Path = relative,
                Length = info.Length,
                WriteTicks = info.LastWriteTimeUtc.Ticks,
                Hash = hash
            });
        }
        var content = string.Join("\n", result.Files.Select(x => $"{x.Path}|{x.Hash}"));
        result.Fingerprint = RuntimeCompat.ToHex(
            RuntimeCompat.HashSha256(System.Text.Encoding.UTF8.GetBytes($"{GameKnowledgeCatalog.Schema}\n{content}")));
        return result;
    }

    private static void AddIfExists(List<string> files, string path)
    {
        if (File.Exists(path))
            files.Add(path);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return RuntimeCompat.ToHex(sha.ComputeHash(stream));
    }
}

public sealed class GameKnowledgeCatalog
{
    public const int Schema = 1;
    private const int ItemsPerFrame = 6;
    private readonly string _path;
    private readonly Task<GameKnowledgeSnapshot> _fingerprintTask;
    private GameKnowledgeSnapshot _snapshot;
    private GameKnowledgeSnapshot? _building;
    private List<string>? _itemKeys;
    private int _nextItem;
    private int _emptyScanAttempts;
    private DateTime _nextRegistryAttemptUtc;
    private DateTime _nextSaveUtc;
    private bool _dirty;
    private bool _fingerprintHandled;

    public GameKnowledgeCatalog(string dataRoot, string gameDirectory)
    {
        _path = Path.Combine(dataRoot, "GameKnowledgeCatalog.json");
        _snapshot = Load(_path);
        _fingerprintTask = Task.Run(() => GameSourceFingerprint.Capture(gameDirectory, _snapshot));
    }

    public GameKnowledgeSnapshot Snapshot => _snapshot;

    public void Tick()
    {
        if (!_fingerprintHandled && _fingerprintTask.IsCompleted)
        {
            _fingerprintHandled = true;
            if (_fingerprintTask.IsCompletedSuccessfully)
            {
                var current = _fingerprintTask.Result;
                if (_snapshot.Schema != Schema || _snapshot.Fingerprint != current.Fingerprint)
                    _building = current;
                else if (_snapshot.Files.Count != current.Files.Count ||
                         _snapshot.Files.Zip(current.Files, (previous, latest) =>
                             previous.Length != latest.Length ||
                             previous.WriteTicks != latest.WriteTicks).Any(changed => changed))
                {
                    _snapshot.Files = current.Files;
                    _dirty = true;
                    _nextSaveUtc = DateTime.UtcNow.AddSeconds(1);
                }
            }
            else
                MelonLogger.Warning($"[当铺世界] 原版资料指纹检查失败，保留旧目录：{_fingerprintTask.Exception?.GetBaseException().Message}");
        }

        if (_building is not null)
            ScanItems();
        if (_dirty && _building is null && DateTime.UtcNow >= _nextSaveUtc)
            Save();
    }

    private void ScanItems()
    {
        if (PlayerStore.Instance is null)
            return;
        if (_itemKeys is null)
        {
            if (DateTime.UtcNow < _nextRegistryAttemptUtc)
                return;
            _nextRegistryAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            try
            {
                var keys = DirectoryMaster.GetIdentifierList<GameItem>();
                if (keys is null || keys.Count == 0)
                    return;
                _itemKeys = new List<string>(keys.Count);
                for (var i = 0; i < keys.Count; i++)
                    if (!string.IsNullOrWhiteSpace(keys[i])) _itemKeys.Add(keys[i]);
                _itemKeys = _itemKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[当铺世界] 等待原版物品目录就绪：{ex.Message}");
                return;
            }
        }

        for (var scanned = 0; scanned < ItemsPerFrame && _nextItem < _itemKeys.Count; scanned++, _nextItem++)
        {
            try
            {
                var item = DirectoryMaster.Item(_itemKeys[_nextItem], false);
                if (item is null) continue;
                var entry = new GameKnowledgeItem
                {
                    Key = _itemKeys[_nextItem],
                    Name = Clean(item.GetDisplayName(true), 100),
                    Description = Clean(string.Join("；", new[]
                    {
                        item.shortDescription, item.longDescription, item.flavorText
                    }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()), 600),
                    BaseValue = item.unitBaseValue
                };
                if (item.itemTypes is not null)
                    for (var i = 0; i < item.itemTypes.Count && i < 12; i++)
                        if (!string.IsNullOrWhiteSpace(item.itemTypes[i])) entry.Types.Add(item.itemTypes[i]);
                var old = _snapshot.Items.FirstOrDefault(x => x.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));
                entry.ObservedByPlayer = old?.ObservedByPlayer == true;
                _building!.Items.Add(entry);
            }
            catch
            {
                // One broken native factory must not prevent the remaining catalog from loading.
            }
        }

        if (_nextItem < _itemKeys.Count)
            return;
        if (_building!.Items.Count < Math.Max(10, _itemKeys.Count / 2))
        {
            _emptyScanAttempts++;
            _building.Items.Clear();
            _itemKeys = null;
            _nextItem = 0;
            _nextRegistryAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            if (_emptyScanAttempts >= 3)
            {
                MelonLogger.Warning("[当铺世界] 原版物品扫描三次未取得足够数据，保留旧目录。");
                _building = null;
            }
            return;
        }
        _snapshot = _building;
        _building = null;
        _itemKeys = null;
        _dirty = true;
        Save();
        MelonLogger.Msg($"[当铺世界] 原版资料目录已更新：物品 {_snapshot.Items.Count}，人物 {_snapshot.People.Count}，事件 {_snapshot.Events.Count}");
    }

    public void Observe(GameConversationContext context, int currentDay)
    {
        var character = context.Character;
        if (GameKnowledgeSearch.CanRecordPerson(character))
        {
            var existing = _snapshot.People.FirstOrDefault(x => x.Key.Equals(character.OriginalIdentifier, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _snapshot.People.Add(new GameKnowledgePerson
                {
                    Key = character.OriginalIdentifier,
                    Name = character.DisplayName,
                    CustomerGroup = First(character.FactionDisplayName, character.FactionKey),
                    PublicRole = character.IsSecurity ? "治安或安保人员" : string.Empty
                });
                _dirty = true;
            }
            if (_building is not null && !_building.People.Any(x => x.Key.Equals(character.OriginalIdentifier, StringComparison.OrdinalIgnoreCase)))
                _building.People.Add(_snapshot.People.First(x => x.Key.Equals(character.OriginalIdentifier, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var item in GameKnowledgeSearch.VisibleItems(context))
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ItemKey)) continue;
            var found = _snapshot.Items.FirstOrDefault(x => x.Key.Equals(item.ItemKey, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                found = new GameKnowledgeItem { Key = item.ItemKey };
                _snapshot.Items.Add(found);
            }
            if (!found.ObservedByPlayer || string.IsNullOrWhiteSpace(found.Name))
            {
                found.ObservedByPlayer = true;
                found.Name = item.DisplayName;
                if (string.IsNullOrWhiteSpace(found.Description))
                    found.Description = Clean(item.Description, 400);
                found.BaseValue = item.BaseValue;
                found.Types = item.ItemTypes.Take(12).ToList();
                _dirty = true;
            }
        }
        foreach (var eventText in context.ActiveWorldEvents.Where(x => x.StartsWith("原版事件：", StringComparison.Ordinal)))
        {
            var name = eventText[5..].Split('；')[0].Trim();
            if (string.IsNullOrWhiteSpace(name) || _snapshot.Events.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            _snapshot.Events.Add(new GameKnowledgeEvent
            {
                Key = name,
                Name = name,
                Description = Clean(eventText, 400),
                ObservedDay = currentDay
            });
            if (_building is not null)
                _building.Events.Add(_snapshot.Events[^1]);
            _dirty = true;
        }
        if (_dirty)
            _nextSaveUtc = DateTime.UtcNow.AddSeconds(15);
    }

    public void Enrich(GameConversationContext context, string playerText)
    {
        if (string.IsNullOrWhiteSpace(playerText) || !_fingerprintHandled || _building is not null) return;
        GameKnowledgeSearch.Enrich(_snapshot, context, playerText);
    }

    public void Save()
    {
        if (!_dirty) return;
        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_snapshot));
            RuntimeCompat.MoveReplace(temporary, _path);
            _dirty = false;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 原版资料目录保存失败，保留旧文件：{ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static GameKnowledgeSnapshot Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var snapshot = JsonSerializer.Deserialize<GameKnowledgeSnapshot>(File.ReadAllText(path)) ?? new();
                snapshot.Files ??= new();
                snapshot.Items ??= new();
                snapshot.People ??= new();
                snapshot.Events ??= new();
                return snapshot;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 原版资料缓存无法读取，将重新建立：{ex.Message}");
        }
        return new GameKnowledgeSnapshot();
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static string Clean(string? value, int maxLength)
    {
        var text = value is null ? string.Empty : RuntimeCompat.SingleLine(value).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

public static class GameKnowledgeSearch
{
    public static bool CanRecordPerson(NativeCharacterContext character) =>
        character.IsIntroduced && !string.IsNullOrWhiteSpace(character.OriginalIdentifier);

    public static IEnumerable<NativeItemContext> VisibleItems(GameConversationContext context) =>
        new[] { context.Trade.Item }
            .Concat(context.Trade.PlayerOfferedItems)
            .OfType<NativeItemContext>()
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemKey));

    public static void Enrich(GameKnowledgeSnapshot snapshot, GameConversationContext context, string playerText)
    {
        if (string.IsNullOrWhiteSpace(playerText)) return;
        foreach (var item in snapshot.Items.Where(x => Matches(playerText, x.Key, x.Name)).Take(3))
        {
            var text = $"原版物品 {item.Name}（{item.Key}）：{item.Description}；基础价值 {item.BaseValue}；类型 {string.Join('、', item.Types)}";
            context.GmKnowledge.Add(text);
            if (item.ObservedByPlayer)
                context.RelevantGameKnowledge.Add(text);
        }
        foreach (var person in snapshot.People.Where(x => Matches(playerText, x.Key, x.Name) &&
                     !x.Key.Equals(context.Character.OriginalIdentifier, StringComparison.OrdinalIgnoreCase)).Take(2))
            context.RelevantGameKnowledge.Add($"玩家曾见过 {person.Name}；这不表示当前人物认识此人，也不能据此回答其身世、经历或组织归属。");
        foreach (var gameEvent in snapshot.Events.Where(x => Matches(playerText, x.Key, x.Name)).Take(2))
            context.RelevantGameKnowledge.Add($"玩家曾在第 {gameEvent.ObservedDay} 天获知：{gameEvent.Description}。这不表示事件当前仍在持续。");
    }

    private static bool Matches(string text, string key, string name) =>
        (!string.IsNullOrWhiteSpace(key) && key.Length >= 3 && text.Contains(key, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(name) && name.Length >= 2 && text.Contains(name, StringComparison.OrdinalIgnoreCase));

}
