using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// MCHOSE (迈丛) 2.4G keyboards.
    ///
    /// Protocol reverse-engineered from M HUB's USB traffic (2026-10-19):
    ///   20-byte frame over HID report ID 0x13, sent with SET_REPORT,
    ///   answered on the interrupt-IN endpoint.
    ///
    ///   frame[0]      = 0x13 report id
    ///   frame[1]      = command
    ///   frame[2..3]   = 0x01 0x00
    ///   frame[4..18]  = payload
    ///   frame[19]     = checksum = sum(frame[0..18]) &amp; 0xFF
    ///
    ///   Battery query   : 0x4A
    ///   Battery reply   : 13 4A 01 00 02 64 01 ... C5
    ///                                ^^ ^^
    ///                                |  +-- percent (0x64 = 100)
    ///                                +----- payload length
    /// </summary>
    public class MchoseProvider : IDeviceProvider
    {
        private const ushort MchoseVid = 0x3554;
        private const byte ReportId = 0x13;
        private const byte CmdBattery = 0x4A;

        public string Name { get { return "MCHOSE HID"; } }

        public List<DeviceInfo> Discover()
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            List<HidDeviceInfo> all = HidNative.Enumerate();

            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();
            foreach (HidDeviceInfo h in all)
            {
                if (h.VendorId != MchoseVid) continue;
                if (h.UsagePage != 0xFF02) continue;      // vendor collection
                if (h.InputReportLength != 20) continue;
                if (h.OutputReportLength != 20) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;

                DeviceInfo d = new DeviceInfo();
                d.Id = "mchose-" + kv.Key;
                d.Brand = "MCHOSE";
                d.Model = ResolveModel(h.ProductId);
                d.Name = "MCHOSE " + d.Model;
                d.Kind = DeviceKind.Keyboard;
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
                byte[] cmd = new byte[20];
                cmd[0] = ReportId;
                cmd[1] = CmdBattery;
                cmd[2] = 0x01;
                cmd[3] = 0x00;
                cmd[19] = Checksum(cmd);

                using (HidSession s = new HidSession(d.HidPath, 20, 20))
                {
                    s.Flush(30);

                    byte[] resp = null;

                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);

                        if (!s.SetOutputReport(cmd))
                        {
                            r.Error = "发送失败（设备被占用？）";
                            return r;
                        }

                        resp = WaitFor(s, ReportId, CmdBattery, 700);
                    }

                    if (resp == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    r.Raw = Hex(resp);

                    int level = resp[5];
                    if (level > 100) level = 100;

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Online = true;
                    r.Detail = "MCHOSE HID";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        private static byte Checksum(byte[] buf)
        {
            int sum = 0;
            for (int i = 0; i < buf.Length - 1; i++) sum += buf[i];
            return (byte)(sum & 0xFF);
        }

        private static byte[] WaitFor(HidSession s, byte rid, byte cmd, int timeoutMs)
        {
            for (int i = 0; i < 12; i++)
            {
                byte[] p = s.Read(Math.Max(60, timeoutMs / 4));
                if (p == null) return null;
                if (p.Length >= 6 && p[0] == rid && p[1] == cmd) return p;
            }
            return null;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        private static string ResolveModel(ushort pid)
        {
            switch (pid)
            {
                case 0xFA09: return "K99 v2";
                default: return "PID_" + pid.ToString("X4");
            }
        }
    }
}
