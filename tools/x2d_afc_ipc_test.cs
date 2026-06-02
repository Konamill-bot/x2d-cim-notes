using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;

// X2D AFC Unlocker - Final Clean Version
// Working protocol: Phocus named pipe + plist XML + ipcFocusMode + Value="2"
// eFocusMode: 0=Manual, 1=AFS, 2=AFC, 3=TrueFocus

class X2D_AFC_Final
{
    const string PhocusPath = @"C:\Program Files\Hasselblad\Phocus 3.8.8";
    const string PipeName   = "Phocus-7DAF5ECD-9ADE-49f4-8B7C-59183189FD68";

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("======================================");
        Console.WriteLine("  Hasselblad X2D AFC Unlocker Final");
        Console.WriteLine("======================================\n");
        Console.WriteLine("Requires Phocus to be open and X2D connected via WiFi.\n");

        // Init Phocus API (needed for ipcInitFromPreferences)
        Assembly asm = null;
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e) {
                string name = new AssemblyName(e.Name).Name;
                string path = Path.Combine(PhocusPath, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            asm = Assembly.LoadFrom(Path.Combine(PhocusPath, "Phocus.dll"));
            Console.WriteLine("[OK] Phocus API loaded.");
        }
        catch (Exception ex) { Console.WriteLine("[WARN] Phocus load: " + ex.Message); }

        // Connect to Phocus named pipe
        Console.Write("Connecting to Phocus IPC pipe...");
        NamedPipeClientStream pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(5000);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" connected!");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[ERROR] " + ex.Message);
            Console.ResetColor();
            Console.WriteLine("Make sure Phocus is open and X2D is connected via WiFi.");
            Pause(); return;
        }

        using (pipe)
        {
            // Step 1: Init
            string initR = Send(pipe, Plist("ipcInitFromPreferences", null, null));
            Console.WriteLine("Init: " + Code(initR));

            // Step 2: Read current mode
            string curR = Send(pipe, Plist("ipcFocusMode", null, null));
            string curText = Extract(curR, "TextReply");
            Console.WriteLine("Current focus mode: '" + curText + "' (code=" + Code(curR) + ")");

            // Step 3: Read selectable modes
            string selR = Send(pipe, Plist("ipcCameraCapabilities", null, null));
            Console.WriteLine("Camera caps code: " + Code(selR));

            Console.WriteLine("\nSelect focus mode:");
            Console.WriteLine("  1. AFC - Auto Focus Continuous  <-- unlock this");
            Console.WriteLine("  2. AFS - Auto Focus Single");
            Console.WriteLine("  3. Manual Focus");
            Console.WriteLine("  4. TrueFocus (AFS+)");
            Console.WriteLine("  5. Exit\n");
            Console.Write("Choice: ");

            string choice = (Console.ReadLine() ?? "").Trim();
            string modeVal;
            if      (choice == "1") modeVal = "2";
            else if (choice == "2") modeVal = "1";
            else if (choice == "3") modeVal = "0";
            else if (choice == "4") modeVal = "3";
            else { Console.WriteLine("Exiting."); return; }

            // Step 4: Set focus mode (confirmed working: Value="2" for AFC)
            Console.WriteLine("\nSetting focus mode " + modeVal + "...");
            string setR = Send(pipe, Plist("ipcFocusMode", "Value", modeVal));
            string setCode = Code(setR);

            if (setCode == "0")
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[SUCCESS] Focus mode set!");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[WARN] Set reply: " + setCode);
                Console.ResetColor();
            }

            // Step 5: Verify
            string verR = Send(pipe, Plist("ipcFocusMode", null, null));
            string verText = Extract(verR, "TextReply");
            string verCode = Code(verR);
            Console.WriteLine("Verified mode: '" + verText + "' (code=" + verCode + ")");

            if (verCode == "0")
            {
                Console.ForegroundColor = ConsoleColor.Green;
                if (modeVal == "2")
                {
                    Console.WriteLine("\nAFC is now active on your X2D.");
                    Console.WriteLine("Half-press shutter to track continuous focus.");
                    Console.WriteLine("Check camera screen - focus mode icon should show AFC.");
                }
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine("\nNote: If camera shows AFC but code is non-zero,");
                Console.WriteLine("the mode may still be firmware-restricted.");
                Console.WriteLine("Check X2D screen for current focus mode indicator.");
            }
        }
        Pause();
    }

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
            if (rsize <= 0 || rsize > 524288) return "(bad len " + rsize + ")";
            byte[] rbuf = new byte[rsize];
            int got = 0;
            while (got < rsize) { int n = pipe.Read(rbuf, got, rsize-got); if (n==0) break; got+=n; }
            return Encoding.UTF8.GetString(rbuf, 0, got);
        }
        catch (Exception ex) { return "(err: " + ex.Message + ")"; }
    }

    static string Code(string plist)
    {
        int i = plist.IndexOf("<key>IPCReply</key><integer>");
        if (i < 0) return "?";
        int s = i + 28, e = plist.IndexOf("</integer>", s);
        return (e < 0) ? "?" : plist.Substring(s, e-s);
    }

    static string Extract(string plist, string key)
    {
        string tag = "<key>" + key + "</key><string>";
        int i = plist.IndexOf(tag);
        if (i < 0) return "";
        int s = i + tag.Length, e = plist.IndexOf("</string>", s);
        return (e < 0) ? "" : plist.Substring(s, e-s);
    }

    static void Pause() { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
}
