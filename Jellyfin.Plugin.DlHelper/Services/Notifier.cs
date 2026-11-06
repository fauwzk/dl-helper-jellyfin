using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DlHelper.Services;

/// <summary>
/// Notifications :
///  • message à l'écran pour les administrateurs connectés à Jellyfin ;
///  • push sur le téléphone via ntfy et/ou un webhook Discord (Jellyfin n'a pas de push natif) ;
///  • journal d'activité (Tableau de bord → Activité).
/// </summary>
public sealed class Notifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ISessionManager _sessions;
    private readonly IUserManager _users;
    private readonly IActivityManager _activity;
    private readonly ILogger<Notifier> _log;

    public Notifier(ISessionManager sessions, IUserManager users, IActivityManager activity, ILogger<Notifier> log)
    {
        _sessions = sessions;
        _users = users;
        _activity = activity;
        _log = log;
    }

    /// <summary>Lien qui ouvre la page Téléchargements (si l'adresse publique de Jellyfin est renseignée).</summary>
    public static string? PageUrl()
    {
        var baseUrl = Plugin.Instance?.Configuration.JellyfinPublicUrl?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(baseUrl) ? null : baseUrl + "/web/#/configurationpage?name=dlhelper";
    }

    /// <summary>Prévient partout (écran + téléphone). tag = emoji ntfy (ex. inbox_tray, warning, x).</summary>
    public Task NotifyAsync(string title, string text, string tag = "inbox_tray", bool urgent = false) =>
        Task.WhenAll(PopupAsync(title, text), NtfyAsync(title, text, tag, urgent), DiscordAsync(title, text, urgent));

    private async Task PopupAsync(string header, string text)
    {
        if (Plugin.Instance?.Configuration.NotifyPopup != true)
        {
            return;
        }

        var targets = _sessions.Sessions
            .Where(s => s.IsActive && !s.UserId.Equals(Guid.Empty)
                        && s.SupportedCommands.Contains(GeneralCommandType.DisplayMessage)
                        && _users.GetUserById(s.UserId)?.HasPermission(PermissionKind.IsAdministrator) == true)
            .ToList();

        foreach (var s in targets)
        {
            try
            {
                await _sessions.SendMessageCommand(
                    null,
                    s.Id,
                    new MessageCommand { Header = header, Text = text, TimeoutMs = 15000 },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "dl-helper : message non envoyé à la session {Session}", s.Id);
            }
        }
    }

    private async Task NtfyAsync(string title, string text, string tag, bool urgent)
    {
        var c = Plugin.Instance?.Configuration;
        if (c is null || string.IsNullOrWhiteSpace(c.NtfyTopic))
        {
            return;
        }

        try
        {
            await SendNtfyAsync(c.NtfyServer, c.NtfyTopic, c.NtfyToken, title, text, tag, urgent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dl-helper : notification ntfy non envoyée");
        }
    }

    /// <summary>Publication ntfy en JSON (gère les accents dans le titre, contrairement aux en-têtes).</summary>
    public static async Task SendNtfyAsync(string server, string topic, string token, string title, string text, string tag, bool urgent)
    {
        var url = string.IsNullOrWhiteSpace(server) ? "https://ntfy.sh" : server.Trim().TrimEnd('/');
        var payload = new Dictionary<string, object>
        {
            ["topic"] = topic.Trim(),
            ["title"] = title,
            ["message"] = text,
            ["tags"] = new[] { tag },
            ["priority"] = urgent ? 4 : 3,
        };
        if (PageUrl() is { } click)
        {
            payload["click"] = click;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async Task DiscordAsync(string title, string text, bool urgent)
    {
        var c = Plugin.Instance?.Configuration;
        if (c is null || string.IsNullOrWhiteSpace(c.DiscordWebhookUrl))
        {
            return;
        }

        try
        {
            await SendDiscordAsync(c.DiscordWebhookUrl, title, text, urgent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dl-helper : notification Discord non envoyée");
        }
    }

    public static async Task SendDiscordAsync(string webhook, string title, string text, bool urgent)
    {
        var embed = new Dictionary<string, object>
        {
            ["title"] = title.Length > 256 ? title[..256] : title,
            ["description"] = (text.Length > 3900 ? text[..3900] : text) + (PageUrl() is { } u ? $"\n\n[Ouvrir dans Jellyfin]({u})" : string.Empty),
            ["color"] = urgent ? 0xED4245 : 0x5865F2,
        };
        if (PageUrl() is { } link)
        {
            embed["url"] = link;
        }

        using var resp = await Http.PostAsJsonAsync(webhook.Trim(), new { username = "dl-helper", embeds = new[] { embed } }).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Ajoute une ligne au journal d'activité de Jellyfin.</summary>
    public async Task ActivityAsync(string name, string overview, LogLevel severity = LogLevel.Information)
    {
        if (Plugin.Instance?.Configuration.NotifyActivityLog != true)
        {
            return;
        }

        try
        {
            await _activity.CreateAsync(new ActivityLog(name, "DlHelper", Guid.Empty)
            {
                ShortOverview = overview,
                LogSeverity = severity,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "dl-helper : impossible d'écrire dans le journal d'activité");
        }
    }
}
