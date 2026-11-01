using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// MCHOSE (迈丛) wireless mice paired to the "2.4G Wireless Receiver"
    /// (USB\VID_3554&amp;PID_FA09). The receiver exposes the radio-connected mouse
    /// as a child HID device USB\VID_3837&amp;PID_100B whose vendor usage page
    /// 0xFF01 carries the battery protocol.
    ///
    /// Protocol reverse-engineered from M HUB's renderer bundle and verified
    /// against live traffic + M HUB's own stored deviceInfoMap (2026-11-01):
    ///
    ///   65-byte FEATURE reports on the FF01 collection, payload = bytes[2..]
    ///   XOR 0xFF. A query is: SET_FEATURE(reportId, ~cmd, 0xFF fill), then the
    ///   mouse raises an interrupt-IN notification "e5 <cmd>" (XOR view) — only
    ///   after that ACK does GET_FEATURE return fresh data (before it, the
    ///   device ping-pongs stale banks and eventually returns zeros).
    ///
    ///   0x11 0x03 bond      : payload[0]=bond, [1..2]=vid, [3..4]=pid
    ///   0x11 0x06 ost       : u16 vid, u16 pid, u32 fwVersion, bits,
    ///                         payload[9]=battery %, payload[10]=charge status
    ///                         (battery 0 == device offline / breakConnect)
    ///
    ///   Verified live: vid=14391 pid=16418 fw=131592 bits=09 battery=74
    ///   matches M HUB {"MCHOSE A5 V3": batteryLevel:74, chargeStatus:0}.
    ///   (M HUB's UI displays floor-to-5 rounding of the same raw value.)
    ///
    ///   A passive e2 announce (battery at payload[3]) also exists, but the
    ///   ost query is what M HUB persists, so we speak that.
    /// </summary>
    public class MchoseMouseProvider : IDeviceProvider
    {
        private const ushort MchoseRadioVid = 0x3837;   // child device of the 2.4G receiver
        private const ushort FeatureLen = 65;
        private const ushort PageVendor = 0xFF01;
        private const byte RidQuery = 0x11;
        private const byte CmdBond = 0x03;
        private const byte CmdOst = 0x06;

        public string Name { get { return "MCHOSE Mouse"; } }

        public List<DeviceInfo> Discover()
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            HashSet<string> seen = new HashSet<string>();

            foreach (HidDeviceInfo h in HidNative.Enumerate())
            {
                if (h.VendorId != MchoseRadioVid) continue;
                if (h.UsagePage != PageVendor) continue;
                if (h.FeatureReportLength != FeatureLen) continue;

                string key = h.Path;
                if (!seen.Add(key)) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "mchosemouse-" + h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                d.Brand = "MCHOSE";
                d.Model = ModelFromName(h.ProductName, h.ProductId);
                d.Name = "MCHOSE " + d.Model;
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            try
            {
                using (HidSession s = new HidSession(d.HidPath, FeatureLen, FeatureLen))
                {
                    s.Flush(30);

                    // wake the vendor channel, like M HUB's entrance sequence
                    SendFeature(s, CmdBond);
                    System.Threading.Thread.Sleep(120);
                    s.Flush(30);

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(200);

                        SendFeature(s, CmdOst);
                        if (!WaitAck(s, CmdOst, 1500))
                        {
                            r.Sleeping = true;
                            r.Error = "设备休眠";
                            continue;
                        }

                        byte[] payload;
                        if (!ReadOst(s, out payload)) continue;

                        int vid = payload[0] | (payload[1] << 8);
                        int level = payload[9];
                        int charge = payload[10];

                        r.Raw = Hex(payload);
                        r.Detail = "2.4G";

                        if (vid != MchoseRadioVid || level <= 0 || level > 100)
                        {
                            r.Sleeping = true;
                            r.Error = "设备休眠";
                            continue;
                        }

                        r.Percent = level;
                        r.Level = BatteryMath.FromPercent(level);
                        r.Charging = charge != 0;
                        if (r.Charging) r.Detail = "2.4G 充电中";
                        r.Online = true;
                        return r;
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        /// <summary>SET_FEATURE: reportId, command XOR-inverted, 0xFF filler.</summary>
        private static void SendFeature(HidSession s, byte cmd)
        {
            byte[] buf = new byte[FeatureLen];
            for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF;
            buf[0] = RidQuery;
            buf[1] = (byte)(cmd ^ 0xFF);
            HidNative.HidD_SetFeature(s.Handle, buf, buf.Length);
        }

        /// <summary>
        /// Wait for the mouse's interrupt-IN ACK: bytes[1..] XOR 0xFF start
        /// with "e5 &lt;cmd&gt;". The ACK precedes the fresh feature bank.
        /// </summary>
        private static bool WaitAck(HidSession s, byte cmd, int totalMs)
        {
            string target = "e5" + cmd.ToString("x2");
            int waited = 0;
            while (waited < totalMs)
            {
                byte[] p = s.Read(120);
                if (p == null) { waited += 120; continue; }
                var sb = new StringBuilder();
                for (int i = 1; i < p.Length; i++) sb.Append(((byte)(p[i] ^ 0xFF)).ToString("x2"));
                if (sb.ToString(0, 4).Equals(target)) return true;
                waited += 120;
            }
            return false;
        }

        /// <summary>
        /// GET_FEATURE after the ACK. The firmware alternates two banks with the
        /// same payload: XOR-encoded (buf[1] == ~cmd) and plain (buf[1] == cmd).
        /// Accept either; decode accordingly.
        /// </summary>
        private static bool ReadOst(HidSession s, out byte[] payload)
        {
            payload = null;
            for (int i = 0; i < 4; i++)
            {
                byte[] gb = new byte[FeatureLen];
                gb[0] = RidQuery;
                if (!HidNative.HidD_GetFeature(s.Handle, gb, gb.Length)) return false;

                bool xorBank = ((~gb[1]) & 0xFF) == CmdOst;
                bool plainBank = gb[1] == CmdOst;
                if (!xorBank && !plainBank) continue;

                byte[] p = new byte[FeatureLen - 2];
                for (int j = 0; j < p.Length; j++)
                    p[j] = xorBank ? (byte)(gb[2 + j] ^ 0xFF) : gb[2 + j];
                payload = p;
                return true;
            }
            return false;
        }

        private static string ModelFromName(string productName, ushort pid)
        {
            if (!string.IsNullOrEmpty(productName))
            {
                // Windows reports the radio child as e.g. "MCHOSE A5 V3"
                string n = productName.Trim();
                if (n.StartsWith("MCHOSE", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6).Trim();
                if (n.Length > 0) return n;
            }
            return "PID_" + pid.ToString("X4");
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 16 && i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
