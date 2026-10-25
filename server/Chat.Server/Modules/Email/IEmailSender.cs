using Chat.Server.Data.Entities;

namespace Chat.Server.Modules.Email;

/// <summary>
/// 邮件发送抽象：验证码发送通道。实现按环境注册（Development = Mock，其它 = SMTP 占位）。
/// 实现方禁止把验证码写入日志文件（只能输出到开发控制台或真实邮件）。
/// </summary>
public interface IEmailSender
{
    /// <summary>发送验证码邮件（purpose=register/login）。</summary>
    Task SendCodeAsync(string email, string code, EmailCodePurpose purpose, CancellationToken cancellationToken);
}
