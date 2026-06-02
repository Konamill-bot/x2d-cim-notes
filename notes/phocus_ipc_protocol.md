# Phocus IPC Protocol — Notes

## Discovery

Phocus 3.8.8 (`Phocus64.exe`) creates a Windows named pipe on startup:

```
\\.\pipe\Phocus-7DAF5ECD-9ADE-49f4-8B7C-59183189FD68
```

The GUID `7DAF5ECD-9ADE-49f4-8B7C-59183189FD68` is hardcoded in `Phocus.dll`. It does not
change between installs.

The pipe accepts and returns **length-prefixed plist (Apple property list) XML messages**.

## Wire Format

```
+----------+---------------------------+
| len (4B) | XML plist payload (len B) |
+----------+---------------------------+
```

The 4-byte length is little-endian uint32.

## Request Format

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<plist version="1.0"><dict>
  <key>IPCCommand</key><string>COMMAND_NAME</string>
  <key>streamableVersion</key><integer>1</integer>
  <!-- Optional value parameter -->
  <key>Value</key><string>VALUE</string>
</dict></plist>
```

## Response Format

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<plist version="1.0"><dict>
  <key>IPCReply</key><integer>STATUS_CODE</integer>
  <key>TextReply</key><string>OPTIONAL_TEXT</string>
  <key>streamableVersion</key><integer>1</integer>
</dict></plist>
```

## Status Codes

| Code (hex)   | Meaning              |
| ------------ | -------------------- |
| `0x00000000` | Success              |
| `0x80D60003` | kIPCInvalidRequest   |
| `0x80D60006` | kIPCUnknownCommand   |
| `0x80D60007` | kIPCSessionNotOpen   |

## IPC Command Reference (subset)

Discovered from string extraction in `PhocusApi64.dll`:

### Camera state
- `ipcFocusMode` — get / set focus mode (0=Manual, 1=AFS, 2=AFC, 3=TrueFocus)
- `ipcAperture`, `ipcExposure`, `ipcISO`, `ipcWhiteBalance`, `ipcLightMeter`
- `ipcExposureMode`, `ipcCaptureType`
- `ipcAutoFocus` — trigger one-shot autofocus
- `ipcMirrorUp`, `ipcTiltSensor`

### Capture
- `ipcCapture` — trigger shutter
- `ipcCanCapture`, `ipcCanCaptureFromCamera`, `ipcCanDoThisCaptureType`
- `ipcCaptureDone`, `ipcCaptureInProgress`, `ipcCaptureProgress` (notifications)
- `ipcTakePictureFromCamera`

### Connection
- `ipcSession`, `ipcInitFromPreferences`, `ipcSynchronizeCamera`
- `ipcIdleCameraConnected`, `ipcEnableCameraControl`
- `ipcDeviceInfo`, `ipcCameraDeviceInfo`, `ipcCameraCapabilities`

### Firmware
- `ipcFirmwareUpdate` — start firmware update flow
- `ipcFirmwareUpdateDone` — completion notification

### Live view
- `ipcLiveVideo`, `ipcLiveVideoFrame`, `ipcLiveVideoGetSize`, `ipcLiveVideoSharpness`
- `ipcInstantPreview`, `ipcCachedPreview`, `ipcPreviewData`, `ipcPreviewSpec`

## Observed behavior — focus mode

```python
# 1. Init
send(plist("ipcInitFromPreferences"))   # returns 0 = OK

# 2. Set to AFC (value 2)
send(plist("ipcFocusMode", Value="2"))  # returns 0 = OK

# 3. Verify
reply = send(plist("ipcFocusMode"))      # returns 0, but TextReply is gibberish
# Subsequent GetFocusMode reverts to AFS — camera silently rejected the change
```

The eFocusMode enum on X2D firmware 4.2.0 **includes** `kAutoContinousFocusMode = 2` as a
defined value, and the IPC layer accepts it. The body's `focusModeRange` bitmask is what
prevents the mode from being exposed.

## Native API (.NET reflection alternative)

```csharp
// Phocus.Native.eFocusMode
enum eFocusMode : uint {
    kManualFocusMode        = 0,
    kAutoSingleFocusMode    = 1,
    kAutoContinousFocusMode = 2,  // note "Continous" typo in actual symbol
    kTrueFocusMode          = 3,
    kUndefinedFocusMode     = 255,
}

class CCameraToolController {
    uint GetFocusMode();
    void SetFocusMode(uint val);
    int  GetSelectableFocusModes();  // bitmask
    bool CanControlFocusMode();
}
```

Calling these from a sibling .NET process produces the same outcome: accepted by Phocus,
rejected by the camera.

## Practical use

Even though this cannot unlock AFC, the IPC layer is useful for:

- Scripted camera control (ISO, aperture, shutter, white balance, capture)
- Custom tethering tools without using Phocus UI
- Camera state monitoring
- Live view streaming

This is all legitimate, supported behavior. A small open-source tethering tool built on this
API would be a genuinely useful contribution unrelated to AFC unlock.
