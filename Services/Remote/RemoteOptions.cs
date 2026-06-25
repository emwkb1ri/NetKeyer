namespace NetKeyer.Services.Remote;

public static class RemoteDefaults
{
    public const int DefaultPort = 49920;
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 64 * 1024;
}

public class RemoteClientOptions
{
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; } = RemoteDefaults.DefaultPort;
    public string SharedToken { get; set; } = "";
    public int ReconnectDelayMs { get; set; } = 1000;
}

public class RemoteHostOptions
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = RemoteDefaults.DefaultPort;
    public int MaxClients { get; set; } = 5;
    public string SharedToken { get; set; } = "";
    public int ClientIdleTimeoutMs { get; set; } = 5000;
}
