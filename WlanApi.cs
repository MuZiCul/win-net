using System.Runtime.InteropServices;

namespace WinNetFix;

/// <summary>
/// Windows WLAN API（wlanapi.dll）P/Invoke 封装。
/// 参照 emoacht/ManagedNativeWifi（经大量实践验证的正确结构）实现：
/// - 查询 radio 状态返回 WLAN_RADIO_STATE = {dwNumberOfPhys(4)} + N * WLAN_PHY_RADIO_STATE(12)
/// - 设置 radio 状态用 WLAN_PHY_RADIO_STATE = {dwPhyIndex(4), dot11SoftwareRadioState(4), dot11HardwareRadioState(4)}
/// 用于真正打开/关闭 WiFi radio 软开关（Software Off）。
/// </summary>
public static class WlanApi
{
    private const int WLAN_OPCODE_RADIO_STATE = 4; // wlan_intf_opcode_radio_state
    private const uint WLAN_CLIENT_VERSION_XP_SP2 = 2;

    // WLAN_INTERFACE_INFO_LIST = { dwNumberOfItems(4); dwIndex(4); WLAN_INTERFACE_INFO[1] }
    // WLAN_INTERFACE_INFO = { GUID(16); WCHAR[256](512); WLAN_INTERFACE_STATE(4) } = 532
    private const int LIST_HEADER_SIZE = 8;
    private const int INTERFACE_INFO_SIZE = 16 + 256 * 2 + 4; // 532

    // WLAN_PHY_RADIO_STATE = { dwPhyIndex(4); DOT11_RADIO_STATE dot11SoftwareRadioState(4); DOT11_RADIO_STATE dot11HardwareRadioState(4) }
    private const int PHY_RADIO_STATE_SIZE = 12;

    // DOT11_RADIO_STATE 枚举：0=unknown, 1=on, 2=off
    private const uint DOT11_RADIO_STATE_ON = 1;
    private const uint DOT11_RADIO_STATE_OFF = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;
        public uint IsState;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int OpCode, uint dwDataSize, IntPtr pData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    /// <summary>查询指定接口名的 radio 状态（软件开关）。true=开, false=关, null=失败。</summary>
    public static bool? GetRadioState(string interfaceName)
    {
        if (!TryOpenHandle(out var h)) return null;
        try
        {
            if (!TryFindGuid(h, interfaceName, out var guid)) return null;

            if (WlanQueryInterface(h, ref guid, WLAN_OPCODE_RADIO_STATE, IntPtr.Zero, out _, out var pData, IntPtr.Zero) != 0)
                return null;

            try
            {
                var numPhys = (uint)Marshal.ReadInt32(pData);
                // 任一 PHY 的软件状态为 On 即视为开（全部 Off 才关）
                for (int i = 0; i < (int)numPhys && i < 64; i++)
                {
                    var sw = (uint)Marshal.ReadInt32(pData, 4 + i * PHY_RADIO_STATE_SIZE + 4);
                    if (sw == DOT11_RADIO_STATE_ON) return true;
                }
                return false;
            }
            finally
            {
                WlanFreeMemory(pData);
            }
        }
        finally
        {
            WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    /// <summary>设置指定接口名的 radio 软件开关。true=设置成功。</summary>
    public static bool SetRadioState(string interfaceName, bool on)
    {
        if (!TryOpenHandle(out var h)) return false;
        try
        {
            if (!TryFindGuid(h, interfaceName, out var guid)) return false;

            // 参照 ManagedNativeWifi：传单个 WLAN_PHY_RADIO_STATE（12 字节）:
            //   { dwPhyIndex(4)=0, dot11SoftwareRadioState(4)=on/off, dot11HardwareRadioState(4)=0 }
            // 不要传整个数组（WlanSetInterface 只接受单个 PHY 状态）
            var buf = new byte[PHY_RADIO_STATE_SIZE];
            BitConverter.GetBytes(0u).CopyTo(buf, 0);                                 // dwPhyIndex = 0
            BitConverter.GetBytes(on ? DOT11_RADIO_STATE_ON : DOT11_RADIO_STATE_OFF)
                .CopyTo(buf, 4);                                                      // dot11SoftwareRadioState
            BitConverter.GetBytes(0u).CopyTo(buf, 8);                                 // dot11HardwareRadioState = 0

            var ptr = Marshal.AllocHGlobal(buf.Length);
            try
            {
                Marshal.Copy(buf, 0, ptr, buf.Length);
                var ret = WlanSetInterface(h, ref guid, WLAN_OPCODE_RADIO_STATE, (uint)buf.Length, ptr, IntPtr.Zero);
                return ret == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        finally
        {
            WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    /// <summary>诊断：设置 radio 并返回错误码字符串。</summary>
    public static string SetRadioStateDiag(string interfaceName, bool on)
    {
        try
        {
            if (!TryOpenHandle(out var h)) return "open handle failed";
            try
            {
                if (!TryFindGuid(h, interfaceName, out var guid)) return "find guid failed";

                var buf = new byte[PHY_RADIO_STATE_SIZE];
                BitConverter.GetBytes(0u).CopyTo(buf, 0);
                BitConverter.GetBytes(on ? DOT11_RADIO_STATE_ON : DOT11_RADIO_STATE_OFF).CopyTo(buf, 4);
                BitConverter.GetBytes(0u).CopyTo(buf, 8);

                var ptr = Marshal.AllocHGlobal(buf.Length);
                try
                {
                    Marshal.Copy(buf, 0, ptr, buf.Length);
                    var ret = WlanSetInterface(h, ref guid, WLAN_OPCODE_RADIO_STATE, (uint)buf.Length, ptr, IntPtr.Zero);
                    return $"ret=0x{ret:X8} ({ret})";
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
        catch (Exception ex) { return "exception: " + ex.Message; }
    }

    /// <summary>从接口列表中找到指定接口名对应的 GUID。</summary>
    private static bool TryFindGuid(IntPtr h, string interfaceName, out Guid guid)
    {
        guid = Guid.Empty;
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var pList) != 0) return false;
        try
        {
            var count = (uint)Marshal.ReadInt32(pList);
            var basePtr = IntPtr.Add(pList, LIST_HEADER_SIZE);
            for (int i = 0; i < (int)count && i < 64; i++)
            {
                var pInfo = IntPtr.Add(basePtr, i * INTERFACE_INFO_SIZE);
                var descPtr = IntPtr.Add(pInfo, 16);
                var desc = Marshal.PtrToStringUni(descPtr, 256)?.TrimEnd('\0');
                if (string.IsNullOrEmpty(desc)) continue;

                var name = interfaceName ?? "";
                bool descIsWireless = desc.Contains("wireless", StringComparison.OrdinalIgnoreCase)
                                   || desc.Contains("wlan", StringComparison.OrdinalIgnoreCase)
                                   || desc.Contains("802.11", StringComparison.OrdinalIgnoreCase)
                                   || desc.Contains("wi-fi", StringComparison.OrdinalIgnoreCase);
                bool match = name.Length >= 3 && (
                        desc.Contains(name, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(desc.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase));
                if (match || (descIsWireless && i == 0))
                {
                    guid = Marshal.PtrToStructure<Guid>(pInfo);
                    return true;
                }
            }
            return false;
        }
        finally
        {
            WlanFreeMemory(pList);
        }
    }

    private static bool TryOpenHandle(out IntPtr h)
    {
        h = IntPtr.Zero;
        uint negotiated;
        return WlanOpenHandle(WLAN_CLIENT_VERSION_XP_SP2, IntPtr.Zero, out negotiated, out h) == 0;
    }
}
