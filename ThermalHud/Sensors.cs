using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ThermalHud;

internal sealed class SensorSnapshot
{
    public string CpuVendor { get; init; } = "未知厂商";
    public string GpuVendor { get; init; } = "未知厂商";
    public float? CpuTemperature { get; init; }
    public float? GpuTemperature { get; init; }
    public string? GpuTemperatureSource { get; init; }
    public FanReading[] Fans { get; init; } = [];
}

internal sealed class FanReading
{
    public string Name { get; init; } = "";
    public float Rpm { get; init; }
}

internal sealed class SensorReader : IDisposable
{
    private readonly string _cpuVendor = ReadCpuVendor();
    private readonly GraphicsDevice _graphics = new();
    private readonly AsusSensors _asus = new();
    private readonly NvidiaTemperature _nvidia = new();

    public SensorSnapshot Read()
    {
        float? firmwareGpu = Temperature(_asus.ReadValue(AsusSensors.GpuTemperature));
        float? driverGpu = null;
        if (_graphics.Vendor == "NVIDIA")
        {
            // A direct sensor query can wake a sleeping discrete GPU. Windows reports
            // the most recent power state separately, without querying NVIDIA telemetry.
            bool? powered = _graphics.IsNvidiaPowered();
            if (powered == true || (powered is null && !firmwareGpu.HasValue))
                driverGpu = _nvidia.Read();
            else
                _nvidia.Dispose();
        }

        return new SensorSnapshot
        {
            CpuVendor = _cpuVendor,
            GpuVendor = _graphics.Vendor,
            CpuTemperature = Temperature(_asus.ReadValue(AsusSensors.CpuTemperature)),
            GpuTemperature = driverGpu ?? firmwareGpu,
            GpuTemperatureSource = driverGpu.HasValue ? "显卡核心 · NVIDIA 驱动" :
                firmwareGpu.HasValue ? "华硕固件 · GPU 温度" : null,
            Fans = _asus.ReadFans()
        };
    }

    private static float? Temperature(int? value) =>
        value is > 0 and < 125 ? value.Value : null;

    private static string ReadCpuVendor()
    {
        var vendor = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "VendorIdentifier", null) as string;

        return vendor switch
        {
            "GenuineIntel" => "Intel",
            "AuthenticAMD" or "AMDisbetter!" => "AMD",
            "CentaurHauls" => "VIA",
            "  Shanghai  " => "兆芯",
            "HygonGenuine" => "海光",
            _ => "未知厂商"
        };
    }

    public void Dispose()
    {
        _nvidia.Dispose();
        _asus.Dispose();
    }
}

internal sealed class GraphicsDevice
{
    private const string DisplaySetupClass = "{4D36E968-E325-11CE-BFC1-08002BE10318}";
    private const uint PresentInClass = 0x100 | 0x200;
    private const uint BinaryProperty = 0x1003;
    private static readonly DevicePropertyKey PowerData = new()
    {
        Category = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        Id = 32
    };

    private readonly uint? _nvidiaNode;
    private readonly byte[] _powerData = new byte[56];
    public string Vendor { get; } = "未知厂商";

    public GraphicsDevice()
    {
        if (CM_Get_Device_ID_List_SizeW(out int size, DisplaySetupClass, PresentInClass) != 0 || size < 2)
            return;

        var ids = new char[size];
        if (CM_Get_Device_ID_ListW(DisplaySetupClass, ids, ids.Length, PresentInClass) != 0)
            return;

        int nvidiaCount = 0;
        string? nvidiaId = null;
        foreach (string id in new string(ids).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // PCI IDs exclude remote/virtual adapters and include a headless hybrid dGPU.
            if (id.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase))
            {
                nvidiaCount++;
                nvidiaId = id;
            }
            else if (id.StartsWith(@"PCI\VEN_1002&", StringComparison.OrdinalIgnoreCase))
                Vendor = "AMD";
            else if (Vendor != "AMD" &&
                     id.StartsWith(@"PCI\VEN_8086&", StringComparison.OrdinalIgnoreCase))
                Vendor = "Intel";
        }

        if (nvidiaId is not null)
            Vendor = "NVIDIA";
        // NVML index zero and the PnP node only unambiguously match with one NVIDIA adapter.
        if (nvidiaCount == 1 && CM_Locate_DevNodeW(out uint node, nvidiaId!, 0) == 0)
            _nvidiaNode = node;
    }

    public bool? IsNvidiaPowered()
    {
        if (_nvidiaNode is not uint node)
            return null;

        int size = _powerData.Length;
        if (CM_Get_DevNode_PropertyW(node, in PowerData, out uint type, _powerData, ref size, 0) != 0 ||
            type != BinaryProperty || size < 8)
            return null;

        // CM_POWER_DATA.PD_MostRecentPowerState follows its 32-bit size field.
        uint state = BinaryPrimitives.ReadUInt32LittleEndian(_powerData.AsSpan(4));
        return state switch { 1 => true, 2 or 3 or 4 => false, _ => null };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevicePropertyKey
    {
        public Guid Category;
        public uint Id;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int size, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_ID_ListW(
        string filter, [Out] char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint node, in DevicePropertyKey key, out uint type, [Out] byte[] buffer,
        ref int size, uint flags);
}

internal sealed class AsusSensors : IDisposable
{
    // ASUS System Control Interface read protocol and sensor IDs:
    // https://github.com/seerge/g-helper/blob/main/app/AsusACPI.cs
    private const uint AcpiRequest = 0x0022240C;
    private const uint ReadStatus = 0x53545344; // DSTS
    private const uint PresentBit = 0x00010000;
    private const uint UnknownBit = 0x00080000;
    internal const uint CpuTemperature = 0x00120094;
    internal const uint GpuTemperature = 0x00120097;
    private const uint CpuFan = 0x00110013;
    private const uint GpuFan = 0x00110014;
    private const uint MidFan = 0x00110031;

    private readonly byte[] _request = new byte[16];
    private readonly byte[] _response = new byte[16];
    // The read request works without requesting read/write access to the driver handle.
    private readonly SafeFileHandle _handle = CreateFile(
        @"\\.\ATKACPI", 0, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);

    public AsusSensors()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_request, ReadStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(_request.AsSpan(4), 8);
    }

    public int? ReadValue(uint sensor)
    {
        if (_handle.IsInvalid)
            return null;

        BinaryPrimitives.WriteUInt32LittleEndian(_request.AsSpan(8), sensor);
        if (!DeviceIoControl(_handle, AcpiRequest, _request, _request.Length,
                _response, _response.Length, out int bytesReturned, IntPtr.Zero) || bytesReturned < 4)
            return null;

        uint status = BinaryPrimitives.ReadUInt32LittleEndian(_response);
        if ((status & (PresentBit | UnknownBit)) != PresentBit)
            return null;

        return (int)(status & 0xFFFF);
    }

    public FanReading[] ReadFans()
    {
        if (_handle.IsInvalid)
            return [];

        var fans = new List<FanReading>(3);
        AddFan(fans, CpuFan, "CPU 风扇");
        AddFan(fans, GpuFan, "GPU 风扇");
        AddFan(fans, MidFan, "机身风扇");
        return fans.Count == 0 ? [] : fans.ToArray();
    }

    private void AddFan(List<FanReading> fans, uint sensor, string name)
    {
        // This firmware supplies the measured fan speed in hundreds of RPM.
        if (ReadValue(sensor) is int speed and <= 120)
            fans.Add(new FanReading { Name = name, Rpm = speed * 100 });
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll")]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle, uint controlCode, byte[] input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);
}

internal sealed class NvidiaTemperature : IDisposable
{
    private const string Library = "nvml.dll";
    private bool _unavailable;
    private bool _initialized;
    private IntPtr _device;
    private long _retryAt;

    public float? Read()
    {
        if (_unavailable || Environment.TickCount64 < _retryAt)
            return null;

        try
        {
            if (!_initialized)
                _initialized = nvmlInit_v2() == 0;

            if (_initialized &&
                (_device != IntPtr.Zero || nvmlDeviceGetHandleByIndex_v2(0, out _device) == 0) &&
                nvmlDeviceGetTemperature(_device, 0, out uint temperature) == 0 &&
                temperature is > 0 and < 125)
                return temperature;

            _retryAt = Environment.TickCount64 + 10_000;
            Dispose();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException)
        {
            _unavailable = true;
            Dispose();
        }

        return null;
    }

    public void Dispose()
    {
        _device = IntPtr.Zero;
        if (!_initialized)
            return;

        _initialized = false;
        try { nvmlShutdown(); }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlInit_v2();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temperature);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlShutdown();
}
