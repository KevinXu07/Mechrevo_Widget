using System.Runtime.InteropServices;

namespace MechrevoWidget;

/// <summary>
/// 直接读机械革命 UWACPIDriver 的 EC 寄存器(温度/风扇/电池/功耗墙),无需官方服务。
/// 需要管理员。寄存器地址见 README「EC 寄存器」一节;已在 AMD 机型上实测核对。
/// </summary>
public sealed class EcReader : IDisposable
{
    public const uint IOCTL_EC_READ = 0x9C40A488u;
    private const uint FILE_GENERIC_READ_WRITE = 0xC0000000;
    private const uint FILE_SHARE_ALL = 3;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint access, uint share,
        IntPtr sa, uint creation, uint flags, IntPtr template_);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr dev, uint code, byte[] inp, uint inSize,
        byte[] outp, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    // EC 寄存器
    private const int EC_CPU_TEMP = 0x43E;
    private const int EC_GPU_TEMP = 0x44F;
    private const int EC_FAN1_HI = 0x464, EC_FAN1_LO = 0x465;
    // 副风扇字节序与主风扇相反:高字节在 0x46C(ECSpec BYTE1=1132),低字节在 0x46B
    private const int EC_FAN2_HI = 0x46C, EC_FAN2_LO = 0x46B;
    private const int EC_FAN1_PWM = 0x75B, EC_FAN2_PWM = 0x75C;
    private const int EC_BATT_SOC = 0x4AB;
    private const int EC_BATT_CYC_LO = 0x4A6, EC_BATT_CYC_HI = 0x4A7;
    private const int EC_APL1 = 0x783, EC_APL2 = 0x784, EC_APL4 = 0x785;

    private IntPtr _h = IntPtr.Zero;
    private bool _triedOpen;
    private static readonly EcReader _inst = new();
    public static EcReader Instance => _inst;
    private EcReader() { }

    public bool IsAvailable { get; private set; }

    public bool Open()
    {
        if (_h != IntPtr.Zero) return true;
        if (_triedOpen) return IsAvailable;
        _triedOpen = true;
        _h = CreateFileW(@"\\.\ACPIDriver", FILE_GENERIC_READ_WRITE, FILE_SHARE_ALL,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        IsAvailable = _h != new IntPtr(-1) && _h != IntPtr.Zero;
        if (!IsAvailable) _h = IntPtr.Zero;
        return IsAvailable;
    }

    public byte? ReadByte(int addr)
    {
        if (_h == IntPtr.Zero) return null;
        var buf = new byte[8];
        BitConverter.GetBytes(addr).CopyTo(buf, 0);
        return DeviceIoControl(_h, IOCTL_EC_READ, buf, 8, buf, 8, out _, IntPtr.Zero) ? buf[0] : null;
    }

    private int? ReadWord(int hi, int lo)
    {
        var h = ReadByte(hi); var l = ReadByte(lo);
        return h is byte hb && l is byte lb ? (hb << 8) | lb : null;
    }

    /// <summary>风扇转速:超出物理范围(笔记本风扇 &lt; 10000 rpm)的读数视为毛刺丢弃。</summary>
    private int? ReadRpm(int hi, int lo) => ReadWord(hi, lo) is int v && v < 10000 ? v : null;

    /// <summary>读取全部遥测;任一失败则对应字段为 null。</summary>
    public Telemetry Read()
    {
        int? cpu = ReadByte(EC_CPU_TEMP);
        int? gpu = ReadByte(EC_GPU_TEMP);
        int? rpm1 = ReadRpm(EC_FAN1_HI, EC_FAN1_LO);
        int? rpm2 = ReadRpm(EC_FAN2_HI, EC_FAN2_LO);
        int? pwm1 = ReadByte(EC_FAN1_PWM);
        int? pwm2 = ReadByte(EC_FAN2_PWM);
        int? soc = ReadByte(EC_BATT_SOC);
        int? cyc = ReadWord(EC_BATT_CYC_HI, EC_BATT_CYC_LO);
        int? pl1 = ReadByte(EC_APL1), pl2 = ReadByte(EC_APL2), pl4 = ReadByte(EC_APL4);
        return new Telemetry(cpu, gpu, rpm1, rpm2, pwm1, pwm2, soc, cyc, pl1, pl2, pl4);
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero && _h != new IntPtr(-1)) CloseHandle(_h);
        _h = IntPtr.Zero;
    }

    public readonly record struct Telemetry(
        int? CpuTemp, int? GpuTemp,
        int? Fan1Rpm, int? Fan2Rpm,
        int? Fan1Pwm, int? Fan2Pwm,
        int? BattSoc, int? BattCycles,
        int? Pl1, int? Pl2, int? Pl4);
}
