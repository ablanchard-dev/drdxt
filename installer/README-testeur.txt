DrDXT — version de test
=======================

Merci de tester DrDXT. Ce build est une pre-version : il n'est pas
encore signe, Windows va donc afficher un
avertissement au lancement de l'installeur. C'est normal.

INSTALLER
  1. Double-clique sur DrDXT-Setup.exe.
  2. Si Windows affiche "Windows a protege votre ordinateur" :
       clique "Informations complementaires" puis "Executer quand meme".
     (ou : clic-droit sur le .exe -> Proprietes -> coche "Debloquer" -> OK,
      avant de le lancer.)
  3. Accepte l'elevation (UAC) : l'installeur ecrit dans Program Files.

  Rare : si "Smart App Control" bloque le fichier (uniquement sur une
  install NEUVE de Windows 11), va dans Securite Windows -> Controle des
  applications et du navigateur -> parametres Smart App Control -> Desactive,
  installe, puis reactive-le. La plupart des PC ne l'ont pas active.

CE QUE FAIT DrDXT
  - Scanne ton PC (CPU / GPU / RAM / reglages) et chiffre honnetement ce
    qui est ameliorable. Le scan ne modifie rien.
  - Mesure tes perfs en jeu (FPS, 1% lows) via PresentMon.
  - Applique des optimisations sures, mesure le gain, et ANNULE tout seul
    si ca ne va pas (point de restauration cree avant).

ANTI-CHEAT
  DrDXT ne s'injecte JAMAIS dans le jeu, ne modifie aucun fichier du jeu,
  ne pose aucun overlay. Il lit les perfs (PresentMon = observation pure)
  et applique des reglages Windows/pilote surs. Sans risque pour ton compte.

SERVICE DE MESURE
  L'installeur enregistre le service "PresentMonService" en mode manuel
  (demarre a la demande, pas au demarrage de Windows). Il sert uniquement
  a mesurer les FPS. La desinstallation le retire.

SERVICE DE DEMARRAGE (option, case decochee)
  Une case de l'installeur, decochee par defaut : "Reappliquer mes reglages
  au demarrage de Windows". Cochee, elle enregistre le service
  "DrDxtReconciler" en demarrage automatique : au demarrage, il remet les
  reglages que tu as appliques avec DrDXT si Windows les a changes. Il ne
  touche jamais la carte graphique. Decochee, rien ne demarre avec Windows.
  La desinstallation le retire.

DESINSTALLER
  Parametres Windows -> Applications -> DrDXT -> Desinstaller.
  (Retire les services et tous les fichiers.)
