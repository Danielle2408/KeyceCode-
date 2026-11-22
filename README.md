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
- Ouvrir un dossier/fichier, navigation du workspace, onglets aperçu/épinglés, modifications non enregistrées, création/enregistrement et demande de confirmation avant fermeture.
- Monaco local avec coloration syntaxique intégrée, multi-cursor, minimap, numéros de ligne, guides, recherche/remplacement Monaco, paramètres de police, indentation, ligatures, minimap et word wrap.
- Thèmes WPF Dark+, Light+, Dark High Contrast et Light High Contrast; thème et options d’édition enregistrés dans `%APPDATA%\KeyceCode`.
- Recherche workspace par expression régulière et filtre de fichiers; palette de commandes et accès rapide aux fichiers.
- Terminal PowerShell/cmd persistant via processus redirigé, profils de base, exécution de fichiers/projets .NET, Python, JavaScript et PowerShell. Ce terminal n’utilise pas encore ConPTY/xterm.js.
- Git : branche/état, stage/unstage/discard par menu contextuel, commit des fichiers déjà stagés.
- Client Gemini KeyceIA pour chat et insertion de suggestion au curseur.
- Transport LSP JSON-RPC en cours de construction dans `Services/Lsp`.

## Gemini

Définissez `GEMINI_API_KEY` dans l’environnement du processus pour activer KeyceIA. Le code ne contient aucune clé. Le modèle est configurable via `GEMINI_MODEL`; le défaut est défini dans `GeminiService` et devra être ajusté selon les modèles actuellement disponibles dans la documentation Google.

Les clés qui ont été collées dans la conversation doivent être révoquées et remplacées. Ne les ajoutez ni au dépôt, ni aux journaux, ni aux captures.

## Limites connues, non présentées comme terminées

La galerie d’extensions ouvre actuellement Open VSX dans le navigateur; l’installation/activation de VSIX dans un extension host KeyceCode n’est pas encore implémentée. LSP est un transport et n’est pas encore connecté aux fonctions IntelliSense de l’interface. Aucun adaptateur DAP n’est intégré; le bouton Déboguer l’indique. Le terminal exécute un shell persistant mais n’a pas de pseudo-terminal complet. Les groupes d’éditeurs multiples, Settings Sync, profils, notebooks et remote development restent à développer.

## Dépôt distant

`origin` pointe vers `https://github.com/Danielle2408/KeyceCode-.git`. Les changements sont publiés par jalons avec des commits explicites.
