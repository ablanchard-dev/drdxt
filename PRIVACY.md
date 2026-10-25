# Privacy

Version française : [CONFIDENTIALITE.md](CONFIDENTIALITE.md).

DrDXT has no account, no telemetry, and no server of its own. What it reads on your PC stays there, except the
files you choose to send yourself, below.

## What DrDXT reads, and where it keeps it

To tell you what is slowing your PC down, DrDXT reads the hardware (processor, graphics card, memory, drives,
display), the Windows and driver settings that matter in games, the Windows event logs (unexpected shutdowns,
hardware errors, updates), the reports your game writes when it crashes, OBS settings and logs (encoder, bitrate,
dropped frames; never your scenes, your account or your paths), how much each program sends during 3 s of the scan
(its name and a rate, never the content or the address), the reports your mouse and controller send when you start
their measurement and, during a match, the frame timing of your game (not its content) and the ping to its server.

What it keeps stays on your machine:

- `%LOCALAPPDATA%\DrDXT`: your history (scans, measured matches, applied settings and their original value);
- `%ProgramData%\DrDxt`: what DrDXT applied so it can revert it, the log of the program that writes settings, and the
  incident records (the time of an unexpected shutdown and the names of the Windows events around it, never their
  message);
- `Documents`: only the files you ask for (report, export of your machine).

Uninstalling removes the program and its services, and reverts the settings it knows; your history stays in
`%LOCALAPPDATA%\DrDXT`, and the rest is listed in the README ("What remains").
Deleting that folder and `%ProgramData%\DrDxt` erases everything.

## What goes over the network

Only what the "What leaves the machine" section of the README lists: the public version of graphics drivers, pings,
one question to your router on the home network (its WAN address, never shown), the update check at startup (turned
off by setting `DRDXT_UPDATE_URL` to `off`), the line test under load if you click it (`speed.cloudflare.com`), the
Windows repair if you start it (DISM downloads from Windows Update), and the pages you open yourself. Nothing that identifies you. A test fails if the code contacts an
address the README does not name.

Your Windows account credentials go to a proxy only if someone configured it: an HTTP(S)_PROXY variable, or a manual
proxy while neither automatic detection nor a setup script is on. A proxy found automatically (WPAD) or given by a script
receives nothing, even when it asks: on a public network, a fake proxy would otherwise get your account's sign-in
exchange. Behind a company proxy found that way that requires sign-in, the update check stays unreachable.

## The files you can send

**"Send my report"** opens your browser on the GitHub issue template, empty: nothing from your machine is in the
link. You attach the report of "Check DrDXT on my PC" yourself (the settings tried, their original value, what was read
back), saved in your `Documents`. DrDXT sends nothing itself. You review the issue before submitting it; once
submitted, it is readable by anyone who can see the repository, under your GitHub account.

**"Prepare a help package"** first shows the list of files it will hold and the exact text of each, already anonymized, then writes a zip where you choose, which you send yourself (Discord, issue). It holds only the doctor's report, your recorded matches, the change log and DrDXT's crash log, with the same removals as the export below. No other file goes in, and never a firmware variable or table.

**"Export my machine"** writes a file to your Documents, which you send if you want your machine to serve as a test.
Before writing, DrDXT removes from it:

- the account's known folders read from Windows (Documents, Desktop, AppData, local AppData, and the profile outside X:\Users), wherever they are, even redirected: they become a marker (<Documents>, <Desktop>…), the rest of the path stays
- the folder of any account (X:\Users\<name>): it becomes X:\Users\joueur, the rest of the path stays
- the Windows account name (even two letters long), the PC name and its domain
- serial numbers and hardware identifiers read on this PC (BIOS, motherboard, drives, memory, machine UUID, volume
  serial numbers)
- the end of device identifiers (`USB\VID_…&PID_…\<instance>`): a serial number, a Bluetooth address; the model stays
- MAC addresses (with dashes, colons or none, for the network cards of this PC)
- IP addresses (v4 and v6); a four-number version (1.4.6.3) looks like a v4 address and is removed too, except in
  version fields (DriverVersion)
- Windows account identifiers (SID S-1-5-21-…)
- volume identifiers (Volume{…})
- email addresses
- the organization name of OneDrive folders ("OneDrive - <organization>")
- dashed keys (license: at least four groups of five letters or digits) and fingerprints (32 hexadecimal characters or more)

What remains: the models of your hardware, your settings, the names of your peripherals (mice, headsets) and of the
programs that start with Windows. Read the file before sending it if any of them bothers you.

## A question

Open an issue on the repository, or write in private as [SECURITY.md](SECURITY.md) explains.
