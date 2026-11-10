using Chat.Server.Common.Options;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chat.Server.Tests;

/// <summary>
/// 部署准备（M6 前置）：SMTP 通道最小校验——未配置时明确抛错、邮件主题/正文按用途区分。
/// 不连接真实 SMTP（无凭据，也避免单测依赖外部网络）。
/// </summary>
public sealed class SmtpEmailSenderTests
{
    [Fact]
    public async Task SendCodeAsync_ThrowsWhenSmtpNotConfigured()
    {
        var sender = new SmtpEmailSender(
            Options.Create(new EmailOptions()),
            NullLogger<SmtpEmailSender>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendCodeAsync("user@example.com", "123456", EmailCodePurpose.Register, CancellationToken.None));
    }

    [Theory]
    [InlineData(EmailCodePurpose.Register, "注册")]
    [InlineData(EmailCodePurpose.Login, "登录")]
    [InlineData(EmailCodePurpose.Reset, "重置密码")]
    public void SubjectAndBody_ContainPurposeAndCode(EmailCodePurpose purpose, string action)
    {
        var subject = SmtpEmailSender.SubjectOf(purpose);
        Assert.Contains("EriseChat", subject);
        Assert.Contains("验证码", subject);

        var body = SmtpEmailSender.BodyOf(purpose, "123456");
        Assert.Contains("123456", body);
        Assert.Contains(action, body);
        Assert.Contains("10 分钟", body);
    }
}
