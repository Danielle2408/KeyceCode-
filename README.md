# KeyceCode

KeyceCode est un éditeur Windows en C# et WPF qui reprend la disposition du workbench de Visual Studio Code. Le cœur d’édition est Monaco, servi localement dans WebView2; il n’y a pas de chargement de scripts depuis un CDN au démarrage.

## Démarrer

Prérequis : Windows, .NET 10 SDK/runtime, Microsoft Edge WebView2 Runtime, Node.js/npm si vous devez régénérer les ressources Monaco.

```powershell
dotnet run --project src/KeyceCode/KeyceCode.csproj
```

Les ressources Monaco préconstruites sont incluses. Pour les régénérer après modification du client Web :

```powershell
npm ci --prefix src/KeyceCode/WebEditor
npm run --prefix src/KeyceCode/WebEditor build
dotnet build src/KeyceCode/KeyceCode.csproj --configuration Release
```

## Fonctions intégrées actuellement

- Disposition workbench : Activity Bar, Explorer, zone Monaco à onglets, breadcrumbs, panneau inférieur et barre d’état; fenêtre sans barre de titre Windows en doublon.
- Ouvrir un dossier/fichier, navigation du workspace, création/renommage/suppression confirmée dans l’Explorateur, onglets aperçu/épinglés et demande de confirmation avant fermeture. Le dernier dossier, les fichiers ouverts et l’onglet actif sont restaurés au redémarrage.
- Groupes d’éditeurs Monaco verticaux ou horizontaux : « Ouvrir dans le groupe secondaire » est disponible dans le menu contextuel d’un fichier de l’Explorateur ou d’un onglet. Chaque groupe peut afficher un fichier différent; l’édition reste synchronisée par modèle Monaco et la fermeture d’un onglet remplace son modèle dans les groupes qui l’affichaient.
- Surveillance des fichiers ouverts dans le workspace (sous-dossiers inclus) avec debounce de 350 ms; les chemins `.git`, `bin`, `obj`, `node_modules`, caches et répertoires de build, ainsi que les fichiers temporaires usuels, sont ignorés. En cas de divergence, KeyceCode propose Comparer, Recharger ou Garder l’éditeur; aucun buffer n’est remplacé sans choix explicite.
- Monaco local avec coloration syntaxique intégrée, multi-cursor, minimap, numéros de ligne, guides, recherche/remplacement Monaco, paramètres de police, indentation, ligatures, hauteur/style de ligne, style du curseur, espaces blancs, défilement et word wrap. Les options d’éditeur s’appliquent aux deux groupes divisés. Le statut éditeur permet aussi de changer l’encodage (UTF-8, UTF-16/32, Windows-1252) et les fins de ligne (CRLF/LF/CR), appliqués à l’enregistrement. Les fichiers avec octets nuls sans BOM texte sont refusés comme binaires.
- Thèmes WPF Dark+, KeyceCode Night (bleu nuit/orange), Light+, Dark High Contrast et Light High Contrast; thème et options d’édition enregistrés dans `%APPDATA%\KeyceCode`.
- Recherche workspace par expression régulière et filtre de fichiers; palette de commandes et accès rapide aux fichiers.
- Disposition de workbench persistée entre sessions : taille de fenêtre, sidebar et largeur du chat KeyceIA redimensionnables; le panneau Terminal/Problèmes/Sortie peut se déplacer en bas, à gauche ou à droite et se redimensionne selon son orientation. Vue latérale, onglet actif et taille du panneau sont mémorisés. Les tailles hors limites sont assainies à la restauration; **Affichage → Réinitialiser la disposition** restaure le cadre initial.
- Terminal xterm local intégré à WebView2 avec plusieurs sessions ConPTY interactives (UTF-8/VT, redimensionnement, Ctrl-C, code de sortie et arrêt du processus). Les onglets de terminal peuvent être sélectionnés, renommés, redémarrés ou fermés; profils intégrés PowerShell 7, Windows PowerShell et cmd. Le split de terminaux, Git Bash/WSL et la persistance des sessions restent à développer.
- Git : branche/état, stage/unstage, diff unifié, changement de branche, commit des fichiers stagés et Pull/Push via l’upstream configuré. Pull est bloqué si le workspace est modifié; Push/Pull échouent avec un message si remote/upstream ou authentification non interactif manquent. La suppression d’un fichier non suivi demande confirmation.
- Run, Build et Tasks : exécution des projets .NET, fichiers C# autonomes avec le SDK .NET 10, Python, JavaScript, PowerShell et batch. **Ctrl+Shift+B** compile le projet C# parent le plus proche, une solution ou un fichier Python/JavaScript (vérification de syntaxe). Pour un fichier `.cs` associé à plusieurs projets voisins, l’utilisateur choisit le projet à lancer ou compiler; la recherche de projet reste dans la limite du workspace. Les diagnostics MSBuild/Roslyn alimentent **Problèmes** avec navigation par double-clic vers le fichier, la ligne et la colonne; la sortie complète reste dans le terminal intégré. Les tâches shell `.vscode/tasks.json` demandent confirmation avant exécution et prennent en charge les arguments et variables usuelles de workspace/fichier/environnement.
- Débogage : lecture de `.vscode/launch.json`, création d’une configuration Python simple, et DAP Python via `debugpy` si le Python et l’extension Python Debugger compatible sont installés. F5/Continuer, Pause, pas à pas, Restart, Stop, points d’arrêt par numéros de ligne, pile d’appels et variables apparaissent dans la console de débogage. L’adaptateur local est vérifié avant lancement et chaque session demande confirmation. Les adaptateurs C#/C++ Microsoft ne sont pas lancés; KeyceCode ne fournit pas d’adaptateur redistribuable pour ces langages.
- Galerie Open VSX intégrée : recherche, détails et téléchargement VSIX Windows dans `%LOCALAPPDATA%\KeyceCode\extensions\downloads`. La vue Extensions peut installer un VSIX téléchargé ou importer en lot les paquets présents dans `%USERPROFILE%\.vscode\extensions`; ils sont vérifiés, copiés dans `%USERPROFILE%\.keycecode\extensions` et listés comme inactifs.
- Import de configuration VS Code depuis **Fichier → Importer configuration VS Code…** : police, taille, indentation, espaces, retour à la ligne, minimap, ligatures, numéros/style de ligne, curseur, hauteur, espaces blancs, défilement, sauvegarde automatique, formatage à la sauvegarde, thèmes intégrés, snippets utilisateur et raccourcis reconnus depuis `keybindings.json`. Les VSIX installés dans `%USERPROFILE%\.vscode\extensions` sont copiés vers KeyceCode pendant ce même import. Une commande personnalisée est activée uniquement si elle est explicitement prise en charge; commandes et contextes inconnus sont ignorés. Les thèmes tiers ne sont pas activés.
- Client KeyceIA utilisant l’API Gemini Interactions, avec requêtes stateless (`store: false`), pour le chat, les suggestions au curseur et le ghost text inline. Le modèle par défaut est `gemini-3.5-flash-lite`; un autre modèle peut être configuré dans Paramètres. Cette dernière fonction reste désactivée par défaut et s’active dans Paramètres; LSP/IntelliSense standard est distinct. Le bouton de test dans Paramètres vérifie la clé et le modèle sans enregistrer une nouvelle clé. Les erreurs Gemini 429/503 incluent le détail de l’API et les erreurs transitoires sont réessayées. Les clés enregistrées par l’utilisateur sont protégées via DPAPI et ne sont pas intégrées au dépôt.
- Serveur LSP configurable par variables d’environnement : completion standard, diagnostics vers les marqueurs Monaco et Problems, synchronisation des documents ouverts, navigation de définition/références/hover, renommage et Code Actions fournissant un `WorkspaceEdit`.
- Préférence « Formater avant l’enregistrement », distincte d’AutoSave : utilise le formateur Monaco pour JSON/JavaScript/TypeScript/HTML/CSS et `textDocument/formatting` pour les langages couverts par le serveur LSP. En l’absence de formateur ou si le formatage échoue, le texte reste sauvegardé sans formatage.

## Gemini

Vous pouvez saisir la clé dans **Paramètres > KeyceIA** : elle est chiffrée avec DPAPI pour le compte Windows courant, jamais réaffichée et effaçable depuis ce même écran. `GEMINI_API_KEY` reste disponible comme solution de repli pour le développement. Le modèle est configurable via `GEMINI_MODEL`.

L’autocomplétion Gemini pendant la frappe est **désactivée par défaut**. Activez **Paramètres > Autocomplétion Gemini** pour l’autoriser. Après une courte pause, seule la partie du fichier située avant le curseur, limitée aux 4 000 derniers caractères, est envoyée; la sélection et le reste du fichier ne sont pas inclus. Le texte fantôme s’accepte avec Tab et se rejette avec Échap. Tout déplacement du curseur, changement du document ou modification du texte annule les requêtes en cours. Cette option ne remplace pas la complétion LSP.

Les clés qui ont été collées dans la conversation doivent être révoquées et remplacées. Ne les ajoutez ni au dépôt, ni aux journaux, ni aux captures.

## Configurer IntelliSense LSP

KeyceCode ne télécharge ni ne fabrique un serveur de langage. Installez d’abord un serveur compatible LSP pour le langage souhaité, puis configurez son exécutable et ses arguments dans **Paramètres → Serveur de langage (LSP)**. Une modification redémarre le serveur pour l’espace de travail courant. Les variables d’environnement restent disponibles comme solution de repli pour le développement :

```powershell
$env:KEYCECODE_LSP_EXECUTABLE = 'C:\chemin\vers\votre-language-server.exe'
# Arguments optionnels : tableau JSON, chaque argument passé séparément (aucun shell intermédiaire).
$env:KEYCECODE_LSP_ARGS = '["--stdio"]'
dotnet run --project src/KeyceCode/KeyceCode.csproj
```

Pour un serveur .NET lancé par `dotnet`, utilisez `$env:KEYCECODE_LSP_EXECUTABLE = 'dotnet'` et par exemple `$env:KEYCECODE_LSP_ARGS = '["C:\\chemin\\serveur.dll"]'`. L’application démarre le processus avec le workspace comme répertoire et racine LSP. Sans `KEYCECODE_LSP_EXECUTABLE`, Monaco reste utilisable sans IntelliSense serveur et le statut indique que le LSP n’est pas configuré. `KEYCECODE_LSP_ARGS` doit être un tableau JSON de chaînes. Le client actuel envoie des changements de document complets; la synchronisation incrémentale et plusieurs requêtes workspace restent à implémenter.

## Limites connues, non présentées comme terminées

L’installation VSIX locale vérifie le manifeste, rejette les chemins absolus, les traversées de répertoire et les liens symboliques, limite l’archive à 20 000 entrées et l’extraction à 512 Mo, puis extrait les fichiers sous `%USERPROFILE%\.keycecode\extensions`. Cette étape n’est pas une analyse antivirus ni une preuve de confiance. Aucune extension n’est chargée ou exécutée : il n’existe pas d’extension host, d’API VS Code, d’activation, de désinstallation, de mise à jour ou de prise en charge garantie des dépendances natives. La galerie permet de rechercher et télécharger des VSIX depuis Open VSX, mais le choix d’un paquet à installer est manuel. Le serveur LSP doit être installé et configuré séparément par l’utilisateur; aucun langage serveur n’est inclus. Le formatage à la sauvegarde requiert `documentFormattingProvider`; si le fournisseur manque ou échoue, le buffer d’origine est sauvegardé. Les Code Actions LSP couvrent celles qui fournissent un `WorkspaceEdit`; les actions qui ne fournissent qu’une commande serveur ne sont pas lancées. Les groupes d’éditeurs peuvent afficher deux vues Monaco et ouvrir des fichiers dans le groupe secondaire, mais la barre d’onglets est globale (elle n’est pas séparée par groupe), le glisser-déposer entre groupes et les ratios personnalisables restent à développer. Le terminal prend en charge plusieurs sessions ConPTY, mais pas le split de terminal, la persistance des sessions ni les profils personnalisés. Settings Sync, profils, notebooks et remote development restent à développer.

La surveillance ne traite que les fichiers déjà ouverts dans l’éditeur. Un fichier externe de plus de 20 Mo n’est pas rechargé automatiquement; un débordement du tampon Windows FileSystemWatcher est signalé, mais ne déclenche pas de resynchronisation complète. Les remplacements atomiques sont regroupés par debounce, les suppressions/renommages externes gardent le contenu dans l’éditeur et sont signalés.

ConPTY requiert Windows 10 version 1809 (build 17763) ou Windows Server 2019. `ConPtySession` démarre une application console par onglet. Le canal de sortie tamponné est non borné : l’interface doit le consommer continuellement. Les tests d’intégration Windows lancent un shell `cmd.exe` et un processus echo, vérifient entrée/sortie/code de sortie, resize et fermeture sans processus vivant.

## Dépôt distant

`origin` pointe vers `https://github.com/Danielle2408/KeyceCode-.git`. Les changements sont publiés par jalons avec des commits explicites.
