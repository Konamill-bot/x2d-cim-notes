using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

// X2D Bitmask Probe — read-only investigation tool.
//
// Goal: collect concrete numeric evidence of what the X2D body self-reports
// as its supported focus modes. The existing x2d_afc_ipc_test.cs sends
// ipcCameraCapabilities but only prints the reply code, not the actual
// capability fields. This tool extracts the focusModeRange bitmask and
// decodes it bit by bit, and cross-checks against the .NET reflection path
// (CCameraToolController.GetSelectableFocusModes()).
//
// Investigation strategy:
//   1. Send several capability-related IPC commands (read-only queries)
//   2. Dump the raw plist response bytes to disk for offline review
//   3. Parse the plist and extract every <key>...</key><type>...</type> pair
//   4. Locate focusModeRange / canAutoFocus / canControlFocusMode / etc.
//   5. Decode focusModeRange bitmask: bit 0 = Manual, 1 = AFS, 2 = AFC, 3 = TrueFocus
//   6. Cross-check with reflection-based GetSelectableFocusModes()
//
// Performs NO writes, NO mode changes, NO firmware modification.

class X2D_BitmaskProbe
{
    const string PhocusPath = @"C:\Program Files\Hasselblad\Phocus 3.8.8";
    const string PipeName   = "Phocus-7DAF5ECD-9ADE-49f4-8B7C-59183189FD68";

    // IPC queries we want to probe. All are read-only.
    static readonly string[] ProbeCommands = new string[] {
        "ipcInitFromPreferences",       // session init
        "ipcIdleCameraConnected",       // is a camera attached?
        "ipcCameraDeviceInfo",          // device descriptor
        "ipcDeviceInfo",                // alternative device descriptor
        "ipcCameraCapabilities",        // ← primary target: capability bitmasks
        "ipcFocusMode",                 // current mode (for context)
        "ipcFocusModeList",             // notification name; may or may not respond
    };

    // Bit positions inside focusModeRange (from eFocusMode enum in Phocus.dll):
    //   kManualFocusMode        = 0  → bit 0 (value 1)
    //   kAutoSingleFocusMode    = 1  → bit 1 (value 2)
    //   kAutoContinousFocusMode = 2  → bit 2 (value 4)   ← AFC
    //   kTrueFocusMode          = 3  → bit 3 (value 8)
    static readonly string[] BitNames = new string[] {
        "Manual (kManualFocusMode)",
        "AFS    (kAutoSingleFocusMode)",
        "AFC    (kAutoContinousFocusMode)",
        "TrueFocus (kTrueFocusMode)"
    };

    // Capability fields we care about within the response. Names taken from
    // Phocus.dll SWIG bindings observed during earlier reflection work.
    static readonly string[] TargetFields = new string[] {
        "focusModeRange",
        "canAutoFocus",
        "canControlFocus",
        "canControlFocusMode",
        "bEnableFocusModeControl",
        "bControlFocusModes",
        "bEnableAutoFocusControl",
        "controlCapabilitiesvalid",
        "cameraLensRangevalid",
        "backType",
        "protocol",
    };

    static string DumpDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "x2d_bitmask_probe_dumps");

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("====================================================");
        Console.WriteLine("  X2D Bitmask Probe — read-only investigation tool");
        Console.WriteLine("====================================================\n");
        Console.WriteLine("Requires Phocus to be open and X2D connected via WiFi.\n");

        Directory.CreateDirectory(DumpDir);

        // ---- Phase A: IPC pipe path ----
        IpcProbeResults ipcResults = ProbeIpc();

        // ---- Phase B: .NET reflection path (cross-check) ----
        ReflectionResults reflResults = ProbeReflection();

        // ---- Summary ----
        Report(ipcResults, reflResults);

        Pause();
    }

    // ---------- IPC probe phase ----------

    class IpcProbeResults
    {
        public bool   pipeConnected     = false;
        public string pipeError         = null;
        public Dictionary<string, string> rawResponses = new Dictionary<string, string>();
        public Dictionary<string, KeyValuePair<string,string>> allKeyValues
            = new Dictionary<string, KeyValuePair<string,string>>();
        public int? focusModeRange      = null;
        public string focusModeRangeSource = null;
    }

    static IpcProbeResults ProbeIpc()
    {
        var r = new IpcProbeResults();
        NamedPipeClientStream pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            Console.Write("Connecting to Phocus IPC pipe...");
            pipe.Connect(5000);
            r.pipeConnected = true;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" connected!");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            r.pipeError = ex.Message;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[ERROR] " + ex.Message);
            Console.WriteLine("Make sure Phocus is open. Continuing to reflection path...\n");
            Console.ResetColor();
            return r;
        }

        using (pipe)
        {
            foreach (string cmd in ProbeCommands)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(">>> " + cmd);
                Console.ResetColor();

                string raw = Send(pipe, Plist(cmd, null, null));
                r.rawResponses[cmd] = raw;

                int replyCode = ParseIPCReply(raw);
                Console.WriteLine("    IPCReply: " + replyCode + "  (size: " + raw.Length + " bytes)");

                // Save raw response to disk for offline inspection
                string outPath = Path.Combine(DumpDir,
                    "response_" + cmd + "_" + DateTime.Now.ToString("HHmmss") + ".xml");
                File.WriteAllText(outPath, raw, Encoding.UTF8);
                Console.WriteLine("    Saved raw response: " + outPath);

                // Parse plist key-value pairs
                var pairs = ParsePlist(raw);
                if (pairs.Count > 0)
                {
                    Console.WriteLine("    Parsed " + pairs.Count + " key-value pair(s):");
                    foreach (var kv in pairs)
                    {
                        string keyName = kv.Key;
                        string typeName = kv.Value.Key;
                        string valStr   = kv.Value.Value;
                        // Highlight target fields
                        bool isTarget = false;
                        foreach (string tf in TargetFields)
                            if (keyName.Equals(tf, StringComparison.OrdinalIgnoreCase)) { isTarget = true; break; }

                        if (isTarget)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("      [TARGET] " + keyName + " : " + typeName + " = " + valStr);
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.WriteLine("      " + keyName + " : " + typeName + " = " + Truncate(valStr, 60));
                        }

                        // Aggregate into the global key map (last writer wins per command run)
                        r.allKeyValues[keyName] = kv.Value;

                        // Capture focusModeRange specifically
                        if (keyName.Equals("focusModeRange", StringComparison.OrdinalIgnoreCase)
                            && typeName == "integer" && r.focusModeRange == null)
                        {
                            int n;
                            if (int.TryParse(valStr, out n))
                            {
                                r.focusModeRange = n;
                                r.focusModeRangeSource = "IPC " + cmd;
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("    (no parseable key-value pairs in response)");
                }
            }
        }
        return r;
    }

    // ---------- .NET reflection cross-check ----------

    class ReflectionResults
    {
        public bool    loaded                 = false;
        public string  error                  = null;
        public bool    controllerObtained     = false;
        public int?    getSelectableFocusModesValue = null;
        public uint?   getFocusModeValue      = null;
        public bool?   canControlFocusMode    = null;
        public bool?   canControlFocus        = null;
        public List<string> focusModeNameList = new List<string>();
        public string  currentFocusModeName   = null;
    }

    static ReflectionResults ProbeReflection()
    {
        Console.WriteLine("\n----------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Reflection path: directly query CCameraToolController");
        Console.ResetColor();

        var r = new ReflectionResults();
        try
        {
            // Set up assembly resolution from Phocus directory
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e) {
                string name = new AssemblyName(e.Name).Name;
                string path = Path.Combine(PhocusPath, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };

            Assembly asm = Assembly.LoadFrom(Path.Combine(PhocusPath, "Phocus.dll"));
            r.loaded = true;

            Type mainCtrlType = asm.GetType("Phocus.Native.CMainController");
            if (mainCtrlType == null) { r.error = "CMainController type not found"; return r; }

            object mainCtrl = Activator.CreateInstance(mainCtrlType);

            // Find the 4-string overload of InitGlobals
            MethodInfo initGlobals = null;
            foreach (var m in mainCtrlType.GetMethods())
            {
                if (m.Name != "InitGlobals") continue;
                var p = m.GetParameters();
                if (p.Length == 4 && p[0].ParameterType == typeof(string)) { initGlobals = m; break; }
            }
            if (initGlobals != null)
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Hasselblad", "Phocus");
                string langPath = Path.Combine(PhocusPath, "en");
                try { initGlobals.Invoke(mainCtrl, new object[] { appData, langPath, "BitmaskProbe", "BitmaskProbe" }); }
                catch { /* tolerated */ }
            }

            // Start IPC inside this process (attaches to running Phocus's namespace)
            var startIpc = mainCtrlType.GetMethod("StartIPC");
            if (startIpc != null) { try { startIpc.Invoke(mainCtrl, null); } catch { } }

            var getCtrl = mainCtrlType.GetMethod("GetCameraController");
            object cameraCtrl = getCtrl.Invoke(mainCtrl, null);
            if (cameraCtrl == null)
            {
                r.error = "GetCameraController returned null (camera not connected via Phocus?)";
                return r;
            }
            r.controllerObtained = true;

            Type ctrlType = cameraCtrl.GetType();

            // GetSelectableFocusModes() returns int bitmask
            var miSel = ctrlType.GetMethod("GetSelectableFocusModes");
            if (miSel != null)
                r.getSelectableFocusModesValue = (int)miSel.Invoke(cameraCtrl, null);

            var miMode = ctrlType.GetMethod("GetFocusMode");
            if (miMode != null)
                r.getFocusModeValue = (uint)miMode.Invoke(cameraCtrl, null);

            var miCanCtrlMode = ctrlType.GetMethod("CanControlFocusMode");
            if (miCanCtrlMode != null)
                r.canControlFocusMode = (bool)miCanCtrlMode.Invoke(cameraCtrl, null);

            var miCanCtrlFocus = ctrlType.GetMethod("CanControlFocus");
            if (miCanCtrlFocus != null)
                r.canControlFocus = (bool)miCanCtrlFocus.Invoke(cameraCtrl, null);

            var miCurName = ctrlType.GetMethod("GetFocusModeName");
            if (miCurName != null)
                r.currentFocusModeName = (string)miCurName.Invoke(cameraCtrl, null);

            // GetFocusModeNameList(StringVector) — needs a StringVector instance
            var stringVecType = asm.GetType("Phocus.Native.StringVector");
            var miNameList = ctrlType.GetMethod("GetFocusModeNameList");
            if (miNameList != null && stringVecType != null)
            {
                object vec = Activator.CreateInstance(stringVecType);
                miNameList.Invoke(cameraCtrl, new object[] { vec });
                // StringVector has Count and indexer
                var countProp = stringVecType.GetProperty("Count");
                if (countProp != null)
                {
                    int n = (int)countProp.GetValue(vec, null);
                    var getItem = stringVecType.GetMethod("get_Item", new Type[] { typeof(int) });
                    if (getItem != null)
                    {
                        for (int i = 0; i < n; i++)
                            r.focusModeNameList.Add((string)getItem.Invoke(vec, new object[] { i }));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            r.error = ex.Message + (ex.InnerException != null ? " | inner: " + ex.InnerException.Message : "");
        }
        return r;
    }

    // ---------- Report ----------

    static void Report(IpcProbeResults ipc, ReflectionResults refl)
    {
        Console.WriteLine("\n====================================================");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("                    SUMMARY");
        Console.ResetColor();
        Console.WriteLine("====================================================\n");

        // IPC path
        Console.WriteLine("IPC pipe path:");
        Console.WriteLine("  Pipe connected:  " + ipc.pipeConnected);
        if (ipc.pipeError != null) Console.WriteLine("  Pipe error:      " + ipc.pipeError);

        if (ipc.focusModeRange.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  focusModeRange:  " + ipc.focusModeRange.Value
                + "  (from " + ipc.focusModeRangeSource + ")");
            Console.ResetColor();
            DecodeBitmask(ipc.focusModeRange.Value);
        }
        else
        {
            Console.WriteLine("  focusModeRange:  not present in any IPC response");
            Console.WriteLine("  (Phocus may not expose this field through plist replies — checking reflection path)");
        }

        // Other target fields seen via IPC
        Console.WriteLine("\n  Other target fields observed in IPC responses:");
        bool anyOther = false;
        foreach (string field in TargetFields)
        {
            if (field == "focusModeRange") continue;
            foreach (var kv in ipc.allKeyValues)
            {
                if (kv.Key.Equals(field, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("    " + field + " = " + kv.Value.Value + "  (" + kv.Value.Key + ")");
                    anyOther = true;
                    break;
                }
            }
        }
        if (!anyOther) Console.WriteLine("    (none of the target capability fields appeared in IPC responses)");

        // Reflection path
        Console.WriteLine();
        Console.WriteLine("Reflection path (CCameraToolController):");
        Console.WriteLine("  Phocus.dll loaded:       " + refl.loaded);
        Console.WriteLine("  Controller obtained:     " + refl.controllerObtained);
        if (refl.error != null) Console.WriteLine("  Error:                   " + refl.error);

        if (refl.getSelectableFocusModesValue.HasValue)
        {
            int v = refl.getSelectableFocusModesValue.Value;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  GetSelectableFocusModes(): " + v + "  (0x" + v.ToString("X") + ")");
            Console.ResetColor();
            DecodeBitmask(v);
        }
        if (refl.getFocusModeValue.HasValue)
            Console.WriteLine("  GetFocusMode():          " + refl.getFocusModeValue.Value
                + "  (" + ModeName((int)refl.getFocusModeValue.Value) + ")");
        if (refl.currentFocusModeName != null)
            Console.WriteLine("  GetFocusModeName():      '" + refl.currentFocusModeName + "'");
        if (refl.canControlFocusMode.HasValue)
            Console.WriteLine("  CanControlFocusMode():   " + refl.canControlFocusMode.Value);
        if (refl.canControlFocus.HasValue)
            Console.WriteLine("  CanControlFocus():       " + refl.canControlFocus.Value);
        if (refl.focusModeNameList.Count > 0)
        {
            Console.WriteLine("  GetFocusModeNameList():  " + refl.focusModeNameList.Count + " entries");
            foreach (string s in refl.focusModeNameList)
                Console.WriteLine("    - '" + s + "'");
        }

        // Cross-check
        Console.WriteLine("\n----------------------------------------------------");
        if (ipc.focusModeRange.HasValue && refl.getSelectableFocusModesValue.HasValue)
        {
            if (ipc.focusModeRange.Value == refl.getSelectableFocusModesValue.Value)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("CROSS-CHECK: IPC and reflection agree on focusModeRange = "
                    + ipc.focusModeRange.Value);
                Console.WriteLine("This is concrete evidence that " + ipc.focusModeRange.Value);
                Console.WriteLine("is the bitmask Phocus believes the camera supports.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("CROSS-CHECK: IPC and reflection DISAGREE.");
                Console.WriteLine("  IPC:        " + ipc.focusModeRange.Value);
                Console.WriteLine("  Reflection: " + refl.getSelectableFocusModesValue.Value);
                Console.WriteLine("Phocus may be filtering values between the two layers.");
                Console.ResetColor();
            }
        }
        else if (refl.getSelectableFocusModesValue.HasValue)
        {
            Console.WriteLine("CROSS-CHECK: only the reflection path returned a bitmask.");
            Console.WriteLine("IPC plist responses do not appear to expose focusModeRange directly.");
        }
        else if (ipc.focusModeRange.HasValue)
        {
            Console.WriteLine("CROSS-CHECK: only the IPC path returned a bitmask.");
        }
        else
        {
            Console.WriteLine("CROSS-CHECK: no bitmask captured by either path.");
            Console.WriteLine("Possible causes: camera not connected, Phocus not running, or this");
            Console.WriteLine("Phocus version routes capabilities through a channel not probed here.");
        }

        Console.WriteLine("\nAll raw IPC responses saved under:");
        Console.WriteLine("  " + DumpDir);
        Console.WriteLine("(Inspect these manually to see exactly what Phocus returned.)");
    }

    static void DecodeBitmask(int v)
    {
        Console.WriteLine("    Binary: 0b" + Convert.ToString(v, 2).PadLeft(8, '0'));
        for (int b = 0; b < BitNames.Length; b++)
        {
            bool set = (v & (1 << b)) != 0;
            string mark = set ? "[X]" : "[ ]";
            ConsoleColor c = (b == 2)  // AFC bit gets special highlighting
                ? (set ? ConsoleColor.Green : ConsoleColor.Red)
                : ConsoleColor.Gray;
            Console.ForegroundColor = c;
            Console.WriteLine("    " + mark + " bit " + b + " (value " + (1<<b) + ")  " + BitNames[b]);
            Console.ResetColor();
        }
    }

    static string ModeName(int v)
    {
        if (v == 0)   return "Manual";
        if (v == 1)   return "AFS";
        if (v == 2)   return "AFC";
        if (v == 3)   return "TrueFocus";
        if (v == 255) return "Undefined";
        return "Unknown(" + v + ")";
    }

    // ---------- Helpers (shared with x2d_afc_ipc_test.cs style) ----------

    static string Plist(string command, string valueKey, string value)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<plist version=\"1.0\"><dict>");
        sb.Append("<key>IPCCommand</key><string>").Append(command).Append("</string>");
        sb.Append("<key>streamableVersion</key><integer>1</integer>");
        if (valueKey != null && value != null)
            sb.Append("<key>").Append(valueKey).Append("</key><string>").Append(value).Append("</string>");
        sb.Append("</dict></plist>");
        return sb.ToString();
    }

    static string Send(NamedPipeClientStream pipe, string plist)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plist);
            byte[] len  = BitConverter.GetBytes(data.Length);
            pipe.Write(len, 0, 4);
            pipe.Write(data, 0, data.Length);
            pipe.Flush();

            byte[] rlen = new byte[4];
            if (pipe.Read(rlen, 0, 4) < 4) return "";
            int rsize = BitConverter.ToInt32(rlen, 0);
            if (rsize <= 0 || rsize > 4 * 1024 * 1024) return "(bad len " + rsize + ")";
            byte[] rbuf = new byte[rsize];
            int got = 0;
            while (got < rsize) { int n = pipe.Read(rbuf, got, rsize-got); if (n==0) break; got += n; }
            return Encoding.UTF8.GetString(rbuf, 0, got);
        }
        catch (Exception ex) { return "(err: " + ex.Message + ")"; }
    }

    static int ParseIPCReply(string plist)
    {
        int i = plist.IndexOf("<key>IPCReply</key><integer>");
        if (i < 0) return -999999;
        int s = i + 28, e = plist.IndexOf("</integer>", s);
        if (e < 0) return -999999;
        int n;
        return int.TryParse(plist.Substring(s, e-s), out n) ? n : -999999;
    }

    // Returns list of (keyName, (typeName, value)) pairs in document order.
    // Handles integer, string, true, false. Nested dict/array keys are flattened by name.
    static List<KeyValuePair<string, KeyValuePair<string, string>>> ParsePlist(string plist)
    {
        var results = new List<KeyValuePair<string, KeyValuePair<string, string>>>();
        if (string.IsNullOrEmpty(plist)) return results;

        // Capture each <key>NAME</key> followed immediately by a typed value tag.
        // The IPC responses we have seen are flat dicts; we tolerate other forms.
        var re = new Regex(
            @"<key>(?<k>[^<]+)</key>\s*" +
            @"(?:" +
              @"<integer>(?<i>-?\d+)</integer>" +
              @"|<string>(?<s>[^<]*)</string>" +
              @"|<true\s*/>(?<t>)" +
              @"|<false\s*/>(?<f>)" +
              @"|<real>(?<r>-?[\d\.eE+-]+)</real>" +
              @"|<dict>" +
              @"|<array>" +
              @"|<data>(?<d>[^<]*)</data>" +
            @")",
            RegexOptions.Singleline);

        foreach (Match m in re.Matches(plist))
        {
            string key = m.Groups["k"].Value;
            string typeName;
            string val;
            if (m.Groups["i"].Success)      { typeName = "integer"; val = m.Groups["i"].Value; }
            else if (m.Groups["s"].Success) { typeName = "string";  val = m.Groups["s"].Value; }
            else if (m.Groups["t"].Success) { typeName = "bool";    val = "true"; }
            else if (m.Groups["f"].Success) { typeName = "bool";    val = "false"; }
            else if (m.Groups["r"].Success) { typeName = "real";    val = m.Groups["r"].Value; }
            else if (m.Groups["d"].Success) { typeName = "data";    val = "(base64, " + m.Groups["d"].Value.Length + " chars)"; }
            else                            { typeName = "container"; val = "(nested dict/array)"; }

            results.Add(new KeyValuePair<string, KeyValuePair<string, string>>(
                key, new KeyValuePair<string, string>(typeName, val)));
        }
        return results;
    }

    static string Truncate(string s, int max)
    {
        if (s == null) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    static void Pause() { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
}
