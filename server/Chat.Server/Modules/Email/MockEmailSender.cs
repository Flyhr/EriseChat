using Chat.Server.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Modules.Email;

/// <summary>
/// Development 环境的 Mock 邮件通道：把 6 位验证码输出到控制台 stdout（不写日志文件，符合 AGENTS §6 / 计划 §4.1）。
/// 固定测试码由 <see cref="EmailCodeService"/> 在 Development 生成时决定，本类只负责展示。
/// </summary>
public sealed class MockEmailSender : IEmailSender
{
    private readonly ILogger<MockEmailSender> _logger;

    public MockEmailSender(ILogger<MockEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendCodeAsync(string email, string code, EmailCodePurpose purpose, CancellationToken cancellationToken)
    {
        // 注意：验证码只输出到控制台，日志里不出现明文（AGENTS §6 密钥纪律）。
        Console.WriteLine($"[MockEmail] 邮箱={email} 用途={purpose.ToString().ToLowerInvariant()} 验证码={code} 有效期=10分钟");
        _logger.LogInformation("Mock 邮件已输出到控制台：邮箱 {Email}，用途 {Purpose}", email, purpose);
        return Task.CompletedTask;
    }
}
