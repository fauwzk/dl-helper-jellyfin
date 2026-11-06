using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.DlHelper.Services;

/// <summary>
/// Cycle de vie d'un élément : baseline (déjà là au premier lancement) → downloading → pending
/// (à ranger) → processing → done / ignored. Un élément déplacé est retiré de l'état.
/// </summary>
public static class ItemStatus
{
    public const string Baseline = "baseline";
    public const string Downloading = "downloading";
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Ignored = "ignored";
}

public class TrackedItem
{
    public string Id { get; set; } = string.Empty;

    public string Status { get; set; } = ItemStatus.Downloading;

    public long Size { get; set; } = -1;

    public int Count { get; set; }

    public bool IsDir { get; set; }

    /// <summary>Heure (UTC) depuis laquelle rien n'a changé.</summary>
    public DateTime StableSince { get; set; } = DateTime.UtcNow;

    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;

    /// <summary>Heure à laquelle l'élément est passé « à ranger » (sert au compte à rebours du mode auto).</summary>
    public DateTime? NotifiedAt { get; set; }

    public string? Suggest { get; set; }

    /// <summary>Destination choisie à la main (sinon la suggestion).</summary>
    public string? Dest { get; set; }

    public string? Rename { get; set; }

    public string? LastError { get; set; }

    public string? Target { get; set; }

    public string? ChosenDest => string.IsNullOrEmpty(Dest) ? Suggest : Dest;
}

public class HistoryEntry
{
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>copy | move | ignore | error | info</summary>
    public string Kind { get; set; } = "info";

    public string Name { get; set; } = string.Empty;

    public string? Dest { get; set; }

    public string? Target { get; set; }

    public string? Who { get; set; }

    public string Message { get; set; } = string.Empty;
}

public class StateData
{
    public bool Initialized { get; set; }

    public Dictionary<string, TrackedItem> Items { get; set; } = new();

    /// <summary>Erreurs ignorées : clé → signature (l'alerte revient si la signature change).</summary>
    public Dictionary<string, string> Muted { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = new();
}

/// <summary>État persistant (survit aux redémarrages de Jellyfin) : state.json dans le dossier du plugin.</summary>
public sealed class StateStore
{
    private const int MaxHistory = 200;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public StateStore(string path)
    {
        _path = path;
        Data = Load();
    }

    public StateData Data { get; private set; }

    public static string ItemId(string name) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();

    private StateData Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<StateData>(File.ReadAllText(_path), Options) ?? new StateData();
            }
        }
        catch (JsonException)
        {
            // Fichier corrompu : on le garde de côté et on repart de zéro.
            File.Copy(_path, _path + ".corrompu", overwrite: true);
        }

        return new StateData();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    public (string? Name, TrackedItem? Item) ById(string id)
    {
        foreach (var (name, it) in Data.Items)
        {
            if (it.Id == id)
            {
                return (name, it);
            }
        }

        return (null, null);
    }

    public IEnumerable<string> WithStatus(params string[] statuses) =>
        Data.Items.Where(kv => statuses.Contains(kv.Value.Status)).Select(kv => kv.Key).ToList();

    public void AddHistory(HistoryEntry e)
    {
        Data.History.Insert(0, e);
        if (Data.History.Count > MaxHistory)
        {
            Data.History.RemoveRange(MaxHistory, Data.History.Count - MaxHistory);
        }
    }
}
