using System.Collections.Generic;
using Voltide.Core;

namespace Voltide.Providers
{
    /// <summary>
    /// A protocol adapter. Add a new brand = add one class here.
    /// The UI never knows which brand it is talking to.
    /// </summary>
    public interface IDeviceProvider
    {
        string Name { get; }

        /// <summary>Find devices this provider can talk to.</summary>
        List<DeviceInfo> Discover();

        /// <summary>Read the current battery state of one device.</summary>
        DeviceReading Read(DeviceInfo device);
    }
}
