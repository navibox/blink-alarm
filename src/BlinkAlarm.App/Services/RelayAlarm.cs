using System.IO;
using System.IO.Ports;
using BlinkAlarm.Core.Alarm;

namespace BlinkAlarm.App.Services;

/// <summary>
/// USB 继电器外接警报器（LCUS-1 / CH340）。继电器触点用于接通独立供电的警报器。
/// 所有方法都在界面线程调用；失败时返回错误信息而不是抛出异常。
/// </summary>
public sealed class RelayAlarm : IDisposable
{
    SerialPort? port;

    public bool IsConnected => port?.IsOpen == true;
    public string? PortName => port?.PortName;

    public static string[] GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name.Length).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>打开串口并确保继电器处于断开状态。</summary>
    /// <returns>错误信息；成功时为 null。</returns>
    public string? Connect(string portName)
    {
        if (IsConnected && string.Equals(PortName, portName, StringComparison.OrdinalIgnoreCase)) return null;
        Disconnect();
        try
        {
            port = new SerialPort(portName, RelayProtocol.BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 1000,
            };
            port.Open();
            return Set(false);
        }
        catch (Exception ex) when (IsPortError(ex))
        {
            Disconnect();
            return ex.Message;
        }
    }

    /// <summary>吸合（on）或断开继电器。</summary>
    /// <returns>错误信息；成功时为 null。</returns>
    public string? Set(bool on)
    {
        if (port?.IsOpen != true) return "继电器未连接";
        try
        {
            byte[] command = RelayProtocol.Command(on);
            port.Write(command, 0, command.Length);
            return null;
        }
        catch (Exception ex) when (IsPortError(ex))
        {
            // 多半是设备被拔掉：关闭串口，下次使用时重新连接。
            Disconnect(sendOff: false);
            return ex.Message;
        }
    }

    public void Disconnect(bool sendOff = true)
    {
        var current = port;
        port = null;
        if (current is null) return;
        try
        {
            if (sendOff && current.IsOpen)
            {
                byte[] off = RelayProtocol.Command(false);
                current.Write(off, 0, off.Length);
            }
        }
        catch (Exception ex) when (IsPortError(ex))
        {
        }
        try { current.Dispose(); } catch (IOException) { }
    }

    static bool IsPortError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or TimeoutException or ArgumentException;

    public void Dispose() => Disconnect();
}
