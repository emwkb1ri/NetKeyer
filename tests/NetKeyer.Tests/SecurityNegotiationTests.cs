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
    public void RemoteClientServiceCiphertextValidation_RejectsPlaintextFrameWhenSecureEstablished()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            RemoteClientService.ValidateCiphertextFrameType(
                RemoteMessageType.Hello,
                ciphertextValidationEnabled: true,
                secureTransportEstablished: true));

        Assert.Contains("expected secure frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteClientServiceCiphertextValidation_AllowsSecureFrameWhenValidationEnabled()
    {
        RemoteClientService.ValidateCiphertextFrameType(
            RemoteMessageType.SecureFrame,
            ciphertextValidationEnabled: true,
            secureTransportEstablished: true);
    }

    [Fact]
    public void RemoteClientSessionCiphertextValidation_RejectsPlaintextFrameWhenValidationEnabled()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            RemoteClientSession.ValidateCiphertextFrameType(
                RemoteMessageType.Heartbeat,
                validateCiphertext: true,
                secureTransportEstablished: true));

        Assert.Contains("expected secure frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteClientSessionCiphertextValidation_AllowsPlaintextWhenValidationDisabled()
    {
        RemoteClientSession.ValidateCiphertextFrameType(
            RemoteMessageType.Heartbeat,
            validateCiphertext: false,
            secureTransportEstablished: true);
    }

    [Fact]
    public void UserFacingSecurityDiagnostic_MapsHandshakeFailureToSafeMessage()
    {
        string message = RemoteClientService.BuildUserFacingSecurityDiagnostic(
            "Secure transport handshake failed: downgrade detected in host response");

        Assert.Contains("Security policy blocked", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("downgrade", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserFacingSecurityDiagnostic_MapsCiphertextFailureToSafeMessage()
    {
        string message = RemoteClientService.BuildUserFacingSecurityDiagnostic(
            "Ciphertext validation failed: expected secure frame, received 'Hello'.");

        Assert.Contains("encrypted frame requirements", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("received 'Hello'", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserFacingSecurityDiagnostic_MapsTokenAuthFailureToActionableMessage()
    {
        string message = RemoteClientService.BuildUserFacingSecurityDiagnostic(
            "Connection refused: shared token mismatch");

        Assert.Contains("Authentication failed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shared token", message, StringComparison.OrdinalIgnoreCase);
    }
}
