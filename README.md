# dl-helper pour Jellyfin

Plugin Jellyfin qui fait la même chose que le bot Discord dl-helper, mais **directement dans l'interface web de Jellyfin**. Il surveille le dossier de téléchargements rempli par **Syncthing**. Pour chaque **nouveau** téléchargement terminé, il te propose de le **copier** ou de le **déplacer** dans le bon dossier, en un clic.

```
📥 Téléchargements                                   [🔄 Actualiser] [⚙️]
Syncthing : idle · Destinations : ✅ · Mode auto : désactivé · Dernier scan : à l'instant

🆕 À ranger  1
┌───────────────────────────────────────────────────────────────┐
│ 📁 Daft Punk - Discovery                                      │
│ 📦 640,0 Mo   🗂️ 14 fichiers   🕘 il y a 3 min                │
│ (🎌 Anime) (🎵 Musique suggéré) (🎬 Films) (📺 Séries)        │
│ [📋 Copier] [✂️ Déplacer] [✏️ Renommer] [🚫 Ignorer]          │
└───────────────────────────────────────────────────────────────┘
⏬ En cours · 🕘 Historique · 📂 Tout le dossier
```

Accès : menu latéral → **Téléchargements** (administrateurs uniquement).

## Ce qui change par rapport au bot

| | Bot Discord | Plugin Jellyfin |
|---|---|---|
| Où on range | boutons Discord | page **Téléchargements** dans Jellyfin |
| Détection, suggestion, hardlinks, renommage, mode auto, erreurs ignorées 🔕 | ✅ | ✅ (même logique) |
| Réglages | `/config` | Tableau de bord → Extensions → **dl-helper** |
| Séries et animes | rangés tels quels | **ajoutés dans le dossier de la série**, dans la bonne saison |
| Après un rangement | — | **médiathèque actualisée** tout de suite (dossier rangé ou tout) |
| Notifications | messages dans le salon | message à l'écran dans Jellyfin, **ntfy** (push téléphone), webhook Discord, journal d'activité |
| Mises à jour | automatiques depuis GitHub | catalogue d'extensions de Jellyfin |

Les commandes du bot ont leur équivalent sur la page : `/statut` correspond aux pastilles et aux problèmes en haut, `/reception` et `/attente` à « En cours » et « À ranger », `/dossier` et `/ranger` à « Tout le dossier » avec son bouton **Ranger**.

> ⚠️ **Ne pas faire tourner le bot et le plugin en même temps avec le mode automatique activé.** Ils pourraient déplacer le même élément deux fois. Le plus simple : arrêter le bot une fois le plugin en place (`sudo systemctl disable --now dl-helper`).

## Notifications

Jellyfin n'a pas de notifications push : ses applis mobiles n'affichent un message que si elles sont ouvertes. Le plugin propose donc :

- **Message à l'écran** pour les administrateurs connectés à Jellyfin (navigateur ou appli ouverte).
- **ntfy** (recommandé pour le téléphone) : installe l'appli [ntfy](https://ntfy.sh) (Android / iOS), abonne-toi à un topic au nom difficile à deviner (par ex. `dlhelper-moon-8f3k2`), et mets le même nom dans les réglages. Renseigne aussi **Adresse de Jellyfin** : toucher la notification ouvre alors la page Téléchargements.
- **Webhook Discord** : prévient dans un salon, avec un lien vers Jellyfin.
- **Journal d'activité** : Tableau de bord → Activité.

Le bouton **🧪 Envoyer une notification de test** vérifie la config avant d'enregistrer.

Tu es prévenu quand un téléchargement est prêt à ranger, quand un transfert échoue et quand un problème apparaît (dossier introuvable, Syncthing injoignable…).

## Installer

Le plugin vise **Jellyfin 12.4**.

1. Dans Jellyfin : **Tableau de bord → Extensions → Dépôts → ➕**, avec :
   - Nom : `dl-helper`
   - URL : `https://raw.githubusercontent.com/fauwzk/dl-helper-jellyfin/main/manifest.json`
2. **Catalogue** → **dl-helper** → **Installer**.
3. Redémarre Jellyfin (Tableau de bord → **Redémarrer**, ou `docker restart jellyfin`).

Les nouvelles versions apparaissent ensuite dans Extensions → **Mises à jour**, comme pour les plugins officiels.

## Configurer

**Tableau de bord → Extensions → dl-helper** :

1. **Dossiers** : chemins tels que vus par Jellyfin. En Docker, ce sont les chemins **dans le conteneur**. Par défaut `/space/...` : sur moon, `/ranger` est monté sur `/space`. Pour que « Copier » fasse des hardlinks, le dossier surveillé et les destinations doivent être dans le **même montage**.
2. **Syncthing** : `http://127.0.0.1:8384` si Jellyfin est en réseau `host`. Colle la **clé API** (interface Syncthing → Actions → Configuration → Général), puis **🔎 Trouver le dossier** et choisis le bon. Si Jellyfin peut lire le `config.xml` de Syncthing, tu peux aussi indiquer son chemin à la place.
3. **Notifications** : ntfy et/ou Discord, puis le bouton de test.
4. **Enregistrer**. Les changements s'appliquent tout de suite, sans redémarrage.

Au premier lancement, tout ce qui est déjà dans le dossier surveillé est mémorisé et ignoré. Pour en ranger un, passe par **📂 Tout le dossier → Ranger**.

## Développer

Il faut le SDK .NET 10.

```bash
./build.sh            # → artifacts/dl-helper_<version>/ (+ .zip)
```

Installation à la main, sans le dépôt : copie `artifacts/dl-helper_<version>/` dans le dossier `plugins` de Jellyfin (`/config/data/plugins/` avec l'image linuxserver), puis redémarre Jellyfin.

**Publier une version** : il suffit de pousser un tag. GitHub Actions compile, crée la release avec le `.zip`, puis met à jour `manifest.json` sur `main`.

```bash
git tag -a v1.0.1.0 -m "Ce qui change"
git push origin v1.0.1.0
```

Si Jellyfin change de version majeure, adapte `<JellyfinVersion>` (et au besoin `<TargetFramework>`) dans le `.csproj`.

## Fonctionnement

- **Détection « terminé »** : Syncthing n'a plus rien à recevoir pour l'élément, il n'y a aucun fichier temporaire (`.part`, `.syncthing.*.tmp`…), et la taille, le nombre de fichiers et la date d'arrivée n'ont pas changé depuis `StableSeconds` (120 s par défaut). Syncthing recopie la date de modification d'origine : le plugin prend donc aussi la date de création locale, qui correspond à l'arrivée du fichier sur le serveur.
- **Suggestion** : surtout de l'audio → Musique ; `[Groupe] Titre - 12` → Anime ; `S01E02`, `Saison`, `Intégrale` ou 3 vidéos et plus → Séries ; sinon une vidéo → Films ; ni vidéo ni audio → pas de suggestion.
- **Copier** fait des hardlinks quand c'est possible (même système de fichiers et même montage), sinon une vraie copie. **Déplacer** renomme, ou copie puis supprime si les dossiers sont sur des disques différents.
- **Doublons** : si le nom existe déjà dans la destination, ` (2)`, ` (3)`… est ajouté.
- **Séries et animes** : en Séries ou en Anime, le plugin reconnaît `Titre S03`, `Titre S03E05`, `Titre Saison 3`, `Titre 3x05` ou `[Groupe] Titre - 12`. Il cherche ensuite le dossier de la série dans la destination, même si le nom diffère un peu (année, « US », accents, apostrophes), puis propose **Ajouter à Série › Saison** :
  - si la saison existe déjà, les épisodes y sont ajoutés, et ceux déjà présents sont signalés ;
  - sinon, une saison est créée sur le modèle des autres (`S03`, `Season.03`, `Saison 3`…) ;
  - si la série n'existe pas encore, elle est créée avec `Titre/Season 03`.

  Une case permet de ranger l'élément tel quel à la place. Un épisode d'anime sans saison va dans la dernière saison de la série.
- **Médiathèque** : après un rangement, Jellyfin analyse seulement le dossier ajouté (réglage *Actualiser la médiathèque*). Tu peux choisir d'analyser toutes les médiathèques, ou de ne rien faire. Le bouton **📚 Médiathèques** lance une analyse complète à la main.
- **État** : `state.json` dans le dossier de données du plugin (`/config/data/plugins/configurations/…` ou à côté du `.dll`, selon Jellyfin). Il survit aux redémarrages.
- **Syncthing en réception seule** : un élément déplacé apparaît dans « Modifications locales ». **Ne clique pas sur « Revert Local Changes »**, sinon Syncthing re-télécharge tout.

## API

Toutes les routes demandent un compte administrateur (`/DlHelper/...`) :

| Méthode | Route | Rôle |
|---|---|---|
| GET | `Status` | santé, éléments suivis, réception Syncthing, historique |
| GET | `Folder` | contenu complet du dossier surveillé (avec tailles) |
| POST | `Items/{id}/Select` | `{Destination, Rename, UseSeries}` |
| POST | `Library/Refresh` | analyser toutes les médiathèques |
| POST | `Items/{id}/Action` | `{Action: copy\|move\|ignore, Destination?}` |
| POST | `Items/{id}/Restore` | remettre un élément dans « À ranger » |
| POST | `Scan` | vérifier tout de suite |
| POST | `Problems/Mute` | `{Key, Mute}` |
| DELETE | `History` | effacer l'historique |
| POST | `Syncthing/Folders` | `{Url, ApiKey}` → dossiers Syncthing |
| POST | `Notify/Test` | `{NtfyServer, NtfyTopic, NtfyToken, DiscordWebhookUrl}` |

## Licence

GPL-3.0, comme Jellyfin, dont le plugin utilise les bibliothèques.
