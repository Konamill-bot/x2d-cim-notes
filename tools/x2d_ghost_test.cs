using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

// X2D Ghost Test — verifies who is actually answering on the Phocus IPC pipe.
//
// Background: Earlier tools observed that ipcFocusMode=2 returns IPCReply=0
// after doing Phocus.dll init + StartIPC in the same process. But sending
// the same command WITHOUT that init returns kIPCSessionNotOpen. The
// difference suggests we may have been talking to a "ghost" CMainController
// inside our own process (because Windows allows multiple named-pipe server
// instances with the same name) rather than the real Phocus64.exe.
//
// This tool does a definitive check using GetNamedPipeServerProcessId(),
// a Windows API that returns the PID of whoever is on the server side of
// our pipe connection.
//
// Two phases:
//   Phase A: Connect to pipe WITHOUT loading Phocus.dll
//            → server PID should be Phocus64.exe (the only legitimate server)
//   Phase B: Load Phocus.dll, instantiate CMainController, call StartIPC(),
//            then connect to pipe
//            → if server PID is our own process, ghost confirmed
//
// In each phase we also send a few capability queries and (in Phase B) try
// setting focus mode to AFC, then pause for the user to physically check
// whether the X2D camera screen actually changed.

class X2D_GhostTest
{
    const string PhocusPath = @"C:\Program Files\Hasselblad\Phocus 3.8.8";
    const string PipeName   = "Phocus-7DAF5ECD-9ADE-49f4-8B7C-59183189FD68";

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(IntPtr Pipe, out uint ServerProcessId);

    static int OurPid = Process.GetCurrentProcess().Id;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("==========================================================");
        Console.WriteLine("  X2D Ghost Test — who actually answers on the Phocus pipe?");
        Console.WriteLine("==========================================================\n");
        Console.WriteLine("Our process PID: " + OurPid);

        // List all Phocus64.exe processes (should be 1 if Phocus is running)
        var phocusProcs = Process.GetProcessesByName("Phocus64");
        foreach (var p in phocusProcs)
            Console.WriteLine("Phocus64.exe found: PID " + p.Id);
        if (phocusProcs.Length == 0)
            Console.WriteLine("[WARN] Phocus64.exe is not running. Phase A will likely fail to connect at all.");

        Console.WriteLine();

        // -------- PHASE A: pure pipe, no Phocus.dll init --------
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("PHASE A: connect to pipe WITHOUT loading Phocus.dll");
        Console.WriteLine("==========================================================");
        Console.ResetColor();
        uint phaseAServerPid = 0;
        string phaseASummary = RunPipePhase("A", null, out phaseAServerPid, false);

        // -------- PHASE B: init Phocus.dll + StartIPC, then pipe --------
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("PHASE B: load Phocus.dll + StartIPC, then connect to pipe");
        Console.WriteLine("==========================================================");
        Console.ResetColor();

        object mainCtrl = null;
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e) {
                string name = new AssemblyName(e.Name).Name;
                string path = Path.Combine(PhocusPath, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            Assembly asm = Assembly.LoadFrom(Path.Combine(PhocusPath, "Phocus.dll"));
            Console.WriteLine("[B-init] Loaded Phocus.dll");

            Type mainCtrlType = asm.GetType("Phocus.Native.CMainController");
            mainCtrl = Activator.CreateInstance(mainCtrlType);
            Console.WriteLine("[B-init] Created CMainController instance");

            // Find 4-string InitGlobals overload
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
                try
                {
                    int r = (int)initGlobals.Invoke(mainCtrl, new object[] {
                        appData, langPath, "GhostTest", "GhostTest" });
                    Console.WriteLine("[B-init] InitGlobals result = " + r);
                }
                catch (Exception ex) { Console.WriteLine("[B-init] InitGlobals threw: " + ex.Message); }
            }

            var startIpc = mainCtrlType.GetMethod("StartIPC");
            if (startIpc != null)
            {
                try
                {
                    int r = (int)startIpc.Invoke(mainCtrl, null);
                    Console.WriteLine("[B-init] StartIPC result = " + r);
                }
                catch (Exception ex) { Console.WriteLine("[B-init] StartIPC threw: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[B-init] Phocus.dll setup failed: " + ex.Message);
        }

        uint phaseBServerPid = 0;
        string phaseBSummary = RunPipePhase("B", mainCtrl, out phaseBServerPid, true);

        // -------- VERDICT --------
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("==========================================================");
        Console.WriteLine("                       VERDICT");
        Console.WriteLine("==========================================================");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Our PID:              " + OurPid);
        if (phocusProcs.Length > 0)
            Console.WriteLine("Phocus64.exe PID:     " + phocusProcs[0].Id);
        Console.WriteLine("Phase A server PID:   " + (phaseAServerPid == 0 ? "(connection failed or PID unavailable)" : phaseAServerPid.ToString()));
        Console.WriteLine("Phase B server PID:   " + (phaseBServerPid == 0 ? "(connection failed or PID unavailable)" : phaseBServerPid.ToString()));
        Console.WriteLine();

        bool phaseAIsGhost = (phaseAServerPid == (uint)OurPid);
        bool phaseBIsGhost = (phaseBServerPid == (uint)OurPid);
        bool phaseAIsReal  = phocusProcs.Length > 0 && phaseAServerPid == (uint)phocusProcs[0].Id;
        bool phaseBIsReal  = phocusProcs.Length > 0 && phaseBServerPid == (uint)phocusProcs[0].Id;

        if (phaseBIsGhost && !phaseAIsGhost)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(">>> GHOST CONFIRMED <<<");
            Console.WriteLine();
            Console.WriteLine("Phase B's StartIPC() created a new named-pipe SERVER instance");
            Console.WriteLine("inside this process. The pipe client then connected to our OWN");
            Console.WriteLine("server instance, not to Phocus64.exe. All 'successful' IPCReply=0");
            Console.WriteLine("results from this kind of setup were answered by our own ghost");
            Console.WriteLine("CMainController — not by the real Phocus. The real Phocus's pipe");
            Console.WriteLine("(seen in Phase A) consistently refuses external clients.");
            Console.WriteLine();
            Console.WriteLine("Implication: prior claims that Phocus 'accepts ipcFocusMode=2 with");
            Console.WriteLine("IPCReply=0' need to be retracted. We were talking to ourselves.");
            Console.ResetColor();
        }
        else if (phaseAIsReal && phaseBIsReal)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">>> NO GHOST — both phases reached real Phocus64.exe <<<");
            Console.WriteLine();
            Console.WriteLine("Phase A and Phase B both connected to the real Phocus server pipe.");
            Console.WriteLine("The difference in IPCReply behavior between them is therefore due to");
            Console.WriteLine("the StartIPC() call performing some authentication / session setup");
            Console.WriteLine("with the real Phocus, not because we were talking to ourselves.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(">>> INCONCLUSIVE <<<");
            Console.WriteLine();
            Console.WriteLine("Could not get a clean server-PID comparison. See raw output above.");
            Console.WriteLine("Possible reasons: pipe didn't accept GetNamedPipeServerProcessId(),");
            Console.WriteLine("Phocus not running, or unusual pipe handling. Manual inspection of");
            Console.WriteLine("the saved XML responses may help.");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("Phase A summary: " + phaseASummary);
        Console.WriteLine("Phase B summary: " + phaseBSummary);

        Pause();
    }

    static string RunPipePhase(string phaseLabel, object mainCtrl, out uint serverPid, bool tryFocusModeChange)
    {
        serverPid = 0;
        NamedPipeClientStream pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            Console.Write("[Phase " + phaseLabel + "] Connecting to pipe...");
            pipe.Connect(5000);
            Console.WriteLine(" connected");
        }
        catch (Exception ex)
        {
            Console.WriteLine(" FAILED: " + ex.Message);
            return "pipe connect failed";
        }

        using (pipe)
        {
            // === The critical PID check ===
            uint pid;
            bool gotPid = false;
            try
            {
                gotPid = GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid);
                if (gotPid)
                {
                    serverPid = pid;
                    string who;
                    if (pid == (uint)OurPid) who = "OUR OWN PROCESS (ghost!)";
                    else
                    {
                        try
                        {
                            Process p = Process.GetProcessById((int)pid);
                            who = p.ProcessName + ".exe (PID " + pid + ")";
                        }
                        catch { who = "PID " + pid + " (process info unavailable)"; }
                    }
                    Console.WriteLine("[Phase " + phaseLabel + "] Pipe server identified as: " + who);
                }
                else
                {
                    Console.WriteLine("[Phase " + phaseLabel + "] GetNamedPipeServerProcessId failed (Win32 err " + Marshal.GetLastWin32Error() + ")");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Phase " + phaseLabel + "] Server PID query exception: " + ex.Message);
            }

            // === Simple capability probes ===
            string[] cmds = { "ipcInitFromPreferences", "ipcFocusMode", "ipcCameraCapabilities" };
            foreach (string cmd in cmds)
            {
                string r = Send(pipe, Plist(cmd, null, null));
                int code = ParseIPCReply(r);
                Console.WriteLine("[Phase " + phaseLabel + "] " + cmd + " → IPCReply=" + code
                    + " (size " + r.Length + " bytes)");
            }

            // === Phase B only: attempt mode change + manual verification ===
            if (tryFocusModeChange)
            {
                Console.WriteLine();
                Console.WriteLine("[Phase " + phaseLabel + "] Attempting to SET ipcFocusMode = 2 (AFC)...");
                string setR = Send(pipe, Plist("ipcFocusMode", "Value", "2"));
                int setCode = ParseIPCReply(setR);
                Console.WriteLine("[Phase " + phaseLabel + "] SetFocusMode(2) → IPCReply=" + setCode);

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("===============================================================");
                Console.WriteLine("  STOP — physically check your X2D camera screen RIGHT NOW.");
                Console.WriteLine();
                Console.WriteLine("  Did the focus mode indicator on the camera change?");
                Console.WriteLine("    - AFS / 單次 = unchanged (command did not reach camera)");
                Console.WriteLine("    - AFC / 連續 = command actually reached and was accepted!");
                Console.WriteLine();
                Console.WriteLine("  Also check Phocus right-side panel:");
                Console.WriteLine("    - 對焦模式: 單次  = unchanged");
                Console.WriteLine("    - 對焦模式: 連續 = changed in Phocus's view");
                Console.WriteLine("===============================================================");
                Console.ResetColor();
                Console.Write("Press Enter once you have looked at the camera screen...");
                Console.ReadLine();

                // Read back what we now see
                string verR = Send(pipe, Plist("ipcFocusMode", null, null));
                int verCode = ParseIPCReply(verR);
                string verText = ExtractStringValue(verR, "TextReply");
                Console.WriteLine("[Phase " + phaseLabel + "] Read-back ipcFocusMode → IPCReply="
                    + verCode + " TextReply='" + verText + "'");

                // Try to restore AFS for safety (Value="1")
                Console.WriteLine("[Phase " + phaseLabel + "] Restoring to AFS for safety...");
                Send(pipe, Plist("ipcFocusMode", "Value", "1"));
            }
        }

        return "ok";
    }

    // ---------- Helpers (same style as other repo tools) ----------

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
        const int READ_TIMEOUT_MS = 5000;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plist);
            byte[] len  = BitConverter.GetBytes(data.Length);
            pipe.Write(len, 0, 4);
            pipe.Write(data, 0, data.Length);
            pipe.Flush();

            // Read with timeout — async pattern because NamedPipeClientStream
            // has no built-in synchronous read timeout on this framework.
            byte[] rlen = new byte[4];
            var ar = pipe.BeginRead(rlen, 0, 4, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(READ_TIMEOUT_MS))
            {
                return "(read timeout — Phocus did not respond within " + READ_TIMEOUT_MS + "ms)";
            }
            int got4 = pipe.EndRead(ar);
            if (got4 < 4) return "(short header read, got " + got4 + " bytes)";
            int rsize = BitConverter.ToInt32(rlen, 0);
            if (rsize <= 0 || rsize > 1024 * 1024) return "(bad len " + rsize + ")";
            byte[] rbuf = new byte[rsize];
            int got = 0;
            while (got < rsize)
            {
                var ar2 = pipe.BeginRead(rbuf, got, rsize - got, null, null);
                if (!ar2.AsyncWaitHandle.WaitOne(READ_TIMEOUT_MS))
                    return "(read timeout mid-body, got " + got + "/" + rsize + " bytes)";
                int n = pipe.EndRead(ar2);
                if (n == 0) break;
                got += n;
            }
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

    static string ExtractStringValue(string plist, string key)
    {
        string tag = "<key>" + key + "</key><string>";
        int i = plist.IndexOf(tag);
        if (i < 0) return "";
        int s = i + tag.Length, e = plist.IndexOf("</string>", s);
        return (e < 0) ? "" : plist.Substring(s, e-s);
    }

    static void Pause() { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
}
