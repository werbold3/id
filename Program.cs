using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;
using LockheedMartin.Prepar3D.SimConnect;

namespace AxisClone
{
    // ---------- Model preset ----------
    [DataContract]
    public class AxisMap
    {
        [DataMember] public int Joy { get; set; }                 // 0-15
        [DataMember] public string Axis { get; set; } = "X";      // X,Y,Z,R,U,V
        [DataMember] public string SimEvent { get; set; } = "AXIS_ELEVATOR_SET";
        [DataMember] public bool Invert { get; set; }
        [DataMember] public int Deadzone { get; set; } = 2;       // persen
    }

    [DataContract]
    public class ButtonMap
    {
        [DataMember] public int Joy { get; set; }
        [DataMember] public int Button { get; set; } = 1;         // 1-32
        [DataMember] public string SimEvent { get; set; } = "GEAR_TOGGLE";
        [DataMember] public int Param { get; set; }
        [DataMember] public string Trigger { get; set; } = "Press"; // Press / Release
    }

    [DataContract]
    public class Preset
    {
        [DataMember] public string Name { get; set; } = "New Preset";
        [DataMember] public List<AxisMap> Axes { get; set; } = new List<AxisMap>();
        [DataMember] public List<ButtonMap> Buttons { get; set; } = new List<ButtonMap>();
    }

    // ---------- Joystick (winmm, tanpa library tambahan) ----------
    static class Joy
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct JOYINFOEX
        {
            public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos,
                        dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
        }
        [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref JOYINFOEX info);

        public static bool Read(int id, out JOYINFOEX j)
        {
            j = new JOYINFOEX();
            j.dwSize = (uint)Marshal.SizeOf(typeof(JOYINFOEX));
            j.dwFlags = 0xFF; // JOY_RETURNALL
            return joyGetPosEx(id, ref j) == 0;
        }
        public static uint GetAxis(JOYINFOEX j, string a)
        {
            switch (a)
            {
                case "X": return j.dwXpos; case "Y": return j.dwYpos;
                case "Z": return j.dwZpos; case "R": return j.dwRpos;
                case "U": return j.dwUpos; default: return j.dwVpos;
            }
        }
    }

    // ---------- Form utama ----------
    public class MainForm : Form
    {
        const int WM_USER_SIMCONNECT = 0x0402;
        enum EV { }
        enum GRP { G1 }

        SimConnect sc;
        readonly Dictionary<string, int> evIds = new Dictionary<string, int>();
        readonly Dictionary<AxisMap, int> lastAxis = new Dictionary<AxisMap, int>();
        readonly Dictionary<string, uint> lastButtons = new Dictionary<string, uint>();

        BindingList<AxisMap> axes = new BindingList<AxisMap>();
        BindingList<ButtonMap> buttons = new BindingList<ButtonMap>();
        DataGridView gridA, gridB;
        Label status;
        Timer poll;
        Preset preset = new Preset();

        public MainForm()
        {
            Text = "Axis Clone - P3D v4.5";
            Width = 900; Height = 600;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
            top.Controls.Add(MakeBtn("Connect P3D", (s, e) => Connect()));
            top.Controls.Add(MakeBtn("Disconnect", (s, e) => Disconnect()));
            top.Controls.Add(MakeBtn("Load Preset", (s, e) => LoadPreset()));
            top.Controls.Add(MakeBtn("Save Preset", (s, e) => SavePreset()));
            status = new Label { Text = "Disconnected", AutoSize = true, Padding = new Padding(10, 8, 0, 0) };
            top.Controls.Add(status);

            gridA = new DataGridView { Dock = DockStyle.Fill, DataSource = axes, AutoGenerateColumns = true };
            gridB = new DataGridView { Dock = DockStyle.Fill, DataSource = buttons, AutoGenerateColumns = true };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(gridA);
            split.Panel1.Controls.Add(new Label { Text = "AXES", Dock = DockStyle.Top, Height = 18 });
            split.Panel2.Controls.Add(gridB);
            split.Panel2.Controls.Add(new Label { Text = "BUTTONS", Dock = DockStyle.Top, Height = 18 });

            Controls.Add(split);
            Controls.Add(top);

            poll = new Timer { Interval = 20 };
            poll.Tick += (s, e) => Poll();
            poll.Start();
        }

        Button MakeBtn(string t, EventHandler h)
        {
            var b = new Button { Text = t, Width = 110, Height = 28 };
            b.Click += h; return b;
        }

        // ----- SimConnect -----
        void Connect()
        {
            try
            {
                sc = new SimConnect("AxisClone", Handle, WM_USER_SIMCONNECT, null, 0);
                sc.OnRecvOpen += (s, d) => status.Text = "Connected";
                sc.OnRecvQuit += (s, d) => Disconnect();
                sc.SetNotificationGroupPriority(GRP.G1, SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
                evIds.Clear();
                status.Text = "Connecting...";
            }
            catch (Exception ex) { status.Text = "Gagal: " + ex.Message; sc = null; }
        }

        void Disconnect()
        {
            try { sc?.Dispose(); } catch { }
            sc = null; evIds.Clear();
            status.Text = "Disconnected";
        }

        protected override void DefWndProc(ref Message m)
        {
            if (m.Msg == WM_USER_SIMCONNECT && sc != null)
            {
                try { sc.ReceiveMessage(); } catch { }
            }
            else base.DefWndProc(ref m);
        }

        void Send(string name, int data)
        {
            if (sc == null || string.IsNullOrWhiteSpace(name)) return;
            try
            {
                if (!evIds.TryGetValue(name, out int id))
                {
                    id = evIds.Count + 1;
                    sc.MapClientEventToSimEvent((EV)id, name);
                    sc.AddClientEventToNotificationGroup(GRP.G1, (EV)id, false);
                    evIds[name] = id;
                }
                sc.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, (EV)id,
                    unchecked((uint)data), GRP.G1,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
            catch (Exception ex) { status.Text = "Err: " + ex.Message; }
        }

        // ----- Polling joystick -----
        void Poll()
        {
            if (sc == null) return;

            foreach (var a in axes)
            {
                if (!Joy.Read(a.Joy, out var j)) continue;
                double v = (Joy.GetAxis(j, a.Axis) - 32767.5) / 32767.5; // -1..1
                if (a.Invert) v = -v;
                if (Math.Abs(v) * 100 < a.Deadzone) v = 0;
                int val = (int)Math.Round(v * 16383);
                if (!lastAxis.TryGetValue(a, out int old) || old != val)
                {
                    lastAxis[a] = val;
                    Send(a.SimEvent, val);
                }
            }

            foreach (var b in buttons)
            {
                if (!Joy.Read(b.Joy, out var j)) continue;
                string key = "J" + b.Joy;
                lastButtons.TryGetValue(key, out uint prev);
                uint mask = 1u << (b.Button - 1);
                bool now = (j.dwButtons & mask) != 0, was = (prev & mask) != 0;
                if (b.Trigger == "Press" && now && !was) Send(b.SimEvent, b.Param);
                if (b.Trigger == "Release" && !now && was) Send(b.SimEvent, b.Param);
            }
            // simpan state tombol (sekali per joystick)
            foreach (int id in buttons.Select(x => x.Joy).Distinct())
                if (Joy.Read(id, out var jj)) lastButtons["J" + id] = jj.dwButtons;
        }

        // ----- Preset -----
        void LoadPreset()
        {
            using (var d = new OpenFileDialog { Filter = "Preset (*.json)|*.json" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                using (var fs = File.OpenRead(d.FileName))
                    preset = (Preset)new DataContractJsonSerializer(typeof(Preset)).ReadObject(fs);
                axes.Clear(); buttons.Clear(); lastAxis.Clear();
                foreach (var a in preset.Axes) axes.Add(a);
                foreach (var b in preset.Buttons) buttons.Add(b);
                Text = "Axis Clone - " + preset.Name;
            }
        }

        void SavePreset()
        {
            using (var d = new SaveFileDialog { Filter = "Preset (*.json)|*.json" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                preset.Name = Path.GetFileNameWithoutExtension(d.FileName);
                preset.Axes = axes.ToList(); preset.Buttons = buttons.ToList();
                using (var fs = File.Create(d.FileName))
                    new DataContractJsonSerializer(typeof(Preset)).WriteObject(fs, preset);
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}
