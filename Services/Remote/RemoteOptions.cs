using System;

namespace NetKeyer.Services.Remote;

public static class RemoteDefaults
{
    public const int DefaultPort = 49923;
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 64 * 1024;
    public const int DefaultClientHoldMs = 1000;
    public const int MinClientHoldMs = 500;
    public const int MaxClientHoldMs = 30000;
    public const int DefaultStaleFrameDropMs = 750;
}

public class RemoteClientOptions
{
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; } = RemoteDefaults.DefaultPort;
    public string SharedToken { get; set; } = "";
    public string Callsign { get; set; } = "";
    public int ReconnectDelayMs { get; set; } = 1000;
    public string RelaySessionId { get; set; } = "";
    public string RelayRole { get; set; } = "";
    public bool EnableSecureTransport { get; set; }
    public bool RequireSecureTransport { get; set; }
    public bool ValidateRelayCiphertext { get; set; }
}

public class RemoteHostOptions
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = RemoteDefaults.DefaultPort;
    public int MaxClients { get; set; } = 5;
    public string SharedToken { get; set; } = "";
    public string HostName { get; set; } = Environment.MachineName;
    public int ClientIdleTimeoutMs { get; set; } = 5000;
    public int ActiveClientHoldMs { get; set; } = RemoteDefaults.DefaultClientHoldMs;
    public int StaleFrameDropMs { get; set; } = RemoteDefaults.DefaultStaleFrameDropMs;
    public bool UseSenderTickStaleGate { get; set; } = false;
    public string RelaySessionId { get; set; } = "";
    public string RelayRole { get; set; } = "";
    public bool EnableSecureTransport { get; set; }
    public bool RequireSecureTransport { get; set; }
    public bool ValidateRelayCiphertext { get; set; }
}
