# Prompt de réalisation — KeyceCode, éditeur de développement complet

Copiez le bloc « Prompt à donner à l’agent » ci-dessous dans Codex ou dans votre agent de développement. Il est conçu pour reprendre le dépôt existant, corriger la direction du premier MVP, puis construire une application réellement utilisable par étapes.

## Prompt à donner à l’agent

```text
Tu es l’équipe d’ingénierie responsable de KeyceCode, un éditeur de développement Windows. Tu travailles dans le dépôt Git déjà ouvert. Le produit de référence fonctionnelle et visuelle est Visual Studio Code (VS Code), pas l’IDE Visual Studio. La première interface KeyceCode a été jugée insatisfaisante : ne la traite pas comme une base de qualité et ne prétends jamais qu’un écran rudimentaire équivaut à VS Code.

OBJECTIF
Construire une application de développement solide, cohérente et réellement fonctionnelle, visant une forte proximité avec le workbench et les flux d’utilisation de VS Code. L’utilisateur veut retrouver les dispositions physiques, les couleurs et thèmes, les polices, l’édition intelligente, l’exécution, le débogage, les extensions et les réglages. Priorise les usages principaux et implémente par tranches complètes et vérifiables. N’annonce pas « tout VS Code » si le produit n’a pas les capacités correspondantes.

RÈGLES DE TRAVAIL
1. Commence par inspecter le dépôt, les instructions locales, les fichiers existants, l’historique Git, la solution et les builds. Fais un plan court, puis implémente dans ce dépôt sans écraser du travail utilisateur.
2. Conserve C# comme langage principal du shell, des services, des processus, des paramètres, de l’extension host et des intégrations système. Tu peux employer TypeScript/JavaScript pour l’éditeur Monaco et les vues Web nécessaires. Évite de réimplémenter un moteur d’édition complet en TextBox WPF.
3. Pour obtenir une vraie expérience d’édition, étudie l’intégration de Monaco Editor dans WebView2 avec un pont local strict vers C#. Le shell Windows peut rester en WPF si c’est le choix déjà établi. Utilise une version maintenue du SDK .NET installée sur la machine et évite les dépendances abandonnées.
4. Concevoir une architecture en modules : Workbench/Shell, Workspace & fichiers, Editor, Language Services (LSP), Terminal/Tasks, Run & Debug (DAP), Source Control (Git), Extensions, Settings/Themes, KeyceIA, Tests/Problems, Accessibility. Pas de logique applicative concentrée dans un unique code-behind.
5. Les fonctions doivent marcher, pas seulement ressembler à des boutons. Tout contrôle visible doit effectuer l’action annoncée, être désactivé avec une explication si l’action est indisponible, ou être masqué tant qu’il n’existe pas. Pas de faux terminal qui n’exécute rien, pas de faux Marketplace, pas de boutons décoratifs, pas de résultats simulés.
6. Avance de façon autonome. Après chaque tranche, lance build, tests automatisés pertinents et contrôles UI disponibles, corrige les erreurs, puis passe à la suivante. N’interromps pas le travail après avoir livré un plan, une maquette ou une liste d’idées. Mets à jour la documentation de lancement et de limitations.
7. Si tu peux déléguer, sépare le travail par modules sans faire modifier les mêmes fichiers en parallèle : shell UI, éditeur/Monaco, outils de développement, extensions, IA et intégration/QA. Intègre toi-même et vérifie chaque contribution.
8. Aucune clé API, aucun jeton, mot de passe ou secret ne doit être écrit dans le code, les ressources, les journaux, les captures, les paramètres versionnés ni les commits. KeyceIA doit lire un secret de l’utilisateur via un stockage sécurisé Windows (Credential Manager/DPAPI) ou une variable d’environnement pour le développement. Les secrets envoyés dans une conversation ou découverts dans le dépôt doivent être considérés exposés : ne pas les réutiliser, ne pas les imprimer, avertir l’utilisateur qu’ils doivent être révoqués.
9. Pour KeyceIA, utiliser Gemini derrière une interface fournisseur remplaçable. Modèle, URL, génération, annulation, limites, erreurs, quotas et préférences doivent être configurables. Ne présume pas qu’un identifiant de modèle restera actuel : consulter la documentation Google à l’implémentation et garder le modèle paramétrable. Ne déclenche aucun appel réseau sans action ou réglage explicite de l’utilisateur.
10. Ne recopie pas les logos, noms de marque ou ressources propriétaires de VS Code. Reproduis les motifs d’interface familiers, tout en conservant l’identité KeyceCode et ses propres ressources.

A. DISPOSITION DU WORKBENCH — priorité P0
Reproduis le modèle d’interface VS Code :
- Barre de titre en haut, avec contrôles de fenêtre Windows, titre du dossier/projet, zone de commande/recherche rapide, commandes de navigation et menu d’application.
- Menu d’application : Fichier, Édition, Sélection, Affichage, Aller à, Exécuter, Terminal, Aide. Les entrées sont reliées à des commandes réelles.
- Barre d’activité à l’extrême gauche avec Explorateur, Recherche, Contrôle de code source, Exécuter/Déboguer, Extensions, Tests et KeyceIA. Icônes cohérentes, états actif/hover, badges de changements/erreurs et menu contextuel pour cacher, réordonner ou déplacer les vues.
- Barre latérale principale immédiatement à droite de la barre d’activité, dont la largeur est redimensionnable. Les vues s’ouvrent dans cette barre et peuvent être déplacées/réordonnées.
- Zone centrale d’éditeurs avec onglets, onglet actif évident, état modifié non enregistré, pin/unpin, fermeture, fermeture des autres, menu contextuel, réordonnancement par glisser-déposer, restauration des fichiers ouverts et groupes d’éditeurs divisibles verticalement/horizontalement.
- Barre latérale secondaire à droite, cachable/déplaçable, pour Chat/KeyceIA, Outline ou autres vues.
- Panel inférieur redimensionnable et déplaçable en bas, à gauche ou à droite : Terminal, Problems, Output, Debug Console, Test Results et Ports quand disponibles. Onglets de panneau et contrôles réels.
- Barre d’état tout en bas : branche Git, erreurs/avertissements, position ligne/colonne, indentation, encodage, fins de ligne, langage, espace de travail et raccourcis de statut utiles. Chaque segment ouvre son sélecteur ou son détail.
- Barre de navigation/breadcrumbs au-dessus de l’éditeur : chemin de fichier et symboles imbriqués, navigation par menus et clics.
- Fournis des séparateurs manipulables pour redimensionner les régions. Supporte masquer/afficher chacune d’elles, plein écran et centrage de l’éditeur.
- Les dimensions, visibilité, position, groupe actif, panneau et onglets sont restaurés au redémarrage. Ajouter « Réinitialiser la disposition ».
- Disposition initiale proche du VS Code Windows standard : Activity Bar verticale à gauche, Explorer à sa droite, éditeur dominant au centre, panneau terminal en bas, status bar en bas, menu/title en haut. Respecte les proportions : l’éditeur occupe la majorité de la fenêtre; ne transforme pas l’éditeur en petite colonne.
- Fenêtre redimensionnable, DPI Windows, scaling, minimums raisonnables, multi-écrans et navigation clavier.

B. COULEURS, APPARENCE, POLICES — priorité P0
Ne livre pas une palette grise improvisée ou des couleurs choisies sans système. Implémente un système de thèmes cohérent :
- Thèmes intégrés : au minimum Dark+ proche de VS Code, Light+, Dark High Contrast, Light High Contrast et un thème sombre bleu nuit KeyceCode bien accordé. Palette complète avec arrière-plans, texte principal/secondaire, bordures, sélection, focus, accents, boutons, Activity Bar, Side Bar, onglets, Panel, Status Bar, menus, listes, widgets de suggestions, infobulles, diagnostics et diff.
- Les couleurs de syntaxe distinguent clairement commentaires, chaînes, mots-clés, types, fonctions, variables, nombres, opérateurs, constantes et erreurs. Gérer coloration syntaxique + tokens sémantiques quand le serveur de langage les fournit.
- Sélecteur de thème avec aperçu et recherche. Permettre l’installation de thèmes d’interface, thèmes de coloration et thèmes d’icônes de fichiers/produits depuis la galerie d’extensions retenue.
- Paramètres de personnalisation avec choix de thème, couleurs principales, taille de l’interface/zoom, densité/espacement, position et visibilité des barres, taille de sidebars et panel, icônes, animations, arrondis et contraste.
- Paramètres de typographie de l’éditeur : famille de police, taille, graisse, ligatures, hauteur de ligne, espacement des lettres, tabulation, indentation, retour à la ligne, glyphes, curseur, animations et rendu. Prévisualiser les changements en direct. Ne pas imposer une police absente : vérifier les polices installées et afficher un fallback.
- Options visuelles de l’éditeur : numéros de ligne absolus/relatifs, marge de pliage, minimap, rulers, guides d’indentation, espaces blancs, caractères de contrôle, surlignage de la ligne active, paires de crochets, coloration des paires, guides de portée, sticky scroll, breadcrumbs, aperçu de diff, scroll smooth et word wrap.
- Les choix de thème, police, zoom, icônes et disposition persistent entre sessions et par profil.

C. GESTION DE PROJET ET EXPLORATEUR — priorité P0
- Ouvrir dossier, fichier, espace de travail multi-dossiers; créer/ouvrir/enregistrer un workspace; ajouter/supprimer un dossier racine; ouvrir récents; fermer workspace; restaurer la session.
- Arbre fichiers/dossiers avec icônes par type, expansion, filtre, recherche de nom, tri configurable, fichiers cachés, exclusions glob, état Git, décorations d’erreurs, menu contextuel et drag-drop.
- Créer/renommer/supprimer fichiers et dossiers; copier/coller/déplacer; révéler dans l’explorateur système; copier chemin/chemin relatif; confirmer les suppressions; gérer les fichiers non sauvegardés.
- Surveiller les modifications sur disque et proposer recharger ou comparer en cas de conflit externe.
- Respecter les limites du workspace, liens symboliques, exclusions et tailles. Ne pas parcourir sans limite `.git`, `bin`, `obj`, `node_modules` ou répertoires ignorés.

D. ÉDITION ET NAVIGATION — priorité P0
Employer un éditeur de code mature (Monaco recommandé) avec sélection, presse-papiers, undo/redo, multi-cursor, Alt+clic, sélection de prochaine occurrence, sélection de toutes occurrences, édition de colonnes, indentation, auto-indentation, déplacement/copie de ligne, duplication, commentaires, suppression/trim de lignes, casse, pliage, recherche/remplacement, regex, correspondance de casse/mot entier, remplacements multi-fichiers et aperçu avant application.
- Onglets, pin, aperçu (preview tab), dirty state, historique de navigation, retour/avance, restaurer les vues, split editor en plusieurs groupes, déplacement d’un éditeur par glisser-déposer et ouverture côte à côte.
- Go to file (Quick Open), Go to line, Go to Symbol, Go to Symbol in Workspace, Go to Definition, Type Definition, Implementation, References, Peek Definition/References, Rename Symbol, Call Hierarchy, Type Hierarchy, document outline et breadcrumbs, quand le langage fournit ces fonctions.
- Minimap, line/column, wrap, indent, encodage UTF-8/autres encodages utiles, fins de ligne LF/CRLF, détection de langage, mode texte brut, fichiers binaires protégés, gros fichiers avec mode performant.
- Différence visuelle side-by-side et inline, compare fichiers avec Git, historique du fichier et aperçu des changements.
- Support de snippets intégrés, utilisateur et projet : préfixes, scope par langage, placeholders, tabstops, choix, variables utiles, insertion via IntelliSense et raccourcis configurables.

E. LANGAGES, INTELLISENSE, FORMATAGE ET CORRECTIONS — priorité P0
- Pour chaque langage, utiliser une implémentation de langage réelle : LSP (Language Server Protocol), extension language server ou tooling fourni. Ne simule pas IntelliSense avec une liste de mots statique.
- API commune pour diagnostics, completion, hover, signature help, document symbols, workspace symbols, définition, références, implémentation, rename, code actions, formatting, folding et semantic tokens.
- Fournir les fonctions génériques: détection/association d’extensions, coloration et bracket matching; installer/configurer les serveurs et outils nécessaires. Priorité initiale à C#, JavaScript/TypeScript, JSON, HTML/CSS, Markdown et PowerShell, puis architecture ouverte aux autres langages.
- Fenêtre de suggestions filtrable avec icônes/types, détails, documentation, snippets, commit characters, acceptation Tab/Enter configurable, liste de paramètres/signatures et tooltips. Complétion automatique selon curseur/contexte, import automatique seulement si le fournisseur la propose.
- Format Document, Format Selection, format on type/paste/save, « organiser les imports » et formatter par défaut/configurable par langage. L’alignement/formatage doit utiliser le formateur de ce langage (ex. dotnet format/roslyn, Prettier ou équivalent), préserver le sens du code, permettre un aperçu et expliquer si le formatter est absent.
- Diagnostics avec soulignement, code couleur, hover, panneau Problems filtrable, source/code, clic vers ligne, compteurs dans status bar, fixes via lightbulb et Code Actions on Save configurables.
- Conflits de formatters gérés par un sélecteur « Configure default formatter ».
- Mettre en place une stratégie de version/configuration des serveurs LSP, démarrage à la demande, journal Output, erreurs récupérables et annulation.

F. AUTOCOMPLÉTION IA ET KEYCEIA — priorité P0
- Séparer IntelliSense de langage (déterministe, fourni par LSP) des suggestions IA.
- Inline completion de style ghost text, acceptation entière et par mot/ligne, ignorer, nouvelle suggestion, annuler, activer/désactiver, déclenchement automatique/manuel, délai, fréquence, longueur max, température et modèle configurables.
- Chat latéral KeyceIA avec conversation, historique local contrôlable, contexte fichier/selection/workspace choisi explicitement, copier réponse, insérer dans l’éditeur, diff/aperçu avant d’appliquer des modifications, possibilité d’annuler. L’IA ne modifie jamais les fichiers silencieusement.
- Actions contextuelles : expliquer sélection, générer code, corriger erreur, refactoriser, générer tests, documenter, expliquer diagnostics et proposer une correction. Demander la confirmation avant d’appliquer un patch multi-fichiers.
- Envoyer le minimum de contexte nécessaire. Exclure par défaut secrets, `.env`, clés, binaires, fichiers exclus et chemins hors workspace. Afficher clairement ce qui sera envoyé et au fournisseur. Contrôle d’annulation/timeout, limite de tokens, erreurs réseau/quota et journal ne contenant aucun secret.
- Gemini implémenté par fournisseur injectable; Gemini key stockée en Credential Manager/DPAPI ou env var de développement; sélecteur de modèle maintenu. Ne jamais coder en dur les clés publiées auparavant par l’utilisateur.

G. TERMINAL ET COMMANDES — priorité P0
- Terminal intégré réel, processus shell PTY/ConPTY sous Windows, avec PowerShell par défaut et profils configurables (PowerShell, cmd, Git Bash, WSL si présents). Aucune simulation TextBox.
- Nouveau terminal, plusieurs onglets, terminal split, renommer, tuer, relancer, choisir profil, ouvrir terminal dans éditeur, copier/coller, rechercher, zoom, définir dossier courant, conserver sessions selon choix utilisateur.
- Gestion des entrées/sorties ANSI, couleurs, liens URL et chemins, sélection, défilement, navigation dans les commandes, code de retour et état du processus. Utiliser shell integration lorsque possible sans modifier dangereusement la stratégie d’exécution Windows.
- Respecter Workspace Trust et consentement utilisateur avant commande externe ou script dangereux; afficher commande et répertoire avant l’exécution automatisée.

H. RUN, BUILD, TASKS ET DÉBOGAGE — priorité P0/P1
- F5 démarrer/continuer, Ctrl+F5 démarrer sans déboguer, Shift+F5 arrêter, restart, choisir configuration et sélecteur environnement.
- Run current file, run selection, build, rebuild, clean, scripts/package scripts et tâches workspace `.vscode/tasks.json`, tâches de fond, dépendances/preLaunchTask, variables de workspace, configuration shell, output et problem matchers vers Problems.
- Conserver configurations workspace `.vscode/launch.json`; créer/éditer configurations depuis UI et permettre `args`, cwd, env, envFile, console, programme, attach process, preLaunchTask et configuration par langage.
- Utiliser debugger réel par DAP (Debug Adapter Protocol) et outils de langage; n’afficher une configuration de debug que si adaptateur disponible. Pour C#, intégrer l’expérience .NET via extension/outils Microsoft dans les limites de licence et d’API disponibles.
- Breakpoints, conditional breakpoints, logpoints, inline breakpoints, enabled/disabled, hit count; continuer, pause, step over/into/out, restart, stop, Run to Cursor, run without debugging.
- Vues Variables/Locals, Watch, Call Stack, Loaded Scripts/Modules, Debug Console, threads et scopes; valeurs inspectables/éditables si adapter le permet.
- Afficher clairement outils/runtimes/SDK manquants et liens d’installation; ne pas confondre « l’éditeur prend en charge C# » et « un SDK .NET est installé ».

I. SOURCE CONTROL GIT — priorité P1
- Détecter une installation Git et initialiser/ouvrir dépôt. Branches et remotes, checkout/créer/renommer/supprimer branche, fetch/pull/push/sync, commit amend/squash selon option, commit signé si configuré, stash, tags, historique.
- Source Control view: Changes, Staged Changes, message de commit, stage/unstage, stage selected ranges, discard avec confirmation, publish repository et remote.
- Décorations gutter et Explorer, diff inline/two-way, vue Graph branches/commits avec incoming/outgoing, historique fichier et blame.
- Résolution des conflits par éditeur 3-way (Current/Incoming/Base/Result), actions accept current/incoming/both, édition manuelle, marquer résolu et validation.
- Commandes Git via libgit2 ou processus Git robuste, jamais concaténer du contenu utilisateur dans une commande shell. UI reflète les changements Git effectués par terminal.
- Authentification via le gestionnaire d’identifiants Git/OS; ne pas stocker de mots de passe en clair.

J. EXTENSIONS/GALERIE — priorité P1, construire par niveau de compatibilité
- Extensions view avec recherche, détails, publisher, catégorie, version, changelog, notes, évaluation/téléchargements quand API galerie l’autorise, recommandations, filtres, tri, vues Installed/Enabled/Disabled/Updates/Built-in/Recommended.
- Installer, télécharger, installer depuis VSIX, désinstaller, mettre à jour, épingler une version, pré-release si galerie le permet, enable/disable global/workspace, auto-update configurable, dépendances et reload/restart extension host.
- Catalogue de thèmes, thèmes d’icônes, snippets, language packs, linters, formatters, debuggers, testing, SCM, notebooks, outils et extension packs.
- Infos de confiance éditeur et permissions avant installation/exécution. Ne pas exécuter un package non vérifié sans avertir. Privilégier un host d’extensions séparé, journalisé, avec mécanisme d’arrêt; expliquer que les extensions ont des capacités puissantes.
- Définir honnêtement la cible de compatibilité. L’API VS Code complète et toutes ses extensions ne sont pas un objectif implicite automatique. Créer une API documentée et compatible LSP/DAP, un manifeste de plugin, un catalogue réel (Open VSX ou galerie dont l’utilisation est autorisée), API versionnée, gestion de dépendances et exemples. Indiquer les limitations de compatibilité VSIX/VS Code.

K. RECHERCHE, PROBLÈMES ET TESTS — priorité P1
- Search view workspace avec regex, case sensitive, whole word, include/exclude glob, fichiers ignorés, replace preview, navigation des résultats et ouverture des fichiers concernés.
- Problems view agrège diagnostics LSP, compilateurs et task problem matchers, tri/filtre par error/warning/info, fichier/source, navigation vers ligne et refresh.
- Testing view via API test provider : découverte, arbre tests, run/debug test/all/file, filtre, résultats historiques, messages inline, couverture et tâches de fond. Support dépend des extensions/installations réellement présentes.
- Output view avec canaux nommés (Extension Host, LSP, Git, Tasks, Debugger, KeyceIA) et niveaux de logs; pas de secrets ou contenus sensibles par défaut.

L. PARAMÈTRES, COMMANDES ET PERSONNALISATION — priorité P1
- Command Palette (Ctrl+Shift+P) avec fuzzy search sur les commandes enregistrées, catégories, historique et commandes extension. Quick Open (Ctrl+P) et séquences de raccourcis.
- Settings UI recherchable avec descriptions, liens vers documentation, valeurs typées, default/user/workspace/folder, overrides de langage, modifications immédiates et édition JSON (settings.json) avec validation/suggestions.
- Keybindings UI/JSON : rechercher, modifier, conflits, key chords, contexte/when, profils clavier, raccourcis principaux Windows et autres OS si multi-plateforme.
- Profils : créer, nommer, icône, changer, temporary, importer/exporter, choisir extensions/settings/snippets/keybindings/UI state synchronisés; espaces de travail associés.
- Auto Save configurable; format on save; backups/restauration fichiers non enregistrés.
- Settings Sync multi-appareil avec authentification uniquement si backend supporté et sécurisé; sélectionner catégories, exclusions, résolution de conflits. Ne pas simuler une synchronisation distante.
- Réglages de fichier et workspace versionnés avec `.vscode` et fichier workspace; définir Workspace Trust/Restricted Mode et demander l’approbation des tasks/commandes/fonctions qui exécutent du contenu du projet.

M. ACCESSIBILITÉ ET QUALITÉ D’USAGE — priorité P1
- Navigation clavier complète, tab order logique, raccourcis cohérents, focus visible, noms accessibles, lecteurs d’écran, high contrast, zoom, taille de police indépendante, non-dépendance à la couleur, préférences de mouvement réduit, aide contextuelle clavier.
- Texte en français par défaut, architecture prête pour langues d’interface; formats locaux adaptés.
- Dialogues non bloquants lorsque cela aide, notifications avec actions, erreurs expliquant la cause et prochaine étape.
- UI réactive : ne pas bloquer le thread UI sur scan, LSP, réseau, installation d’extensions ou Git. Support annulation/progression, gros workspaces, redémarrage/restauration.

N. FONCTIONS ÉTENDUES, À PLANIFIER APRÈS P0/P1
Évaluer puis implémenter en modules séparés, sans les confondre avec le socle initial : notebooks/Jupyter et vues interactives; remote development (WSL, SSH, Dev Containers, Tunnels) avec serveur distant; synchronisation; GitHub Pull Requests/Issues; ports; Live Share/collaboration; éditeur web; tâches et sessions multi-workspaces; outils MCP/agents; cartes/keymaps/langues; vue ports/couverture; profiles exportables. Chaque fonction peut dépendre d’un compte, d’un service distant ou d’une extension et doit être indiquée comme telle. N’affiche pas ces entrées comme opérationnelles avant leur réalisation.

ARCHITECTURE ET CONTRATS TECHNIQUES À ÉVALUER
- UI: WPF C# pour shell natif Windows + WebView2/Monaco pour le cœur éditeur; bridge JSON typé, versionné, whitelisté et validé entre JS et C#; aucun appel arbitraire à des chemins ou commandes depuis la webview.
- Services C#: WorkspaceService, EditorSessionService, SettingsService, ThemeService, CommandRegistry, TerminalService (ConPTY), TaskService, GitService, LspClient, DapClient, ExtensionCatalog/Manager/Host, GeminiProvider, DiagnosticsService.
- Communication LSP via stdio/JSON-RPC et DAP via transport documenté. Gestion des cycles de vie, annulation, timeout, stderr, logs et redémarrage.
- Fichiers : sauvegarde atomique, détection conflits disque, encodage/EOL, chemins Unicode et permissions. Ne suis pas des liens hors workspace sans action explicite.
- Services et UI découplés avec interfaces DI; fichiers paramétrage versionnés clairement séparés des secrets utilisateur. Ajouter un mécanisme de migration de paramètres.
- Vérifier licences et conditions des dépendances, Marketplace et outils intégrés avant livraison; documenter toute incompatibilité.

PLAN D’EXÉCUTION OBLIGATOIRE
Phase 0 — audit du dépôt, matrice fonctionnalité/capacité actuelle, architecture cible et risques; pas de refonte destructrice silencieuse.
Phase 1 — Shell/workbench visuel fidèle, gestion de disposition persistante, vrais menus/vues/command palette; Monaco complet dans la fenêtre, ouvertures et sauvegardes; thème, polices, raccourcis. Fournir captures de l’application à fenêtre 1440×900, une session avec editor+sidebar+terminal et une session avec thème clair.
Phase 2 — LSP, complétion standard, diagnostics, hover/navigation, quick fixes, formatage automatique configuré pour C# et au moins JavaScript/JSON; panneau Problems; fichiers multi-groupes.
Phase 3 — Terminal PTY réel, Tasks réelles, build/run .NET et launch/debug DAP avec configuration et points d’arrêt si adapter installé.
Phase 4 — Git UI complet avec staging/diff/branches/sync/conflits; tests et output.
Phase 5 — Galerie et extension manager définis honnêtement, VSIX/updates/trust, API d’extensions; themes/snippets/languages intégrés.
Phase 6 — KeyceIA Gemini avec inline completion contrôlable, chat, aperçu/acceptation de patch, sécurisation des clés, settings/profiles/accessibilité, installer Windows.
Phase 7 — support étendu notebooks/remote et autres intégrations, évalué en fonction des ressources réelles.
Ne pas rester bloqué à une phase si le temps ou l’environnement limite la portée : finir chaque tranche verticale, livrer buildable, puis continuer le backlog dans le dépôt. À chaque étape, noter ce qui fonctionne et les dépendances externes restantes.

CRITÈRES D’ACCEPTATION PRINCIPAUX
1. Au démarrage, la fenêtre présente une vraie disposition d’éditeur : Activity Bar gauche, Explorer gauche, éditeur principal à onglets, terminal inférieur, status bar. Sidebars, panel et éditeurs se redimensionnent/déplacent et leur état est restauré.
2. L’utilisateur peut ouvrir un workspace, créer/ouvrir/modifier/enregistrer/renommer/supprimer des fichiers, naviguer dans le workspace, fermer sans perdre silencieusement des changements et reprendre après redémarrage.
3. Un fichier C# est réellement éditable dans Monaco, avec coloration, sélection/multi-cursor, recherche/remplacement, numéros, minimap optionnelle, paramètres de police, formatage via l’outil .NET installé et IntelliSense/diagnostics via un language server installé/configuré.
4. Auto-completion standard et Gemini sont deux fonctions identifiables. Les suggestions IA apparaissent de façon non intrusive, peuvent être acceptées/rejetées/annulées et ne modifient pas le code sans l’utilisateur.
5. Les commandes F5/Ctrl+F5 correspondent à un lancement/build réel pour un projet supporté; le terminal reçoit une vraie session shell; les sorties/errors ouvrent la bonne ligne.
6. Les actions d’extension n’apparaissent qu’après intégration d’une galerie et d’un gestionnaire; installation/disable/update/uninstall modifient l’état réel et celui-ci survit au redémarrage.
7. Une séquence Git courante (stage, unstage, commit, diff, branch, pull/push si remote authentifié) est disponible; un conflit est visible et peut être résolu sans perdre silencieusement le contenu.
8. Thèmes sombre, clair et high contrast peuvent être changés immédiatement; police/taille/ligatures/indentation modifiables; préférences enregistrées.
9. Les menus, la command palette, les raccourcis, l’Explorateur, le terminal et les paramètres sont utilisables au clavier; aucune erreur n’est dissimulée.
10. Build Release reproductible, tests pertinents exécutés, pas de secret dans le dépôt, README d’installation/lancement/dépendances et matrice exacte des fonctions disponibles.

À LA FIN, FOURNIS
- Une synthèse des fonctionnalités réellement implémentées (pas promises).
- Les fonctions incomplètes, leur dépendance externe et l’étape restante.
- Les commandes de lancement/build/packaging et les résultats de build et tests.
- Les captures de l’interface et chemins de fichiers modifiés.
- Les limitations de compatibilité des extensions VS Code et du Marketplace.

Commence maintenant par inspecter le dépôt et continue jusqu’à ce que la tranche fonctionnelle définie soit réellement intégrée. Ne livre pas un simple mockup à la place de l’application.
```

## Références officielles consultées

- [Interface utilisateur et zones principales](https://code.visualstudio.com/docs/editing/getting-started/userinterface)
- [Personnaliser la disposition, panneaux, sidebars et groupes d’éditeurs](https://code.visualstudio.com/docs/configure/custom-layout)
- [Fonctions de l’éditeur et cycle edit/run/debug/test](https://code.visualstudio.com/docs/editing/getting-started/overview)
- [Édition et formatage de code](https://code.visualstudio.com/docs/editing/codebasics)
- [IntelliSense](https://code.visualstudio.com/docs/editing/intellisense)
- [Navigation et symboles](https://code.visualstudio.com/docs/editing/editingevolved)
- [Refactorings et Code Actions](https://code.visualstudio.com/docs/editing/refactoring)
- [Snippets](https://code.visualstudio.com/docs/editing/userdefinedsnippets)
- [Thèmes, couleurs, icônes et coloration](https://code.visualstudio.com/docs/configure/themes)
- [Paramètres utilisateur/workspace et priorité des scopes](https://code.visualstudio.com/docs/configure/settings)
- [Profils et import/export](https://code.visualstudio.com/docs/configure/profiles)
- [Synchronisation des paramètres](https://code.visualstudio.com/docs/configure/settings-sync)
- [Extensions et gestionnaire du Marketplace](https://code.visualstudio.com/docs/configure/extensions/extension-marketplace)
- [Débogage](https://code.visualstudio.com/docs/debugtest/debugging)
- [Tasks et intégration aux outils externes](https://code.visualstudio.com/docs/debugtest/tasks)
- [Tests](https://code.visualstudio.com/docs/debugtest/testing)
- [Terminal intégré](https://code.visualstudio.com/docs/terminal/basics)
- [Profils de terminal](https://code.visualstudio.com/docs/terminal/profiles)
- [Intégration terminal/shell](https://code.visualstudio.com/docs/terminal/shell-integration)
- [Contrôle de code source Git](https://code.visualstudio.com/docs/sourcecontrol/overview)
- [Conflits Git](https://code.visualstudio.com/docs/sourcecontrol/merge-conflicts)
- [Langages et fonctions qui dépendent des extensions](https://code.visualstudio.com/docs/languages/overview)
- [Accessibilité](https://code.visualstudio.com/docs/configure/accessibility/accessibility)
- [Remote Development](https://code.visualstudio.com/docs/remote/remote-overview)

## Note sur « tout »

VS Code possède des milliers d’extensions et un très grand nombre de paramètres. Les capacités de langage, de tests, de formatage et de débogage dépendent souvent d’extensions et d’outils installés. Le prompt demande donc une base de workbench complète, une architecture d’extensions explicite et un inventaire honnête des compatibilités; aucune liste finie ne peut garantir à elle seule une équivalence absolue avec chaque extension et réglage existant de VS Code.
