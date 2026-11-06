using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.DlHelper.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DlHelper.Api;

/// <summary>API utilisée par la page « Téléchargements ». Réservée aux administrateurs.</summary>
[ApiController]
[Route("DlHelper")]
[Authorize(Policy = "RequiresElevation")]
public class DlHelperController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DownloadWatcher _watcher;

    public DlHelperController(DownloadWatcher watcher)
    {
        _watcher = watcher;
    }

    private string Who => User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name ?? "?";

    /// <summary>JSON en camelCase, quelle que soit la configuration JSON de Jellyfin.</summary>
    private ContentResult Json(object value) =>
        Content(JsonSerializer.Serialize(value, JsonOptions), "application/json; charset=utf-8");

    private async Task<IActionResult> Run(Func<Task<object?>> action)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            return result is null ? NoContent() : Json(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>État complet : santé, éléments suivis, réception Syncthing, historique.</summary>
    [HttpGet("Status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct) =>
        Json(await _watcher.GetStatusAsync(ct).ConfigureAwait(false));

    /// <summary>Contenu complet du dossier surveillé, avec les tailles.</summary>
    [HttpGet("Folder")]
    public async Task<IActionResult> GetFolder(CancellationToken ct) =>
        Json(await _watcher.GetFolderAsync(ct).ConfigureAwait(false));

    /// <summary>Choisit la destination et/ou le nouveau nom d'un élément (chaîne vide = réinitialiser).</summary>
    [HttpPost("Items/{id}/Select")]
    public Task<IActionResult> Select([FromRoute] string id, [FromBody] SelectRequest body, CancellationToken ct) =>
        Run(async () => (object?)await _watcher.SelectAsync(id, body.Destination, body.Rename, ct).ConfigureAwait(false));

    /// <summary>copy | move | ignore.</summary>
    [HttpPost("Items/{id}/Action")]
    public Task<IActionResult> DoAction([FromRoute] string id, [FromBody] ActionRequest body, CancellationToken ct) =>
        Run(async () =>
        {
            await _watcher.StartActionAsync(id, body.Action ?? string.Empty, body.Destination, Who, ct).ConfigureAwait(false);
            return null;
        });

    /// <summary>Remet un élément (déjà présent, ignoré, copié) dans la liste « à ranger ».</summary>
    [HttpPost("Items/{id}/Restore")]
    public Task<IActionResult> Restore([FromRoute] string id, CancellationToken ct) =>
        Run(async () =>
        {
            await _watcher.RestoreAsync(id, ct).ConfigureAwait(false);
            return null;
        });

    [HttpPost("Scan")]
    public IActionResult Scan()
    {
        _watcher.WakeUp();
        return NoContent();
    }

    [HttpPost("Problems/Mute")]
    public Task<IActionResult> Mute([FromBody] ProblemRequest body, CancellationToken ct) =>
        Run(async () =>
        {
            await _watcher.SetMutedAsync(body.Key ?? string.Empty, body.Mute, ct).ConfigureAwait(false);
            return null;
        });

    [HttpDelete("History")]
    public Task<IActionResult> ClearHistory(CancellationToken ct) =>
        Run(async () =>
        {
            await _watcher.ClearHistoryAsync(ct).ConfigureAwait(false);
            return null;
        });

    // ------------------------------------------------------------ outils pour la page de réglages

    /// <summary>Liste les dossiers Syncthing (pour choisir le bon sans recopier son ID).</summary>
    [HttpPost("Syncthing/Folders")]
    public async Task<IActionResult> SyncthingFolders([FromBody] SyncthingRequest body, CancellationToken ct)
    {
        try
        {
            var folders = await SyncthingClient.FoldersAsync(body.Url ?? string.Empty, body.ApiKey ?? string.Empty, ct).ConfigureAwait(false);
            return Json(folders);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or JsonException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Envoie une notification de test (ntfy et/ou Discord) avec les valeurs saisies.</summary>
    [HttpPost("Notify/Test")]
    public async Task<IActionResult> TestNotify([FromBody] NotifyTestRequest body)
    {
        var errors = new List<string>();
        const string Title = "🧪 Test dl-helper";
        const string Text = "Les notifications fonctionnent. Tu seras prévenu quand un téléchargement sera prêt à ranger.";
        if (!string.IsNullOrWhiteSpace(body.NtfyTopic))
        {
            try
            {
                await Notifier.SendNtfyAsync(body.NtfyServer ?? string.Empty, body.NtfyTopic, body.NtfyToken ?? string.Empty, Title, Text, "test_tube", false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add("ntfy : " + ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(body.DiscordWebhookUrl))
        {
            try
            {
                await Notifier.SendDiscordAsync(body.DiscordWebhookUrl, Title, Text, false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add("Discord : " + ex.Message);
            }
        }

        return errors.Count == 0 ? NoContent() : BadRequest(new { error = string.Join(" · ", errors) });
    }
}

public class SelectRequest
{
    public string? Destination { get; set; }

    public string? Rename { get; set; }
}

public class ActionRequest
{
    public string? Action { get; set; }

    public string? Destination { get; set; }
}

public class ProblemRequest
{
    public string? Key { get; set; }

    public bool Mute { get; set; } = true;
}

public class SyncthingRequest
{
    public string? Url { get; set; }

    public string? ApiKey { get; set; }
}

public class NotifyTestRequest
{
    public string? NtfyServer { get; set; }

    public string? NtfyTopic { get; set; }

    public string? NtfyToken { get; set; }

    public string? DiscordWebhookUrl { get; set; }
}
