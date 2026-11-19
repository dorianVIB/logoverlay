# VIB Log Overlay

Overlay de logs affiché en jeu (build + éditeur), au-dessus de toute l'UI, à 80 % d'opacité.

## Raccourcis
| Touche | Action |
|---|---|
| `Ctrl+H` | Afficher / masquer |
| `Ctrl+L` | Vider les logs (overlay visible) |
| Clic sur les boutons `Infos` / `Warnings` / `Erreurs`, ou `Ctrl+1` / `Ctrl+2` / `Ctrl+3` | Afficher / cacher ce niveau de logs |
| Molette / `PageUp` / `PageDown` | Défiler |
| `Fin` | Revenir en bas (suivi auto des nouveaux logs) |

## Installation dans un autre projet
- **Copier** le dossier `com.vib.logoverlay` dans le dossier `Packages/` du projet, **ou**
- l'ajouter dans `Packages/manifest.json` en pointant vers un dossier partagé :
  `"com.vib.logoverlay": "file:D:/chemin/vers/com.vib.logoverlay"`
- ou via Git (Package Manager > Add package from git URL).

Rien d'autre à faire : l'overlay se crée automatiquement au démarrage (`RuntimeInitializeOnLoadMethod`)
et capture tous les `Debug.Log*`, y compris ceux émis depuis d'autres threads.

## VR
L'overlay utilise un Canvas *Screen Space - Overlay* : Unity ne le rend jamais dans le casque,
uniquement sur la fenêtre PC (vue miroir). Il ne capte aucun clic/raycast et ne gêne donc pas
les interactions XR.

## Input
Compatible ancien Input Manager et nouveau Input System (détecté automatiquement).

## Options
- `LogOverlay.Toggle()` / `LogOverlay.Show(bool)` pour piloter l'overlay par code.
- Définir le symbole `LOGOVERLAY_DISABLED` dans les Scripting Define Symbols pour le désactiver complètement.
