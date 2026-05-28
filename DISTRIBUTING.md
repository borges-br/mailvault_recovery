# MailVault Recovery — Distribution Guide

## Building the distribution

### Prerequisites

- .NET 10 SDK (required only to build; end users do not need it for self-contained builds)
- Windows 10 or later, x64

### Running publish.ps1

From the repository root:

```powershell
# Default: self-contained, win-x64, output to .\dist
.\publish.ps1

# Non-self-contained (requires .NET 10 runtime on target machine)
.\publish.ps1 -SelfContained:$false

# Custom output directory or runtime
.\publish.ps1 -OutputDir ".\release\v1.0" -Runtime "win-x64"
```

### What the script does

1. Cleans and recreates the output directory.
2. Publishes the Desktop GUI (non-single-file, all assets alongside the exe).
3. Publishes the CLI as a single self-extracting binary (`mailvault.exe`).
4. Prints the sizes of both output folders.

---

## Output folder structure

```
dist\
├── MailVaultRecovery\          # Desktop GUI application
│   ├── MailVault.Desktop.exe   # Main launcher
│   ├── MailVault.Desktop.dll
│   ├── Avalonia*.dll
│   └── ...                     # All runtime dependencies
│
└── MailVaultCli\               # CLI tool
    └── mailvault.exe           # Single self-contained executable
```

---

## System requirements

| Component | Requirement |
|-----------|-------------|
| OS | Windows 10 or later (64-bit) |
| Architecture | x64 |
| .NET runtime | Not required — self-contained builds bundle the runtime |
| Visual C++ Redistributable | May be required for native libpff adapter |
| Disk space | ~120 MB Desktop, ~70 MB CLI (approximate, self-contained) |

---

## Running the Desktop GUI

Double-click `MailVaultRecovery\MailVault.Desktop.exe`.

The GUI guides you through:
1. Creating or opening a recovery case
2. Selecting your OST/PST source file
3. Indexing messages into the local case database
4. Browsing, searching, and exporting emails

---

## Running the CLI

The CLI executable is `dist\MailVaultCli\mailvault.exe`. Add it to `PATH` or run it directly.

### Available commands

```
mailvault inspect <file.ost>               # Hash and inspect source file
mailvault tree <file.ost>                  # Show folder hierarchy
mailvault list <file.ost> --folder <id>    # List messages in a folder
mailvault preview <file.ost> --message-id <id>  # Preview a single message

mailvault index <file.ost> --out <cases-dir>    # Index all messages into case.db
mailvault stats <case-folder>                   # Show statistics for an indexed case
mailvault search <case-folder> --query <text>   # Search indexed messages

mailvault export <case-folder> --format eml --out <output-dir>   # Export to EML files
mailvault export <case-folder> --format mbox --out <output-dir>  # Export to MBOX

mailvault validate <case-folder> --export-folder <output-dir>    # Validate export integrity
```

### End-to-end example (OST to EML)

```powershell
# Step 1: Index the source file
mailvault index "C:\Evidence\mailbox.ost" --out "C:\Cases"

# Step 2: Check the case folder name printed in step 1, then export
mailvault export "C:\Cases\<case-id>" --format eml --out "C:\Cases\<case-id>\exports"

# Step 3: Validate the export
mailvault validate "C:\Cases\<case-id>" --export-folder "C:\Cases\<case-id>\exports"
```

Each command produces an `audit.log` and a `manifest.json` inside the case folder for forensic traceability.

---

## Supported source formats

| Extension | Format | Adapter |
|-----------|--------|---------|
| `.ost` | Microsoft Outlook OST | XstReader / libpff |
| `.pst` | Microsoft Outlook PST | XstReader / libpff |

---

## Notes

- The Desktop GUI uses Avalonia 11 and cannot be tested headlessly (no virtual display required on Windows).
- The libpff adapter ships as a native DLL alongside the binaries; it is included automatically by the publish script.
- For CI/CD, use `publish.ps1` with `-SelfContained:$false` to produce framework-dependent builds, which are smaller but require the .NET 10 runtime to be installed on the target machine.
