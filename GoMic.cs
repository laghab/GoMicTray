using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

// Talks to the Samson Go Mic's own USB audio controls through the Windows Core Audio device topology.
// Deliberately touches ONLY: the monitor-path mute (direct monitoring), the monitor-path volume,
// and the capture mute. The microphone's capture gain is never read or written.
namespace GoMicNative {
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDevEnumComObj {}
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
  void EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection c);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection { void GetCount(out int n); void Item(int i, out IMMDevice d); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
  void Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  void OpenPropertyStore(int a, out IntPtr ps);
  void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}
[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology {
  void GetConnectorCount(out int n);
  void GetConnector(int i, out IConnector c);
  void GetSubunitCount(out int n);
  void GetSubunit(int i, out IPart p);
  void GetPartById(int id, out IPart p);
  void GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  void GetSignalPath(IPart a, IPart b, bool rejectMixed, out IPartsList l);
}
[ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector {
  void GetType(out int t);
  void GetDataFlow(out int f);
  void ConnectTo(IConnector c);
  void Disconnect();
  void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool b);
  void GetConnectedTo(out IConnector c);
  void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
  void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}
[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPart {
  void GetName([MarshalAs(UnmanagedType.LPWStr)] out string n);
  void GetLocalId(out int id);
  void GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  void GetPartType(out int t);
  void GetSubType(out Guid g);
  void GetControlInterfaceCount(out int n);
  void GetControlInterface(int i, out IControlInterface c);
  void EnumPartsIncoming(out IPartsList l);
  void EnumPartsOutgoing(out IPartsList l);
  void GetTopologyObject(out IDeviceTopology t);
  void Activate(int ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  void RegisterControlChangeCallback(ref Guid iid, IntPtr cb);
  void UnregisterControlChangeCallback(IntPtr cb);
}
[ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPartsList { void GetCount(out int n); void GetPart(int i, out IPart p); }
[ComImport, Guid("45D37C3F-5140-444A-AE24-400789F3CBF3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IControlInterface { void GetName([MarshalAs(UnmanagedType.LPWStr)] out string n); void GetIID(out Guid g); }

[ComImport, Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMute {
  void SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
  void GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
}
[ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioVolumeLevel {
  void GetChannelCount(out int n);
  void GetLevelRange(int ch, out float min, out float max, out float step);
  void GetLevel(int ch, out float db);
  void SetLevel(int ch, float db, ref Guid ctx);
  void SetLevelUniform(float db, ref Guid ctx);
  void SetLevelAllChannels([MarshalAs(UnmanagedType.LPArray)] float[] l, int n, ref Guid ctx);
}

public class GoMic {
  static readonly Guid IID_Topo = new Guid("2A07407E-6497-4A18-9787-32F79BD0D98F");
  static readonly Guid IID_Mute = new Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E");
  static readonly Guid IID_Vol  = new Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC");
  const int CLSCTX_ALL = 23;
  Guid ctx = Guid.Empty;
  IAudioMute monMute, capMute;
  IAudioVolumeLevel hpVol;
  public string VidMatch = "vid_17a0";   // Samson
  public bool Connected { get { return monMute != null; } }
  public bool HasMonitorVolume { get { return hpVol != null; } }
  public bool HasMicMute { get { return capMute != null; } }

  static List<IPart> Children(IPart p) {
    var r = new List<IPart>();
    try { IPartsList l; p.EnumPartsOutgoing(out l); if (l == null) return r;
      int n; l.GetCount(out n);
      for (int i = 0; i < n; i++) { IPart q; l.GetPart(i, out q); r.Add(q); } } catch { }
    return r;
  }
  static List<IPart> Chain(IPart p) {
    var r = new List<IPart>(); int guard = 0;
    while (p != null && guard++ < 32) {
      r.Add(p);
      var c = Children(p);
      p = c.Count > 0 ? c[0] : null;
    }
    return r;
  }
  static bool Has(IPart p, Guid iid) {
    int n; p.GetControlInterfaceCount(out n);
    for (int i = 0; i < n; i++) { IControlInterface c; p.GetControlInterface(i, out c); Guid g; c.GetIID(out g); if (g == iid) return true; }
    return false;
  }
  static string NameOf(IPart p) { string s; p.GetName(out s); return s ?? ""; }
  static T Act<T>(IPart p, Guid iid) { object o; p.Activate(CLSCTX_ALL, ref iid, out o); return (T)o; }

  public void Disconnect() { monMute = capMute = null; hpVol = null; }

  public bool Connect() {
    Disconnect();
    try {
      var en = (IMMDeviceEnumerator)new MMDevEnumComObj();
      IMMDeviceCollection col; en.EnumAudioEndpoints(1 /*capture*/, 1 /*active*/, out col);
      int n; col.GetCount(out n);
      for (int i = 0; i < n; i++) {
        IMMDevice d; col.Item(i, out d);
        object o; Guid iid = IID_Topo; d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
        var topo = (IDeviceTopology)o;
        IConnector con; topo.GetConnector(0, out con);
        string devId; con.GetDeviceIdConnectedTo(out devId);
        if (devId == null || devId.IndexOf(VidMatch, StringComparison.OrdinalIgnoreCase) < 0) continue;
        IConnector adapterCon; con.GetConnectedTo(out adapterCon);
        IPart mic = (IPart)adapterCon;
        // mic -> ADC -> { capture branch: Mute -> Volume(gain, NOT USED) -> ... ,
        //                 monitor branch: Mute -> SuperMix -> Sum -> Mute -> Volume -> DAC }
        foreach (var adc in Children(mic)) {
          foreach (var branch in Children(adc)) {
            var ch = Chain(branch);
            bool isMonitor = false; int sumIdx = -1;
            for (int k = 0; k < ch.Count; k++) {
              string nm = NameOf(ch[k]);
              if (nm == "SuperMix" || nm == "Sum") isMonitor = true;
              if (nm == "Sum" && sumIdx < 0) sumIdx = k;
            }
            if (isMonitor) {
              if (Has(ch[0], IID_Mute)) monMute = Act<IAudioMute>(ch[0], IID_Mute);
              for (int k = Math.Max(sumIdx, 0); k < ch.Count; k++)
                if (Has(ch[k], IID_Vol)) { hpVol = Act<IAudioVolumeLevel>(ch[k], IID_Vol); break; }
            } else {
              if (Has(ch[0], IID_Mute)) capMute = Act<IAudioMute>(ch[0], IID_Mute);
            }
          }
        }
        if (Connected) return true;
        Disconnect();
      }
    } catch { Disconnect(); }
    return false;
  }

  static bool GetMute(IAudioMute m) { bool b; m.GetMute(out b); return b; }
  public bool MonitorOn { get { return !GetMute(monMute); } set { monMute.SetMute(!value, ref ctx); } }
  public bool MicMuted  { get { return capMute != null && GetMute(capMute); } set { if (capMute != null) capMute.SetMute(value, ref ctx); } }

  public float MonitorVolDb {
    get { float f; hpVol.GetLevel(0, out f); return f; }
    set { hpVol.SetLevelUniform(value, ref ctx); }
  }
  public void MonitorRange(out float mn, out float mx, out float st) { hpVol.GetLevelRange(0, out mn, out mx, out st); }

  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
}
}
