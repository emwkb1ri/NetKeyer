using System;
using NetKeyer.Services.Remote;
using NetKeyer.Services.Remote.Security;
using Xunit;

namespace NetKeyer.Tests;

public class SecurityNegotiationTests
{
    [Fact]
    public void EnsureVersionAccepted_AllowsExpectedVersion()
    {
        SimpleRemoteSecureSessionNegotiator.EnsureVersionAccepted(1, 1, "test-stage");
    }

    [Fact]
    public void EnsureVersionAccepted_RejectsDowngradeVersion()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            SimpleRemoteSecureSessionNegotiator.EnsureVersionAccepted(0, 1, "test-stage"));

        Assert.Contains("downgrade", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureVersionAccepted_RejectsUnsupportedUpgradeVersion()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            SimpleRemoteSecureSessionNegotiator.EnsureVersionAccepted(2, 1, "test-stage"));

        Assert.Contains("unsupported-upgrade", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSuiteAccepted_AllowsExactSuiteMatch()
    {
        SimpleRemoteSecureSessionNegotiator.EnsureSuiteAccepted(
            "P256+ECDSA+HKDF-SHA256+AES-GCM",
            "P256+ECDSA+HKDF-SHA256+AES-GCM",
            "test-stage");
    }

    [Fact]
    public void EnsureSuiteAccepted_RejectsSuiteMismatch()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            SimpleRemoteSecureSessionNegotiator.EnsureSuiteAccepted(
                "X25519+Ed25519+HKDF-SHA256+ChaCha20-Poly1305",
                "P256+ECDSA+HKDF-SHA256+AES-GCM",
                "test-stage"));

        Assert.Contains("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteClientServiceRelayValidation_RejectsPlaintextFrameWhenSecureEstablished()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            RemoteClientService.ValidateRelayFrameType(
                RemoteMessageType.Hello,
                relayCiphertextValidationEnabled: true,
                secureTransportEstablished: true));

        Assert.Contains("expected secure frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteClientServiceRelayValidation_AllowsSecureFrameWhenValidationEnabled()
    {
        RemoteClientService.ValidateRelayFrameType(
            RemoteMessageType.SecureFrame,
            relayCiphertextValidationEnabled: true,
            secureTransportEstablished: true);
    }

    [Fact]
    public void RemoteClientSessionRelayValidation_RejectsPlaintextFrameWhenRelayValidationEnabled()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            RemoteClientSession.ValidateRelayFrameType(
                RemoteMessageType.Heartbeat,
                isRelayTransport: true,
                validateRelayCiphertext: true,
                secureTransportEstablished: true));

        Assert.Contains("expected secure frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteClientSessionRelayValidation_AllowsPlaintextWhenNotRelay()
    {
        RemoteClientSession.ValidateRelayFrameType(
            RemoteMessageType.Heartbeat,
            isRelayTransport: false,
            validateRelayCiphertext: true,
            secureTransportEstablished: true);
    }
}
