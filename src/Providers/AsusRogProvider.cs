using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ASUS ROG keyboards that talk through the ROG OMNI RECEIVER (2.4G dongle).
    ///
    /// Protocol reverse-engineered from Armoury Crate's USB traffic (2026-10-24):
    ///   64-byte frame written to the vendor collection (UsagePage 0xFF00),
    ///   answered on the same collection's interrupt-IN endpoint.
    ///
    ///   query : 02 12 01 00 00 ...            (64 bytes)
    ///   reply : 02 12 01 00 00 00 30 02 01 00 14 30 ED 0E ...
    ///                             ^^                ^^  ^^^^^
    ///                             |                 |   +-- voltage, little endian (mV)
    ///                             |                 +------ low-battery threshold (%)
    ///                             +------------------------ charge percent
    ///
    /// Verified against Armoury Crate on a ROG Azoth Extreme: reply byte 6 read 0x30
    /// while the UI showed 48%, and byte 10 read 0x14 (the 20% warning level).
    ///
    /// The same dongle also answers 02 12 14 02 with the serial number, which is a
    /// useful sanity check but not needed for the reading.
    /// </summary>
    public class AsusRogProvider : IDeviceProvider
    {
        private const ushort AsusVid = 0x0B05;
        private const ushort OmniReceiverPid = 0x1ACE;
        private const ushort VendorPage = 0xFF00;

        private const byte Magic = 0x02;
        private const byte Family = 0x12;
        private const byte CmdStatus = 0x01;

        private const int FrameSize = 64;
        private const int BatteryIndex = 6;

        public string Name { get { return "ROG HID"; } }

        public List<DeviceInfo> Discover()
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            foreach (HidDeviceInfo h in HidNative.Enumerate())
            {
                if (h.VendorId != AsusVid) continue;
                if (h.ProductId != OmniReceiverPid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.InputReportLength < FrameSize) continue;
                if (h.OutputReportLength < FrameSize) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "asus-" + h.ProductId.ToString("X4");
                d.Brand = "ROG";
                d.Model = "AZOTH EX";
                d.Name = "ROG AZOTH EXTREME";
                d.ShortModel = "AZOTH";
                d.ProviderName = Name;
                d.HidPath = h.Path;

                found.Add(d);
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;

            try
            {
                // readOnly must be false: the first query is a write, and a read-only
                // handle fails it with no diagnostic at all.
                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize, false))
                {
                    s.Flush(30);

                    byte[] req = new byte[FrameSize];
                    req[0] = Magic;
                    req[1] = Family;
                    req[2] = CmdStatus;

                    if (!s.Write(req, 500))
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    byte[] resp = s.Read(1500);

                    if (resp == null || resp.Length <= BatteryIndex)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    if (resp[0] != Magic || resp[1] != Family || resp[2] != CmdStatus)
                    {
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    int percent = resp[BatteryIndex];
                    if (percent < 0 || percent > 100)
                    {
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    r.Raw = BitConverter.ToString(resp, 0, 14);
                    r.Online = true;
                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);
                    r.Detail = "ROG HID";

                    // The receiver reports whether the keyboard is charging on its own
                    // channel; the status reply does not carry it, so leave it false.
                    r.Charging = false;

                    return r;
                }
            }
            catch (Exception ex)
            {
                r.Sleeping = true;
                r.Error = "设备休眠";
                r.Detail = ex.Message;
                return r;
            }
        }
    }
}
