using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// X2D Port Scan — passive network reconnaissance of the camera's WiFi endpoint.
//
// Goal: when the PC is connected to the X2D's WiFi hotspot, identify which
// TCP ports the camera has open. These are the ports Phocus uses to communicate
// with the camera. Knowing them is the prerequisite for any future protocol
// analysis (Wireshark capture / alternative client research).
//
// Strategy:
//   1. Verify the X2D IP (default 192.168.2.1) is reachable.
//   2. Scan ports in batches:
//        a) "famous" service ports (80, 443, 554, 8080, etc.)
//        b) Hasselblad-relevant range (1500–9999) inferred from DLL strings
//        c) High dynamic range sample (40000–60000)
//   3. For each open port, attempt a polite identification probe
//      (HTTP HEAD, RTSP OPTIONS, raw banner read).
//   4. Save full results to disk.
//
// This tool does NOT modify, attack, or attempt to bypass anything. It is
// equivalent to running `nmap` against a device on your own network. The
// user owns the camera; the network is between the user's PC and their own
// device.

class X2D_PortScan
{
    const string CameraIP = "192.168.2.1";

    static string DumpDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "x2d_portscan_results");

    // Concurrency: how many simultaneous TCP connect attempts.
    const int Concurrency = 100;
    // Per-port connect timeout
    const int ConnectTimeoutMs = 400;
    // Identification probe read timeout
    const int ProbeReadTimeoutMs = 600;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("====================================================");
        Console.WriteLine("  X2D Port Scan — find open TCP ports on the camera");
        Console.WriteLine("====================================================\n");

        Directory.CreateDirectory(DumpDir);

        // ---- Network sanity ----
        Console.WriteLine("PC network interfaces (looking for 192.168.2.x):");
        bool onX2DNet = false;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = addr.Address.ToString();
                bool x2d = ip.StartsWith("192.168.2.");
                if (x2d) onX2DNet = true;
                Console.WriteLine("  " + ni.Name + ": " + ip + (x2d ? "  <-- X2D subnet" : ""));
            }
        }
        Console.WriteLine();

        if (!onX2DNet)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WARN] No 192.168.2.x interface found. You are probably NOT on the");
            Console.WriteLine("       X2D WiFi hotspot. Connect to the camera's 'Hasselblad-XXXX'");
            Console.WriteLine("       network, then run this tool again.");
            Console.ResetColor();
            Console.WriteLine();
            // Continue anyway — sometimes Windows reports a 192.168.x.x on a USB
            // network interface that is actually the camera.
        }

        // Ping
        Console.Write("Pinging " + CameraIP + "... ");
        try
        {
            var ping = new Ping();
            var reply = ping.Send(CameraIP, 2000);
            if (reply.Status == IPStatus.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("alive (RTT " + reply.RoundtripTime + "ms)");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("no reply (" + reply.Status + ")");
                Console.ResetColor();
                Console.WriteLine("Continuing anyway — some devices block ICMP but still serve TCP.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ping error: " + ex.Message);
        }

        // ---- Port list ----
        var ports = BuildPortList();
        Console.WriteLine("\nScanning " + ports.Count + " ports on " + CameraIP + " with concurrency " + Concurrency + "...\n");

        var openPorts = new List<int>();
        var openLock = new object();
        int done = 0;
        int total = ports.Count;
        DateTime start = DateTime.Now;

        var sem = new SemaphoreSlim(Concurrency);
        var tasks = new List<Task>();
        foreach (int port in ports)
        {
            sem.Wait();
            int p = port;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    if (IsPortOpen(CameraIP, p, ConnectTimeoutMs))
                    {
                        lock (openLock) openPorts.Add(p);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  OPEN: " + p);
                        Console.ResetColor();
                    }
                }
                finally
                {
                    sem.Release();
                    int d = Interlocked.Increment(ref done);
                    if (d % 500 == 0)
                    {
                        double pct = 100.0 * d / total;
                        TimeSpan el = DateTime.Now - start;
                        Console.WriteLine("    ... " + d + "/" + total + " (" + pct.ToString("F0") + "%, " + el.TotalSeconds.ToString("F0") + "s)");
                    }
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // ---- Report ----
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("====================================================");
        Console.WriteLine("                 SCAN COMPLETE");
        Console.WriteLine("====================================================");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Total ports scanned : " + total);
        Console.WriteLine("Open ports found    : " + openPorts.Count);
        Console.WriteLine("Elapsed             : " + (DateTime.Now - start).TotalSeconds.ToString("F1") + "s");
        Console.WriteLine();

        openPorts.Sort();
        if (openPorts.Count == 0)
        {
            Console.WriteLine("No open TCP ports detected. Possible reasons:");
            Console.WriteLine("  - You are not actually on the X2D WiFi network");
            Console.WriteLine("  - The camera firewall blocks all but a specific client");
            Console.WriteLine("  - Phocus must be running and authenticated first to open ports");
            Console.WriteLine("  - The camera uses UDP, not TCP, for its protocol");
        }
        else
        {
            Console.WriteLine("Identifying protocol on each open port:");
            string outPath = Path.Combine(DumpDir, "scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            using (var writer = new StreamWriter(outPath))
            {
                writer.WriteLine("X2D Port Scan results");
                writer.WriteLine("Camera IP: " + CameraIP);
                writer.WriteLine("Scan time: " + DateTime.Now);
                writer.WriteLine("Open ports: " + string.Join(", ", openPorts));
                writer.WriteLine();
                foreach (int p in openPorts)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("--- port " + p + " ---");
                    Console.ResetColor();
                    writer.WriteLine("=== port " + p + " ===");

                    string fingerprint = IdentifyService(CameraIP, p);
                    Console.WriteLine(fingerprint);
                    writer.WriteLine(fingerprint);
                    writer.WriteLine();
                }
            }
            Console.WriteLine("\nFull results saved to: " + outPath);
        }

        Pause();
    }

    static List<int> BuildPortList()
    {
        var s = new HashSet<int>();

        // Famous service ports
        int[] famous = {
            21, 22, 23, 25, 53, 80, 81, 88, 110, 111, 135, 139, 143, 161, 389, 443, 445,
            465, 514, 554, 587, 631, 636, 902, 990, 993, 995, 1080, 1194, 1433, 1521,
            1723, 1883, 1900, 2049, 2222, 2375, 2376, 3000, 3128, 3260, 3306, 3389,
            3690, 3784, 4000, 4040, 4369, 4505, 4506, 5000, 5001, 5060, 5222, 5269,
            5353, 5432, 5500, 5555, 5601, 5672, 5683, 5900, 5984, 6000, 6379, 6443,
            6553, 6660, 6667, 6697, 7000, 7070, 7100, 7474, 7547, 7777, 8000, 8001,
            8008, 8009, 8080, 8081, 8082, 8083, 8086, 8088, 8090, 8123, 8126, 8161,
            8181, 8200, 8222, 8333, 8388, 8443, 8554, 8765, 8800, 8888, 8983, 9000,
            9001, 9042, 9090, 9091, 9092, 9100, 9200, 9300, 9418, 9443, 9595, 9696,
            9999, 10000, 11211, 13720, 13724, 13782, 13783, 17500, 18080, 19999,
            20000, 20800, 27017, 27018, 32400, 49152, 50000, 50050, 50070, 51000,
            54321, 55555
        };
        foreach (int p in famous) s.Add(p);

        // Hasselblad-relevant range inferred from DLL string analysis (1500–9999)
        for (int p = 1500; p <= 9999; p++) s.Add(p);

        // Common dynamic / Unicast Open Network Computing range sample
        for (int p = 49152; p <= 50100; p++) s.Add(p);

        var list = new List<int>(s);
        list.Sort();
        return list;
    }

    static bool IsPortOpen(string host, int port, int timeoutMs)
    {
        var client = new TcpClient();
        try
        {
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
            if (!client.Connected) return false;
            client.EndConnect(ar);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    static string IdentifyService(string host, int port)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  port " + port + " open");

        // Probe 1: HTTP HEAD /
        string http = TrySend(host, port,
            "HEAD / HTTP/1.0\r\nHost: " + host + "\r\nUser-Agent: x2d-port-scan/1.0\r\n\r\n",
            ProbeReadTimeoutMs);
        if (!string.IsNullOrEmpty(http))
        {
            sb.AppendLine("  HTTP HEAD response:");
            sb.AppendLine(Indent(http, "    "));
            return sb.ToString();
        }

        // Probe 2: RTSP OPTIONS
        string rtsp = TrySend(host, port,
            "OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: x2d-port-scan/1.0\r\n\r\n",
            ProbeReadTimeoutMs);
        if (!string.IsNullOrEmpty(rtsp))
        {
            sb.AppendLine("  RTSP OPTIONS response:");
            sb.AppendLine(Indent(rtsp, "    "));
            return sb.ToString();
        }

        // Probe 3: raw read (banner-grabbing)
        string banner = TryRead(host, port, ProbeReadTimeoutMs);
        if (!string.IsNullOrEmpty(banner))
        {
            sb.AppendLine("  Raw banner (first read after connect):");
            sb.AppendLine(Indent(banner, "    "));
            return sb.ToString();
        }

        sb.AppendLine("  (no response to HTTP/RTSP/passive read — custom binary protocol?)");
        return sb.ToString();
    }

    static string TrySend(string host, int port, string payload, int readTimeoutMs)
    {
        var client = new TcpClient();
        try
        {
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) return null;
            if (!client.Connected) return null;
            client.EndConnect(ar);

            using (var ns = client.GetStream())
            {
                byte[] data = Encoding.ASCII.GetBytes(payload);
                ns.Write(data, 0, data.Length);
                ns.Flush();
                return ReadAvailable(ns, readTimeoutMs);
            }
        }
        catch { return null; }
        finally { try { client.Close(); } catch { } }
    }

    static string TryRead(string host, int port, int readTimeoutMs)
    {
        var client = new TcpClient();
        try
        {
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) return null;
            if (!client.Connected) return null;
            client.EndConnect(ar);

            using (var ns = client.GetStream())
            {
                return ReadAvailable(ns, readTimeoutMs);
            }
        }
        catch { return null; }
        finally { try { client.Close(); } catch { } }
    }

    static string ReadAvailable(NetworkStream ns, int timeoutMs)
    {
        var sb = new StringBuilder();
        var buf = new byte[2048];
        DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        ns.ReadTimeout = timeoutMs;
        try
        {
            while (DateTime.Now < deadline)
            {
                if (!ns.DataAvailable)
                {
                    Thread.Sleep(50);
                    if (!ns.DataAvailable) break;
                }
                int n = ns.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                // Both bytes (for binary) and ASCII view
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == 0x0D || b == 0x0A || (b >= 0x20 && b < 0x7F))
                        sb.Append((char)b);
                    else
                        sb.Append("\\x").Append(b.ToString("X2"));
                }
            }
        }
        catch { /* timeout, no more data */ }
        string s = sb.ToString();
        return s.Length > 800 ? s.Substring(0, 800) + "...(truncated)" : s;
    }

    static string Indent(string text, string prefix)
    {
        var sb = new StringBuilder();
        foreach (string line in text.Split('\n'))
            sb.AppendLine(prefix + line.TrimEnd('\r'));
        return sb.ToString().TrimEnd();
    }

    static void Pause()
    {
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}
