using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace Jellyfin.Plugin.DlHelper.Services;

/// <summary>Client minimal de l'API REST Syncthing.</summary>
public sealed class SyncthingClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _url;
    private readonly string _apiKey;

    public SyncthingClient(string url, string apiKey, string folderId)
    {
        _url = url.TrimEnd('/');
        _apiKey = apiKey;
        FolderId = folderId;
    }

    public string FolderId { get; }

    /// <summary>
    /// Construit le client à partir des réglages. La clé API et l'ID du dossier peuvent être lus dans
    /// config.xml (si Jellyfin y a accès). Lève InvalidOperationException si quelque chose manque.
    /// </summary>
    public static SyncthingClient Create(string url, string apiKey, string folderId, string configPath, string watchDir)
    {
        if ((apiKey.Length == 0 || folderId.Length == 0) && configPath.Length > 0)
        {
            var root = XDocument.Load(configPath).Root!;
            if (apiKey.Length == 0)
            {
                apiKey = root.Element("gui")?.Element("apikey")?.Value ?? string.Empty;
            }

            if (folderId.Length == 0)
            {
                folderId = MatchFolder(
                    root.Elements("folder").Select(f => ((string?)f.Attribute("id") ?? string.Empty, (string?)f.Attribute("path") ?? string.Empty)),
                    watchDir) ?? string.Empty;
            }
        }

        if (apiKey.Length == 0 || folderId.Length == 0)
        {
            throw new InvalidOperationException("clé API ou ID de dossier Syncthing manquant (voir les réglages du plugin)");
        }

        return new SyncthingClient(url, apiKey, folderId);
    }

    /// <summary>
    /// Trouve le dossier Syncthing qui correspond au dossier surveillé. Le chemin exact peut différer
    /// (conteneurs Docker différents) : à défaut on compare le dernier élément du chemin.
    /// </summary>
    public static string? MatchFolder(IEnumerable<(string Id, string Path)> folders, string watchDir)
    {
        var list = folders.ToList();
        string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');
        var exact = list.FirstOrDefault(f => Norm(f.Path) == Norm(watchDir));
        if (exact.Id is { Length: > 0 })
        {
            return exact.Id;
        }

        var leaf = Path.GetFileName(Norm(watchDir));
        var byLeaf = list.Where(f => string.Equals(Path.GetFileName(Norm(f.Path)), leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        return byLeaf.Count == 1 ? byLeaf[0].Id : null;
    }

    private async Task<JsonElement> GetAsync(string endpoint, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url + endpoint);
        req.Headers.Add("X-API-Key", _apiKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} sur {endpoint}");
        }

        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
    }

    /// <summary>{chemin relatif: taille} des fichiers encore à recevoir (sans les suppressions en attente).</summary>
    public async Task<Dictionary<string, long>> NeededAsync(CancellationToken ct)
    {
        var data = await GetAsync($"/rest/db/need?folder={Uri.EscapeDataString(FolderId)}&page=1&perpage=1000000", ct).ConfigureAwait(false);
        var result = new Dictionary<string, long>();
        foreach (var key in new[] { "progress", "queued", "rest" })
        {
            if (!data.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var f in arr.EnumerateArray())
            {
                if (f.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var name = f.GetProperty("name").GetString()!.Replace('\\', '/');
                result[name] = f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            }
        }

        return result;
    }

    public async Task<SyncthingStatus> StatusAsync(CancellationToken ct)
    {
        var s = await GetAsync($"/rest/db/status?folder={Uri.EscapeDataString(FolderId)}", ct).ConfigureAwait(false);
        return new SyncthingStatus(
            s.TryGetProperty("state", out var st) ? st.GetString() ?? "?" : "?",
            s.TryGetProperty("needFiles", out var nf) ? nf.GetInt64() : 0,
            s.TryGetProperty("needBytes", out var nb) ? nb.GetInt64() : 0);
    }

    /// <summary>[(chemin, message)] des éléments que Syncthing n'arrive pas à traiter.</summary>
    public async Task<List<(string Path, string Error)>> FolderErrorsAsync(CancellationToken ct)
    {
        var data = await GetAsync($"/rest/folder/errors?folder={Uri.EscapeDataString(FolderId)}&page=1&perpage=1000", ct).ConfigureAwait(false);
        var list = new List<(string, string)>();
        if (data.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errs.EnumerateArray())
            {
                list.Add((e.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                          e.TryGetProperty("error", out var m) ? m.GetString() ?? string.Empty : string.Empty));
            }
        }

        return list;
    }

    /// <summary>Liste des dossiers Syncthing (pour la page de réglages). Ne nécessite pas d'ID de dossier.</summary>
    public static async Task<List<SyncthingFolder>> FoldersAsync(string url, string apiKey, CancellationToken ct)
    {
        var client = new SyncthingClient(url, apiKey, string.Empty);
        var data = await client.GetAsync("/rest/config/folders", ct).ConfigureAwait(false);
        return data.EnumerateArray()
            .Select(f => new SyncthingFolder(
                f.GetProperty("id").GetString() ?? string.Empty,
                f.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty,
                f.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                f.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty))
            .ToList();
    }
}

public record SyncthingStatus(string State, long NeedFiles, long NeedBytes);

public record SyncthingFolder(string Id, string Label, string Path, string Type);
