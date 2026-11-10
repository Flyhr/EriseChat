using System.Net;
using System.Net.Mail;
using System.Text;
using Chat.Server.Common.Options;
using Chat.Server.Data.Entities;
using Microsoft.Extensions.Options;

namespace Chat.Server.Modules.Email;

/// <summary>
/// 真实 SMTP 邮件通道（非 Development 环境注册）：发送邮箱验证码。
/// 配置见 <see cref="EmailOptions"/>（"Email" 节）；密码只允许来自 secrets.json 或环境变量，日志不输出凭据与验证码。
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    /// <summary>SMTP 发送超时（毫秒）：验证码接口是同步等待发信的，避免请求长时间挂起。</summary>
    private const int SendTimeoutMilliseconds = 15_000;

    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendCodeAsync(string email, string code, EmailCodePurpose purpose, CancellationToken cancellationToken)
    {
        var from = string.IsNullOrWhiteSpace(_options.FromAddress) ? _options.SmtpUser : _options.FromAddress;
        if (string.IsNullOrWhiteSpace(_options.SmtpHost) || string.IsNullOrWhiteSpace(from))
        {
            _logger.LogError("SMTP 邮件通道未配置（Email:SmtpHost / Email:SmtpUser 为空），验证码邮件无法发送");
            throw new InvalidOperationException("SMTP 邮件通道未配置");
        }

        using var message = new MailMessage(new MailAddress(from, _options.FromName), new MailAddress(email))
        {
            Subject = SubjectOf(purpose),
            Body = BodyOf(purpose, code),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.SmtpUseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = SendTimeoutMilliseconds,
        };
        if (!string.IsNullOrWhiteSpace(_options.SmtpUser))
        {
            client.Credentials = new NetworkCredential(_options.SmtpUser, _options.SmtpPassword);
        }

        try
        {
            await client.SendMailAsync(message);
        }
        catch (Exception exception)
        {
            // 只记录用途与 SMTP 主机，不记录验证码、收件人明文与凭据。
            _logger.LogError(exception, "验证码邮件发送失败（purpose={Purpose}, smtpHost={SmtpHost}）", purpose, _options.SmtpHost);
            throw;
        }

        _logger.LogInformation("验证码邮件已发送（purpose={Purpose}）", purpose);
    }

    internal static string SubjectOf(EmailCodePurpose purpose) => purpose switch
    {
        EmailCodePurpose.Register => "EriseChat 注册验证码",
        EmailCodePurpose.Login => "EriseChat 登录验证码",
        EmailCodePurpose.Reset => "EriseChat 密码重置验证码",
        _ => "EriseChat 验证码",
    };

    internal static string BodyOf(EmailCodePurpose purpose, string code)
    {
        var action = purpose switch
        {
            EmailCodePurpose.Register => "注册",
            EmailCodePurpose.Login => "登录",
            EmailCodePurpose.Reset => "重置密码",
            _ => "身份验证",
        };
        return $"你的 EriseChat {action}验证码是：{code}\n\n验证码 10 分钟内有效，请勿转发给他人。\n若非本人操作，请忽略本邮件。";
    }
}
