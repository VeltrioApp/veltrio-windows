# Où en est Veltrio Windows

_Mis à jour le 19/11/2026, après le commit `669d959`. À relire en début de session._

Le portage de l'app macOS (`veltrio-macos`, environ 20 000 lignes de Swift) vers
WinUI 3 / C# avance par lots. Chaque lot est testé dans la VM Parallels « Windows 11 »,
puis poussé sur `main` ; la CI doit passer.

## Fait

| Lot | Contenu | Commit |
|---|---|---|
| Écrans de base | Accueil (hero, Reprendre, rangées numérotées, Tout voir), fiche (saisons, épisodes, flux classés et filtrés), Recherche, Collection, Direct, Sources, Réglages ; look éditorial du macOS, interface en français | `4e51659` |
| Lecteur libmpv | mpv → ANGLE (GL ES sur D3D11) → `SwapChainPanel`, contrôles XAML par-dessus, `player.log` dans le dossier de données | `84a7647` |
| Release | `v0.1.0-beta.2` publiée (la beta.1 ne démarre pas : `Veltrio.pri` manquait). Un tag `vX.Y.Z-suffixe` crée une préversion | `18c5717` |
| Debrid et catalogue d'addons | Real-Debrid (code), AllDebrid (PIN, via le core), TorBox et Premiumize (clé) ; badges ⚡ et filtre « En cache » ; catalogue officiel et stremio-addons.net | `c6a2c3e` |
| IPTV | Xtream Codes (direct, films, séries, replay), guide XMLTV, vue TV et grille du guide, zapping, replay dans le lecteur | `45179fd` |
| Lecteur complet et écarts IPTV | Épisode suivant, saut d'intro, sous-titres d'addons ou de fichier, menu ⚙ (format, zoom, décalages, tone mapping, profils, son direct vers l'ampli), infos média ; M3U local, recherche Xtream côté serveur, cartes EPG, panneau Replay | `669d959` |

## Testé ou pas

Chaque lot a été vérifié à l'écran dans la VM. Les tests IPTV utilisent le faux
panneau `scripts/mock-xtream.py`.

**Jamais testé :**
- Un compte debrid réel, de la connexion jusqu'à la lecture d'un torrent via debrid.
- La lecture d'un torrent en général.
- Un vrai panneau Xtream, et un vrai guide XMLTV de plusieurs dizaines de Mo.
- L'heure du replay : elle est passée en UTC comme sur macOS ; certains panneaux attendent l'heure locale du serveur.
- Les sous-menus du menu ⚙ du lecteur. Seuls les noms des options mpv ont été vérifiés.
- Le saut d'intro (aucun fichier de test avec un chapitre d'intro).
- Les sous-titres d'addons et l'« épisode suivant » sur un flux d'addon (il faut un flux d'addon lisible).
- Le décodage matériel (la VM n'en a pas).
- Le premier lancement sur un profil Windows vide.
- La beta.2 chez un vrai testeur.

## Prochaines étapes (dans l'ordre)

1. **Comptes** : import de compte Stremio (bibliothèque), Trakt et SIMKL (synchro, scrobbling, import).
   Côté macOS : `StremioAccountService.swift`, `TraktService.swift`, `SimklService.swift`, `WatchStore.merge`.
2. **Serveurs média** : Jellyfin, Emby, Plex (connexion, bibliothèques dans Collection, séries).
   Côté macOS : `JellyfinService`, `EmbyService`, `PlexService`, `MediaLibrary`, `Views/MediaLibraryViews`, `Views/MediaServerCards`.
3. **Sources directes sur les fiches** : copies du titre trouvées chez le fournisseur IPTV et sur les serveurs média, à côté des flux d'addons.
   Côté macOS : `DirectSources.swift`, `MediaBrowserMatching.swift`, `XtreamClient.findMovie/findEpisode`.
4. **Réglages** : langues préférées (audio, sous-titres ; aujourd'hui « fr » est figé dans `MpvPlayer` et `PlayerPage.AutoAddSubtitle`), thème clair, stockage et cache.
5. **Divers** : téléchargements hors ligne (`DownloadManager`), Chromecast (`ChromecastService`).
6. **Distribution Windows** : signature du code, mises à jour automatiques, installeur (MSIX ou setup), build ARM64 native.

## Décisions en attente (côté utilisateur)

- **Copier libmpv dans une release du dépôt.** `mpv.version` pointe sur une version de zhongfly/mpv-winbuild, qui supprime ses anciennes versions : le build cassera le jour où celle-ci disparaîtra. Une tentative de `gh release create` a été refusée par les permissions ; il faut un accord explicite.
- **Correctif Real-Debrid à reporter sur macOS.** RD a désactivé `instantAvailability`, donc le macOS n'utilise sans doute plus RD pour lire. Windows ajoute le torrent à la lecture, le lit s'il est déjà téléchargé, sinon le retire.
- **Publier une `v0.1.0-beta.3`** avec le debrid, l'IPTV et le lecteur complet.

## Pièges connus

- **Compilateur XAML de WindowsAppSDK 2.6.5.** `x:Bind` dans des `DataTemplate` de `Page.Resources` le fait planter (WMC9999), et son message d'erreur ne s'affiche pas. Utiliser `{Binding}` dans ces templates (voir `DetailPage.xaml`).
- **`EnableMsixTooling` doit rester à `true`**, sinon `dotnet publish` n'inclut pas `Veltrio.pri` et l'app se ferme au lancement. Le workflow de release vérifie ce fichier.
- **mpv avec `keep-open=yes`** : la fin naturelle arrive par `eof-reached`, pas par `end-file`.
- **TLS de mpv** (OpenSSL) : il vérifie contre `cacert.pem` (bundle de curl), livré dans `mpv/`.
- **Wine / CrossOver ne peut pas lancer l'app** : Windows App SDK appelle `NtCreateWaitCompletionPacket`, absent de Wine.

## Tester depuis le Mac

```bash
./scripts/fetch-core.sh && ./scripts/fetch-mpv.sh            # une fois
./scripts/parallels-run.sh                                    # compile dans la VM et lance l'app
python3 scripts/mock-xtream.py 10.211.55.2 8770               # faux panneau Xtream (test / pass)
```

- **Horloge de la VM** : si la VM sort de pause avec une horloge en retard, TLS échoue ; `parallels-run.sh` la recale.
- **Captures d'écran** : celles de Parallels (`prlctl capture`) sortent parfois noires pendant la vidéo. Une capture faite depuis Windows est plus fiable, mais elle ferme les menus ouverts.
- **Windows Defender** a classé « Trojan:Win32/Commando.B!ml » une commande PowerShell qui télécharge un script puis l'exécute avec `-ExecutionPolicy Bypass`. Copier les scripts dans la VM d'abord, puis les lancer.
- **Restes dans la VM :**
  - les sources de test « Panneau de test », « Tests » et « Liste locale », à supprimer dans Sources ;
  - le SDK .NET dans `C:\dotnet` ;
  - le code dans `C:\src` ;
  - les scripts de test dans `C:\veltrio-test`.
