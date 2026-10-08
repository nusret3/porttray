// PortTray - lists serial (COM) ports from the system tray.
// Left or right click the tray icon -> menu of ports with device name and VID:PID.
// Click a port to copy "COMx" to the clipboard. Pops a notification when a port appears/disappears.
// Builds with the C# compiler that ships with Windows (.NET Framework 4.x) - see build.bat.
// Written for C# 5 (no string interpolation / ?. ) so the in-box csc.exe can compile it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "HAT_PortTray_SingleInstance", out created))
        {
            if (!created) return; // already running
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }
}

class PortInfo
{
    public string Com;      // "COM7"
    public int Num;         // 7
    public string Desc;     // "USB Serial Port"
    public string Mfr;      // "FTDI"
    public string VidPid;   // "0403:6001" or ""
    public string Serial;   // USB serial number if Windows exposes one, else ""
    public string DeviceId; // full PNP device id
}

static class PortScanner
{
    // GUID of the "Ports (COM & LPT)" device class
    const string PortsClass = "{4d36e978-e325-11ce-bfc1-08002be10318}";
    static readonly Regex ComRx = new Regex(@"\((COM\d+)\)", RegexOptions.IgnoreCase);
    static readonly Regex VidPidRx = new Regex(@"VID_([0-9A-F]{4}).PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

    public static List<PortInfo> Scan()
    {
        var result = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, PNPDeviceID FROM Win32_PnPEntity WHERE ClassGuid='" + PortsClass + "'"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject mo in results)
                {
                    string name = (mo["Name"] as string) ?? "";
                    var m = ComRx.Match(name);
                    if (!m.Success) continue; // LPT etc.

                    var p = new PortInfo();
                    p.Com = m.Groups[1].Value.ToUpperInvariant();
                    p.Desc = name.Replace(m.Value, "").Trim();
                    p.Mfr = (mo["Manufacturer"] as string) ?? "";
                    p.DeviceId = (mo["PNPDeviceID"] as string) ?? "";
                    ParseIds(p);
                    result[p.Com] = p;
                    mo.Dispose();
                }
            }
        }
        catch { /* WMI hiccup - fall back to plain names below */ }

        // Ports that exist but aren't in the Ports class (com0com, some BT/virtual drivers)
        try
        {
            foreach (string name in SerialPort.GetPortNames())
            {
                string com = name.Trim().TrimEnd('\0').ToUpperInvariant();
                if (com.Length == 0 || result.ContainsKey(com)) continue;
                var p = new PortInfo();
                p.Com = com; p.Desc = "(virtual / no device info)"; p.Mfr = "";
                p.VidPid = ""; p.Serial = ""; p.DeviceId = "";
                result[com] = p;
            }
        }
        catch { }

        foreach (var p in result.Values)
        {
            int n; p.Num = int.TryParse(p.Com.Substring(3), out n) ? n : 9999;
        }
        return result.Values.OrderBy(p => p.Num).ToList();
    }

    static void ParseIds(PortInfo p)
    {
        p.VidPid = ""; p.Serial = "";
        var m = VidPidRx.Match(p.DeviceId);
        if (m.Success) p.VidPid = m.Groups[1].Value.ToUpperInvariant() + ":" + m.Groups[2].Value.ToUpperInvariant();

        string id = p.DeviceId;
        if (id.StartsWith("FTDIBUS\\", StringComparison.OrdinalIgnoreCase))
        {
            // FTDIBUS\VID_0403+PID_6001+A50285BIA\0000
            var parts = id.Split('\\');
            if (parts.Length > 1)
            {
                var plus = parts[1].Split('+');
                // FTDI appends the channel letter (A/B/C/D) to the chip serial
                if (plus.Length > 2 && plus[2].Length > 1) p.Serial = plus[2].Substring(0, plus[2].Length - 1);
            }
        }
        else if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
        {
            // USB\VID_xxxx&PID_xxxx\SERIAL  - Windows-made instance ids contain '&', real serials don't
            var parts = id.Split('\\');
            if (parts.Length > 2 && parts[2].IndexOf('&') < 0) p.Serial = parts[2];
        }
    }
}

// Hidden top-level window: receives WM_DEVICECHANGE broadcasts for COM ports without registration
class DeviceWatcher : NativeWindow, IDisposable
{
    const int WM_DEVICECHANGE = 0x0219;
    const int DBT_DEVICEARRIVAL = 0x8000;
    const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    const int DBT_DEVNODES_CHANGED = 0x0007;

    public event EventHandler Changed;

    public DeviceWatcher() { CreateHandle(new CreateParams()); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
        {
            int ev = m.WParam.ToInt32();
            if (ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE || ev == DBT_DEVNODES_CHANGED)
            {
                var h = Changed;
                if (h != null) h(this, EventArgs.Empty);
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose() { DestroyHandle(); }
}

class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "PortTray";

    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu;
    readonly DeviceWatcher watcher;
    readonly System.Windows.Forms.Timer debounce;
    Dictionary<string, PortInfo> known = new Dictionary<string, PortInfo>();
    List<PortInfo> current = new List<PortInfo>();
    bool notifications = true;

    public TrayContext()
    {
        menu = new ContextMenuStrip();
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.Opening += (s, e) => { Refresh(false); BuildMenu(); e.Cancel = false; };

        tray = new NotifyIcon();
        tray.Icon = MakeIcon();
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // NotifyIcon only opens its menu on right click; reuse the same private method for left click
                var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(tray, null);
            }
        };

        debounce = new System.Windows.Forms.Timer();
        debounce.Interval = 800; // let the driver finish enumerating before we look
        debounce.Tick += (s, e) => { debounce.Stop(); Refresh(true); };

        watcher = new DeviceWatcher();
        watcher.Changed += (s, e) => { debounce.Stop(); debounce.Start(); };

        Refresh(false);
    }

    void Refresh(bool notify)
    {
        current = PortScanner.Scan();
        var now = current.ToDictionary(p => p.Com, StringComparer.OrdinalIgnoreCase);

        if (notify && notifications)
        {
            var added = current.Where(p => !known.ContainsKey(p.Com)).ToList();
            var removed = known.Values.Where(p => !now.ContainsKey(p.Com)).OrderBy(p => p.Num).ToList();

            if (added.Count > 0 || removed.Count > 0)
            {
                string title;
                var lines = new List<string>();
                foreach (var p in added) lines.Add("+ " + p.Com + "  " + Label(p));
                foreach (var p in removed) lines.Add("- " + p.Com + "  " + Label(p));

                if (added.Count == 1 && removed.Count == 0) title = added[0].Com + " connected";
                else if (removed.Count == 1 && added.Count == 0) title = removed[0].Com + " removed";
                else title = "Serial ports changed";

                tray.ShowBalloonTip(4000, title, string.Join("\n", lines),
                    added.Count > 0 ? ToolTipIcon.Info : ToolTipIcon.None);
            }
        }

        known = now;
        string tip = current.Count == 0 ? "No serial ports"
                   : string.Join(", ", current.Select(p => p.Com));
        tray.Text = tip.Length > 63 ? tip.Substring(0, 60) + "..." : tip;
    }

    static string Label(PortInfo p)
    {
        string s = p.Desc;
        if (p.VidPid.Length > 0) s += "  [" + p.VidPid + "]";
        return s;
    }

    void BuildMenu()
    {
        menu.Items.Clear();
        if (current.Count == 0)
        {
            var none = new ToolStripMenuItem("No serial ports");
            none.Enabled = false;
            menu.Items.Add(none);
        }

        foreach (var p in current)
        {
            string text = p.Com.PadRight(7) + "\t" + Label(p);
            if (p.Serial.Length > 0) text += "  SN " + p.Serial;

            var item = new ToolStripMenuItem(text);
            item.ToolTipText = (p.Mfr.Length > 0 ? p.Mfr + "\n" : "") +
                               (p.DeviceId.Length > 0 ? p.DeviceId + "\n" : "") +
                               "Click to copy \"" + p.Com + "\"";
            string com = p.Com;
            item.Click += (s, e) =>
            {
                try { Clipboard.SetText(com); } catch { }
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        var notif = new ToolStripMenuItem("Notify on plug / unplug");
        notif.Checked = notifications;
        notif.Click += (s, e) => { notifications = !notifications; };
        menu.Items.Add(notif);

        var startup = new ToolStripMenuItem("Start with Windows");
        startup.Checked = IsStartupEnabled();
        startup.Click += (s, e) => SetStartup(!IsStartupEnabled());
        menu.Items.Add(startup);

        var devmgr = new ToolStripMenuItem("Open Device Manager");
        devmgr.Click += (s, e) =>
        {
            try { System.Diagnostics.Process.Start("devmgmt.msc"); } catch { }
        };
        menu.Items.Add(devmgr);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (s, e) => ExitThread();
        menu.Items.Add(exit);
    }

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
            return k != null && k.GetValue(AppName) != null;
    }

    static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k == null) return;
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(AppName, false);
        }
    }

    // DB9-style connector icon drawn in code, so there is no .ico file to ship
    static Icon MakeIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var shell = new[] { new PointF(1, 7), new PointF(31, 7), new PointF(26, 25), new PointF(6, 25) };
            using (var b = new SolidBrush(Color.FromArgb(0, 120, 215))) g.FillPolygon(b, shell);
            using (var pen = new Pen(Color.White, 1.5f)) g.DrawPolygon(pen, shell);
            using (var w = new SolidBrush(Color.White))
            {
                for (int i = 0; i < 5; i++) g.FillEllipse(w, 5.5f + i * 4.6f, 10.5f, 3.4f, 3.4f);
                for (int i = 0; i < 4; i++) g.FillEllipse(w, 8f + i * 4.6f, 17f, 3.4f, 3.4f);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        tray.Dispose();
        watcher.Dispose();
        debounce.Dispose();
        base.ExitThreadCore();
    }
}
