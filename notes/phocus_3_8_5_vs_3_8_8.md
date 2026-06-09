# Phocus 3.8.5 vs Phocus 3.8.8 — AF-C symbol presence across versions

Hasselblad released **Phocus 3.8.8** alongside / shortly after the X2D II 100C launch. That
version is the first to officially expose AF-C in the X2D II's tethered workflow. A natural
question is: **did Phocus's AF-C symbols appear in 3.8.8, or were they already present in
earlier versions?** If they were already present before the X2D II launched, the Phocus
codebase had architectural support for AF-C earlier than the X2D II launch and the binding
constraint on AF-C delivery is at minimum not "Phocus needs new declarations written from
scratch."

This note documents a direct binary comparison of **Phocus 3.8.5** (a pre-X2D II release,
verified by absence of any X2D II identifier strings) and **Phocus 3.8.8** (the current
X2D II-aware release).

It is important up front to be precise about what this kind of comparison can and cannot
show. See [Epistemic limits](#epistemic-limits) at the end.

## How the comparison was done

For each version of `Phocus.dll` and `PhocusApi64.dll`:

- Load `Phocus.dll` via .NET reflection (`Phocus.Native` namespace)
- Enumerate `Phocus.Native.eFocusMode` to dump the enum values defined in that build
- ASCII-string-grep `PhocusApi64.dll` for: AF-C mode codes, IPC command names,
  SWIG-bound camera-controller methods, capability-structure field names, firmware-module
  identifiers
- Compare what is present in 3.8.5 versus 3.8.8

Both binaries were installed and inspected on the same machine, with no firmware files,
licence keys, or copyrighted resources copied out of the installations.

**This is a binary-symbol analysis, not a behavioural test.** It tells us what names and
declarations are present in the shipped DLLs. It does not tell us what those declarations
do at runtime.

## Findings

### Phocus 3.8.5 does not know about X2D II

- The string `HASSLX30` (the X2D II's firmware module identifier, present in Phocus 3.8.8)
  is absent from `PhocusApi64.dll` in 3.8.5.
- `HASSLX29.CIM` (the X2D 100C firmware module identifier) is present in both.

This confirms Phocus 3.8.5 was built before X2D II support was wired in. Any AF-C symbols
observed in 3.8.5 therefore predate the X2D II launch.

### AF-C-related symbols are present in 3.8.5

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

Every element of the AF-C-related interface surface that exists in 3.8.8 is **also**
declared in 3.8.5: enum slot, wire-protocol string, IPC command name, SWIG binding,
capability-field accessor. The interface surface for AF-C in Phocus pre-dates the X2D II
launch.

### Phocus's eFocusMode enum has long reserved value 2 for AF-C

The four real mode values defined by `Phocus.Native.eFocusMode` are:

```
kManualFocusMode        = 0
kAutoSingleFocusMode    = 1
kAutoContinousFocusMode = 2     ← AF-C
kTrueFocusMode          = 3
```

`kTrueFocusMode = 3` is the dedicated Hasselblad TrueFocus mode used on the H-series
medium format bodies. `kAutoContinousFocusMode = 2` is distinct from TrueFocus and is the
generic continuous AF mode value.

The presence of enum value 2 in 3.8.5, without any X2D II identifier in that same build,
means Hasselblad's Phocus codebase has had this enum slot reserved at least since 3.8.5.

## Interpretation

### What this evidence shows

The AF-C *interface surface* in Phocus pre-dates the X2D II launch. The enum slot exists,
the IPC command name exists, the SWIG bindings to set/get focus mode exist, the wire
protocol code `AfC9` exists as a string in the shipped binary, and the capability-field
accessors exist on both `sCameraInterface` and `sControlCapabilities`.

### What this evidence does NOT show

**That the C++ implementation behind these declared interfaces is functional in 3.8.5.**
Symbol presence in a binary is consistent with multiple realities:

1. The code works end-to-end — when the camera reports AF-C as supported and the client
   issues `SetFocusMode(2)`, the camera actually changes mode.
2. The code is present and routed but gated, returning success without effect (a soft stub).
3. The code is a hard stub returning a status without doing anything.
4. The symbol exists because it is declared in a shared C++/SWIG header that is
   compiled into many Hasselblad camera SDK products. The Phocus binary contains the
   declaration but no Hasselblad camera body has ever wired the implementation to
   anything meaningful.
5. The code is dead — referenced from nothing, retained because nobody pruned it.

A binary-symbol scan cannot distinguish these. Distinguishing them requires connecting
a real camera to Phocus 3.8.5 and observing IPC behaviour, motor activity, and screen
state directly. That test is planned but had not been performed at the time of writing.

### What this evidence does NOT prove about the X2D 100C's silicon

This study makes no claim about whether the X2D 100C's image processor, sensor readout
pipeline, or AF firmware can deliver a Hasselblad-quality AF-C experience. The silicon
question is independent of the Phocus codebase question.

### The minimum claim the evidence supports

Enabling AF-C on the X2D 100C does not require Hasselblad to write the Phocus-side
declarations from scratch. The enum slot, the IPC command name, the SWIG bindings, and
the capability-field accessors are already present in the shipped Phocus codebase and
have been since at least 3.8.5. Whether anything behind those declarations is
functional is a separate question that this analysis cannot answer.

## What would falsify or strengthen this study

The straightforward test:

1. Start Phocus 3.8.5 with an X2D connected and tethered.
2. Send `ipcFocusMode` with `Value="2"` over the named pipe.
3. Observe the IPCReply code and the camera screen.

Possible outcomes and what they would mean:

- **IPCReply = 0** and camera screen does not change → 3.8.5 IPC accepts the command (the
  code path is at least routed to *something*); the camera body is what declines. This
  would match what was observed earlier in 3.8.8 and would suggest the Phocus-side AF-C
  code path is at least non-trivial.
- **IPCReply = `kIPCUnknownCommand` or similar error** → 3.8.5 has the strings in the
  binary but no IPC dispatcher entry. The AF-C symbols would then be evidence of
  declarations only, not of working code.
- **IPCReply = 0** and camera screen *changes* → the camera body in this firmware
  combination *does* honour AF-C requests, which would change the entire framing of
  this investigation. (Considered unlikely based on direct observation of 3.8.8
  behaviour, but the test is still worth running.)

## IPC test result (2026-06-10)

Test performed: USB-tethered X2D 100C (firmware 4.2.0) with Phocus 3.8.5
(installed at `E:\Hasselblad\Phocus 3.8.5`, build version 3.8.5.0).
The test client mirrors the existing `x2d_afc_ipc_test.cs` design, with the
constructor path repointed at the 3.8.5 install. The client opens the named
pipe `\\.\pipe\Phocus-7DAF5ECD-9ADE-49f4-8B7C-59183189FD68` and exchanges plist XML
messages.

Sequence executed and observed responses:

| Step | Command sent                  | IPCReply        | TextReply | Size  |
| ---- | ----------------------------- | --------------- | --------- | ----- |
| 1    | `ipcInitFromPreferences`      | `-2133196793`   | (empty)   | 223 B |
| 2    | `ipcFocusMode` (read)         | `-2133196794`   | (empty)   | 223 B |
| 3    | `ipcFocusMode` (Value=`"2"`)  | `-2133196793`   | (empty)   | 223 B |
| 4    | `ipcFocusMode` (read again)   | **`0`**         | **`单次`**| variable |
| 5    | `ipcFocusMode` (Value=`"1"`)  | `-2133196793`   | (empty)   | 223 B |

**Direct observation: the X2D camera screen showed no change at any point during the
test. The focus mode indicator remained at AFS / 單次 (single).**

What changes in our knowledge as a result:

1. **Step 4 supplies the strongest single piece of evidence in this note.**
   `ipcFocusMode` (read) on Phocus 3.8.5 returned `IPCReply = 0` with `TextReply = "单次"`
   — the Chinese-localised name for AFS / single AF mode. This is real data, not an
   error template. It demonstrates that Phocus 3.8.5's IPC dispatcher routes
   `ipcFocusMode` read requests to a functional handler that queries the actual camera
   focus-mode state and returns it as a localised string to the IPC client.

   This upgrades the earlier symbol-level claim. The reading path of `ipcFocusMode` in
   Phocus 3.8.5 is not a stub, not dead code, and not an unwired SDK declaration. It is
   a working handler.

2. **Step 3 (the AF-C set) was rejected at the IPC protocol layer** with
   `-2133196793`. This is the same status code returned by `ipcInitFromPreferences` and
   `ipcFocusMode` (Value=1, the restore), and matches the behaviour observed in 3.8.8
   from an external (non-Phocus-internal) client.

   The X2D camera screen did not change. This is consistent with the SET command being
   blocked at the IPC layer before reaching the camera relay path. It does not tell us
   whether the SET handler in Phocus 3.8.5, if reached by an authorised internal client,
   would in turn relay the command to the camera — that question remains open.

3. **Steps 1, 2, 3, 5 returning error codes alongside Step 4 returning real data** is
   the same alternating-error / occasional-success pattern previously observed against
   Phocus 3.8.8 by external clients. The IPC layer evidently has a session-warming
   pattern that admits some commands after others have run, but does not admit
   external-client SET requests under any sequence we have tested.

4. **The behavioural pattern observed against Phocus 3.8.5 is indistinguishable from
   the pattern observed against Phocus 3.8.8 to the external client.** No version-
   specific difference in IPC response was observed in this single run. This is
   consistent with the cross-version symbol comparison: both versions have the same
   AF-C interface surface, and from outside they exhibit the same IPC behaviour.

What remains unanswered (and unanswerable from an external client):

- Whether the SET handler behind `ipcFocusMode` (Value=2) in Phocus 3.8.5 would actually
  relay AF-C to the X2D body if invoked from an authorised client inside the Phocus
  process. The IPC protocol layer refuses the external SET before it reaches the
  handler, so this layer cannot be observed externally.
- Whether the camera body's `focusModeRange` bitmask would change in response to the
  command even if the handler did relay it. Direct observation says it did not, but
  that is consistent with both "Phocus did not relay" and "Phocus relayed and the
  camera declined."

The minimum claim of this note is now strengthened: enabling AF-C on the X2D 100C does
not require Hasselblad to write the Phocus-side declarations *or* to wire up at least
the READ-side dispatcher for AF-related IPC commands. Both already exist in 3.8.5. The
write-side dispatcher remains unverifiable from outside, and the camera's response
remains gated by `focusModeRange`.

## Epistemic limits

This is a string-and-reflection analysis of two specific Phocus releases. It is suitable
for answering questions about *what is present in the binary*. It is not suitable for
answering questions about *what those present things do at runtime*. Several of the
strongest possible interpretations of this evidence have been deliberately not made above,
because they go beyond what binary inspection can support.

Readers who want a stronger claim than "AF-C interface declarations exist in 3.8.5"
should wait for the IPC test described in the previous section to be run, or run it
themselves.

## Cross-check method, for future researchers

Any reader with `Phocus 3.8.5` and `Phocus 3.8.8` (or any other pair of versions) can
reproduce the symbol table above without privileged tooling:

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
