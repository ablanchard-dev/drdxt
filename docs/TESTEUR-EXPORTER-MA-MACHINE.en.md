# Tester: export your machine

Version française : [TESTEUR-EXPORTER-MA-MACHINE.md](TESTEUR-EXPORTER-MA-MACHINE.md).

DrDXT is developed on a single machine (Intel 14th gen, RTX 4060, desktop). To know what it says on an AMD or Intel Arc
card, a laptop, an old PC or an account without administrator rights, we need what DrDXT reads on those machines. The
"Export my machine" button writes that reading to a file, which you send yourself if you want to help.

## How to do it

1. Open DrDXT and run a scan.
2. At the bottom of the result screen, under "See the report", click **Export my machine**.
3. The screen shows the path of the file: `Documents\machine-exportee-<date>.json`.
4. Open it in Notepad if you want to see what it contains, then send it (Discord, email, as you prefer). DrDXT sends
   nothing: the file stays on your PC until you send it.

No administrator rights needed: without them, the file says exactly what an account without rights sees, and that is
useful too.

## What it contains

- **The hardware**: processor model and core count, graphics card(s), video memory and driver version, memory sticks
  (part number, maker, frequency, voltage, slot), motherboard (maker, model), BIOS version and date, kind of machine
  (desktop, laptop).
- **The settings read**: XMP/EXPO memory profile, Resizable BAR, power plan, HAGS, Game Mode, parked cores, VRR,
  screen refresh rates, network card settings (name and model of the card), dates of recent hard shutdowns, and so on.
- **The doctor's findings**, as the screen shows them: failing services, scheduled tasks and services whose file can
  be replaced (with their path), kernel incidents, software and drivers installed or updated recently, and so on.
- Whether DrDXT ran with administrator rights, and the DrDXT version.

## What is removed from it

Before writing the file, DrDXT removes from every text:

- the known folders of the account as Windows reads them (Documents, Desktop, AppData, local AppData, and the profile
  outside `X:\Users`), wherever they are, even redirected: they become a marker (`<Documents>`, `<Desktop>`…), the rest
  of the path stays;
- the folder of any account (`C:\Users\<name>`): it becomes `C:\Users\joueur`, the rest of the path stays;
- the name of your Windows account (even two letters long), the name of the PC and its domain;
- the serial numbers and hardware identifiers read on this PC (BIOS, motherboard, disks, memory, machine UUID, volume
  serial numbers);
- the end of device identifiers (`USB\VID_…&PID_…\<instance>`): a serial number, a Bluetooth address; the model stays;
- MAC addresses;
- IP addresses (v4 and v6);
- Windows account identifiers (SID);
- volume identifiers (`Volume{…}`);
- email addresses;
- the organization name of OneDrive folders (`OneDrive - <organization>`);
- dashed keys (licence keys: at least four groups of five letters or digits) and hashes (32 hexadecimal characters or
  more).

Each removed value becomes `<retiré>`. This list is the code's (`MachineExportee.CeQuiEstRetire`), and a test checks
that none of these values, planted in a fake machine, comes out.

## What the removal does not cover

- **Names you chose yourself**: a scheduled task, a folder outside your profile, a renamed network card. If they
  contain your account name, it is removed; if they contain something else (a nickname, a first name different from
  the account), it stays. Read the file before sending it if that worries you.
- **A one-letter account name** is not removed from the texts (it would be removed everywhere); only your
  `C:\Users\<name>` folder becomes `joueur`.
- **The list of recent software and drivers** the doctor mentions stays: it says what is installed on your machine.
- A four-number version (`1.4.6.3`) has the shape of an IP address: in the doctor's texts, it is removed too. Hardware
  versions (driver, BIOS) stay.

## For the maintainer

The received file goes as is into `tests/Shared/machines/` (renamed if needed: the file name is the machine name); the
`MachinesEnregistreesTests` tests replay it up to the screen, and in DEBUG `DrDxt.App.exe machine=<name>` shows it in
the app. The same format comes out of `tools/enregistreur`.
