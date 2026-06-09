# Phocus 3.8.5 vs Phocus 3.8.8 — AF-C infrastructure was already in place

Hasselblad released **Phocus 3.8.8** alongside / shortly after the X2D II 100C launch. That
version is the first to officially expose AF-C in the X2D II's tethered workflow. A natural
question is: **did Phocus's AF-C code path appear in 3.8.8, or was it already there in earlier
versions?** If it was already there before the X2D II launched, then the PC-side software
infrastructure for AF-C has been ready for some time and the binding constraint on AF-C
delivery is not at the PC layer.

This note documents a direct binary comparison of **Phocus 3.8.5** (a pre-X2D II release,
verified by absence of any X2D II identifier strings) and **Phocus 3.8.8** (the current X2D II-
aware release).

## How the comparison was done

For each version of `Phocus.dll` and `PhocusApi64.dll`:

- Load `Phocus.dll` via .NET reflection (`Phocus.Native` namespace)
- Enumerate `Phocus.Native.eFocusMode` to dump the enum values defined
- ASCII-string-grep `PhocusApi64.dll` for: AF-C mode codes, IPC command names,
  SWIG-bound camera-controller methods, capability-structure field names, firmware-module
  identifiers
- Compare what's present in 3.8.5 versus 3.8.8

Both binaries were installed and inspected on the same machine, with no firmware files,
licence keys, or copyrighted resources copied out of the installations.

## Findings

### Phocus 3.8.5 does not know about X2D II

- The string `HASSLX30` (the X2D II's firmware module identifier, present in Phocus 3.8.8)
  is absent from `PhocusApi64.dll` in 3.8.5.
- `HASSLX29.CIM` (the X2D 100C firmware module identifier) is present in both.

This confirms Phocus 3.8.5 was built before X2D II support was wired in. Any AF-C
infrastructure observed in 3.8.5 must therefore have existed for reasons unrelated to
the X2D II launch.

### AF-C code path is complete in 3.8.5

| Element                                                | 3.8.5 | 3.8.8 |
| ------------------------------------------------------ | ----- | ----- |
| `Phocus.Native.eFocusMode.kAutoContinousFocusMode = 2` | ✅     | ✅     |
| ASCII string `AfC9` (AF-C wire-protocol code)          | ✅     | ✅     |
| ASCII string `AfF9` (AF-F wire-protocol code)          | ✅     | ✅     |
| IPC command name `ipcFocusMode`                        | ✅     | ✅     |
| IPC notification name `ipcFocusModeList`               | ✅     | ✅     |
| `GetSelectableFocusModes()` SWIG binding               | ✅     | ✅     |
| `SetFocusMode()` / `SetFocusModeName()` SWIG bindings  | ✅     | ✅     |
| `focusModeRange` getter/setter on `sCameraInterface`   | ✅     | ✅     |
| `focusModeRange` getter/setter on `sControlCapabilities`| ✅    | ✅     |
| `bEnableFocusModeControl` / `bControlFocusModes` flags | ✅     | ✅     |
| `EnableFocusModeControl` / `ControlFocusModes`         | ✅     | ✅     |
| `ClientSupportedFocusMode`                             | ✅     | ✅     |

In other words: every part of the AF-C plumbing that we have previously documented as
existing in 3.8.8 is **also** present in 3.8.5. The pre-X2D-II version of Phocus already
had the complete infrastructure to: define an AF-C mode value, send the corresponding wire
code (`AfC9`), receive an AF-C mode in the supported-modes list, query the camera's
focus-mode-range capability bitmask, and write the mode back to the camera.

### Phocus's eFocusMode enum has always reserved value 2 for AF-C

The four real mode values defined by `Phocus.Native.eFocusMode` are:

```
kManualFocusMode        = 0
kAutoSingleFocusMode    = 1
kAutoContinousFocusMode = 2     ← AF-C
kTrueFocusMode          = 3
```

`kTrueFocusMode = 3` is the dedicated Hasselblad TrueFocus mode used on the H-series
medium format bodies. `kAutoContinousFocusMode = 2` is distinct from TrueFocus and is the
generic continuous AF mode used by every modern mirrorless camera.

Phocus has thus reserved enum value 2 for ordinary continuous AF for **years** without
any Hasselblad camera body actually exposing it — until the X2D II in 2025.

## Interpretation

What this evidence supports:

- **The Phocus PC-side software has been ready to drive AF-C for some time.** The
  client / IPC / wire-protocol / SWIG-binding layers were all in place at least as early
  as version 3.8.5 of Phocus, before any Hasselblad camera exposed AF-C in its product UI.

- **No major Phocus update is required to use AF-C on the X2D 100C body.** From the
  PC software's perspective, all that needs to change is the camera body's
  `focusModeRange` bitmask returning a value that includes bit 2.

What this evidence does **not** prove:

- It does **not** prove the X2D 100C's silicon can run a usable AF-C implementation
  at acceptable refresh rates. The camera's image processor, sensor readout pipeline,
  and AF firmware all have to be capable, and we have no visibility into those.

- It does **not** prove Hasselblad's intent behind keeping AF-C disabled on the X2D 100C.
  The PC-side AF-C code may exist for a number of legitimate engineering reasons,
  including: long-term roadmap planning across multiple camera generations, code sharing
  with industrial / scientific Hasselblad imaging products, or simply that the same
  underlying camera SDK is reused across products with different capability bitmasks.

- It does **not** confirm or deny that an X2D 100C firmware update could enable a
  Hasselblad-quality AF-C experience. The architectural-vs-policy question that the
  README opens with remains an internal Hasselblad question.

What this evidence does **shift**:

It shifts weight away from the strongest version of the *silicon-cannot* interpretation —
the version that would assume Phocus's AF-C code was *added* alongside the X2D II as
new capability. That version is contradicted: the code was already present in 3.8.5, well
before X2D II existed. Whatever the binding constraint on AF-C delivery for the X2D 100C
turns out to be, the binding constraint is not "Phocus is not ready."

## Cross-check method, for future researchers

Any reader with `Phocus 3.8.5` and `Phocus 3.8.8` (or any other pair of versions) can
reproduce the table above without privileged tooling:

```powershell
# Enum dump
$asm = [System.Reflection.Assembly]::LoadFrom("$PhocusDir\Phocus.dll")
$enum = $asm.GetType("Phocus.Native.eFocusMode")
[Enum]::GetNames($enum) | ForEach-Object {
    "{0,-28} = {1}" -f $_, [int][Enum]::Parse($enum, $_)
}

# String presence check on PhocusApi64.dll
$bytes = [System.IO.File]::ReadAllBytes("$PhocusDir\PhocusApi64.dll")
$text  = [System.Text.Encoding]::ASCII.GetString($bytes)
foreach ($needle in 'AfC9','AfF9','HASSLX29','HASSLX30','ipcFocusMode','focusModeRange') {
    if ($text.IndexOf($needle) -ge 0) { "✅ $needle" } else { "❌ $needle" }
}
```

No firmware files or licensed resources are needed for the cross-check. The DLLs ship as
part of any standard Phocus install.
