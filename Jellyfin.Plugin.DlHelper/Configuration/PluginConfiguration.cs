using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DlHelper.Configuration;

/// <summary>Réglages du plugin (modifiables dans Tableau de bord → Extensions → dl-helper).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // --- Dossiers (chemins tels que vus par Jellyfin : dans le conteneur s'il tourne en Docker) ---
    public string WatchDir { get; set; } = "/space/TELECHARGEMENTS";

    public string DestAnime { get; set; } = "/space/ANIME";

    public string DestMusique { get; set; } = "/space/musique";

    public string DestFilms { get; set; } = "/space/FILMS";

    public string DestSeries { get; set; } = "/space/SERIES";

    // --- Syncthing (vide = détection par stabilité des fichiers uniquement) ---
    public string SyncthingUrl { get; set; } = "http://127.0.0.1:8384";

    /// <summary>config.xml de Syncthing : la clé API et l'ID du dossier y sont lus si les champs ci-dessous sont vides.</summary>
    public string SyncthingConfig { get; set; } = string.Empty;

    public string SyncthingApiKey { get; set; } = string.Empty;

    public string SyncthingFolderId { get; set; } = "2ygqt-rnkbj";

    // --- Détection ---
    public int PollInterval { get; set; } = 30;

    public int StableSeconds { get; set; } = 120;

    // --- Rangement ---
    public bool UseHardlinks { get; set; } = true;

    /// <summary>none | copy | move</summary>
    public string AutoAction { get; set; } = "none";

    public double AutoDelayMinutes { get; set; } = 60;

    /// <summary>Lance une analyse des médiathèques Jellyfin après chaque rangement réussi.</summary>
    public bool RefreshLibraryAfterTransfer { get; set; } = true;

    // --- Notifications ---

    /// <summary>Message à l'écran pour les administrateurs connectés (nouveau téléchargement, échec…).</summary>
    public bool NotifyPopup { get; set; } = true;

    /// <summary>Rangements et problèmes dans Tableau de bord → Activité.</summary>
    public bool NotifyActivityLog { get; set; } = true;

    /// <summary>Adresse publique de Jellyfin (ex. https://jellyfin.exemple.fr) : lien « ouvrir » dans les notifications.</summary>
    public string JellyfinPublicUrl { get; set; } = string.Empty;

    // ntfy : push sur le téléphone (appli ntfy). Topic vide = désactivé.
    public string NtfyServer { get; set; } = "https://ntfy.sh";

    public string NtfyTopic { get; set; } = string.Empty;

    public string NtfyToken { get; set; } = string.Empty;

    /// <summary>Webhook d'un salon Discord (vide = désactivé).</summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;
}
