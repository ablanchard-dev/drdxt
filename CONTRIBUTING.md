# Contributing to DrDXT

Version française : [CONTRIBUTING.fr.md](CONTRIBUTING.fr.md).

DrDXT is free and licensed under GPL v3. Three ways to help, from the simplest to the most technical.

## 1. Prove DrDXT on your hardware

This is the most useful help: DrDXT is developed on few machines, and nothing is called "validated" without proof.

- **Check DrDXT on my PC** (in the app) applies each setting, reads it back, reverts it and reads it again, with your
  consent. The report lands in `Documents`. Open a "Verification report" issue and attach it.
- **No install needed**: the zips in the [Release](https://github.com/ablanchard-dev/drdxt/releases) read your PC, or
  run the same verification, and write the same report to your `Downloads` folder. Their instructions are in
  `READ-ME.txt` and `READ-ME-VALIDATION.txt`.
- **Export my machine** writes what DrDXT reads on your PC, with nothing that identifies you. Open a "Machine" issue
  and attach the file.

Reports, the app and the zips are in French or English; you can write your issue in either language.

A validated report goes into `tests/Shared/validations` (named `<architecture>-<card>-<date>.txt`), then its
architecture is added to `GpuTuningCoverage.ValideesEnVie`; a guard test refuses the opposite.
[docs/COUVERTURE.md](docs/COUVERTURE.md) says what DrDXT does on which PC.

## 2. Report a problem

A "Bug" issue: what you did, what you expected, what happened, and your DrDXT version (bottom of the screen). Attach
the diagnostic report ("View report") if the app produced one. A security issue is reported in private: see
[SECURITY.md](SECURITY.md).

## 3. Write code

Start with [ARCHITECTURE.md](ARCHITECTURE.md): the projects, the rules the code keeps, and how to add a setting
from end to end.

Requirements: Windows 10 or 11, the .NET 10 SDK.

```
dotnet build DrDXT.sln -c Release
dotnet test DrDXT.sln -c Release --no-build --filter "Category!=Machine"
```

`Category=Machine` tests read the real hardware; they run by hand, not in CI.

The rules the test suite enforces:

- **A fix starts with a failing test**, then the code makes it pass. A guard that cannot fail proves nothing: every
  guard has its witness.
- **Windows and hardware settings are written by the helper**, behind its allowlist; values from the user's session
  are written with the user's token, after the same allowlist. Every write has its inverse, read before writing, and
  a test that reverts it. The table of write sites ([docs/NON-NUISANCE.md](docs/NON-NUISANCE.md)) is recomputed by CI.
- **No gain is shown without its source**: a displayed figure carries a URL, otherwise it is not displayed.
- **What leaves the machine is stated in the README**; a test checks the addresses written in the code that makes
  the network call.
- **App texts exist in French and English.**
- The test count in the README follows the last run (`python tools/gates/compte_des_tests.py`).

The code is written in French on purpose (identifiers, comments, test names), and new code follows that rule; each
file opens with a short French summary of what it does and why. See "Language of the code" in
[ARCHITECTURE.md](ARCHITECTURE.md). Pull requests and issues are welcome in English or French. A commit says what
changes for the player, then why.
