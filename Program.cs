using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Drawing;
using System.Windows.Forms;
using LockheedMartin.Prepar3D.SimConnect;

namespace AxisClone
{
    // ---------- Form utama ----------
    public class MainForm : Form
    {
        const int WM_USER_SIMCONNECT = 0x0402;
        enum EV { }
        enum GRP { G1 }
        enum DEF { }

        enum LearnMode { None, Axis, Button }
        LearnMode learn = LearnMode.None;
        readonly Dictionary<string, int> learnAx = new Dictionary<string, int>();
        readonly Dictionary<Guid, bool[]> learnBtn = new Dictionary<Guid, bool[]>();
        readonly AutoCompleteStringCollection eventSource = new AutoCompleteStringCollection();
        CheckBox chkAuto;
        Timer autoTimer;
        string lastPresetPath = "";
        bool loadingSettings;

        enum REQ { }
        const int TITLE_ID = 9000;
        readonly Dictionary<string, int> readIds = new Dictionary<string, int>();
        readonly Dictionary<int, List<ButtonMap>> readButtons = new Dictionary<int, List<ButtonMap>>();
        Label lblAircraft;
        TextBox txtMatch;
        CheckBox chkProfile;
        string profileFolder = "";
        string currentTitle = "";
        bool opened;
        int tick;

        SimConnect sc;
        readonly Dictionary<string, int> evIds = new Dictionary<string, int>();
        readonly Dictionary<string, int> defIds = new Dictionary<string, int>();
        readonly Dictionary<AxisMap, double> lastAxis = new Dictionary<AxisMap, double>();
        readonly Dictionary<Guid, bool[]> prevBtn = new Dictionary<Guid, bool[]>();
        readonly Dictionary<string, int> pressTime = new Dictionary<string, int>();
        readonly Dictionary<ButtonMap, bool> armed = new Dictionary<ButtonMap, bool>();
        Label lblDevices;
        int lastDevCount = -1;
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
            top.Controls.Add(MakeBtn("Disconnect", (s, e) => { chkAuto.Checked = false; Disconnect(); }));
            top.Controls.Add(MakeBtn("Load Preset", (s, e) => LoadPreset()));
            top.Controls.Add(MakeBtn("Save Preset", (s, e) => SavePreset()));
            top.Controls.Add(MakeBtn("Learn Axis", (s, e) => StartLearn(LearnMode.Axis)));
            top.Controls.Add(MakeBtn("Learn Button", (s, e) => StartLearn(LearnMode.Button)));
            chkAuto = new CheckBox { Text = "Auto-connect", Checked = true, AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
            chkAuto.CheckedChanged += (s, e) => SaveSettings();
            top.Controls.Add(chkAuto);
            status = new Label { Text = "Disconnected", AutoSize = true, Padding = new Padding(10, 8, 0, 0) };
            top.Controls.Add(status);

            gridA = new DataGridView { Dock = DockStyle.Fill, DataSource = axes, AutoGenerateColumns = true };
            gridB = new DataGridView { Dock = DockStyle.Fill, DataSource = buttons, AutoGenerateColumns = true };
            MakeCombo(gridA, "Axis", Devices.AxisNames);
            MakeCombo(gridB, "Action", "Event", "LVarSet", "LVarToggle", "SimVarSet");
            MakeCombo(gridB, "Trigger", "Press", "Release", "Both", "ShortClick", "LongClick");
            if (gridA.Columns.Contains("Live")) gridA.Columns["Live"].ReadOnly = true;
            if (gridB.Columns.Contains("State")) gridB.Columns["State"].ReadOnly = true;
            eventSource.AddRange(EventList.All);
            HookAutocomplete(gridA);
            HookAutocomplete(gridB);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(gridA);
            split.Panel1.Controls.Add(new Label { Text = "AXES  (target: EVENT / L:lvar / SIMVAR,units)", Dock = DockStyle.Top, Height = 18 });
            split.Panel2.Controls.Add(gridB);
            split.Panel2.Controls.Add(new Label { Text = "BUTTONS  (Both: Press=Value, Release=ValueOff | Combo = tombol yang harus ditahan | Short/LongClick: batas LongMs)", Dock = DockStyle.Top, Height = 18 });

            var top2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
            top2.Controls.Add(new Label { Text = "Aircraft match:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            txtMatch = new TextBox { Width = 220 };
            top2.Controls.Add(txtMatch);
            chkProfile = new CheckBox { Text = "Auto profile", AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
            chkProfile.CheckedChanged += (s, e) => SaveSettings();
            top2.Controls.Add(chkProfile);
            top2.Controls.Add(MakeBtn("Profile folder...", (s, e) => PickProfileFolder()));
            top2.Controls.Add(MakeBtn("Import Lorby...", (s, e) => ImportLorby()));
            lblDevices = new Label { Text = "Devices: -", AutoSize = true, Padding = new Padding(10, 6, 0, 0) };
            top2.Controls.Add(lblDevices);
            lblAircraft = new Label { Text = "Aircraft: -", AutoSize = true, Padding = new Padding(10, 6, 0, 0) };
            top2.Controls.Add(lblAircraft);

            Controls.Add(split);
            Controls.Add(top2);
            Controls.Add(top);

            poll = new Timer { Interval = 20 };
            poll.Tick += (s, e) => Poll();
            poll.Start();

            autoTimer = new Timer { Interval = 3000 };
            autoTimer.Tick += (s, e) => { if (chkAuto.Checked && sc == null) Connect(); };
            autoTimer.Start();

            LoadSettings();
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
                sc.OnRecvOpen += (s, d) => { status.Text = "Connected"; OnOpened(); };
                sc.OnRecvSimobjectData += OnSimData;
                sc.OnRecvQuit += (s, d) => BeginInvoke(new Action(Disconnect));
                sc.SetNotificationGroupPriority(GRP.G1, SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
                evIds.Clear(); defIds.Clear();
                status.Text = "Connecting...";
            }
            catch (Exception ex) { status.Text = "Menunggu P3D... (" + ex.Message + ")"; sc = null; }
        }

        void Disconnect()
        {
            try { sc?.Dispose(); } catch { }
            sc = null; evIds.Clear(); defIds.Clear();
            readIds.Clear(); readButtons.Clear(); opened = false; currentTitle = "";
            if (lblAircraft != null) lblAircraft.Text = "Aircraft: -";
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
        static bool ShouldFire(ButtonMap b, bool pressed, int held, out bool off)
        {
            off = false;
            switch (b.Trigger)
            {
                case "Release": return !pressed;
                case "Both": if (!pressed) off = true; return true;
                case "ShortClick": return !pressed && held < b.LongMs;
                case "LongClick": return !pressed && held >= b.LongMs;
                default: return pressed; // Press
            }
        }

        void Fire(ButtonMap b, bool pressed, int held)
        {
            if (sc == null) return;
            if (!ShouldFire(b, pressed, held, out bool off)) return;
            string tgt = b.SimEvent;
            double val = off ? b.ValueOff : b.Value;
            switch (b.Action)
            {
                case "LVarToggle":
                    if (off) return;
                    toggleState.TryGetValue(b, out bool on);
                    on = !on; toggleState[b] = on;
                    WriteTarget("L:" + LName(tgt), on ? b.Value : b.ValueOff);
                    return;
                case "LVarSet": WriteTarget("L:" + LName(tgt), val); return;
                case "Event": SendEvent(tgt, (int)Math.Round(val)); return;
                default: WriteTarget(tgt, val); return; // SimVarSet
            }
        }

        void OnButtonEdge(Dev dev, int btn, bool pressed)
        {
            string pk = dev.Guid + ":" + btn;
            int held = 0;
            if (pressed) pressTime[pk] = Environment.TickCount;
            else if (pressTime.TryGetValue(pk, out int t0)) held = unchecked(Environment.TickCount - t0);

            foreach (var b in buttons.ToList())
            {
                if (b.Button != btn || Devices.Find(b.JoyName, b.Joy) != dev) continue;
                if (pressed)
                {
                    bool ok = b.Combo <= 0 || (b.Combo - 1 < Dev.BtnCount && dev.Btn[b.Combo - 1]);
                    armed[b] = ok;
                    if (ok) Fire(b, true, 0);
                }
                else
                {
                    if (!armed.TryGetValue(b, out bool wasArmed) || !wasArmed) continue;
                    armed[b] = false;
                    Fire(b, false, held);
                }
            }
        }

        // ----- Polling joystick (DirectInput) -----
        void Poll()
        {
            if (++tick % 100 == 0 && learn == LearnMode.None) Devices.Refresh();
            Devices.PollAll();

            if (Devices.List.Count != lastDevCount)
            {
                lastDevCount = Devices.List.Count;
                lblDevices.Text = "Devices (" + lastDevCount + "): " +
                    (lastDevCount == 0 ? "-" : string.Join(", ", Devices.List.Select(d => d.Name).Distinct()));
            }

            if (learn != LearnMode.None) { DoLearn(); return; }

            foreach (var a in axes.ToList())
            {
                var dev = Devices.Find(a.JoyName, a.Joy);
                if (dev == null || !dev.Ok) { a.Live = "n/a"; continue; }
                double v = (dev.Ax[Devices.AxisIndex(a.Axis)] - 32767.5) / 32767.5; // -1..1
                if (a.Invert) v = -v;
                if (Math.Abs(v) * 100 < a.Deadzone) v = 0;
                a.Live = Math.Round(v * 100).ToString("0") + "%";

                if (sc == null) continue;
                double outVal = IsVarTarget(a.SimEvent)
                    ? Math.Round(a.Min + (v + 1) / 2 * (a.Max - a.Min), 4)
                    : Math.Round(v * 16383);

                if (!lastAxis.TryGetValue(a, out double old) || old != outVal)
                {
                    lastAxis[a] = outVal;
                    WriteTarget(a.SimEvent, outVal);
                }
            }

            foreach (var dev in Devices.List)
            {
                if (!dev.Ok) continue;
                if (!prevBtn.TryGetValue(dev.Guid, out bool[] prev)) { prev = new bool[Dev.BtnCount]; prevBtn[dev.Guid] = prev; }
                for (int i = 0; i < Dev.BtnCount; i++)
                {
                    if (dev.Btn[i] == prev[i]) continue;
                    prev[i] = dev.Btn[i];
                    OnButtonEdge(dev, i + 1, dev.Btn[i]);
                }
            }

            foreach (var b in buttons)
            {
                var dev = Devices.Find(b.JoyName, b.Joy);
                b.State = dev == null ? "n/a" : (b.Button >= 1 && b.Button <= Dev.BtnCount && dev.Btn[b.Button - 1] ? "ON" : "");
            }

            if (tick % 4 == 0) { gridA.Invalidate(); gridB.Invalidate(); }
        }

        // ----- Preset -----
        void LoadPreset()
        {
            using (var d = new OpenFileDialog { Filter = "Preset (*.json)|*.json" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                LoadPresetFile(d.FileName);
            }
        }

        void LoadPresetFile(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                    preset = (Preset)new DataContractJsonSerializer(typeof(Preset)).ReadObject(fs);
                axes.Clear(); buttons.Clear(); lastAxis.Clear(); toggleState.Clear(); armed.Clear();
                foreach (var a in preset.Axes) axes.Add(a);
                foreach (var b in preset.Buttons) buttons.Add(b);
                Text = "Axis Clone - " + preset.Name;
                txtMatch.Text = preset.AircraftMatch ?? "";
                lastPresetPath = path;
                SaveSettings();
                SyncToggles();
            }
            catch (Exception ex) { MessageBox.Show("Gagal load preset: " + ex.Message); }
        }

        void SavePreset()
        {
            using (var d = new SaveFileDialog { Filter = "Preset (*.json)|*.json" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                preset.Name = Path.GetFileNameWithoutExtension(d.FileName);
                preset.AircraftMatch = txtMatch.Text.Trim();
                preset.Axes = axes.ToList(); preset.Buttons = buttons.ToList();
                using (var fs = File.Create(d.FileName))
                    new DataContractJsonSerializer(typeof(Preset)).WriteObject(fs, preset);
                lastPresetPath = d.FileName; SaveSettings();
            }
        }

        // ----- Learn: deteksi axis / tombol otomatis -----
        void StartLearn(LearnMode m)
        {
            Devices.Refresh();
            learn = m; learnAx.Clear(); learnBtn.Clear();
            status.Text = m == LearnMode.Axis ? "LEARN: gerakkan sebuah axis..." : "LEARN: tekan sebuah tombol...";
        }

        void DoLearn()
        {
            foreach (var dev in Devices.List)
            {
                if (!dev.Ok) continue;
                if (learn == LearnMode.Axis)
                {
                    for (int i = 0; i < dev.Ax.Length; i++)
                    {
                        string key = dev.Guid + ":" + i;
                        if (!learnAx.TryGetValue(key, out int b0)) { learnAx[key] = dev.Ax[i]; continue; }
                        if (Math.Abs(dev.Ax[i] - b0) > 12000)
                        {
                            axes.Add(new AxisMap { JoyName = dev.Name, Joy = dev.Ordinal, Axis = Devices.AxisNames[i], SimEvent = "" });
                            FinishLearn(gridA); return;
                        }
                    }
                }
                else
                {
                    if (!learnBtn.TryGetValue(dev.Guid, out bool[] bs))
                    {
                        learnBtn[dev.Guid] = (bool[])dev.Btn.Clone(); continue;
                    }
                    for (int i = 0; i < Dev.BtnCount; i++)
                    {
                        if (dev.Btn[i] && !bs[i])
                        {
                            buttons.Add(new ButtonMap { JoyName = dev.Name, Joy = dev.Ordinal, Button = i + 1, SimEvent = "" });
                            FinishLearn(gridB); return;
                        }
                        bs[i] = bs[i] && dev.Btn[i]; // yang dilepas boleh terdeteksi lagi
                    }
                }
            }
        }

        void FinishLearn(DataGridView g)
        {
            learn = LearnMode.None;
            status.Text = sc == null ? "Disconnected" : "Connected";
            try
            {
                int r = g.Rows.Count - 2; // baris terakhir sebelum baris 'new'
                if (r >= 0) { g.CurrentCell = g.Rows[r].Cells["SimEvent"]; g.Focus(); }
            }
            catch { }
            // samakan state agar tombol yang baru ditekan tidak langsung memicu aksi
            foreach (var dev in Devices.List) prevBtn[dev.Guid] = (bool[])dev.Btn.Clone();
        }

        // ----- Autocomplete nama event -----
        void HookAutocomplete(DataGridView g)
        {
            g.EditingControlShowing += (s, e) =>
            {
                var tb = e.Control as TextBox;
                if (tb == null) return;
                bool isTarget = g.CurrentCell != null &&
                    g.Columns[g.CurrentCell.ColumnIndex].DataPropertyName == "SimEvent";
                if (isTarget)
                {
                    tb.AutoCompleteMode = AutoCompleteMode.Suggest;
                    tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                    tb.AutoCompleteCustomSource = eventSource;
                }
                else tb.AutoCompleteMode = AutoCompleteMode.None;
            };
        }

        // ----- Settings (preset terakhir + auto-connect) -----
        static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AxisClone", "settings.txt");
            }
        }

        void SaveSettings()
        {
            if (loadingSettings) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[] { chkAuto.Checked ? "1" : "0", lastPresetPath ?? "", profileFolder ?? "", chkProfile.Checked ? "1" : "0" });
            }
            catch { }
        }

        void LoadSettings()
        {
            loadingSettings = true;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var l = File.ReadAllLines(SettingsPath);
                    if (l.Length > 0) chkAuto.Checked = l[0] == "1";
                    if (l.Length > 2) profileFolder = l[2];
                    if (l.Length > 3) chkProfile.Checked = l[3] == "1";
                    loadingSettings = false;
                    if (l.Length > 1 && File.Exists(l[1])) LoadPresetFile(l[1]);
                }
            }
            catch { }
            finally { loadingSettings = false; }
        }

        // ----- Baca data dari sim: judul pesawat + status LVar untuk toggle -----
        void OnOpened()
        {
            opened = true;
            try
            {
                sc.AddToDataDefinition((DEF)TITLE_ID, "TITLE", null,
                    SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
                sc.RegisterDataDefineStruct<TitleStruct>((DEF)TITLE_ID);
                sc.RequestDataOnSimObject((REQ)TITLE_ID, (DEF)TITLE_ID,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
                    SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
            }
            catch (Exception ex) { status.Text = "Err title: " + ex.Message; }
            SyncToggles();
        }

        static string LName(string t)
        {
            t = (t ?? "").Trim();
            return t.StartsWith("L:", StringComparison.OrdinalIgnoreCase) ? t.Substring(2).Trim() : t;
        }

        // Baca nilai LVar untuk tiap tombol LVarToggle supaya posisi toggle sinkron dengan kokpit
        void SyncToggles()
        {
            if (sc == null || !opened) return;
            try
            {
                foreach (var l in readButtons.Values) l.Clear();
                foreach (var b in buttons.Where(x => x.Action == "LVarToggle" && !string.IsNullOrWhiteSpace(x.SimEvent)))
                {
                    string name = LName(b.SimEvent);
                    if (!readIds.TryGetValue(name, out int id))
                    {
                        id = 10001 + readIds.Count;
                        sc.AddToDataDefinition((DEF)id, "L:" + name, "number",
                            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
                        sc.RegisterDataDefineStruct<DoubleStruct>((DEF)id);
                        sc.RequestDataOnSimObject((REQ)id, (DEF)id,
                            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
                            SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
                        readIds[name] = id;
                        readButtons[id] = new List<ButtonMap>();
                    }
                    readButtons[id].Add(b);
                }
            }
            catch (Exception ex) { status.Text = "Err sync: " + ex.Message; }
        }

        void OnSimData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            try
            {
                int rid = (int)data.dwRequestID;
                if (rid == TITLE_ID)
                {
                    string t = ((TitleStruct)data.dwData[0]).Title ?? "";
                    if (t != currentTitle) { currentTitle = t; OnAircraftChanged(t); }
                }
                else if (readButtons.TryGetValue(rid, out var list))
                {
                    double v = ((DoubleStruct)data.dwData[0]).V;
                    foreach (var b in list)
                        toggleState[b] = Math.Abs(v - b.Value) <= Math.Abs(v - b.ValueOff);
                }
            }
            catch { }
        }

        // ----- Pergantian profil otomatis per pesawat -----
        void OnAircraftChanged(string title)
        {
            lblAircraft.Text = "Aircraft: " + title;
            if (!chkProfile.Checked || string.IsNullOrWhiteSpace(profileFolder) || !Directory.Exists(profileFolder)) return;

            foreach (var file in Directory.GetFiles(profileFolder, "*.json"))
            {
                try
                {
                    Preset p;
                    using (var fs = File.OpenRead(file))
                        p = (Preset)new DataContractJsonSerializer(typeof(Preset)).ReadObject(fs);
                    if (string.IsNullOrWhiteSpace(p.AircraftMatch)) continue;

                    bool match = p.AircraftMatch.Split(',')
                        .Select(x => x.Trim()).Where(x => x.Length > 0)
                        .Any(x => title.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) continue;

                    if (!string.Equals(file, lastPresetPath, StringComparison.OrdinalIgnoreCase))
                        LoadPresetFile(file);
                    status.Text = "Connected - profil: " + p.Name;
                    return;
                }
                catch { }
            }
        }

        void PickProfileFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Pilih folder berisi preset (.json)" })
            {
                if (!string.IsNullOrEmpty(profileFolder) && Directory.Exists(profileFolder))
                    d.SelectedPath = profileFolder;
                if (d.ShowDialog() != DialogResult.OK) return;
                profileFolder = d.SelectedPath;
                chkProfile.Checked = true;
                SaveSettings();
                if (!string.IsNullOrEmpty(currentTitle)) OnAircraftChanged(currentTitle);
            }
        }

        // ----- Import preset Lorby (.tmpl) -----
        void ImportLorby()
        {
            using (var d = new OpenFileDialog { Filter = "Lorby template (*.tmpl;*.xml)|*.tmpl;*.xml|Semua file|*.*" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                try
                {
                    var r = LorbyImport.Import(d.FileName);
                    preset = r.Preset;
                    axes.Clear(); buttons.Clear(); lastAxis.Clear(); toggleState.Clear(); armed.Clear();
                    foreach (var a in preset.Axes) axes.Add(a);
                    foreach (var b in preset.Buttons) buttons.Add(b);
                    Text = "Axis Clone - " + preset.Name + " (import Lorby)";
                    txtMatch.Text = "";
                    lastPresetPath = ""; SaveSettings();
                    SyncToggles();
                    ShowReport(string.Join(Environment.NewLine, r.Report));
                }
                catch (Exception ex) { MessageBox.Show("Gagal import: " + ex.Message); }
            }
        }

        void ShowReport(string text)
        {
            using (var f = new Form { Text = "Hasil Import Lorby", Width = 760, Height = 540, StartPosition = FormStartPosition.CenterParent })
            {
                var tb = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), Text = text
                };
                f.Controls.Add(tb);
                tb.Select(0, 0);
                f.ShowDialog(this);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Devices.Init(Handle);
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}
