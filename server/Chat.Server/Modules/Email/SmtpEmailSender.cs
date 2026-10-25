using Chat.Server.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Modules.Email;

/// <summary>
/// SMTP 邮件通道占位（非 Development 环境注册）：M1-04 里程碑不接真实邮件，
/// 保留抽象接口，M6 上线前再补实现（配置项见 <see cref="Common.Options.EmailOptions"/>）。
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(ILogger<SmtpEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendCodeAsync(string email, string code, EmailCodePurpose purpose, CancellationToken cancellationToken)
    {
        _logger.LogError("SMTP 邮件通道尚未实现：当前仅 Development Mock 通道可发送验证码（M1-04 范围）");
        throw new NotSupportedException("SMTP 发信尚未实现（当前里程碑仅 Development Mock 通道）");
    }
}
