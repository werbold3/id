using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace AxisClone
{
    // Target / SimEvent bisa berisi:
    //   NAMA_EVENT_P3D            -> kirim event (mis. GEAR_TOGGLE, AXIS_ELEVATOR_SET)
    //   L:namaLvar                -> set Local Variable
    //   NAMA SIMVAR,units         -> set SimVar yang bisa ditulis (mis. LIGHT NAV,bool)
    //
    // JoyName: nama perangkat (mis. "Bravo Throttle Quadrant"). Joy: urutan di antara perangkat
    // bernama sama (0 = pertama). Jika JoyName kosong, Joy = indeks di daftar semua perangkat.
    [DataContract]
    public class AxisMap
    {
        [DataMember] public string JoyName { get; set; } = "";
        [DataMember] public int Joy { get; set; }
        [DataMember] public string Axis { get; set; } = "X";   // X,Y,Z,RX,RY,RZ,S0,S1
        [DataMember] public string SimEvent { get; set; } = "AXIS_ELEVATOR_SET";
        [DataMember] public bool Invert { get; set; }
        [DataMember] public int Deadzone { get; set; } = 2;
        [DataMember] public double Min { get; set; } = 0;
        [DataMember] public double Max { get; set; } = 1;
        public string Live { get; set; }

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            if (JoyName == null) JoyName = "";
            if (SimEvent == null) SimEvent = "";
            // nama axis lama (winmm) -> DirectInput
            if (Axis == null) Axis = "X";
            else if (Axis == "R") Axis = "RZ";
            else if (Axis == "U") Axis = "RX";
            else if (Axis == "V") Axis = "RY";
        }
    }

    [DataContract]
    public class ButtonMap
    {
        [DataMember] public string JoyName { get; set; } = "";
        [DataMember] public int Joy { get; set; }
        [DataMember] public int Button { get; set; } = 1;      // 1-128 (129+ = POV virtual)
        [DataMember] public int Combo { get; set; }            // 0 = tanpa; >0 = tombol lain (perangkat sama) yang harus ditahan
        // Event | LVarSet | LVarToggle | SimVarSet
        [DataMember] public string Action { get; set; } = "Event";
        [DataMember] public string SimEvent { get; set; } = "GEAR_TOGGLE";
        [DataMember] public double Value { get; set; }
        [DataMember] public double ValueOff { get; set; }
        // Press | Release | Both | ShortClick | LongClick
        [DataMember] public string Trigger { get; set; } = "Press";
        [DataMember] public int LongMs { get; set; } = 400;    // batas short/long click
        public string State { get; set; }

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            if (JoyName == null) JoyName = "";
            if (Action == null) Action = "Event";
            if (SimEvent == null) SimEvent = "";
            if (Trigger == null) Trigger = "Press";
            if (LongMs <= 0) LongMs = 400;
        }
    }

    [DataContract]
    public class Preset
    {
        [DataMember] public string Name { get; set; } = "New Preset";
        [DataMember] public List<AxisMap> Axes { get; set; } = new List<AxisMap>();
        [DataMember] public List<ButtonMap> Buttons { get; set; } = new List<ButtonMap>();
        [DataMember] public string AircraftMatch { get; set; } = "";

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            if (Axes == null) Axes = new List<AxisMap>();
            if (Buttons == null) Buttons = new List<ButtonMap>();
            if (AircraftMatch == null) AircraftMatch = "";
            if (Name == null) Name = "Preset";
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    struct TitleStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct DoubleStruct { public double V; }
}
