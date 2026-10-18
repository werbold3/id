using System;
using System.Collections.Generic;
using System.Linq;
using SharpDX.DirectInput;

namespace AxisClone
{
    public class Dev
    {
        public const int BtnCount = 144;          // 128 tombol + 16 POV virtual (129..144)
        public string Name;
        public Guid Guid;
        public Joystick Stick;
        public bool Ok;
        public int Ordinal;                        // urutan di antara perangkat bernama sama
        public bool[] Btn = new bool[BtnCount];
        public int[] Ax = new int[8];              // X,Y,Z,RX,RY,RZ,S0,S1  (0..65535)
    }

    public static class Devices
    {
        public static readonly string[] AxisNames = { "X", "Y", "Z", "RX", "RY", "RZ", "S0", "S1" };
        public static List<Dev> List = new List<Dev>();
        static DirectInput di;
        static IntPtr hwnd;

        public static void Init(IntPtr handle)
        {
            hwnd = handle;
            try { di = new DirectInput(); } catch { di = null; }
            Refresh();
        }

        public static void Refresh()
        {
            if (di == null) return;
            IList<DeviceInstance> found;
            try { found = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly); }
            catch { return; }

            var keep = new List<Dev>();
            foreach (var inst in found)
            {
                var ex = List.FirstOrDefault(d => d.Guid == inst.InstanceGuid);
                if (ex != null) { keep.Add(ex); continue; }
                try
                {
                    var js = new Joystick(di, inst.InstanceGuid);
                    js.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    try { js.Properties.Range = new InputRange(0, 65535); } catch { }
                    js.Acquire();
                    keep.Add(new Dev { Name = inst.InstanceName, Guid = inst.InstanceGuid, Stick = js });
                }
                catch { }
            }
            foreach (var old in List.Where(o => !keep.Contains(o)))
                try { old.Stick.Dispose(); } catch { }

            keep = keep.OrderBy(d => d.Name).ThenBy(d => d.Guid.ToString()).ToList();
            foreach (var g in keep.GroupBy(d => d.Name))
            {
                int i = 0;
                foreach (var d in g) d.Ordinal = i++;
            }
            List = keep;
        }

        public static void PollAll()
        {
            foreach (var d in List)
            {
                try
                {
                    d.Stick.Poll();
                    var s = d.Stick.GetCurrentState();
                    d.Ax[0] = s.X; d.Ax[1] = s.Y; d.Ax[2] = s.Z;
                    d.Ax[3] = s.RotationX; d.Ax[4] = s.RotationY; d.Ax[5] = s.RotationZ;
                    var sl = s.Sliders;
                    d.Ax[6] = sl != null && sl.Length > 0 ? sl[0] : 0;
                    d.Ax[7] = sl != null && sl.Length > 1 ? sl[1] : 0;

                    var b = s.Buttons;
                    int n = Math.Min(128, b == null ? 0 : b.Length);
                    for (int i = 0; i < n; i++) d.Btn[i] = b[i];

                    var pov = s.PointOfViewControllers;
                    for (int p = 0; p < 4; p++)
                    {
                        int v = pov != null && p < pov.Length ? pov[p] : -1;
                        bool has = v >= 0 && v <= 35999;
                        d.Btn[128 + p * 4 + 0] = has && (v >= 31500 || v <= 4500);   // utara
                        d.Btn[128 + p * 4 + 1] = has && v >= 4500 && v <= 13500;      // timur
                        d.Btn[128 + p * 4 + 2] = has && v >= 13500 && v <= 22500;     // selatan
                        d.Btn[128 + p * 4 + 3] = has && v >= 22500 && v <= 31500;     // barat
                    }
                    d.Ok = true;
                }
                catch
                {
                    d.Ok = false;
                    try { d.Stick.Acquire(); } catch { }
                }
            }
        }

        public static int AxisIndex(string a)
        {
            int i = Array.IndexOf(AxisNames, a);
            return i < 0 ? 0 : i;
        }

        public static Dev Find(string joyName, int joy)
        {
            if (string.IsNullOrEmpty(joyName))
                return joy >= 0 && joy < List.Count ? List[joy] : null;
            var c = List.Where(d => string.Equals(d.Name, joyName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (c.Count == 0) return null;
            return joy >= 0 && joy < c.Count ? c[joy] : c[0];
        }
    }
}
