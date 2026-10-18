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
    // SimEvent / Target bisa berisi:
    //   NAMA_EVENT_P3D            -> kirim event (mis. GEAR_TOGGLE, AXIS_ELEVATOR_SET)
    //   L:namaLvar                -> set Local Variable (mis. L:SWITCH_BATT)
    //   NAMA SIMVAR,units         -> set SimVar yang bisa ditulis (mis. LIGHT NAV,bool)
    [DataContract]
    public class AxisMap
    {
        [DataMember] public int Joy { get; set; }
        [DataMember] public string Axis { get; set; } = "X";
        [DataMember] public string SimEvent { get; set; } = "AXIS_ELEVATOR_SET";
        [DataMember] public bool Invert { get; set; }
        [DataMember] public int Deadzone { get; set; } = 2;
        [DataMember] public double Min { get; set; } = 0;   // dipakai hanya untuk target L:/SimVar
        [DataMember] public double Max { get; set; } = 1;
    }

    [DataContract]
    public class ButtonMap
    {
        [DataMember] public int Joy { get; set; }
        [DataMember] public int Button { get; set; } = 1;
        // Event | LVarSet | LVarToggle | SimVarSet
        [DataMember] public string Action { get; set; } = "Event";
        [DataMember] public string SimEvent { get; set; } = "GEAR_TOGGLE";
        [DataMember] public double Value { get; set; }        // nilai saat Press (atau param event)
        [DataMember] public double ValueOff { get; set; }     // nilai saat Release (Both) / posisi OFF (Toggle)
        // Press | Release | Both
        [DataMember] public string Trigger { get; set; } = "Press";
    }

    [DataContract]
    public class Preset
    {
        [DataMember] public string Name { get; set; } = "New Preset";
        [DataMember] public List<AxisMap> Axes { get; set; } = new List<AxisMap>();
        [DataMember] public List<ButtonMap> Buttons { get; set; } = new List<ButtonMap>();
    }

    // ---------- Joystick (winmm) ----------
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
            j.dwFlags = 0xFF;
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
        enum DEF { }

        SimConnect sc;
        readonly Dictionary<string, int> evIds = new Dictionary<string, int>();
        readonly Dictionary<string, int> defIds = new Dictionary<string, int>();
        readonly Dictionary<AxisMap, double> lastAxis = new Dictionary<AxisMap, double>();
        readonly Dictionary<int, uint> lastButtons = new Dictionary<int, uint>();
        readonly Dictionary<ButtonMap, bool> toggleState = new Dictionary<ButtonMap, bool>();

        BindingList<AxisMap> axes = new BindingList<AxisMap>();
        BindingList<ButtonMap> buttons = new BindingList<ButtonMap>();
        DataGridView gridA, gridB;
        Label status;
        Timer poll;
        Preset preset = new Preset();

        public MainForm()
        {
            Text = "Axis Clone - P3D v4.5";
            Width = 1000; Height = 620;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
            top.Controls.Add(MakeBtn("Connect P3D", (s, e) => Connect()));
            top.Controls.Add(MakeBtn("Disconnect", (s, e) => Disconnect()));
            top.Controls.Add(MakeBtn("Load Preset", (s, e) => LoadPreset()));
            top.Controls.Add(MakeBtn("Save Preset", (s, e) => SavePreset()));
            status = new Label { Text = "Disconnected", AutoSize = true, Padding = new Padding(10, 8, 0, 0) };
            top.Controls.Add(status);

            gridA = new DataGridView { Dock = DockStyle.Fill, DataSource = axes, AutoGenerateColumns = true };
            gridB = new DataGridView { Dock = DockStyle.Fill, DataSource = buttons, AutoGenerateColumns = true };
            MakeCombo(gridA, "Axis", "X", "Y", "Z", "R", "U", "V");
            MakeCombo(gridB, "Action", "Event", "LVarSet", "LVarToggle", "SimVarSet");
            MakeCombo(gridB, "Trigger", "Press", "Release", "Both");

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(gridA);
            split.Panel1.Controls.Add(new Label { Text = "AXES  (target: EVENT / L:lvar / SIMVAR,units)", Dock = DockStyle.Top, Height = 18 });
            split.Panel2.Controls.Add(gridB);
            split.Panel2.Controls.Add(new Label { Text = "BUTTONS / SWITCHES  (Both = Press kirim Value, Release kirim ValueOff)", Dock = DockStyle.Top, Height = 18 });

            Controls.Add(split);
            Controls.Add(top);

            poll = new Timer { Interval = 20 };
            poll.Tick += (s, e) => Poll();
            poll.Start();
        }

        static void MakeCombo(DataGridView g, string prop, params string[] items)
        {
            if (!g.Columns.Contains(prop)) return;
            int idx = g.Columns[prop].Index;
            g.Columns.RemoveAt(idx);
            g.Columns.Insert(idx, new DataGridViewComboBoxColumn
            {
                DataPropertyName = prop, HeaderText = prop, Name = prop,
                DataSource = items, FlatStyle = FlatStyle.Flat
            });
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
                evIds.Clear(); defIds.Clear();
                status.Text = "Connecting...";
            }
            catch (Exception ex) { status.Text = "Gagal: " + ex.Message; sc = null; }
        }

        void Disconnect()
        {
            try { sc?.Dispose(); } catch { }
            sc = null; evIds.Clear(); defIds.Clear();
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

        void SendEvent(string name, int data)
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
            catch (Exception ex) { status.Text = "Err event: " + ex.Message; }
        }

        // Tulis nilai ke L:var atau SimVar lewat data definition
        void SetVar(string datum, string units, double value)
        {
            if (sc == null || string.IsNullOrWhiteSpace(datum)) return;
            try
            {
                string key = datum + "|" + units;
                if (!defIds.TryGetValue(key, out int id))
                {
                    id = defIds.Count + 1;
                    sc.AddToDataDefinition((DEF)id, datum, units,
                        SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
                    defIds[key] = id;
                }
                sc.SetDataOnSimObject((DEF)id, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT, value);
            }
            catch (Exception ex) { status.Text = "Err var: " + ex.Message; }
        }

        static bool IsVarTarget(string t)
        {
            return !string.IsNullOrWhiteSpace(t) &&
                   (t.StartsWith("L:", StringComparison.OrdinalIgnoreCase) || t.Contains(","));
        }

        void WriteTarget(string target, double value)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            target = target.Trim();
            if (target.StartsWith("L:", StringComparison.OrdinalIgnoreCase))
                SetVar("L:" + target.Substring(2).Trim(), "number", value);
            else if (target.Contains(","))
            {
                var p = target.Split(new[] { ',' }, 2);
                SetVar(p[0].Trim(), p[1].Trim(), value);
            }
            else
                SendEvent(target, (int)Math.Round(value));
        }

        // ----- Aksi tombol / switch -----
        void Fire(ButtonMap b, bool pressed)
        {
            switch (b.Action)
            {
                case "LVarToggle":
                    if (!pressed) return;
                    toggleState.TryGetValue(b, out bool on);
                    on = !on; toggleState[b] = on;
                    WriteTarget(b.SimEvent.StartsWith("L:", StringComparison.OrdinalIgnoreCase)
                        ? b.SimEvent : "L:" + b.SimEvent, on ? b.Value : b.ValueOff);
                    return;

                case "LVarSet":
                case "SimVarSet":
                case "Event":
                default:
                    double val;
                    if (pressed && (b.Trigger == "Press" || b.Trigger == "Both")) val = b.Value;
                    else if (!pressed && b.Trigger == "Release") val = b.Value;
                    else if (!pressed && b.Trigger == "Both") val = b.ValueOff;
                    else return;

                    if (b.Action == "LVarSet")
                        WriteTarget(b.SimEvent.StartsWith("L:", StringComparison.OrdinalIgnoreCase)
                            ? b.SimEvent : "L:" + b.SimEvent, val);
                    else if (b.Action == "Event")
                        SendEvent(b.SimEvent, (int)Math.Round(val));
                    else
                        WriteTarget(b.SimEvent, val);
                    return;
            }
        }

        // ----- Polling joystick -----
        void Poll()
        {
            if (sc == null) return;

            foreach (var a in axes.ToList())
            {
                if (!Joy.Read(a.Joy, out var j)) continue;
                double v = (Joy.GetAxis(j, a.Axis) - 32767.5) / 32767.5; // -1..1
                if (a.Invert) v = -v;
                if (Math.Abs(v) * 100 < a.Deadzone) v = 0;

                double outVal = IsVarTarget(a.SimEvent)
                    ? Math.Round(a.Min + (v + 1) / 2 * (a.Max - a.Min), 4)
                    : Math.Round(v * 16383);

                if (!lastAxis.TryGetValue(a, out double old) || old != outVal)
                {
                    lastAxis[a] = outVal;
                    WriteTarget(a.SimEvent, outVal);
                }
            }

            var joys = buttons.Select(x => x.Joy).Distinct().ToList();
            var current = new Dictionary<int, uint>();
            foreach (int id in joys)
                if (Joy.Read(id, out var jj)) current[id] = jj.dwButtons;

            foreach (var b in buttons.ToList())
            {
                if (!current.TryGetValue(b.Joy, out uint now32)) continue;
                lastButtons.TryGetValue(b.Joy, out uint prev);
                if (b.Button < 1 || b.Button > 32) continue;
                uint mask = 1u << (b.Button - 1);
                bool now = (now32 & mask) != 0, was = (prev & mask) != 0;
                if (now && !was) Fire(b, true);
                else if (!now && was) Fire(b, false);
            }
            foreach (var kv in current) lastButtons[kv.Key] = kv.Value;
        }

        // ----- Preset -----
        void LoadPreset()
        {
            using (var d = new OpenFileDialog { Filter = "Preset (*.json)|*.json" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                using (var fs = File.OpenRead(d.FileName))
                    preset = (Preset)new DataContractJsonSerializer(typeof(Preset)).ReadObject(fs);
                axes.Clear(); buttons.Clear(); lastAxis.Clear(); toggleState.Clear();
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
