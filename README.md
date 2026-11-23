# KeyceCode

KeyceCode est un éditeur Windows en C# et WPF qui reprend la disposition du workbench de Visual Studio Code. Le cœur d’édition est Monaco, servi localement dans WebView2; il n’y a pas de chargement de scripts depuis un CDN au démarrage.

## Démarrer

Prérequis en développement : Windows, .NET 10 SDK/runtime, Microsoft Edge WebView2 Runtime, Node.js/npm si vous devez régénérer les ressources Monaco ou lancer KeyceCode hors Setup. Le Setup inclut Node.js pour l’hôte d’extensions.

```powershell
dotnet run --project src/KeyceCode/KeyceCode.csproj
```

## Installer Windows

Le programme d’installation x64 autonome peut être généré avec :

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-installer.ps1
```

Le script publie KeyceCode avec le runtime .NET inclus et crée `KeyceCode-Setup-0.2.0.exe` sur le Bureau. Il utilise Inno Setup 6 et télécharge le bootstrapper WebView2 officiel de Microsoft; l’installateur vérifie sa signature Microsoft avant de le compiler dans le Setup. WebView2 est installé silencieusement pendant l’installation si nécessaire. Les artefacts de génération restent dans `artifacts/` et ne sont pas versionnés.

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
- Monaco local avec coloration syntaxique intégrée, multi-cursor, minimap, numéros de ligne, guides, recherche/remplacement Monaco, paramètres de police, indentation, ligatures, hauteur/style de ligne, style du curseur, espaces blancs, défilement et word wrap. Les options d’éditeur s’appliquent aux deux groupes divisés. Le statut éditeur permet aussi de changer l’encodage (UTF-8, UTF-16/32, Windows-1252) et les fins de ligne (CRLF/LF/CR), appliqués à l’enregistrement. Les fichiers avec octets nuls sans BOM texte sont refusés comme binaires.
- Thèmes WPF Dark+, KeyceCode Night (bleu nuit/orange), Light+, Dark High Contrast et Light High Contrast; thème et options d’édition enregistrés dans `%APPDATA%\KeyceCode`.
- Recherche workspace par expression régulière et filtre de fichiers; palette de commandes et accès rapide aux fichiers.
- Disposition de workbench persistée entre sessions : taille de fenêtre, sidebar et largeur du chat KeyceIA redimensionnables; le panneau Terminal/Problèmes/Sortie peut se déplacer en bas, à gauche ou à droite et se redimensionne selon son orientation. Vue latérale, onglet actif et taille du panneau sont mémorisés. Les tailles hors limites sont assainies à la restauration; **Affichage → Réinitialiser la disposition** restaure le cadre initial.
- Terminal xterm local intégré à WebView2 avec plusieurs sessions ConPTY interactives (UTF-8/VT, redimensionnement, Ctrl-C, code de sortie et arrêt du processus). Les onglets de terminal peuvent être sélectionnés, renommés, redémarrés ou fermés; profils intégrés PowerShell 7, Windows PowerShell et cmd. Le split de terminaux, Git Bash/WSL et la persistance des sessions restent à développer.
- Git : branche/état, stage/unstage, diff unifié, changement de branche, commit des fichiers stagés et Pull/Push via l’upstream configuré. **Associer GitHub** configure `origin` à partir d’une URL HTTPS/SSH GitHub puis publie la branche et ouvre le dépôt; **Ouvrir GitHub** affiche le remote associé. Git Credential Manager peut ouvrir son flux de connexion par navigateur pour Push/Pull. Pull est bloqué si le workspace est modifié; la suppression d’un fichier non suivi demande confirmation.
- Run, Build et Tasks : exécution des projets .NET, fichiers C# autonomes avec le SDK .NET 10, Python, JavaScript, PowerShell et batch. **Ctrl+Shift+B** compile le projet C# parent le plus proche, une solution ou un fichier Python/JavaScript (vérification de syntaxe). Pour un fichier `.cs` associé à plusieurs projets voisins, l’utilisateur choisit le projet à lancer ou compiler; la recherche de projet reste dans la limite du workspace. Les diagnostics MSBuild/Roslyn alimentent **Problèmes** avec navigation par double-clic vers le fichier, la ligne et la colonne; la sortie complète reste dans le terminal intégré. Les tâches shell `.vscode/tasks.json` demandent confirmation avant exécution et prennent en charge les arguments et variables usuelles de workspace/fichier/environnement.
- Débogage : lecture de `.vscode/launch.json`, création d’une configuration Python simple, et DAP Python via `debugpy` si le Python et l’extension Python Debugger compatible sont installés. F5/Continuer, Pause, pas à pas, Restart, Stop, points d’arrêt par numéros de ligne, pile d’appels et variables apparaissent dans la console de débogage. L’adaptateur local est vérifié avant lancement et chaque session demande confirmation. Les adaptateurs C#/C++ Microsoft ne sont pas lancés; KeyceCode ne fournit pas d’adaptateur redistribuable pour ces langages.
- Galerie Open VSX intégrée : recherche, détails, téléchargement VSIX Windows et installation directe depuis les résultats. Les paquets peuvent aussi être enregistrés seuls dans `%LOCALAPPDATA%\KeyceCode\extensions\downloads`, installés depuis un VSIX local, ou importés en lot depuis `%USERPROFILE%\.vscode\extensions`. Les VSIX sont validés et extraits dans `%USERPROFILE%\.keycecode\extensions`. L’utilisateur peut activer explicitement une extension CommonJS (`main`) dans un processus Node séparé; ses commandes déclarées et enregistrées sont accessibles depuis la palette et la vue Extensions, et les notifications basiques ainsi que quelques opérations `workspace.fs` sont disponibles. Le Setup embarque Node.js officiel et vérifie l’archive avec son SHA-256 publié.
- Import de configuration VS Code depuis **Fichier → Importer configuration VS Code…** : police, taille, indentation, espaces, retour à la ligne, minimap, ligatures, numéros/style de ligne, curseur, hauteur, espaces blancs, défilement, sauvegarde automatique, formatage à la sauvegarde, thèmes intégrés, snippets utilisateur et raccourcis reconnus depuis `keybindings.json`. Les VSIX installés dans `%USERPROFILE%\.vscode\extensions` sont copiés vers KeyceCode pendant ce même import. Une commande personnalisée est activée uniquement si elle est explicitement prise en charge; commandes et contextes inconnus sont ignorés. Les thèmes tiers ne sont pas activés.
- Client KeyceIA utilisant l’API Gemini Interactions, avec requêtes stateless (`store: false`), pour le chat, les suggestions au curseur, le ghost text inline et les modifications de document. Le modèle par défaut est `gemini-3.5-flash-lite`; un autre modèle peut être configuré dans Paramètres. Pour activer les suggestions automatiques, coche **Autocomplétion Gemini** dans Paramètres : après une courte pause de frappe, le préfixe avant le curseur (jusqu’à 4 000 caractères) est envoyé; le ghost text est accepté avec **Tab** et ignoré avec **Échap**. La complétion manuelle et l’édition de fichier nécessitent aussi une action explicite. **Proposer du code au curseur** et **Modifier le fichier avec KeyceIA** affichent d’abord un aperçu; **Appliquer au fichier** demande confirmation, vérifie que le document n’a pas changé et crée une édition annulable avec Ctrl+Z, non enregistrée tant que l’utilisateur ne fait pas Ctrl+S. LSP/IntelliSense standard est distinct. Le bouton de test dans Paramètres vérifie la clé et le modèle sans enregistrer une nouvelle clé. Les erreurs Gemini 429/503 incluent le détail de l’API et les erreurs transitoires sont réessayées. Les clés enregistrées par l’utilisateur sont protégées via DPAPI et ne sont pas intégrées au dépôt.
- Serveur LSP configurable par Paramètres ou variables d’environnement : completion standard, diagnostics vers les marqueurs Monaco et Problems, synchronisation des documents ouverts, navigation de définition/références/hover, renommage et Code Actions fournissant un `WorkspaceEdit`. Le bouton « Installer l’IntelliSense C# (Roslyn) » télécharge le serveur Roslyn globalement via le SDK .NET; chaque espace de travail demande confirmation avant son chargement, car MSBuild peut exécuter des cibles du projet.
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

L’installation VSIX locale vérifie le manifeste, rejette les chemins absolus, les traversées de répertoire et les liens symboliques, limite l’archive à 20 000 entrées et l’extraction à 512 Mo, puis extrait les fichiers sous `%USERPROFILE%\.keycecode\extensions`. Cette étape n’est pas une analyse antivirus ni une preuve de confiance. L’hôte actuel fournit une compatibilité partielle avec l’API VS Code : activation CommonJS (`main`), enregistrement/exécution de commandes, messages, lecture/écriture de base via `workspace.fs` et accès au dossier courant. Le code d’extension a les permissions du compte Windows et le réseau : le processus Node séparé n’est pas un bac à sable, et KeyceCode demande donc une confirmation avant l’activation. Les extensions qui utilisent des API d’éditeur, fournisseurs de langage, thèmes, contributions UI complexes, tâches, débogueurs, WebView, modules natifs ou autres fonctions non implémentées ne sont pas garanties. Les extensions désactivées restent inertes; activer du code non fiable peut compromettre les fichiers du compte. Il n’existe pas encore de désactivation individuelle ni de mise à jour automatique. La galerie permet de rechercher et télécharger des VSIX depuis Open VSX, mais le choix d’un paquet à installer est manuel. Le serveur LSP doit être installé et configuré séparément par l’utilisateur; aucun langage serveur n’est inclus. Le formatage à la sauvegarde requiert `documentFormattingProvider`; si le fournisseur manque ou échoue, le buffer d’origine est sauvegardé. Les Code Actions LSP couvrent celles qui fournissent un `WorkspaceEdit`; les actions qui ne fournissent qu’une commande serveur ne sont pas lancées. Les groupes d’éditeurs peuvent afficher deux vues Monaco et ouvrir des fichiers dans le groupe secondaire, mais la barre d’onglets est globale (elle n’est pas séparée par groupe), le glisser-déposer entre groupes et les ratios personnalisables restent à développer. Le terminal prend en charge plusieurs sessions ConPTY, mais pas le split de terminal, la persistance des sessions ni les profils personnalisés. Settings Sync, profils, notebooks et remote development restent à développer.

Les changements faits par un autre programme dans un fichier déjà ouvert ne sont pas surveillés ni rechargés automatiquement; ferme et rouvre le fichier pour relire la version externe.

ConPTY requiert Windows 10 version 1809 (build 17763) ou Windows Server 2019. `ConPtySession` démarre une application console par onglet. Le canal de sortie tamponné est non borné : l’interface doit le consommer continuellement. Les tests d’intégration Windows lancent un shell `cmd.exe` et un processus echo, vérifient entrée/sortie/code de sortie, resize et fermeture sans processus vivant.

## Dépôt distant

`origin` pointe vers `https://github.com/Danielle2408/KeyceCode-.git`. Les changements sont publiés par jalons avec des commits explicites.
