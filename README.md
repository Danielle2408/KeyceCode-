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
- Surveillance des fichiers ouverts dans le workspace (sous-dossiers inclus) avec debounce de 350 ms; les chemins `.git`, `bin`, `obj`, `node_modules`, caches et répertoires de build, ainsi que les fichiers temporaires usuels, sont ignorés. En cas de divergence, KeyceCode propose Comparer, Recharger ou Garder l’éditeur; aucun buffer n’est remplacé sans choix explicite.
- Monaco local avec coloration syntaxique intégrée, multi-cursor, minimap, numéros de ligne, guides, recherche/remplacement Monaco, paramètres de police, indentation, ligatures, minimap et word wrap. Le statut éditeur permet aussi de changer l’encodage (UTF-8, UTF-16/32, Windows-1252) et les fins de ligne (CRLF/LF/CR), appliqués à l’enregistrement. Les fichiers avec octets nuls sans BOM texte sont refusés comme binaires.
- Thèmes WPF Dark+, KeyceCode Night (bleu nuit/orange), Light+, Dark High Contrast et Light High Contrast; thème et options d’édition enregistrés dans `%APPDATA%\KeyceCode`.
- Recherche workspace par expression régulière et filtre de fichiers; palette de commandes et accès rapide aux fichiers.
- Terminal PowerShell/cmd persistant via processus redirigé, profils de base, exécution de fichiers/projets .NET, Python, JavaScript et PowerShell. Ce terminal n’utilise pas encore ConPTY/xterm.js.
- Git : branche/état, stage/unstage/discard par menu contextuel, commit des fichiers déjà stagés.
- Galerie Open VSX intégrée : recherche, détails et téléchargement VSIX Windows dans `%LOCALAPPDATA%\KeyceCode\extensions\downloads`. Depuis la vue Extensions, **Installer un VSIX téléchargé** ouvre un sélecteur local et installe une archive VSIX dans `%USERPROFILE%\.keycecode\extensions`; les extensions présentes sont listées comme inactives.
- Client Gemini KeyceIA pour chat, insertion de suggestion au curseur et suggestions ghost text inline. Cette dernière fonction reste désactivée par défaut et s’active dans Paramètres; LSP/IntelliSense standard est distinct.
- Serveur LSP configurable par variables d’environnement : completion standard, diagnostics vers les marqueurs Monaco et Problems, synchronisation des documents ouverts.
- Préférence « Formater avant l’enregistrement », distincte d’AutoSave : utilise `textDocument/formatting` si le serveur LSP annonce `documentFormattingProvider`. Si le serveur est absent, annule ou renvoie des éditions invalides, le texte d’origine reste intact et est sauvegardé.

## Gemini

Vous pouvez saisir la clé dans **Paramètres > KeyceIA** : elle est chiffrée avec DPAPI pour le compte Windows courant, jamais réaffichée et effaçable depuis ce même écran. `GEMINI_API_KEY` reste disponible comme solution de repli pour le développement. Le modèle est configurable via `GEMINI_MODEL`.

L’autocomplétion Gemini pendant la frappe est **désactivée par défaut**. Activez **Paramètres > Autocomplétion Gemini** pour l’autoriser. Après une courte pause, seule la partie du fichier située avant le curseur, limitée aux 4 000 derniers caractères, est envoyée; la sélection et le reste du fichier ne sont pas inclus. Le texte fantôme s’accepte avec Tab et se rejette avec Échap. Tout déplacement du curseur, changement du document ou modification du texte annule les requêtes en cours. Cette option ne remplace pas la complétion LSP.

Les clés qui ont été collées dans la conversation doivent être révoquées et remplacées. Ne les ajoutez ni au dépôt, ni aux journaux, ni aux captures.

## Configurer IntelliSense LSP

KeyceCode ne télécharge ni ne fabrique un serveur de langage. Installez d’abord un serveur compatible LSP pour le langage souhaité, puis configurez son exécutable avant de lancer l’application :

```powershell
$env:KEYCECODE_LSP_EXECUTABLE = 'C:\chemin\vers\votre-language-server.exe'
# Arguments optionnels : tableau JSON, chaque argument passé séparément (aucun shell intermédiaire).
$env:KEYCECODE_LSP_ARGS = '["--stdio"]'
dotnet run --project src/KeyceCode/KeyceCode.csproj
```

Pour un serveur .NET lancé par `dotnet`, utilisez `$env:KEYCECODE_LSP_EXECUTABLE = 'dotnet'` et par exemple `$env:KEYCECODE_LSP_ARGS = '["C:\\chemin\\serveur.dll"]'`. L’application démarre le processus avec le workspace comme répertoire et racine LSP. Sans `KEYCECODE_LSP_EXECUTABLE`, Monaco reste utilisable sans IntelliSense serveur et le statut indique que le LSP n’est pas configuré. `KEYCECODE_LSP_ARGS` doit être un tableau JSON de chaînes. Le client actuel envoie des changements de document complets; la synchronisation incrémentale, le traitement des code actions et plusieurs requêtes workspace restent à implémenter.

## Limites connues, non présentées comme terminées

L’installation VSIX locale vérifie le manifeste, rejette les chemins absolus, les traversées de répertoire et les liens symboliques, limite l’archive à 20 000 entrées et l’extraction à 512 Mo, puis extrait les fichiers sous `%USERPROFILE%\.keycecode\extensions`. Cette étape n’est pas une analyse antivirus ni une preuve de confiance. Aucune extension n’est chargée ou exécutée : il n’existe pas d’extension host, d’API VS Code, d’activation, de désinstallation, de mise à jour ou de prise en charge garantie des dépendances natives. La galerie permet de rechercher et télécharger des VSIX depuis Open VSX, mais le choix d’un paquet à installer est manuel. Le serveur LSP doit être installé et configuré séparément par l’utilisateur; aucun langage serveur n’est inclus. Le formatage à la sauvegarde requiert `documentFormattingProvider`; si le fournisseur manque ou échoue, le buffer d’origine est sauvegardé. Les Actions rapides/code actions ne sont pas intégrées, car les WorkspaceEdit LSP ne sont pas encore pris en charge. Aucun adaptateur DAP n’est intégré; le bouton Déboguer l’indique. Le terminal exécute un shell persistant mais n’a pas de pseudo-terminal complet. Les groupes d’éditeurs multiples, Settings Sync, profils, notebooks et remote development restent à développer.

La surveillance ne traite que les fichiers déjà ouverts dans l’éditeur. Un fichier externe de plus de 20 Mo n’est pas rechargé automatiquement; un débordement du tampon Windows FileSystemWatcher est signalé, mais ne déclenche pas de resynchronisation complète. Les remplacements atomiques sont regroupés par debounce, les suppressions/renommages externes gardent le contenu dans l’éditeur et sont signalés.

## Dépôt distant

`origin` pointe vers `https://github.com/Danielle2408/KeyceCode-.git`. Les changements sont publiés par jalons avec des commits explicites.
