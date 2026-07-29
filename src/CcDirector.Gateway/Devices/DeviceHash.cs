using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Gateway.Devices;

/// <summary>
/// The one-way hash that identifies a DEVICE in stored data without storing the device's credential.
///
/// It lived in the Car Mode timing-diagnostics file, because that is what first needed to group records by
/// device. Car Mode was removed from the product (#1028) and those diagnostics went with it; the hash did not,
/// because the browser error channel partitions by it too. It is here now, named for what it is.
/// </summary>
public static class DeviceHash
{
    /// <summary>A short, stable, one-way hash of a device credential (first 12 hex of its SHA-256), so one
    ///  device's records group together without the raw credential ever being stored or logged (DT-05). A
    ///  blank credential (auth-off debug) maps to a fixed "anonymous" bucket.</summary>
    public static string Of(string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential)) return "anonymous";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        var sb = new StringBuilder(12);
        for (var i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
