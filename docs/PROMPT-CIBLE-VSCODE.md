# Prompt maître — KeyceCode, environnement de développement Windows

Copier le prompt ci-dessous dans l’agent de développement qui travaille sur ce dépôt. C’est un cahier des charges fonctionnel et visuel évolutif; il sert à piloter des livraisons successives, pas à prétendre qu’un IDE complet peut être réalisé en une heure.

---

## Prompt à exécuter

Tu es l’ingénieur principal chargé de faire évoluer **KeyceCode**, un IDE Windows natif principalement en C#/.NET, dans le dépôt `Danielle2408/KeyceCode-`. Le résultat recherché est une application stable et utilisable au quotidien qui reprend environ 80 % des workflows et des repères visuels de Visual Studio Code. Inspire-toi du comportement et de la disposition documentés de VS Code; ne copie pas son code propriétaire, ses marques, ses ressources, ni ses icônes. Le logo KeyceCode fourni dans le dépôt est la marque de l’application.

### Règles de conduite

1. **Commence par inspecter l’existant** : lis README, AGENTS.md, solution, architecture, état Git, captures et limitations connues. Conserve les fonctionnalités déjà implémentées. Ne réécris pas une zone entière avant d’avoir compris ses dépendances.
2. **Travaille par petits jalons verticaux réellement utilisables**. Avant chaque jalon, annonce en une phrase son résultat attendu. Implémente-le, lance les vérifications pertinentes, corrige les échecs, mets README à jour, puis crée un commit descriptif et pousse-le sur `origin main`. Ne force jamais un push. N’inclus jamais secrets, clés, fichiers temporaires ou sorties de build inutiles.
3. **Ne simule jamais une fonction absente.** Chaque action visible doit fonctionner ou afficher clairement pourquoi elle est indisponible. Aucun bouton décoratif ou raccourci silencieusement sans effet.
4. Priorité au C# pour shell, services, persistance, processus, fichiers, sécurité et intégration Windows. Réutilise Monaco pour le cœur d’édition si sa licence et son intégration du dépôt le permettent; JavaScript/TypeScript est acceptable uniquement dans le client WebEditor. Garde les dépendances épinglées, les ressources d’exécution locales et la compilation reproductible.
5. Aucun secret dans le code, Git, le navigateur, les journaux ou les captures. Pour Gemini, lire la clé depuis un stockage chiffré Windows ou variable d’environnement, prévoir modèle configurable, délais, annulation, limites de taille, erreurs lisibles et consentement avant d’envoyer le contexte du code.
6. Avant modification d’un workspace, restreins les chemins à la racine autorisée, détecte traversal/liens symboliques, demande confirmation pour suppression/destruction et évite de lire des fichiers binaires ou secrets par défaut.
7. **N’annonce “terminé” qu’après vérification observable.** Donne toujours fichiers touchés, commandes réellement lancées, résultats, commit/push, fonctions encore manquantes et prérequis externes.

### Cible d’interface : disposition physique

Reproduis les repères familiers d’un IDE de code sombre, dense, net et redimensionnable, avec séparateurs fins et typographie lisible. Les couleurs doivent être cohérentes par thème; pas de palette arbitraire ni de gros aplats décoratifs. L’interface doit bien fonctionner en fenêtre redimensionnée, haute densité, thème clair et contraste élevé.

La fenêtre doit offrir, avec commandes pour afficher/masquer et déplacer les zones :

- **Barre de titre** : nom du dossier/espace de travail, champ de commande/recherche, actions de disposition, contrôles de fenêtre Windows.
- **Barre de menus** : Fichier, Édition, Sélection, Affichage, Aller à, Exécuter, Terminal, Aide; chaque entrée ouvre un menu complet, clavier accessible.
- **Activity Bar** à l’extrême gauche : Explorateur, Recherche, Contrôle de code source, Exécuter/Déboguer, Extensions, Tests; badges de changements/erreurs; réordonnable et masquable.
- **Barre latérale principale** : vue choisie, sections repliables et actions/context menus; largeur ajustable, gauche/droite, glisser-déposer des vues.
- **Éditeur central** : onglets, groupes d’éditeurs horizontaux/verticaux, déplacement d’onglets, onglet épinglé/aperçu, fermeture et indicateur de modifications, breadcrumbs, toolbar contextuelle, minimap optionnelle, guides, marge de numéros/breakpoints, ascenseur avec marqueurs.
- **Barre latérale secondaire** à droite, notamment pour le chat KeyceIA et les vues déplaçables.
- **Panneau inférieur** : onglets Problèmes, Sortie, Console de débogage, Terminal, Tests; panneaux redimensionnables et déplaçables à gauche/droite/éditeur.
- **Barre d’état** : branche Git et état, erreurs/avertissements, position ligne/colonne, espaces/tabulation, encodage, EOL, langage, indentation, notifications et cible de lancement; chaque segment ouvre son sélecteur.
- **Palette de commandes** et Quick Open flottants en haut au clavier, avec fuzzy search, navigation flèches, historique et commandes récentes.

Le layout et l’état des vues/éditeurs/workspace doivent être conservés par utilisateur et restaurés au redémarrage. Référence fonctionnelle : [interface officielle](https://code.visualstudio.com/docs/editing/getting-started/userinterface), [layouts personnalisables](https://code.visualstudio.com/docs/configure/custom-layout).

### Fonctionnalités à couvrir

#### 1. Workspace et explorateur

- Ouvrir dossier, fichier seul, plusieurs dossiers et espace de travail sauvegardé; ajouter/retirer dossier; récents; restaurer workspace, groupe et curseurs.
- Arborescence paresseuse pour gros projets; filtrage/exclusion configurables; fichiers masqués; icônes cohérentes; compact folders; expansion, sélection clavier, drag/drop, copier/coller, multi-sélection.
- Nouveau fichier/dossier, ouvrir, renommer, supprimer avec confirmation, copier chemin, révéler dans l’Explorateur Windows, ouvrir dans terminal; annuler/rétablir opérations quand possible.
- Détecter les changements externes, fichiers supprimés, conflit disque/éditeur, et proposer recharger/comparer/garder éditeur.
- Timeline locale/Git par fichier, aperçu image/texte, fichiers non texte identifiés correctement.

#### 2. Édition et navigation de code

- Édition Monaco complète : Undo/Redo, plusieurs curseurs, sélection colonne, indentation intelligente, auto-paires/fermeture, commentaires, pliage, bracket matching/coloration, sélection occurrence, rename symbol, code actions/refactorings via LSP, snippets, commandes par langage.
- Complétion automatique pendant la frappe, déclencheurs, `Ctrl+Space`, filtre/fuzzy, détails/docs/signatures, acceptation Enter/Tab/commit chars, snippets et suggestions locales. Les fonctions langage dépendent des providers réellement installés; indiquer l’état du serveur.
- Navigation Go to Definition/Declaration/Implementation/Type Definition/References, symboles fichier/workspace, breadcrumbs, Peek Definition/References, liens, go-to-line, back/forward, multi-root; symboles via LSP.
- Format document/sélection, format à la sauvegarde/au collage/à la frappe selon capacité du formatter; choix du formatter par langage; organise imports, code actions et erreurs de provider visibles.
- Recherche/remplacement dans fichier et workspace avec casse, mot entier, regex, inclusion/exclusion, résultats navigables et vue de recherche. Raccourcis familiers : `Ctrl+F`, `Ctrl+H`, `Ctrl+Shift+F`, `Ctrl+P`, `Ctrl+Shift+P`, `F12`, `Alt+F12`, `Shift+Alt+F`, `Ctrl+G`.
- Sauvegarde manuelle/auto, dirty-state, sauvegarde avant run/debug, encodages, BOM, LF/CRLF, fin de fichier, language mode.
- Raccourcis modifiables, conflits détectés, keymap importable/exportable; raccourcis Windows VS Code de base cohérents.

VS Code expose de nombreux réglages de complétion comme suggestions rapides, délai, sélection et tab completion; ne réduis pas IntelliSense à un bouton manuel. Voir [IntelliSense](https://code.visualstudio.com/docs/editing/intellisense) et [édition/formatage](https://code.visualstudio.com/docs/editing/codebasics).

#### 3. Apparence, thèmes et paramètres

- Thèmes intégrés crédibles : Dark+, Light+, High Contrast clair/sombre et thème KeyceCode nuit/orange utilisé avec retenue; sélecteur avec aperçu, persistance et contraste vérifié.
- Installer/sélectionner thèmes de couleur, thèmes d’icônes de fichiers, police mono et fallback, taille, graisse, ligatures, hauteur de ligne, espace lettres, rendu, curseur; toutes les couleurs (éditeur, syntaxe, sélection, diagnostics, menus, vues, terminal, panneau, statusbar, focus et contrastes) pilotées par tokens de thème.
- Éditeur de paramètres graphique filtrable, édition JSON directe avec validation, IntelliSense et erreurs; vues Utilisateur/Espace de travail/Dossier, valeurs héritées, reset, portée et recherche. Règles par langage.
- Options d’éditeur : word wrap, minimap côté gauche/droite/masquée, numéros/relatifs, whitespace, rulers, guides, glyph margin, folding, sticky scroll, bracket pair colorization, smooth scrolling, ligatures, auto-save, format-on-save, tab size, spaces/tabs, auto closing, suggest, quick suggestions, inlay hints, render whitespace, cursor style/blink, scroll beyond last line, line highlight, rulers, accessibility/screen reader.
- UI : taille zoom, densité, visibilité/menu compact, Activity Bar, sidebars, status bar, breadcrumbs, tabs, panel position/taille, centrer editor, Zen mode, plein écran, layouts, notifications et animations.
- Préférences exportables/importables et profils isolant réglages, raccourcis, extensions et thème. Settings Sync seulement si service authentifié explicite et sécurisé.

La documentation officielle distingue thèmes couleur et thèmes d’icônes et permet le réglage précis des couleurs UI : [thèmes VS Code](https://code.visualstudio.com/docs/configure/themes), [paramètres utilisateur/workspace](https://code.visualstudio.com/docs/configure/settings).

#### 4. Extensions

- Vue Extensions installées, recommandées, populaires, mises à jour, désactivées; recherche, catégorie, publisher, identité, détails, version, notes, licences, compatibilité et rating/source.
- Recherche/téléchargement depuis Open VSX ou marketplace configuré; versions, préversions, VSIX local, progression/annulation/reprise, hash/signature si disponible, erreurs proxy/réseau, proxy configuré.
- Installer/désinstaller/mettre à jour/activer/désactiver/recharger, dépendances et extension packs, recommandations `.vscode/extensions.json`, mises à jour auto globale/par extension, versions épinglées, CLI listing/install.
- Conception d’un **extension host isolé** compatible avec manifest/contributions : commandes, menus, keybindings, vues, thèmes, langages, grammaires, snippets, configuration, tasks/debug adapters/providers. API supportée documentée. Permissions/consentement publisher; arrêt/crash logs; extension non fiable désactivable. Ne jamais prétendre qu’un VSIX simplement téléchargé est installé/activé.
- Comme VS Code, les extensions exécutent du code et ont une surface de permissions importante; appliquer consentement, validation du paquet, confinement adapté et provenance. La fonction marketplace inclut install, désactivation, mise à jour, gestion VSIX et versions : [documentation extensions](https://code.visualstudio.com/docs/configure/extensions/extension-marketplace).

#### 5. Terminal, tâches et exécution

- Terminal intégré interactif utilisant un vrai pseudo-terminal Windows/ConPTY avec rendu xterm.js, sélection/copier/coller, liens, recherche, plusieurs terminaux, groupes/splits, renommer/couleur/icône, kill/restart, envoyer sélection/ligne, sortie persistante.
- Détection et configuration profils PowerShell, cmd, Git Bash, WSL et shells présents; exécutable/arguments/env/cwd; variable workspace; terminal automation; shell integration, code de sortie et navigation d’erreurs.
- Tâches `.vscode/tasks.json`, détection auto build/test, task runner, dépendances, défauts build/test, matcher de problèmes, tâches background/watch, arrêter/réexécuter, sortie et diagnostic.
- Exécuter fichier/projet configuré, choix runtime/target, arguments, variables d’environnement, répertoire courant, build avant lancement, terminal/output adapté; pas de commande présumée sans montrer son effet.

Voir [terminal](https://code.visualstudio.com/docs/terminal/basics), [profils de terminal](https://code.visualstudio.com/docs/terminal/profiles) et la documentation tâches/run de VS Code.

#### 6. Débogage et tests

- Client Debug Adapter Protocol (DAP), sélection/découverte debugger, `.vscode/launch.json`, configurations, compounds, pre/post tasks, arguments, env, cwd, attach et logs.
- Breakpoints, conditions, hit count, logpoints, data/function breakpoints si adaptateur compatible; indicateurs margin, disabled/unverified avec motif.
- Run and Debug avec variables, locals/globals, watch, call stack, scopes, modules, breakpoints, threads, debug console REPL, hover, valeurs inline; step over/in/out, continue, pause, restart, stop, frame selection.
- Vue Tests : découverte par extension, arbre, run/debug/all/failed, résultats, messages, output, couverture si provider; historique et filtres.
- Problèmes agrégés workspace: erreurs, avertissements, source, emplacement, filtre, navigation; diagnostics LSP et task matcher.

Modélise fidèlement les vrais workflows de pause, frames, variables, watch et console décrits dans la [documentation débogage](https://code.visualstudio.com/docs/debugtest/debugging) et les [tests](https://code.visualstudio.com/docs/debugtest/testing). Signaler clairement les capabilities non fournies par un adaptateur.

#### 7. Git et collaboration locale

- Source Control : changements, untracked, stage/unstage/discard, diff inline et côte à côte, stage sélection/bloc, commit/message, amend, commit history, branches, switch/create/merge/rebase avec état, conflits et résolution, stash, fetch/pull/push, remotes, tags et worktrees.
- Confirmation ciblée avant discard, reset destructif, rebase et autres actions irréversibles; journal et erreurs d’authentification compréhensibles.
- Ne jamais lancer de push automatiquement par l’application sans commande utilisateur; dans ce dépôt de développement, l’agent publie chaque jalon validé sur `origin main` conformément à l’instruction du propriétaire.

#### 8. KeyceIA Gemini

- Chat dans sidebar secondaire, contexte actif/selection/fichiers choisis avec aperçu exact et consentement avant envoi; pas d’envoi silencieux du workspace complet.
- Complétion inline fantôme non bloquante, accepter/rejeter/mot/ligne/édition; chat Explain/Fix/Refactor/Test/documentation; streaming, annuler, historique local, erreurs, limites et modèle sélectionnable.
- Clé saisie masquée, stockage Windows DPAPI, suppression, jamais affichée ni écrite dans des logs; modèle configurable. Montrer les données transmises, coûts/quotas indicatifs si disponibles, erreurs de réseau/limite/API.

#### 9. Sûreté, accessibilité et qualité

- Parcours clavier complet, focus visible, raccourcis annoncés, lecteurs d’écran, labels accessibles, tailles et contraste WCAG visés, réduction animation, zoom 80–200 %.
- Workspace trust, dossier non fiable, exclusion secrets/binaires/build output; autorisations minimum et messages d’erreur actionnables.
- Journal local accessible via Output, niveaux configurables, crash recovery, gestion processus enfant, annulation et arrêt propre; ne pas laisser de processus orphelin.
- Tests ciblés pour services fichiers, persistance, encodage, chemins, commandes, protocoles LSP/DAP, installer VSIX et UI smoke test. Pour chaque version: compilation Release, tests unitaires existants, syntax check client, lancement manuel, captures aux résolutions usuelles, inspection des erreurs et `git diff --check`.
- Mesurer démarrage, mémoire, grands workspaces, recherche rapide, interaction clavier; chargement paresseux, debounce et annulation pour rester fluide.

### Architecture cible

- WPF/.NET pour la fenêtre Windows, menus, services et interop; WebView2 local pour Monaco/xterm; bridge JS–C# typé, versionné, validé et borné. Aucune navigation Web arbitraire privilégiée dans WebView2.
- Services séparés : Workspace/FileSystem, session/layout, settings/themes, editor bridge, process/terminal, Git, LSP, DAP, extensions/marketplace, tasks/tests, Gemini, logs. Interfaces DI et annulation async; pas de logique métier dans le code-behind.
- Protocoles LSP/DAP transportés en processus isolés avec Content-Length correct, timeouts, stdout protocol-only, stderr loggé, shutdown et restart; capabilities déclarées et erreurs affichées. Auto-completion Gemini n’écrase jamais les propositions LSP.
- Persistences versionnées/migrables sous AppData; settings workspace dans `.vscode`, secrets à part; sauvegardes atomiques.
- Installation Windows propre : icône utilisateur, nom KeyceCode, raccourci, association optionnelle, runtime vérifié, ressources intégrées, désinstallation propre, versioning et release reproductible.

### Ordre de livraison et critères d’acceptation

Ne tente pas de tout implémenter en une fois. Ordonne le travail ainsi, en signalant les dépendances et l’estimation :

**P0 – solide au quotidien** : disposition workbench exacte, navigation et menus clavier, workspace/explorer sûr, onglets/groups, recherche, éditeur/formattage/LSP opérationnels, settings visibles et persistants, themes/icônes/polices, terminal ConPTY, Git de base, run/tasks, problèmes, restauration session, pack Windows.

**P1 – boucle de développement** : Debug Adapter Protocol avec breakpoints/variables/watch/console, Tests API, extension host isolé et extensions réellement exécutables, marketplace install/update/rollback, keybindings, profils, workspace multi-root, snapshots/diffs et tâches avancées.

**P2 – finition** : remote/WSL/dev containers, notebooks, Settings Sync, collaboration, extension API étendue, optimisations grands mono-repos, accessibilité complète.

Pour chaque jalon, associe chaque commande/UI à un critère vérifiable et automatisable. Au minimum : démarrer sur Windows propre; ouvrir/fermer workspace et restaurer session; créer/renommer/supprimer fichier en refusant path traversal; éditer plusieurs onglets/groupes; format à la sauvegarde; obtenir completion/diagnostic depuis un serveur LSP de test; lancer et interagir avec PowerShell dans ConPTY; rechercher/remplacer dans workspace; stage/diff/commit Git; installer puis désactiver une extension d’essai isolée; lancer une config DAP et atteindre un breakpoint; afficher une erreur de tâche/test; changer thème/police/zoom, redémarrer et retrouver ces options; vérification DPAPI avec clé factice uniquement.

Avant de coder, dresse une matrice **existant / partiel / manquant / dépendance / étape** à partir du dépôt. Ensuite livre le prochain jalon testable. Termine le compte rendu par:

```text
Jalon :
Comportements livrés :
Vérifications réellement exécutées :
Commit et push :
Limites et dépendances restantes :
Prochain jalon :
```

---

## Références produit consultées

- [Interface utilisateur et vues](https://code.visualstudio.com/docs/editing/getting-started/userinterface)
- [Éditeur, workspace et tâches de développement](https://code.visualstudio.com/docs/editing/getting-started/overview)
- [Disposition personnalisée](https://code.visualstudio.com/docs/configure/custom-layout)
- [Complétion IntelliSense](https://code.visualstudio.com/docs/editing/intellisense)
- [Édition et formatage](https://code.visualstudio.com/docs/editing/codebasics)
- [Thèmes et icônes](https://code.visualstudio.com/docs/configure/themes)
- [Paramètres utilisateur et workspace](https://code.visualstudio.com/docs/configure/settings)
- [Extensions, Marketplace et VSIX](https://code.visualstudio.com/docs/configure/extensions/extension-marketplace)
- [Débogage](https://code.visualstudio.com/docs/debugtest/debugging)
- [Tests](https://code.visualstudio.com/docs/debugtest/testing)
- [Terminal intégré](https://code.visualstudio.com/docs/terminal/basics)
- [Profils de terminal](https://code.visualstudio.com/docs/terminal/profiles)
