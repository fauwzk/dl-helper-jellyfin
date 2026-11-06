using Jellyfin.Plugin.DlHelper.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DlHelper.Services;

/// <summary>
/// Surveille le dossier de téléchargements, détecte les téléchargements terminés et range les éléments
/// (copie / déplacement) à la demande de l'interface web ou automatiquement.
/// </summary>
public sealed class DownloadWatcher : BackgroundService
{
    public static readonly string[] DestKeys = { "ANIME", "MUSIQUE", "FILMS", "SERIES" };

    public static readonly Dictionary<string, string> DestEmoji = new()
    {
        ["ANIME"] = "🎌", ["MUSIQUE"] = "🎵", ["FILMS"] = "🎬", ["SERIES"] = "📺",
    };

    public static readonly Dictionary<string, string> DestLabel = new()
    {
        ["ANIME"] = "Anime", ["MUSIQUE"] = "Musique", ["FILMS"] = "Films", ["SERIES"] = "Séries",
    };

    private readonly ILogger<DownloadWatcher> _log;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly Notifier _notifier;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Problem> _problems = new();
    private readonly Dictionary<string, int> _okStreak = new();
    private StateStore? _state;
    private CancellationTokenSource _wake = new();

    private SyncthingClient? _syncthing;
    private bool _syncthingDirty = true;
    private Dictionary<string, long>? _lastNeeded;
    private SyncthingStatus? _lastStatus;
    private bool _transferRunning;

    public DownloadWatcher(ILogger<DownloadWatcher> log, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, Notifier notifier)
    {
        _log = log;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _notifier = notifier;
    }

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public DateTime? LastScan { get; private set; }

    /// <summary>Syncthing activé mais clé API / ID de dossier manquant ou config.xml illisible.</summary>
    public bool SyncthingNeedsSetup { get; private set; }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private StateStore State => _state ??= new StateStore(Path.Combine(Plugin.Instance!.DataFolderPath, "state.json"));

    public static string? DestPath(PluginConfiguration c, string? key) => key switch
    {
        "ANIME" => c.DestAnime,
        "MUSIQUE" => c.DestMusique,
        "FILMS" => c.DestFilms,
        "SERIES" => c.DestSeries,
        _ => null,
    };

    // ================================================================ boucle

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Plugin.Instance!.ConfigurationChanged += OnConfigurationChanged;
        _log.LogInformation("dl-helper démarré : surveillance de {Dir}", Config.WatchDir);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
                    Resolved("Scan");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "dl-helper : erreur pendant le scan");
                    Report("Scan", ex.Message);
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _wake.Token);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, Config.PollInterval)), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_wake.IsCancellationRequested)
                    {
                        _wake.Dispose();
                        _wake = new CancellationTokenSource();
                    }
                }
            }
        }
        finally
        {
            Plugin.Instance!.ConfigurationChanged -= OnConfigurationChanged;
            _log.LogInformation("dl-helper arrêté");
        }
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        _syncthingDirty = true;
        WakeUp();
    }

    /// <summary>Relance un scan tout de suite.</summary>
    public void WakeUp()
    {
        try
        {
            _wake.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        var c = Config;
        await CheckHealthAsync(c, ct).ConfigureAwait(false);
        if (_problems.ContainsKey("Dossier surveillé"))
        {
            return;
        }

        var entries = await Task.Run(() => FileOps.ListEntries(c.WatchDir), ct).ConfigureAwait(false);
        var names = entries.ToDictionary(e => e.Name, e => e);
        var now = DateTime.UtcNow;
        var toAuto = new List<(string Name, string Dest)>();

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var st = State;
            LastScan = now;
            var needed = _lastNeeded;

            if (!st.Data.Initialized)
            {
                InitBaseline(st, entries, needed);
                return;
            }

            // Éléments disparus du dossier
            foreach (var name in st.Data.Items.Keys.ToList())
            {
                if (!names.ContainsKey(name) && st.Data.Items[name].Status != ItemStatus.Processing)
                {
                    var it = st.Data.Items[name];
                    st.Data.Items.Remove(name);
                    if (it.Status == ItemStatus.Pending)
                    {
                        st.AddHistory(new HistoryEntry { Kind = "info", Name = name, Message = "Supprimé ou déplacé hors de dl-helper." });
                    }
                }
            }

            // Nouveaux éléments
            foreach (var (name, info) in names)
            {
                if (!st.Data.Items.ContainsKey(name))
                {
                    st.Data.Items[name] = new TrackedItem
                    {
                        Id = StateStore.ItemId(name), Status = ItemStatus.Downloading, IsDir = info is DirectoryInfo,
                        StableSince = now, FirstSeen = now,
                    };
                    _log.LogInformation("dl-helper : nouveau téléchargement {Name}", name);
                }
            }

            // Téléchargements en cours → terminé ?
            foreach (var name in st.WithStatus(ItemStatus.Downloading))
            {
                var it = st.Data.Items[name];
                var path = Path.Combine(c.WatchDir, name);
                var fp = FileOps.GetFingerprint(path);
                var syncing = needed is not null && needed.Keys.Any(p => p == name || p.StartsWith(name + "/", StringComparison.Ordinal));
                if (syncing || fp.HasTemp || fp.Size == 0 || fp.Size != it.Size || fp.Count != it.Count
                    || (now - fp.Newest).TotalSeconds < c.StableSeconds)
                {
                    // Encore en réception : on remet le compteur de stabilité à zéro.
                    if (fp.Size != it.Size || fp.Count != it.Count || syncing || fp.HasTemp)
                    {
                        it.StableSince = now;
                    }

                    it.Size = fp.Size;
                    it.Count = fp.Count;
                    continue;
                }

                if ((now - it.StableSince).TotalSeconds >= c.StableSeconds)
                {
                    it.Suggest = FileOps.SuggestDest(path);
                    it.IsDir = Directory.Exists(path);
                    AnalyzeSeries(c, name, it);
                    it.Status = ItemStatus.Pending;
                    it.NotifiedAt = now;
                    _log.LogInformation("dl-helper : à ranger {Name} (suggestion {Suggest})", name, it.Suggest ?? "aucune");
                    var hint = it.Suggest is { } sg ? $"Destination suggérée : {DestLabel[sg]}." : "Choisis une destination.";
                    _ = _notifier.NotifyAsync("📥 Téléchargement terminé", name + Environment.NewLine + $"{FileOps.HumanSize(fp.Size)} · {hint}");
                    _ = _notifier.ActivityAsync($"Téléchargement terminé : {name}", $"{FileOps.HumanSize(fp.Size)}, {fp.Count} fichier(s). {hint}");
                }
            }

            // Action automatique
            if (c.AutoAction is "copy" or "move")
            {
                foreach (var name in st.WithStatus(ItemStatus.Pending))
                {
                    var it = st.Data.Items[name];
                    if (it.ChosenDest is { } dest && it.NotifiedAt is { } t && (now - t).TotalMinutes >= c.AutoDelayMinutes)
                    {
                        toAuto.Add((name, dest));
                    }
                }
            }

            st.Save();
        }
        finally
        {
            _lock.Release();
        }

        foreach (var (name, dest) in toAuto)
        {
            await ProcessAsync(name, c.AutoAction, dest, "auto", ct).ConfigureAwait(false);
        }
    }

    /// <summary>Premier lancement : ce qui est déjà là est ignoré, sauf ce que Syncthing reçoit encore.</summary>
    private void InitBaseline(StateStore st, List<FileSystemInfo> entries, Dictionary<string, long>? needed)
    {
        var receiving = (needed ?? new()).Keys.Select(TopLevel).ToHashSet();
        var now = DateTime.UtcNow;
        foreach (var e in entries)
        {
            st.Data.Items[e.Name] = new TrackedItem
            {
                Id = StateStore.ItemId(e.Name),
                Status = receiving.Contains(e.Name) ? ItemStatus.Downloading : ItemStatus.Baseline,
                IsDir = e is DirectoryInfo, StableSince = now, FirstSeen = now,
            };
        }

        st.Data.Initialized = true;
        st.AddHistory(new HistoryEntry
        {
            Kind = "info", Name = "dl-helper",
            Message = $"Premier lancement : {entries.Count} élément(s) déjà présent(s) mémorisé(s) et ignoré(s).",
        });
        st.Save();
    }

    public static string TopLevel(string relPath) => relPath.Split('/', 2)[0];

    // ================================================================ santé

    private async Task CheckHealthAsync(PluginConfiguration c, CancellationToken ct)
    {
        if (Directory.Exists(c.WatchDir))
        {
            Resolved("Dossier surveillé");
        }
        else
        {
            Report("Dossier surveillé", $"{c.WatchDir} introuvable ou inaccessible.");
        }

        foreach (var key in DestKeys)
        {
            var path = DestPath(c, key)!;
            if (Directory.Exists(path))
            {
                Resolved($"Destination {key}");
            }
            else
            {
                Report($"Destination {key}", $"{path} introuvable.");
            }
        }

        if (string.IsNullOrWhiteSpace(c.SyncthingUrl))
        {
            _syncthing = null;
            _lastNeeded = null;
            _lastStatus = null;
            Resolved("Syncthing");
            Resolved("Erreurs Syncthing");
            return;
        }

        try
        {
            if (_syncthingDirty || _syncthing is null)
            {
                _syncthing = null;
                try
                {
                    _syncthing = SyncthingClient.Create(c.SyncthingUrl, c.SyncthingApiKey.Trim(), c.SyncthingFolderId.Trim(), c.SyncthingConfig.Trim(), c.WatchDir);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    // Réglage manquant ou config.xml illisible : pas une panne de Syncthing.
                    _lastNeeded = null;
                    _lastStatus = null;
                    SyncthingNeedsSetup = true;
                    var why = ex is InvalidOperationException
                        ? "clé API ou ID du dossier à renseigner"
                        : $"config.xml illisible depuis Jellyfin ({ex.Message})";
                    Report("Syncthing", $"Syncthing à configurer : {why}. Réglages du plugin → Syncthing (bouton « Trouver le dossier »). "
                        + "En attendant, détection par stabilité des fichiers uniquement.");
                    return;
                }

                _syncthingDirty = false;
                SyncthingNeedsSetup = false;
                _log.LogInformation("dl-helper : dossier Syncthing {Id} surveillé via l'API", _syncthing.FolderId);
            }

            _lastNeeded = await _syncthing.NeededAsync(ct).ConfigureAwait(false);
            _lastStatus = await _syncthing.StatusAsync(ct).ConfigureAwait(false);
            Resolved("Syncthing");
            var errors = await _syncthing.FolderErrorsAsync(ct).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                var lines = errors.Take(5).Select(e => $"• {e.Path} — {e.Error.Replace(Path.Combine(c.WatchDir, e.Path), "…", StringComparison.Ordinal)}");
                Report(
                    "Erreurs Syncthing",
                    $"Syncthing n'arrive pas à traiter {errors.Count} élément(s) :\n" + string.Join('\n', lines)
                        + (errors.Count > 5 ? $"\n… et {errors.Count - 5} autres" : string.Empty),
                    sig: $"{errors.Count} erreur(s)");
            }
            else
            {
                Resolved("Erreurs Syncthing", after: 4); // Syncthing réessaie : on évite le yo-yo
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _lastNeeded = null;
            _lastStatus = null;
            _syncthingDirty = true;
            var detail = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
                         || ex.Message.Contains("HTTP 403", StringComparison.Ordinal) || ex.Message.Contains("HTTP 401", StringComparison.Ordinal)
                ? "clé API refusée par Syncthing (vérifie-la dans les réglages)"
                : $"API injoignable : {ex.Message}";
            Report("Syncthing", $"{detail}. Détection par stabilité des fichiers uniquement.");
        }
    }

    private void Report(string key, string message, string? sig = null)
    {
        _okStreak.Remove(key);
        var isNew = !_problems.TryGetValue(key, out var old) || old.Message != message;
        _problems[key] = new Problem(key, message, sig ?? message, old?.Since ?? DateTime.UtcNow);
        if (isNew)
        {
            _log.LogWarning("dl-helper : {Key} — {Message}", key, message);
            var muted = _state?.Data.Muted.TryGetValue(key, out var mutedSig) == true && mutedSig == (sig ?? message);
            if (!muted && old is null)
            {
                _ = _notifier.NotifyAsync($"⚠️ dl-helper : {key}", message, "warning", urgent: true);
                _ = _notifier.ActivityAsync($"dl-helper : {key}", message, LogLevel.Warning);
            }
        }
    }

    private void Resolved(string key, int after = 1)
    {
        if (!_problems.ContainsKey(key))
        {
            return;
        }

        _okStreak[key] = _okStreak.GetValueOrDefault(key) + 1;
        if (_okStreak[key] >= after)
        {
            _problems.Remove(key);
            _okStreak.Remove(key);
            _log.LogInformation("dl-helper : rétabli — {Key}", key);
            _ = _notifier.ActivityAsync($"dl-helper : rétabli — {key}", "Le problème est résolu.");
        }
    }

    // ================================================================ actions

    public async Task<TrackedItem> SelectAsync(string id, string? dest, string? rename, bool? useSeries, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (name, it) = State.ById(id);
            if (name is null || it is null)
            {
                throw new KeyNotFoundException("élément introuvable");
            }

            if (dest is not null)
            {
                if (dest.Length > 0 && !DestKeys.Contains(dest))
                {
                    throw new ArgumentException("destination inconnue");
                }

                it.Dest = dest.Length == 0 ? null : dest;
            }

            if (rename is not null)
            {
                var clean = FileOps.CleanNewName(rename, name, !it.IsDir);
                it.Rename = clean == name ? null : clean;
            }

            if (useSeries is not null)
            {
                it.UseSeries = useSeries.Value;
            }

            AnalyzeSeries(Config, name, it); // le dossier de la série a pu changer depuis
            State.Save();
            return it;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Remet un élément déjà présent, ignoré ou copié dans la liste « à ranger ».</summary>
    public async Task RestoreAsync(string id, CancellationToken ct)
    {
        var c = Config;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (name, it) = State.ById(id);
            if (name is null || it is null)
            {
                throw new KeyNotFoundException("élément introuvable");
            }

            if (it.Status is ItemStatus.Processing or ItemStatus.Downloading)
            {
                throw new InvalidOperationException("élément en cours de traitement ou de réception");
            }

            var path = Path.Combine(c.WatchDir, name);
            var fp = FileOps.GetFingerprint(path);
            it.Size = fp.Size;
            it.Count = fp.Count;
            it.IsDir = Directory.Exists(path);
            it.Suggest = FileOps.SuggestDest(path);
            AnalyzeSeries(c, name, it);
            it.Status = ItemStatus.Pending;
            it.NotifiedAt = DateTime.UtcNow;
            it.LastError = null;
            State.Save();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Lance une action (copy | move | ignore). Le transfert se fait en arrière-plan.</summary>
    public async Task StartActionAsync(string id, string action, string? dest, string who, CancellationToken ct)
    {
        string name;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (n, it) = State.ById(id);
            if (n is null || it is null)
            {
                throw new KeyNotFoundException("élément introuvable");
            }

            if (it.Status is ItemStatus.Processing)
            {
                throw new InvalidOperationException("transfert déjà en cours");
            }

            if (it.Status is ItemStatus.Downloading)
            {
                throw new InvalidOperationException("téléchargement pas encore terminé");
            }

            name = n;
            if (!string.IsNullOrEmpty(dest))
            {
                it.Dest = dest;
            }

            dest = it.ChosenDest;
        }
        finally
        {
            _lock.Release();
        }

        if (action is "copy" or "move" && (dest is null || !DestKeys.Contains(dest)))
        {
            throw new ArgumentException("choisis d'abord une destination");
        }

        if (action is not ("copy" or "move" or "ignore"))
        {
            throw new ArgumentException("action inconnue");
        }

        // Le transfert peut durer (vraie copie entre disques) : on ne fait pas attendre la requête HTTP.
        _ = Task.Run(() => ProcessAsync(name, action, dest!, who, CancellationToken.None), CancellationToken.None);
    }

    private async Task ProcessAsync(string name, string action, string dest, string who, CancellationToken ct)
    {
        var c = Config;
        TrackedItem it;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!State.Data.Items.TryGetValue(name, out it!) || it.Status == ItemStatus.Processing)
            {
                return;
            }

            if (action == "ignore")
            {
                it.Status = ItemStatus.Ignored;
                State.AddHistory(new HistoryEntry { Kind = "ignore", Name = name, Who = who, Message = "Ignoré." });
                State.Save();
                return;
            }

            it.Status = ItemStatus.Processing;
            it.LastError = null;
            _transferRunning = true;
            State.Save();
        }
        finally
        {
            _lock.Release();
        }

        var verb = action == "copy" ? "Copie" : "Déplacement";
        string? target = null;
        Exception? error = null;
        try
        {
            var destDir = DestPath(c, dest)!;
            var src = Path.Combine(c.WatchDir, name);
            target = await Task.Run(
                () =>
                {
                    // Série : on revérifie au dernier moment (la saison a pu être créée entre-temps).
                    var placement = it.UseSeries && dest is "SERIES" or "ANIME" ? SeriesMatcher.Analyze(src, destDir) : null;
                    return placement is null
                        ? FileOps.Transfer(src, destDir, action, it.Rename, c.UseHardlinks)
                        : FileOps.TransferInto(src, Path.Combine(destDir, placement.RelativeTarget), action, it.Rename, c.UseHardlinks);
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
            _log.LogError(ex, "dl-helper : échec du transfert de {Name}", name);
        }

        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _transferRunning = false;
            if (error is not null)
            {
                it.Status = ItemStatus.Pending;
                it.NotifiedAt = DateTime.UtcNow; // évite une boucle d'action automatique en échec
                it.LastError = error.Message;
                State.AddHistory(new HistoryEntry { Kind = "error", Name = name, Dest = dest, Who = who, Message = $"{verb} vers {DestLabel[dest]} : {error.Message}" });
                Report("Transfert", $"{verb} de {name} vers {DestLabel[dest]} : {error.Message}");
                _ = _notifier.NotifyAsync($"❌ {verb} échoué", $"{name} → {DestLabel[dest]}" + Environment.NewLine + error.Message, "x", urgent: true);
            }
            else
            {
                _log.LogInformation("dl-helper : {Verb} OK {Name} → {Target} ({Who})", verb, name, target, who);
                it.Status = ItemStatus.Done;
                it.Target = target;
                if (action == "move")
                {
                    State.Data.Items.Remove(name);
                }

                State.AddHistory(new HistoryEntry
                {
                    Kind = action, Name = name, Dest = dest, Target = target, Who = who,
                    Message = $"{verb} {(action == "copy" ? "terminée" : "terminé")}",
                });
                _problems.Remove("Transfert");
                var by = who == "auto" ? "automatiquement" : $"par {who}";
                _ = _notifier.ActivityAsync($"{(action == "copy" ? "Copié" : "Déplacé")} vers {DestLabel[dest]} : {name}", $"{target} ({by})");
            }

            State.Save();
        }
        finally
        {
            _lock.Release();
        }

        if (error is null && target is not null)
        {
            RefreshLibrary(c, target);
        }
    }

    /// <summary>Calcule où irait l'élément s'il est rangé en Séries ou en Anime (dossier de la série + saison).</summary>
    private void AnalyzeSeries(PluginConfiguration c, string name, TrackedItem it)
    {
        var src = Path.Combine(c.WatchDir, name);
        it.Series = new Dictionary<string, SeriesPlacement>();
        foreach (var key in new[] { "SERIES", "ANIME" })
        {
            try
            {
                if (SeriesMatcher.Analyze(src, DestPath(c, key)!) is { } p)
                {
                    it.Series[key] = p;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "dl-helper : analyse de série impossible pour {Name}", name);
            }
        }
    }

    /// <summary>Fait apparaître le nouvel élément dans Jellyfin, selon le réglage LibraryRefresh.</summary>
    private void RefreshLibrary(PluginConfiguration c, string target)
    {
        try
        {
            switch (c.LibraryRefresh)
            {
                case "folder":
                    // Comme la surveillance en temps réel : Jellyfin n'analyse que le dossier concerné.
                    _libraryMonitor.ReportFileSystemChanged(target);
                    break;
                case "all":
                    _libraryManager.QueueLibraryScan();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dl-helper : impossible d'actualiser la médiathèque");
        }
    }

    /// <summary>Lance tout de suite l'analyse de toutes les médiathèques (bouton de la page).</summary>
    public void RefreshAllLibraries() => _libraryManager.QueueLibraryScan();

    public async Task SetMutedAsync(string key, bool mute, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (mute)
            {
                if (!_problems.TryGetValue(key, out var p))
                {
                    throw new KeyNotFoundException("ce problème n'est plus d'actualité");
                }

                State.Data.Muted[key] = p.Signature;
            }
            else
            {
                State.Data.Muted.Remove(key);
            }

            State.Save();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearHistoryAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            State.Data.History.Clear();
            State.Save();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ================================================================ lecture (API)

    public async Task<StatusSnapshot> GetStatusAsync(CancellationToken ct)
    {
        var c = Config;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var st = State.Data;
            var recv = ReceivingSummary();
            foreach (var (name, it) in st.Items.Where(kv => kv.Value.Status == ItemStatus.Pending && kv.Value.Series is null).ToList())
            {
                AnalyzeSeries(c, name, it); // éléments en attente d'avant cette version
            }
            var problems = _problems.Values
                .Select(p => new ProblemDto(p.Key, p.Message, st.Muted.TryGetValue(p.Key, out var sig) && sig == p.Signature, p.Since))
                .OrderBy(p => p.Muted)
                .ToList();
            return new StatusSnapshot
            {
                WatchDir = c.WatchDir,
                LastScan = LastScan,
                StartedAt = StartedAt,
                Version = Plugin.Instance!.Version.ToString(),
                SyncthingEnabled = !string.IsNullOrWhiteSpace(c.SyncthingUrl),
                SyncthingOk = _lastStatus is not null,
                SyncthingNeedsSetup = SyncthingNeedsSetup,
                SyncthingState = _lastStatus?.State,
                SyncthingNeedFiles = _lastStatus?.NeedFiles ?? 0,
                SyncthingNeedBytes = _lastStatus?.NeedBytes ?? 0,
                AutoAction = c.AutoAction,
                AutoDelayMinutes = c.AutoDelayMinutes,
                LibraryRefresh = c.LibraryRefresh,
                TransferRunning = _transferRunning,
                Problems = problems,
                Destinations = DestKeys.Select(k => new DestinationDto(
                    k, DestLabel[k], DestEmoji[k], DestPath(c, k)!, !_problems.ContainsKey($"Destination {k}"))).ToList(),
                Items = st.Items.Select(kv => ToDto(kv.Key, kv.Value, c, recv)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Receiving = recv.Where(r => !st.Items.ContainsKey(r.Key) || st.Items[r.Key].Status != ItemStatus.Downloading)
                    .Select(r => new ReceivingDto(r.Key, r.Value.Files, r.Value.Bytes)).ToList(),
                History = st.History.Take(50).ToList(),
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private Dictionary<string, (int Files, long Bytes)> ReceivingSummary()
    {
        var output = new Dictionary<string, (int Files, long Bytes)>();
        foreach (var (path, size) in _lastNeeded ?? new())
        {
            var top = TopLevel(path);
            var (n, b) = output.GetValueOrDefault(top);
            output[top] = (n + 1, b + size);
        }

        return output;
    }

    private static ItemDto ToDto(string name, TrackedItem it, PluginConfiguration c, Dictionary<string, (int Files, long Bytes)> recv)
    {
        DateTime? autoAt = null;
        if (c.AutoAction is "copy" or "move" && it.Status == ItemStatus.Pending && it.ChosenDest is not null && it.NotifiedAt is { } t)
        {
            autoAt = t.AddMinutes(c.AutoDelayMinutes);
        }

        recv.TryGetValue(name, out var r);
        return new ItemDto
        {
            Id = it.Id, Name = name, Status = it.Status, Size = it.Size, Count = it.Count, IsDir = it.IsDir,
            Suggest = it.Suggest, Dest = it.Dest, ChosenDest = it.ChosenDest, Rename = it.Rename,
            LastError = it.LastError, Target = it.Target, NotifiedAt = it.NotifiedAt, FirstSeen = it.FirstSeen,
            StableSince = it.StableSince, AutoActionAt = autoAt, ReceivingFiles = r.Files, ReceivingBytes = r.Bytes,
            Series = it.Series, UseSeries = it.UseSeries,
        };
    }

    /// <summary>Contenu complet du dossier surveillé (tailles calculées : peut prendre un moment).</summary>
    public async Task<List<FolderEntryDto>> GetFolderAsync(CancellationToken ct)
    {
        var c = Config;
        var entries = await Task.Run(
            () => FileOps.ListEntries(c.WatchDir).Select(e =>
            {
                var fp = FileOps.GetFingerprint(e.FullName);
                return (e.Name, IsDir: e is DirectoryInfo, fp.Size, fp.Count);
            }).ToList(),
            ct).ConfigureAwait(false);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return entries.Select(e =>
            {
                State.Data.Items.TryGetValue(e.Name, out var it);
                return new FolderEntryDto(it?.Id ?? StateStore.ItemId(e.Name), e.Name, e.IsDir, e.Size, e.Count, it?.Status ?? ItemStatus.Baseline);
            }).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record Problem(string Key, string Message, string Signature, DateTime Since);
}

public record ProblemDto(string Key, string Message, bool Muted, DateTime Since);

public record DestinationDto(string Key, string Label, string Emoji, string Path, bool Ok);

public record ReceivingDto(string Name, int Files, long Bytes);

public record FolderEntryDto(string Id, string Name, bool IsDir, long Size, int Count, string Status);

public class ItemDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public long Size { get; set; }

    public int Count { get; set; }

    public bool IsDir { get; set; }

    public string? Suggest { get; set; }

    public string? Dest { get; set; }

    public string? ChosenDest { get; set; }

    public string? Rename { get; set; }

    public string? LastError { get; set; }

    public string? Target { get; set; }

    public DateTime? NotifiedAt { get; set; }

    public DateTime FirstSeen { get; set; }

    public DateTime StableSince { get; set; }

    public DateTime? AutoActionAt { get; set; }

    public int ReceivingFiles { get; set; }

    public long ReceivingBytes { get; set; }

    public Dictionary<string, SeriesPlacement>? Series { get; set; }

    public bool UseSeries { get; set; }
}

public class StatusSnapshot
{
    public string WatchDir { get; set; } = string.Empty;

    public DateTime? LastScan { get; set; }

    public DateTime StartedAt { get; set; }

    public string Version { get; set; } = string.Empty;

    public bool SyncthingEnabled { get; set; }

    public bool SyncthingOk { get; set; }

    public bool SyncthingNeedsSetup { get; set; }

    public string? SyncthingState { get; set; }

    public long SyncthingNeedFiles { get; set; }

    public long SyncthingNeedBytes { get; set; }

    public string AutoAction { get; set; } = "none";

    public double AutoDelayMinutes { get; set; }

    public string LibraryRefresh { get; set; } = "folder";

    public bool TransferRunning { get; set; }

    public List<ProblemDto> Problems { get; set; } = new();

    public List<DestinationDto> Destinations { get; set; } = new();

    public List<ItemDto> Items { get; set; } = new();

    public List<ReceivingDto> Receiving { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = new();
}
