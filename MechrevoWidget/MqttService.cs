using System.Net.Sockets;
using System.Text;

namespace MechrevoWidget;

/// <summary>从 Fan/Status 提取的小组件关心的字段。</summary>
public sealed record FanStatus(
    string? OperatingMode,
    string? ProfileName,
    string? CpuPl1,
    string? CpuPl2,
    string? CpuPl4,
    string? GpuTgp,
    string? FanTable,
    string? Extreme,
    string? Silent);

/// <summary>
/// 与 GCUBridge broker(127.0.0.1:13688)通信的服务:
/// 订阅 Fan/Status 拿当前档位,向 Fan/Control 发切档命令,断线 5 秒后自动重连。
/// 协议与官方控制中心一致,与 python 脚本 mechrevo_mode.py 等效。
///
/// 内置一个只覆盖本项目所需子集的 MQTT 3.1.1 客户端(CONNECT / SUBSCRIBE / PUBLISH / PING),
/// 省掉 MQTTnet 依赖。GCUBridge 只接受 v3.1.1,不支持 v5 握手。
/// </summary>
public sealed class MqttService : IDisposable
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "mechrevo_widget.log");
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);
    private const ushort KeepAliveSec = 15;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private volatile bool _connected;
    private ushort _packetId;
    private Task? _loop;
    private bool _disposed;

    /// <summary>收到 Fan/Status(后台线程)。</summary>
    public event Action<FanStatus>? StatusUpdated;

    /// <summary>连接状态变化(后台线程)。</summary>
    public event Action<bool>? ConnectionChanged;

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [mqtt] {msg}\n"); }
        catch { }
    }

    public void Start()
    {
        Log("start");
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    // ───────────────────────── 连接循环 ─────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                Log("connected");
                _connected = true;
                ConnectionChanged?.Invoke(true);

                await SubscribeAsync("Fan/Status");
                await QueryStatusAsync();

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ping = PingLoopAsync(pingCts.Token);
                await ReadLoopAsync(ct); // 正常情况下一直阻塞到断线
                pingCts.Cancel();
                try { await ping; } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log("connection error: " + ex.GetBaseException().Message);
            }

            CloseSocket();
            if (_connected)
            {
                _connected = false;
                ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(Retry, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        CloseSocket();
        var tcp = new TcpClient { NoDelay = true };
        var connectTask = tcp.ConnectAsync(AppConfig.MqttHost, AppConfig.MqttPort);
        if (await Task.WhenAny(connectTask, Task.Delay(5000, ct)) != connectTask)
        {
            tcp.Close();
            throw new TimeoutException("TCP connect timeout");
        }
        await connectTask; // 抛出连接异常(如果有)
        _tcp = tcp;
        _stream = tcp.GetStream();

        // CONNECT:协议名 "MQTT",级别 4 (=3.1.1),标志 = 用户名|密码|CleanSession
        var body = new List<byte>();
        WriteString(body, "MQTT");
        body.Add(4);
        body.Add(0b1100_0010);
        body.Add((byte)(KeepAliveSec >> 8)); body.Add((byte)(KeepAliveSec & 0xFF));
        WriteString(body, AppConfig.ClientId);
        WriteString(body, AppConfig.Username);
        WriteString(body, AppConfig.Password);
        await SendAsync(0x10, body);

        // CONNACK:固定 4 字节 20 02 00 rc
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(5000);
        var (type, payload) = await ReadPacketAsync(timeout.Token);
        if (type != 0x20 || payload.Length < 2)
            throw new IOException($"unexpected packet 0x{type:X2} instead of CONNACK");
        if (payload[1] != 0)
            throw new IOException($"CONNACK refused, return code {payload[1]}");
    }

    private async Task SubscribeAsync(string topic)
    {
        var body = new List<byte>();
        ushort id = NextId();
        body.Add((byte)(id >> 8)); body.Add((byte)(id & 0xFF));
        WriteString(body, topic);
        body.Add(0); // 请求 QoS 0:只读状态,丢一条无所谓,下一条马上就来
        await SendAsync(0x82, body); // SUBSCRIBE 固定头必须是 0x82
        Log("subscribed " + topic);
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(KeepAliveSec * 2 / 3.0);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            await SendAsync(0xC0, new List<byte>()); // PINGREQ
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, payload) = await ReadPacketAsync(ct);
            if ((type & 0xF0) != 0x30) continue; // 只关心 PUBLISH;SUBACK/PUBACK/PINGRESP 忽略

            int qos = (type >> 1) & 0x03;
            int topicLen = (payload[0] << 8) | payload[1];
            string topic = Encoding.UTF8.GetString(payload, 2, topicLen);
            int offset = 2 + topicLen;
            if (qos > 0)
            {
                // broker 以 QoS1 下发时需要回 PUBACK
                byte hi = payload[offset], lo = payload[offset + 1];
                offset += 2;
                await SendAsync(0x40, new List<byte> { hi, lo });
            }

            if (topic != "Fan/Status") continue;
            var text = Encoding.UTF8.GetString(payload, offset, payload.Length - offset);
            var status = ParseFanStatus(text);
            if (status is not null)
            {
                try { StatusUpdated?.Invoke(status); }
                catch (Exception ex) { Log("StatusUpdated handler: " + ex.Message); }
            }
        }
    }

    // ───────────────────────── 报文读写 ─────────────────────────

    private async Task SendAsync(byte header, List<byte> body)
    {
        var s = _stream ?? throw new IOException("not connected");
        var pkt = new List<byte>(body.Count + 5) { header };
        int len = body.Count;
        do
        {
            byte b = (byte)(len % 128);
            len /= 128;
            if (len > 0) b |= 0x80;
            pkt.Add(b);
        } while (len > 0);
        pkt.AddRange(body);
        var bytes = pkt.ToArray();

        await _writeLock.WaitAsync();
        try { await s.WriteAsync(bytes, 0, bytes.Length); }
        finally { _writeLock.Release(); }
    }

    private async Task<(byte type, byte[] payload)> ReadPacketAsync(CancellationToken ct)
    {
        var s = _stream ?? throw new IOException("not connected");
        byte type = (await ReadExactAsync(s, 1, ct))[0];
        int len = 0, mul = 1;
        while (true)
        {
            byte b = (await ReadExactAsync(s, 1, ct))[0];
            len += (b & 0x7F) * mul;
            if ((b & 0x80) == 0) break;
            mul *= 128;
            if (mul > 128 * 128 * 128) throw new IOException("bad remaining length");
        }
        var payload = len > 0 ? await ReadExactAsync(s, len, ct) : Array.Empty<byte>();
        return (type, payload);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        // NetworkStream 在 net48 上不理会 CancellationToken,取消时靠关闭 socket 打断读取
        using (ct.Register(() => { try { s.Close(); } catch { } }))
        {
            while (got < n)
            {
                int r;
                try { r = await s.ReadAsync(buf, got, n - got, ct); }
                catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                if (r == 0) throw new IOException("connection closed by broker");
                got += r;
            }
        }
        return buf;
    }

    private static void WriteString(List<byte> buf, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        buf.Add((byte)(b.Length >> 8));
        buf.Add((byte)(b.Length & 0xFF));
        buf.AddRange(b);
    }

    private ushort NextId()
    {
        _packetId = (ushort)(_packetId % 65535 + 1);
        return _packetId;
    }

    private void CloseSocket()
    {
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
    }

    // ───────────────────────── 业务命令 ─────────────────────────

    private static FanStatus? ParseFanStatus(string json)
    {
        var r = MiniJson.ParseObject(json);
        if (r is null) return null;
        return new FanStatus(
            r.GetString("OperatingMode"), r.GetString("ProfileName"), r.GetString("CPU_PL1"), r.GetString("CPU_PL2"),
            r.GetString("CPU_PL4"), r.GetString("GPU_ConfigurableTGPTarget"), r.GetString("FAN_TableName"),
            r.GetString("Extreme"), r.GetString("Silent"));
    }

    public Task QueryStatusAsync() => PublishAsync("Fan/Control", "{\"Action\":\"GETSTATUS\"}");

    /// <summary>切换狂暴档的子模式:true=高能狂暴,false=静音狂暴(仅在狂暴档下有意义)。</summary>
    public Task SwitchTurboSubModeAsync(bool extreme) => PublishAsync("Fan/Control", extreme
        ? "{\"Action\":\"SET_CPU_CORE_OFFSET_EXTREME\",\"EXTREME\":1}"
        : "{\"Action\":\"SET_CPU_CORE_OFFSET_SILENT\",\"SILENT\":1}");

    public Task SwitchModeAsync(string modeKey)
    {
        var action = modeKey switch
        {
            "office" => "OPERATING_OFFICE_MODE",
            "gaming" => "OPERATING_GAMING_MODE",
            "turbo" => "OPERATING_TURBO_MODE",
            "custom" => "OPERATING_CUSTOM_MODE",
            _ => throw new ArgumentException($"未知档位: {modeKey}", nameof(modeKey)),
        };
        var pi = AppConfig.GetProfileIndex(modeKey);
        return PublishAsync("Fan/Control", $"{{\"Action\":\"{action}\",\"ProfileIndex\":\"{pi}\"}}");
    }

    /// <summary>以 QoS 1 发布(与官方客户端一致);未连接时静默丢弃。</summary>
    private async Task PublishAsync(string topic, string payload)
    {
        if (!_connected) return;
        try
        {
            var body = new List<byte>();
            WriteString(body, topic);
            ushort id = NextId();
            body.Add((byte)(id >> 8)); body.Add((byte)(id & 0xFF));
            body.AddRange(Encoding.UTF8.GetBytes(payload));
            await SendAsync(0x32, body); // PUBLISH, QoS 1
        }
        catch (Exception ex) { Log("publish failed: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try
        {
            if (_stream is not null) SendAsync(0xE0, new List<byte>()).Wait(500); // DISCONNECT
        }
        catch { }
        CloseSocket();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
