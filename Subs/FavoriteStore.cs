using System.IO;
using System.Text.Json;

namespace LoafEar.Subs;

/// <summary>
/// 收藏夹的落地存储。就是一个 favorites.json，写在配置文件旁边 ——
/// 和 config.json 同一个目录，换 exe 目录时一起拷走，不用另找。
/// </summary>
public sealed class FavoriteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<FavoriteCue> _items = new();
    private readonly string _path;

    public FavoriteStore(string path) => _path = path;

    public IReadOnlyList<FavoriteCue> Items => _items;

    public int Count => _items.Count;

    public string FilePath => _path;

    /// <summary>读盘。文件不存在 / 坏掉都当作空收藏，不让启动流程因此失败。</summary>
    public void Load()
    {
        _items.Clear();

        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<FavoriteCue>>(json, JsonOptions);

            if (loaded is null)
                return;

            // 空路径的记录没有意义，顺便挡掉手改出来的脏数据
            foreach (var item in loaded)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.AudioPath))
                    continue;

                _items.Add(item);
            }
        }
        catch
        {
            // 坏了就当空收藏，用户重收一遍即可，不阻断使用
            _items.Clear();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOptions));
        }
        catch
        {
            // 写不了就算了，收藏不该把程序拖崩
        }
    }

    public FavoriteCue? Find(string audioPath, int cueIndex)
    {
        var key = FavoriteCue.KeyOf(audioPath, cueIndex);
        return _items.FirstOrDefault(i => i.Key == key);
    }

    public bool Contains(string audioPath, int cueIndex) => Find(audioPath, cueIndex) is not null;

    /// <summary>收一句。已经收过的直接返回旧的，不产生重复行。</summary>
    public FavoriteCue Add(FavoriteCue item)
    {
        var existing = Find(item.AudioPath, item.CueIndex);
        if (existing is not null)
            return existing;

        _items.Add(item);
        Save();
        return item;
    }

    public void Remove(FavoriteCue item)
    {
        if (_items.Remove(item))
            Save();
    }

    /// <summary>取消收藏，返回是否真的删掉了一条。</summary>
    public bool Remove(string audioPath, int cueIndex)
    {
        var existing = Find(audioPath, cueIndex);
        if (existing is null)
            return false;

        _items.Remove(existing);
        Save();
        return true;
    }

    public int CountFor(string audioPath)
    {
        var target = FavoriteCue.Normalize(audioPath);
        return _items.Count(i => FavoriteCue.Normalize(i.AudioPath) == target);
    }

    /// <summary>有收藏指向的音频已经不在盘上了（换目录、删文件）。</summary>
    public static bool IsMissing(FavoriteCue item) =>
        string.IsNullOrWhiteSpace(item.AudioPath) || !File.Exists(item.AudioPath);
}
