using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WireRoute.RouterOS;

public sealed class RouterOSServerCertificate : IEquatable<RouterOSServerCertificate>
{
    private readonly byte[] derEncodedCertificate;

    public RouterOSServerCertificate(string host, int port, ReadOnlySpan<byte> derEncodedCertificate)
    {
        Host = host.ToLowerInvariant();
        Port = port;
        this.derEncodedCertificate = derEncodedCertificate.ToArray();
        FingerprintSha256 = string.Join(
            ':',
            SHA256.HashData(this.derEncodedCertificate).Select(value => value.ToString("X2")));

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(this.derEncodedCertificate);
            SubjectSummary = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(SubjectSummary))
            {
                SubjectSummary = null;
            }
        }
        catch (CryptographicException)
        {
            SubjectSummary = null;
        }
    }

    public string Host { get; }

    public int Port { get; }

    public ReadOnlyMemory<byte> DerEncodedCertificate => derEncodedCertificate;

    public string FingerprintSha256 { get; }

    public string? SubjectSummary { get; }

    public bool Matches(string host, int port, ReadOnlySpan<byte> derEncodedCertificate) =>
        Host.Equals(host, StringComparison.OrdinalIgnoreCase)
        && Port == port
        && derEncodedCertificate.SequenceEqual(this.derEncodedCertificate);

    public bool Equals(RouterOSServerCertificate? other) =>
        other is not null && Matches(other.Host, other.Port, other.derEncodedCertificate);

    public override bool Equals(object? obj) => Equals(obj as RouterOSServerCertificate);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Host, StringComparer.OrdinalIgnoreCase);
        hash.Add(Port);
        foreach (var value in derEncodedCertificate)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

public abstract class RouterOSTlsCertificateException : AuthenticationException
{
    protected RouterOSTlsCertificateException(string message, RouterOSServerCertificate receivedCertificate)
        : base(message)
    {
        ReceivedCertificate = receivedCertificate;
    }

    public RouterOSServerCertificate ReceivedCertificate { get; }
}

public sealed class RouterOSUntrustedCertificateException : RouterOSTlsCertificateException
{
    public RouterOSUntrustedCertificateException(RouterOSServerCertificate receivedCertificate)
        : base("RouterOS presented a certificate that is not trusted by this PC.", receivedCertificate)
    {
    }
}

public sealed class RouterOSChangedCertificateException : RouterOSTlsCertificateException
{
    public RouterOSChangedCertificateException(
        string expectedFingerprint,
        RouterOSServerCertificate receivedCertificate)
        : base("The RouterOS certificate has changed since it was trusted.", receivedCertificate)
    {
        ExpectedFingerprint = expectedFingerprint;
    }

    public string ExpectedFingerprint { get; }
}

public sealed class RouterOSTlsConnectionException : AuthenticationException
{
    public RouterOSTlsConnectionException(Exception innerException)
        : base(
            "RouterOS ended the TLS handshake before presenting a certificate. "
            + "Verify that the www-ssl service has a certificate assigned and supports TLS 1.2 or newer.",
            innerException)
    {
    }
}

internal sealed class RouterOSCertificateValidator
{
    private readonly string host;
    private readonly int port;
    private readonly RouterOSServerCertificate? trustedCertificate;
    private readonly object failureLock = new();
    private RouterOSTlsCertificateException? certificateFailure;

    public RouterOSCertificateValidator(
        string host,
        int port,
        RouterOSServerCertificate? trustedCertificate)
    {
        this.host = host.ToLowerInvariant();
        this.port = port;
        this.trustedCertificate = trustedCertificate;
    }

    public bool Validate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            return false;
        }

        var received = new RouterOSServerCertificate(host, port, certificate.Export(X509ContentType.Cert));
        if (trustedCertificate is not null
            && trustedCertificate.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            && trustedCertificate.Port == port)
        {
            if (trustedCertificate.Matches(host, port, received.DerEncodedCertificate.Span))
            {
                return true;
            }

            RecordFailure(new RouterOSChangedCertificateException(
                trustedCertificate.FingerprintSha256,
                received));
            return false;
        }

        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        RecordFailure(new RouterOSUntrustedCertificateException(received));
        return false;
    }

    public RouterOSTlsCertificateException? CertificateFailure()
    {
        lock (failureLock)
        {
            return certificateFailure;
        }
    }

    private void RecordFailure(RouterOSTlsCertificateException failure)
    {
        lock (failureLock)
        {
            certificateFailure ??= failure;
        }
    }
}
