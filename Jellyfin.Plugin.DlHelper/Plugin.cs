using Jellyfin.Plugin.DlHelper.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DlHelper;

/// <summary>
/// dl-helper : range les nouveaux téléchargements (Syncthing) dans ANIME / MUSIQUE / FILMS / SERIES,
/// depuis l'interface web de Jellyfin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("252483f4-bd35-44be-ba5e-49e971a89efe");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "dl-helper";

    public override Guid Id => PluginId;

    public override string Description =>
        "Range les nouveaux téléchargements Syncthing dans Anime, Musique, Films ou Séries, en un clic.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            // Page de réglages, en premier : c'est celle qu'ouvre Tableau de bord → Extensions → dl-helper.
            new PluginPageInfo
            {
                Name = "dlhelperconfig",
                EmbeddedResourcePath = ns + ".Configuration.configPage.html",
            },
            // Page principale : visible dans le menu latéral (section administration).
            new PluginPageInfo
            {
                Name = "dlhelper",
                DisplayName = "Téléchargements",
                EmbeddedResourcePath = ns + ".Web.dlhelper.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "download",
            },
        };
    }
}
