using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DlHelper.Services;

/// <summary>Analyse du système de fichiers : empreinte, suggestion de catégorie, transfert.</summary>
public static partial class FileOps
{
    public static readonly string[] TempSuffixes =
        { ".part", ".!qb", ".!ut", ".crdownload", ".tmp", ".aria2", ".partial", ".download", ".filepart" };

    public static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".alac", ".ape", ".wv", ".wma" };

    public static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".webm", ".mpg", ".mpeg" };

    private static readonly Regex SeriesRe = new(
        @"\bS\d{1,2}(E\d{1,3})?\b|\b\d{1,2}x\d{2}\b|saison|season|int[ée]grale|complete",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnimeRe = new(
        @"^\[[^\]]+\]|\banime\b|subsplease|erai-raws|horriblesubs|judas|\bnyaa\b|crunchyroll|\s-\s\d{2,4}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Éléments visibles à la racine du dossier surveillé (sans .stfolder, .DS_Store…).</summary>
    public static List<FileSystemInfo> ListEntries(string watchDir)
    {
        var dir = new DirectoryInfo(watchDir);
        return dir.EnumerateFileSystemInfos()
            .Where(e => !e.Name.StartsWith('.'))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Tous les fichiers (récursif) sous path, ou path lui-même si c'est un fichier.</summary>
    public static IEnumerable<FileInfo> IterFiles(string path)
    {
        if (File.Exists(path))
        {
            return new[] { new FileInfo(path) };
        }

        if (!Directory.Exists(path))
        {
            return Array.Empty<FileInfo>();
        }

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return new DirectoryInfo(path).EnumerateFiles("*", opts);
    }

    public static bool IsTemp(string name) =>
        TempSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// (taille totale, nb fichiers, dernière activité locale, contient-un-fichier-temporaire).
    /// Syncthing recopie le mtime d'origine : on prend aussi la date de création locale (btime),
    /// qui correspond au moment où le fichier est arrivé sur ce serveur.
    /// </summary>
    public static Fingerprint GetFingerprint(string path)
    {
        long size = 0;
        int count = 0;
        var newest = DateTime.MinValue;
        var hasTemp = IsTemp(Path.GetFileName(path));
        foreach (var f in IterFiles(path))
        {
            try
            {
                f.Refresh();
                size += f.Length;
                var t = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc;
                if (t > newest)
                {
                    newest = t;
                }

                count++;
                if (IsTemp(f.Name))
                {
                    hasTemp = true;
                }
            }
            catch (IOException)
            {
                hasTemp = true; // fichier qui disparaît pendant le scan → encore en cours
            }
        }

        // Une date dans le futur (horloge d'origine) ne doit pas bloquer la détection indéfiniment.
        if (newest > DateTime.UtcNow)
        {
            newest = DateTime.UtcNow;
        }

        return new Fingerprint(size, count, newest, hasTemp);
    }

    /// <summary>Devine la catégorie. null = ni vidéo ni audio (logiciel, jeu…).</summary>
    public static string? SuggestDest(string path)
    {
        long audio = 0, video = 0;
        int nVideo = 0;
        foreach (var f in IterFiles(path))
        {
            long sz;
            try
            {
                sz = f.Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (AudioExt.Contains(f.Extension))
            {
                audio += sz;
            }
            else if (VideoExt.Contains(f.Extension))
            {
                video += sz;
                nVideo++;
            }
        }

        if (audio == 0 && video == 0)
        {
            return null;
        }

        if (audio > video)
        {
            return "MUSIQUE";
        }

        var name = Path.GetFileName(path);
        if (AnimeRe.IsMatch(name))
        {
            return "ANIME";
        }

        if (SeriesRe.IsMatch(name) || nVideo >= 3)
        {
            return "SERIES";
        }

        return "FILMS";
    }

    /// <summary>Valide un nouveau nom. Vide = nom d'origine. Garde l'extension d'un fichier.</summary>
    public static string CleanNewName(string? raw, string original, bool isFile)
    {
        var parts = (raw ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join(' ', parts);
        if (name.Length == 0)
        {
            return original;
        }

        if (name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0 || name is "." or ".." || name.StartsWith('.'))
        {
            throw new ArgumentException("pas de « / », et le nom ne peut pas commencer par un point");
        }

        var ext = isFile ? Path.GetExtension(original) : string.Empty;
        if (ext.Length > 0 && !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        {
            name += ext;
        }

        if (Encoding.UTF8.GetByteCount(name) > 255)
        {
            throw new ArgumentException("nom trop long (255 octets max)");
        }

        return name;
    }

    public static string UniqueTarget(string destDir, string name, bool isFile)
    {
        var target = Path.Combine(destDir, name);
        if (!Exists(target))
        {
            return target;
        }

        var stem = isFile ? Path.GetFileNameWithoutExtension(name) : name;
        var suffix = isFile ? Path.GetExtension(name) : string.Empty;
        var i = 2;
        while (Exists(Path.Combine(destDir, $"{stem} ({i}){suffix}")))
        {
            i++;
        }

        return Path.Combine(destDir, $"{stem} ({i}){suffix}");
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>
    /// Copie (hardlinks si possible) ou déplace src dans destDir, éventuellement sous un nouveau nom
    /// (la source n'est pas renommée). Bloquant. Renvoie le chemin final.
    /// </summary>
    public static string Transfer(string src, string destDir, string action, string? newName, bool useHardlinks)
    {
        Directory.CreateDirectory(destDir);
        var isFile = File.Exists(src);
        if (!isFile && !Directory.Exists(src))
        {
            throw new FileNotFoundException("élément introuvable dans le dossier surveillé", src);
        }

        var target = UniqueTarget(destDir, newName ?? Path.GetFileName(src), isFile);
        if (action == "move")
        {
            Move(src, target, isFile, useHardlinks);
        }
        else if (isFile)
        {
            LinkOrCopy(src, target, useHardlinks);
        }
        else
        {
            CopyTree(src, target, useHardlinks);
        }

        return target;
    }

    private static void Move(string src, string target, bool isFile, bool useHardlinks)
    {
        try
        {
            if (isFile)
            {
                File.Move(src, target);
            }
            else
            {
                Directory.Move(src, target);
            }
        }
        catch (IOException) when (!Exists(target))
        {
            // Autre système de fichiers (ou autre point de montage) : copie puis suppression.
            if (isFile)
            {
                LinkOrCopy(src, target, useHardlinks);
                File.Delete(src);
            }
            else
            {
                CopyTree(src, target, useHardlinks);
                Directory.Delete(src, recursive: true);
            }
        }
    }

    private static void CopyTree(string src, string target, bool useHardlinks)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(src, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            LinkOrCopy(file, Path.Combine(target, Path.GetRelativePath(src, file)), useHardlinks);
        }
    }

    private static void LinkOrCopy(string src, string dst, bool useHardlinks)
    {
        if (useHardlinks && TryHardLink(src, dst))
        {
            return;
        }

        File.Copy(src, dst);
        File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
    }

    // ------------------------------------------------------------------ hardlinks

    public static bool TryHardLink(string existing, string newPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLinkW(newPath, existing, IntPtr.Zero);
            }

            try
            {
                return LinkGlibc(existing, newPath) == 0; // glibc (image linuxserver/jellyfin : Ubuntu)
            }
            catch (DllNotFoundException)
            {
                return Link(existing, newPath) == 0; // musl, macOS…
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc.so.6", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkGlibc(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string oldPath, string newPath);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string newFile, string existingFile, IntPtr securityAttributes);

    // ------------------------------------------------------------------ affichage

    public static string HumanSize(double n)
    {
        string[] units = { "o", "Ko", "Mo", "Go", "To" };
        foreach (var u in units)
        {
            if (n < 1024)
            {
                return u == "o" ? $"{(long)n} o" : $"{n:0.0} {u}";
            }

            n /= 1024;
        }

        return $"{n:0.0} Po";
    }
}

public readonly record struct Fingerprint(long Size, int Count, DateTime Newest, bool HasTemp);
