DrDXT - test version
====================

Thanks for testing DrDXT. This build is a pre-release: it is not
signed yet, so Windows will show a warning when the installer
starts. That is expected.

INSTALL
  1. Double-click DrDXT-Setup.exe.
  2. If Windows shows "Windows protected your PC":
       click "More info" then "Run anyway".
     (or: right-click the .exe -> Properties -> tick "Unblock" -> OK,
      before running it.)
  3. Accept the elevation (UAC): the installer writes to Program Files.

  Rare: if "Smart App Control" blocks the file (only on a FRESH
  Windows 11 install), go to Windows Security -> App & browser
  control -> Smart App Control settings -> Off, install, then turn
  it back on. Most PCs do not have it turned on.

WHAT DrDXT DOES
  - Scans your PC (CPU / GPU / RAM / settings) and honestly puts a figure
    on what can be improved. The scan changes no setting (it may install
    or start the PresentMon measuring service if it is missing).
  - Measures your in-game performance (FPS, 1% lows) through PresentMon.
  - Applies safe optimizations and judges each one on your real
    matches: removed if it makes things worse, and a gain is only
    announced once measured (restore point created first).

ANTI-CHEAT
  DrDXT NEVER injects into the game, changes no game file,
  and draws no overlay. It reads performance (PresentMon = observation
  only) and applies safe Windows/driver settings. No risk to your account.

MEASURING SERVICE
  The installer registers the "PresentMonService" service in manual mode
  (started on demand, not when Windows starts). It is only used to
  measure FPS. The uninstall removes it.

STARTUP SERVICE (option, checked box)
  One installer box, checked by default: "Reapply my settings when
  Windows starts". Checked, it registers the "DrDxtReconciler" service
  with automatic start: at startup, it puts back the settings you
  applied with DrDXT if Windows changed them. It never touches the
  graphics card. It also reads the processor temperature for match
  recording. Unchecked, nothing starts with Windows.
  The uninstall removes it.

UNINSTALL
  Windows Settings -> Apps -> DrDXT -> Uninstall.
  (Removes the services and the program files, and puts back the kept
  settings. What remains is listed in the project README.)
