# Privacy

Version française : [CONFIDENTIALITE.md](CONFIDENTIALITE.md).

DrDXT has no account, no telemetry, and no server of its own. What it reads on your PC stays there, except the two
files you choose to send yourself, below.

## What DrDXT reads, and where it keeps it

To tell you what is slowing your PC down, DrDXT reads the hardware (processor, graphics card, memory, drives,
display), the Windows and driver settings that matter in games, the Windows event logs (unexpected shutdowns,
hardware errors, updates) and, during a match, the frame timing of your game (not its content) and the ping to its
server.

What it keeps stays on your machine:

- `%LOCALAPPDATA%\DrDXT`: your history (scans, measured matches, applied settings and their original value);
- `%ProgramData%\DrDxt`: what DrDXT applied so it can revert it, and the log of the program that writes settings;
- `Documents`: only the files you ask for (report, export of your machine).

Uninstalling removes the program, its service and what it applied; your history stays in `%LOCALAPPDATA%\DrDXT`.
Deleting that folder and `%ProgramData%\DrDxt` erases everything.

## What goes over the network

Only what the "What leaves the machine" section of the README lists: the public version of graphics drivers, pings,
the update check, and the pages you open yourself. Nothing that identifies you. A test fails if the code contacts an
address the README does not name.

## The two files you can send

**"Send my report"** opens your browser on a pre-filled GitHub issue: the title carries the model of your processor
and graphics card, the body the report of "Check DrDXT on my PC" (the settings tried, their original value, what was
read back). DrDXT sends nothing itself. You review the issue before submitting it; once submitted, it is readable by
anyone who can see the repository, under your GitHub account.

**"Export my machine"** writes a file to your Documents, which you send if you want your machine to serve as a test.
Before writing, DrDXT removes from it:

- the folder of any account (X:\Users\<name>): it becomes X:\Users\joueur, the rest of the path stays
- the Windows account name (even two letters long), the PC name and its domain
- serial numbers and hardware identifiers read on this PC (BIOS, motherboard, drives, memory, machine UUID, volume
  serial numbers)
- MAC addresses (with dashes, colons or none, for the network cards of this PC)
- IP addresses (v4 and v6); a four-number version (1.4.6.3) looks like a v4 address and is removed too, except in
  version fields (DriverVersion)
- Windows account identifiers (SID S-1-5-21-…)
- volume identifiers (Volume{…})
- email addresses
- the organization name of OneDrive folders ("OneDrive - <organization>")

What remains: the models of your hardware, your settings, the names of your peripherals (mice, headsets) and of the
programs that start with Windows. Read the file before sending it if any of them bothers you.

## A question

Open an issue on the repository, or write in private as [SECURITY.md](SECURITY.md) explains.
