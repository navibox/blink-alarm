namespace BlinkAlarm.Core.Alarm;

/// <summary>
/// LCUS 系列 USB 继电器（CH340 串口，9600bps）的控制指令：
/// 起始字节 0xA0、通道号、状态（1 吸合 / 0 断开）、前三字节之和的低 8 位。
/// </summary>
public static class RelayProtocol
{
    public const int BaudRate = 9600;

    public static byte[] Command(bool on, int channel = 1)
    {
        if (channel is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channel));
        byte state = on ? (byte)1 : (byte)0;
        return new byte[] { 0xA0, (byte)channel, state, (byte)(0xA0 + channel + state) };
    }
}
