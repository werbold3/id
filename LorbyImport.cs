using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AxisClone
{
    public class ImportResult
    {
        public Preset Preset;
        public List<string> Report = new List<string>();
    }

    // Importer template Lorby Axis and Ohs (.tmpl, XML <TemplateExport>)
    public static class LorbyImport
    {
        class Tr { public string Action; public string Target; public double? Value; }

        // Event khusus MSFS yang tidak ada di P3D -> padanan (SimVar). Belum teruji di P3D.
        static readonly Dictionary<string, Tr> Translate = BuildTranslate();

        static Dictionary<string, Tr> BuildTranslate()
        {
            var d = new Dictionary<string, Tr>(StringComparer.OrdinalIgnoreCase);
            d["PARKING_BRAKE_SET"] = new Tr { Action = "SimVarSet", Target = "BRAKE PARKING POSITION,bool", Value = null };
            string[,] lights = {
                { "TAXI_LIGHTS", "LIGHT TAXI" }, { "BEACON_LIGHTS", "LIGHT BEACON" },
                { "NAV_LIGHTS", "LIGHT NAV" }, { "WING_LIGHTS", "LIGHT WING" },
                { "CABIN_LIGHTS", "LIGHT CABIN" }, { "LANDING_LIGHTS", "LIGHT LANDING" }
            };
            for (int i = 0; i < lights.GetLength(0); i++)
            {
                d[lights[i, 0] + "_ON"] = new Tr { Action = "SimVarSet", Target = lights[i, 1] + ",bool", Value = 1 };
                d[lights[i, 0] + "_OFF"] = new Tr { Action = "SimVarSet", Target = lights[i, 1] + ",bool", Value = 0 };
            }
            return d;
        }

        static string S(XElement e, string n) { var x = e.Element(n); return x == null ? "" : (x.Value ?? "").Trim(); }
        static int I(XElement e, string n, int def)
        {
            return int.TryParse(S(e, n), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
        }
        static double D(XElement e, string n, double def)
        {
            return double.TryParse(S(e, n), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
        }
        static bool B(XElement e, string n) { return string.Equals(S(e, n), "true", StringComparison.OrdinalIgnoreCase); }

        public static ImportResult Import(string path)
        {
            var doc = XDocument.Load(path);
            var tpl = doc.Root == null ? null : doc.Root.Element("template");
            if (tpl == null) throw new InvalidDataException("Bukan template Lorby (elemen <template> tidak ditemukan).");

            var res = new ImportResult();
            var p = new Preset { Name = S(tpl, "aircraftId") };
            if (p.Name.Length == 0) p.Name = Path.GetFileNameWithoutExtension(path);
            res.Preset = p;

            var known = new HashSet<string>(EventList.All, StringComparer.OrdinalIgnoreCase);
            var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var translated = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var skipped = new Dictionary<string, int>();
            var devices = new SortedSet<string>();
            int total = 0, rows = 0;

            Action<string> skip = why => { skipped.TryGetValue(why, out int n); skipped[why] = n + 1; };

            var hotkeys = tpl.Element("buttons") == null
                ? new List<XElement>() : tpl.Element("buttons").Elements("GlobalHotkey").ToList();

            foreach (var h in hotkeys)
            {
                total++;
                int btn = I(h, "joystickButton", -1);
                if (btn < 0)
                {
                    if (I(h, "joystickPovControl", -1) >= 0) skip("POV hat (belum didukung importer)");
                    else if (I(h, "midiDeviceId", -1) >= 0) skip("MIDI");
                    else skip("bukan tombol joystick (mis. keyboard)");
                    continue;
                }
                if (I(h, "ControlLayer", 0) != 0) { skip("Control Layer > 0 (belum didukung)"); continue; }

                string joyName = S(h, "joystickName");
                if (joyName.Length > 0) devices.Add(joyName);

                int combo = 0;
                var ck = h.Element("combinedKey");
                if (ck != null)
                {
                    int cb = I(ck, "joystickButton", -1);
                    if (cb >= 0)
                    {
                        if (!string.Equals(S(ck, "joystickGuid"), S(h, "joystickGuid"), StringComparison.OrdinalIgnoreCase))
                        { skip("combo dengan perangkat berbeda"); continue; }
                        combo = cb + 1;
                    }
                }

                bool longClick = B(h, "longClick");
                int longMs = I(h, "longClickTimeoutMs", 0);
                if (longMs <= 0) longMs = 400;

                foreach (string kind in new[] { "downEvent", "upEvent" })
                {
                    var ev = h.Element(kind);
                    if (ev == null) continue;

                    string name = S(ev, "SimulatorEventName");
                    string type = S(ev, "type");
                    double val = D(ev, "ValueToSet", 0);

                    if (name.Length == 0) { skip("event kosong"); continue; }
                    if (B(ev, "IsAaoVirtualEvent")) { skip("virtual event AAO (khusus MSFS)"); continue; }
                    if (type.Length > 0 && !string.Equals(type, "SINGLE", StringComparison.OrdinalIgnoreCase))
                    { skip("tipe event " + type + " (belum didukung)"); continue; }
                    if (name.StartsWith("(") || name.Contains("(L:") || name.Contains("(A:") || name.Contains("(>"))
                    { skip("skrip RPN/AAO"); continue; }

                    var row = new ButtonMap
                    {
                        JoyName = joyName, Joy = 0, Button = btn + 1, Combo = combo, LongMs = longMs,
                        ValueOff = 0,
                        Trigger = kind == "downEvent" ? (longClick ? "LongClick" : "Press")
                                                      : (longClick ? "ShortClick" : "Release")
                    };

                    if (name.StartsWith("L:", StringComparison.OrdinalIgnoreCase))
                    {
                        row.Action = "LVarSet"; row.SimEvent = name; row.Value = val;
                    }
                    else if (Translate.TryGetValue(name, out Tr tr))
                    {
                        row.Action = tr.Action; row.SimEvent = tr.Target;
                        row.Value = tr.Value.HasValue ? tr.Value.Value : val;
                        translated.Add(name + "  ->  " + tr.Target);
                    }
                    else
                    {
                        row.Action = "Event"; row.SimEvent = name; row.Value = val;
                        if (!known.Contains(name)) unknown.Add(name);
                    }
                    p.Buttons.Add(row);
                    rows++;
                }
            }

            var r = res.Report;
            r.Add("Template : " + p.Name);
            r.Add("Perangkat: " + (devices.Count == 0 ? "-" : string.Join(", ", devices)));
            r.Add("");
            r.Add("Hotkey di file       : " + total);
            r.Add("Baris diimpor        : " + rows + " (satu hotkey bisa jadi 2 baris: down + up)");
            r.Add("");
            r.Add("CATATAN PENTING");
            r.Add(" - Nama perangkat di preset harus sama dengan nama perangkat di Windows");
            r.Add("   (kolom JoyName). Kalau beda, edit kolom JoyName di tabel.");
            r.Add(" - Combo (mis. knob selector + encoder) dipetakan ke kolom Combo: tombol");
            r.Add("   selector harus sedang ditekan/aktif.");
            r.Add(" - Long click: UP event = klik singkat (ShortClick), DOWN event = klik panjang");
            r.Add("   (LongClick, batas LongMs ms). Ini asumsi saya dari isi preset; ubah kolom");
            r.Add("   Trigger kalau perilakunya terbalik.");

            if (translated.Count > 0)
            {
                r.Add(""); r.Add("DITERJEMAHKAN (event MSFS -> SimVar P3D, belum teruji):");
                foreach (var t in translated) r.Add("   " + t);
            }
            if (unknown.Count > 0)
            {
                r.Add(""); r.Add("EVENT TIDAK ADA DI DAFTAR P3D SAYA (mungkin khusus MSFS, bisa tidak bereaksi):");
                foreach (var u in unknown) r.Add("   " + u);
            }
            if (skipped.Count > 0)
            {
                r.Add(""); r.Add("DILEWATI:");
                foreach (var kv in skipped) r.Add("   " + kv.Value + " x " + kv.Key);
            }

            var axisEl = tpl.Element("axis");
            int axisCount = axisEl == null ? 0 : axisEl.Elements().Count();
            r.Add("");
            r.Add(axisCount == 0 ? "Axis: template ini tidak berisi axis."
                                 : "Axis: " + axisCount + " entri axis TIDAK diimpor (format axis belum diketahui). Atur manual / Learn Axis.");

            int auto = tpl.Element("autoScripts") == null ? 0 : tpl.Element("autoScripts").Elements().Count();
            int vars = tpl.Element("overrideSaitekVariables") == null ? 0 : tpl.Element("overrideSaitekVariables").Elements().Count();
            int scripts = doc.Root.Element("scripts") == null ? 0 : doc.Root.Element("scripts").Elements().Count();
            if (auto + vars + scripts > 0)
                r.Add("Tidak diimpor: " + auto + " auto-script, " + vars + " variabel Saitek, " + scripts +
                      " script (mis. LED Honeycomb lewat HIDWRITE) - butuh Lorby/AAO.");
            return res;
        }
    }
}
