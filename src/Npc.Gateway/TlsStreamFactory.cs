using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Npc.Gateway;

/// <summary>링크 암호화 모드 (A-06).</summary>
public enum LinkTlsMode
{
    /// <summary>평문. 신뢰 경계 안(같은 VPC·같은 호스트)에서만 쓴다.</summary>
    Off,

    /// <summary>TLS. 게임서버가 서버 인증서를 낸다.</summary>
    Tls,

    /// <summary>상호 TLS. NPC 서버도 클라이언트 인증서를 낸다.</summary>
    Mtls,
}

/// <summary>
/// TLS 연결 생성기 (A-06).
///
/// <b><see cref="TcpGameServerLink"/> 본문을 건드리지 않는다.</b> 그 클래스는 이미
/// <c>Func&lt;CancellationToken, Task&lt;Stream&gt;&gt;</c> 로 연결을 주입받게 되어 있고,
/// 여기서 그 이음매에 <see cref="SslStream"/> 을 끼운다 — 재접속 백오프·핸드셰이크·
/// 프레임 코덱은 스트림이 무엇인지 모른 채 그대로 돈다.
/// </summary>
public sealed class TlsStreamFactory
{
    private readonly TcpLinkOptions _options;

    /// <summary>생성기를 만든다.</summary>
    public TlsStreamFactory(TcpLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <summary>
    /// 게임서버에 붙고 필요하면 TLS 로 감싼다.
    ///
    /// <b>인증서 검증을 끄는 옵션을 만들지 않는다.</b> 자체 서명 인증서를 쓰려면 그것을 신뢰
    /// 저장소에 넣는다 — "검증을 끈다" 는 순간 TLS 는 도청만 막고 위장은 못 막는 반쪽이 된다.
    /// </summary>
    public async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        NetworkStream raw = client.GetStream();

        if (_options.Tls == LinkTlsMode.Off)
        {
            return raw;
        }

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false);

        try
        {
            await ssl.AuthenticateAsClientAsync(
                BuildClientOptions(), ct).ConfigureAwait(false);

            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 클라이언트 인증서를 읽는다. <c>mtls</c> 가 아니면 빈 모음이다.
    ///
    /// 비밀번호는 환경변수 <c>NPC_LINK_CERT_PASSWORD</c> 로만 온다 — 인자에 두면 <c>ps</c> 에 보인다.
    /// </summary>
    public static X509Certificate2Collection LoadClientCertificate(string? path, string? password)
    {
        var certificates = new X509Certificate2Collection();

        if (string.IsNullOrEmpty(path))
        {
            return certificates;
        }

        certificates.Add(X509CertificateLoader.LoadPkcs12FromFile(
            path, password, X509KeyStorageFlags.EphemeralKeySet));

        return certificates;
    }

    private SslClientAuthenticationOptions BuildClientOptions() => new()
    {
        TargetHost = _options.TlsHost ?? _options.Host,
        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13
            | System.Security.Authentication.SslProtocols.Tls12,
        ClientCertificates = _options.Tls == LinkTlsMode.Mtls
            ? LoadClientCertificate(_options.ClientCertificatePath, _options.ClientCertificatePassword)
            : null,
    };
}
