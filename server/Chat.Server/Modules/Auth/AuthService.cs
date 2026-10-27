using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Common.Security;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chat.Server.Modules.Auth;

/// <summary>登录/注册返回的用户信息（与 apps/web/src/api/auth.ts 的 AuthUser 对齐）。</summary>
public sealed record AuthUserDto(string UserId, string Email, string DisplayName, string? AvatarFileId, string Role);

/// <summary>认证成功结果：accessToken + 过期时间（UTC 毫秒）+ 用户信息。</summary>
public sealed record AuthSessionResult(string AccessToken, long ExpiresAt, AuthUserDto User);

/// <summary>当前用户资料（与 apps/web/src/api/users.ts 的 UserProfile 对齐）。</summary>
public sealed record UserProfileDto(string UserId, string Email, string DisplayName, string? AvatarFileId, string Role, string Status, long CreatedAt, bool HasPassword);

/// <summary>F-13：登录设备列表项（Current 表示发起请求所用的 JWT 会话）。</summary>
public sealed record SessionInfoDto(string SessionId, string DeviceId, long CreatedAt, long LastSeenAt, bool Current);

/// <summary>
/// 账号认证业务（计划 §4.1 / §7）：邮箱验证码注册、验证码登录、密码登录、注销、当前用户资料。
/// 验证码校验/消费、JWT 签发与会话（sessions）落库都在本层；REST 端点只做参数绑定与响应映射。
/// </summary>
public sealed class AuthService
{
    private const int DisplayNameMaxLength = 32;
    private const int PasswordMinLength = 8;
    private const int PasswordMaxLength = 128;
    private const int DeviceIdMaxLength = 128;
    private const int CodeMaxLength = 16;

    /// <summary>F-12：可作为头像的文件 MIME 白名单（与上传校验一致）。</summary>
    private static readonly HashSet<string> AvatarMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    };

    private readonly ChatDbContext _db;
    private readonly JwtOptions _jwtOptions;
    private readonly RateLimitOptions _rateLimitOptions;
    private readonly JwtTokenService _jwtTokens;
    private readonly LoginAttemptLimiter _loginAttempts;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        ChatDbContext db,
        IOptions<JwtOptions> jwtOptions,
        IOptions<RateLimitOptions> rateLimitOptions,
        JwtTokenService jwtTokens,
        LoginAttemptLimiter loginAttempts,
        ILogger<AuthService> logger)
    {
        _db = db;
        _jwtOptions = jwtOptions.Value;
        _rateLimitOptions = rateLimitOptions.Value;
        _jwtTokens = jwtTokens;
        _loginAttempts = loginAttempts;
        _logger = logger;
    }

    /// <summary>验证码注册：校验验证码 → 创建用户（role=user/status=active/email_verified_at）→ 签发 JWT。</summary>
    public async Task<AuthSessionResult> RegisterAsync(
        string? rawEmail,
        string? rawCode,
        string? rawDisplayName,
        string? password,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryNormalize(rawEmail, out var email))
        {
            throw ApiException.ValidationFailed("邮箱格式不正确");
        }

        var displayName = (rawDisplayName ?? string.Empty).Trim();
        if (displayName.Length is < 1 or > DisplayNameMaxLength)
        {
            throw ApiException.ValidationFailed($"昵称长度应为 1~{DisplayNameMaxLength} 个字符");
        }

        if (displayName.Any(char.IsControl))
        {
            throw ApiException.ValidationFailed("昵称不能包含控制字符");
        }

        var code = (rawCode ?? string.Empty).Trim();
        if (code.Length is < 1 or > CodeMaxLength)
        {
            throw ApiException.ValidationFailed("验证码不能为空");
        }

        string? passwordHash = null;
        if (!string.IsNullOrEmpty(password))
        {
            if (password.Length is < PasswordMinLength or > PasswordMaxLength)
            {
                throw ApiException.ValidationFailed($"密码长度应为 {PasswordMinLength}~{PasswordMaxLength} 个字符");
            }

            passwordHash = PasswordHasher.Hash(password);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (await _db.Users.AnyAsync(user => user.Email == email, cancellationToken))
        {
            throw new ApiException(ErrorCodes.EmailAlreadyRegistered, "该邮箱已注册，请直接登录", StatusCodes.Status409Conflict);
        }

        await ConsumeEmailCodeAsync(email, EmailCodePurpose.Register, code, now, cancellationToken);

        var entity = new User
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = email,
            DisplayName = displayName,
            PasswordHash = passwordHash,
            EmailVerifiedAt = now,
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = now,
        };

        _db.Users.Add(entity);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发注册同一邮箱时由 users.email 唯一索引兜底。
            throw new ApiException(ErrorCodes.EmailAlreadyRegistered, "该邮箱已注册，请直接登录", StatusCodes.Status409Conflict);
        }

        _logger.LogInformation("新用户注册成功：{UserId}（{Email}）", entity.Id, entity.Email);
        return await CreateSessionAsync(entity, NormalizeOptionalDeviceId(deviceId, "register"), now, cancellationToken);
    }

    /// <summary>验证码登录（purpose=login）：校验并消费验证码 → 签发 JWT + 写入会话。</summary>
    public async Task<AuthSessionResult> LoginWithCodeAsync(
        string? rawEmail,
        string? rawCode,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryNormalize(rawEmail, out var email))
        {
            throw ApiException.ValidationFailed("邮箱格式不正确");
        }

        var code = (rawCode ?? string.Empty).Trim();
        if (code.Length is < 1 or > CodeMaxLength)
        {
            throw ApiException.ValidationFailed("验证码不能为空");
        }

        var normalizedDeviceId = RequireDeviceId(deviceId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await ConsumeEmailCodeAsync(email, EmailCodePurpose.Login, code, now, cancellationToken);

        var user = await _db.Users.FirstOrDefaultAsync(item => item.Email == email, cancellationToken);
        if (user is null)
        {
            // 验证码已消费：先持久化再返回，避免记录残留可重放。
            await _db.SaveChangesAsync(cancellationToken);
            throw ApiException.Unauthorized("邮箱或验证码错误");
        }

        if (user.Status != UserStatus.Active)
        {
            await _db.SaveChangesAsync(cancellationToken);
            throw new ApiException(ErrorCodes.AccountDisabled, "账号已被禁用，请联系管理员", StatusCodes.Status403Forbidden);
        }

        return await CreateSessionAsync(user, normalizedDeviceId, now, cancellationToken);
    }

    /// <summary>密码登录：PBKDF2 校验 + 失败 5 次锁 5 分钟（内存计数）。</summary>
    public async Task<AuthSessionResult> LoginWithPasswordAsync(
        string? rawEmail,
        string? password,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryNormalize(rawEmail, out var email))
        {
            throw ApiException.ValidationFailed("邮箱格式不正确");
        }

        var normalizedDeviceId = RequireDeviceId(deviceId);
        if (string.IsNullOrEmpty(password))
        {
            throw ApiException.ValidationFailed("密码不能为空");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var remainingLock = _loginAttempts.GetRemainingLockMilliseconds(email, now);
        if (remainingLock > 0)
        {
            throw BuildAccountLockedException(remainingLock);
        }

        var user = await _db.Users.FirstOrDefaultAsync(item => item.Email == email, cancellationToken);
        var verified = user is not null &&
                       !string.IsNullOrEmpty(user.PasswordHash) &&
                       PasswordHasher.Verify(password, user.PasswordHash);

        if (!verified)
        {
            var lockedMilliseconds = _loginAttempts.RecordFailure(email, now);
            if (lockedMilliseconds > 0)
            {
                throw BuildAccountLockedException(lockedMilliseconds);
            }

            throw new ApiException(ErrorCodes.PasswordInvalid, "邮箱或密码错误", StatusCodes.Status401Unauthorized);
        }

        if (user!.Status != UserStatus.Active)
        {
            throw new ApiException(ErrorCodes.AccountDisabled, "账号已被禁用，请联系管理员", StatusCodes.Status403Forbidden);
        }

        _loginAttempts.Clear(email);
        return await CreateSessionAsync(user, normalizedDeviceId, now, cancellationToken);
    }

    /// <summary>注销当前会话：按 jti 置 sessions.revoked_at，之后同一 token 校验失败（401）。</summary>
    public async Task LogoutAsync(string userId, string jwtId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var affected = await _db.Sessions
            .Where(session => session.JwtId == jwtId && session.UserId == userId && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now), cancellationToken);

        if (affected == 0)
        {
            throw ApiException.Unauthorized("会话不存在或已失效");
        }
    }

    /// <summary>
    /// F-13：当前用户未撤销的登录会话（按 lastSeenAt 降序；Current 标识发起请求的 JWT 会话）。
    /// </summary>
    public async Task<IReadOnlyList<SessionInfoDto>> ListSessionsAsync(
        string userId,
        string? currentJwtId,
        CancellationToken cancellationToken)
    {
        var sessions = await _db.Sessions.AsNoTracking()
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .OrderByDescending(session => session.LastSeenAt)
            .ThenByDescending(session => session.CreatedAt)
            .ToListAsync(cancellationToken);

        return sessions
            .Select(session => new SessionInfoDto(
                session.Id,
                session.DeviceId,
                session.CreatedAt,
                session.LastSeenAt,
                !string.IsNullOrEmpty(currentJwtId)
                    && string.Equals(session.JwtId, currentJwtId, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// F-13：按会话 ID 远程下号（软撤销）。会话不存在或不属于当前用户 → NOT_FOUND（不泄漏存在性）；
    /// 已撤销 → 幂等成功。撤销后由调用方踢掉该会话的 WS 连接。
    /// </summary>
    public async Task RevokeSessionAsync(string userId, string sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == sessionId && item.UserId == userId, cancellationToken);
        if (session is null)
        {
            throw ApiException.NotFound("会话不存在");
        }

        if (session.RevokedAt is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _db.Sessions
            .Where(item => item.Id == sessionId && item.UserId == userId && item.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RevokedAt, now), cancellationToken);
        _logger.LogInformation("远程下号：用户 {UserId} 退出会话 {SessionId}", userId, sessionId);
    }

    /// <summary>当前登录用户资料（禁用/不存在视为未登录）。</summary>
    public async Task<UserProfileDto> GetProfileAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }

        return new UserProfileDto(
            user.Id,
            user.Email,
            user.DisplayName,
            user.AvatarFileId,
            user.Role.ToString().ToLowerInvariant(),
            user.Status.ToString().ToLowerInvariant(),
            user.CreatedAt,
            user.PasswordHash is not null);
    }

    /// <summary>
    /// F-12：修改当前用户昵称与头像引用（计划 §F-12）。两项至少一项，null 表示不修改；
    /// displayName 去空白后 1~32 字符且不含控制字符；头像只能引用本人上传的图片文件；返回最新资料。
    /// </summary>
    public async Task<UserProfileDto> UpdateProfileAsync(
        string userId,
        string? displayName,
        string? avatarFileId,
        CancellationToken cancellationToken)
    {
        if (displayName is null && avatarFileId is null)
        {
            throw ApiException.ValidationFailed("displayName 与 avatarFileId 至少提供一个");
        }

        var user = await _db.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }

        if (displayName is not null)
        {
            user.DisplayName = NormalizeDisplayName(displayName);
        }

        if (avatarFileId is not null)
        {
            user.AvatarFileId = await ValidateAvatarFileAsync(userId, avatarFileId, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("更新个人资料：用户 {UserId}", user.Id);

        return new UserProfileDto(
            user.Id,
            user.Email,
            user.DisplayName,
            user.AvatarFileId,
            user.Role.ToString().ToLowerInvariant(),
            user.Status.ToString().ToLowerInvariant(),
            user.CreatedAt,
            user.PasswordHash is not null);
    }

    /// <summary>
    /// F-13：修改/设置密码（计划 §F-13）。已有密码时必须校验旧密码；新密码 8~128 且不得与当前密码相同；
    /// 从未设置过密码的用户忽略旧密码直接设置（即「设置密码」）；不撤销已有会话。
    /// </summary>
    public async Task ChangePasswordAsync(
        string userId,
        string? oldPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }

        if (string.IsNullOrEmpty(newPassword) ||
            newPassword.Length is < PasswordMinLength or > PasswordMaxLength)
        {
            throw ApiException.ValidationFailed($"密码长度应为 {PasswordMinLength}~{PasswordMaxLength} 个字符");
        }

        var hasPassword = !string.IsNullOrEmpty(user.PasswordHash);
        if (hasPassword)
        {
            if (string.IsNullOrEmpty(oldPassword) || !PasswordHasher.Verify(oldPassword, user.PasswordHash!))
            {
                throw new ApiException(ErrorCodes.PasswordInvalid, "旧密码不正确", StatusCodes.Status401Unauthorized);
            }

            if (PasswordHasher.Verify(newPassword, user.PasswordHash!))
            {
                throw ApiException.ValidationFailed("新密码不能与当前密码相同");
            }
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await _db.SaveChangesAsync(cancellationToken);
        _loginAttempts.Clear(user.Email);
        _logger.LogInformation("修改密码：用户 {UserId}", user.Id);
    }

    /// <summary>
    /// F-13：邮箱验证码重置密码（计划 §F-13）。先消费 reset 用途验证码，再按邮箱查找用户；
    /// 邮箱不存在/已禁用时仍消费并落库消费记录（防重放），随后返回统一错误，不泄漏账号是否存在；
    /// 成功后更新密码哈希并清除登录失败计数，不撤销已有会话。
    /// </summary>
    public async Task ResetPasswordAsync(
        string? rawEmail,
        string? code,
        string? newPassword,
        CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryNormalize(rawEmail, out var email))
        {
            throw ApiException.ValidationFailed("邮箱格式不正确");
        }

        if (string.IsNullOrEmpty(code) || code.Length > CodeMaxLength)
        {
            throw ApiException.ValidationFailed("验证码格式不正确");
        }

        if (string.IsNullOrEmpty(newPassword) ||
            newPassword.Length is < PasswordMinLength or > PasswordMaxLength)
        {
            throw ApiException.ValidationFailed($"密码长度应为 {PasswordMinLength}~{PasswordMaxLength} 个字符");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await ConsumeEmailCodeAsync(email, EmailCodePurpose.Reset, code, now, cancellationToken);

        var user = await _db.Users.FirstOrDefaultAsync(item => item.Email == email, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            // 邮箱不存在/禁用：保留消费结果（验证码已删除，避免被重放），返回不泄漏存在性的统一错误。
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("密码重置失败：邮箱未注册或账号不可用");
            throw ApiException.Unauthorized("邮箱或验证码错误");
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await _db.SaveChangesAsync(cancellationToken);
        _loginAttempts.Clear(email);
        _logger.LogInformation("重置密码：用户 {UserId}", user.Id);
    }

    private static string NormalizeDisplayName(string raw)
    {
        var value = raw.Trim();
        if (value.Length is < 1 or > DisplayNameMaxLength)
        {
            throw ApiException.ValidationFailed($"昵称长度应为 1~{DisplayNameMaxLength} 个字符");
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw ApiException.ValidationFailed("昵称不能包含控制字符");
            }
        }

        return value;
    }

    private async Task<string> ValidateAvatarFileAsync(string userId, string rawFileId, CancellationToken cancellationToken)
    {
        var fileId = rawFileId.Trim();
        if (fileId.Length is < 1 or > 64 || fileId.Any(char.IsWhiteSpace))
        {
            throw ApiException.ValidationFailed("avatarFileId 格式不正确");
        }

        var file = await _db.Files.AsNoTracking().FirstOrDefaultAsync(item => item.Id == fileId, cancellationToken);
        if (file is null)
        {
            throw ApiException.ValidationFailed("头像文件不存在");
        }

        if (file.OwnerId != userId)
        {
            throw ApiException.Forbidden("只能使用自己上传的文件作为头像");
        }

        if (!AvatarMimeTypes.Contains(file.Mime))
        {
            throw ApiException.Forbidden("头像仅支持图片文件");
        }

        return file.Id;
    }

    /// <summary>
    /// 校验并消费邮箱验证码：取该邮箱+用途最新一条记录，按哈希恒定时间比较；
    /// 错误累加 attempts（达到上限作废），成功则删除记录（一次性）。
    /// </summary>
    private async Task ConsumeEmailCodeAsync(
        string email,
        EmailCodePurpose purpose,
        string code,
        long nowUtcMilliseconds,
        CancellationToken cancellationToken)
    {
        var latest = await _db.EmailCodes
            .Where(item => item.Email == email && item.Purpose == purpose)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            throw new ApiException(ErrorCodes.CodeInvalid, "验证码错误或已失效", StatusCodes.Status400BadRequest);
        }

        if (latest.ExpiresAt <= nowUtcMilliseconds)
        {
            throw new ApiException(ErrorCodes.CodeExpired, "验证码已过期，请重新获取", StatusCodes.Status400BadRequest);
        }

        var maxAttempts = Math.Max(1, _rateLimitOptions.EmailCodeMaxAttempts);
        if (latest.Attempts >= maxAttempts)
        {
            throw new ApiException(ErrorCodes.CodeAttemptsExceeded, "验证码错误次数过多，请重新获取", StatusCodes.Status400BadRequest);
        }

        if (!EmailCodeHasher.Verify(_jwtOptions.Key, email, purpose, code, latest.CodeHash))
        {
            latest.Attempts++;
            await _db.SaveChangesAsync(cancellationToken);
            if (latest.Attempts >= maxAttempts)
            {
                throw new ApiException(ErrorCodes.CodeAttemptsExceeded, "验证码错误次数过多，请重新获取", StatusCodes.Status400BadRequest);
            }

            throw new ApiException(ErrorCodes.CodeInvalid, "验证码错误", StatusCodes.Status400BadRequest);
        }

        _db.EmailCodes.Remove(latest);
    }

    /// <summary>创建会话（sessions.jwt_id = jti）并签发 JWT。</summary>
    private async Task<AuthSessionResult> CreateSessionAsync(
        User user,
        string deviceId,
        long nowUtcMilliseconds,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUtcMilliseconds);
        var jwtId = Guid.NewGuid().ToString("N");
        var token = _jwtTokens.CreateToken(user, jwtId, now);

        _db.Sessions.Add(new Session
        {
            Id = Guid.NewGuid().ToString("D"),
            UserId = user.Id,
            DeviceId = deviceId,
            JwtId = jwtId,
            CreatedAt = nowUtcMilliseconds,
            LastSeenAt = nowUtcMilliseconds,
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("签发 JWT：用户 {UserId}，设备 {DeviceId}，过期 {ExpiresAt}", user.Id, deviceId, token.ExpiresAt);
        return new AuthSessionResult(
            token.AccessToken,
            token.ExpiresAt,
            new AuthUserDto(
                user.Id,
                user.Email,
                user.DisplayName,
                user.AvatarFileId,
                user.Role.ToString().ToLowerInvariant()));
    }

    private static string RequireDeviceId(string? deviceId)
    {
        var value = (deviceId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw ApiException.ValidationFailed("deviceId 不能为空");
        }

        if (value.Length > DeviceIdMaxLength)
        {
            throw ApiException.ValidationFailed($"deviceId 过长（最多 {DeviceIdMaxLength} 个字符）");
        }

        return value;
    }

    private static string NormalizeOptionalDeviceId(string? deviceId, string fallbackPrefix)
    {
        var value = (deviceId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return $"{fallbackPrefix}-{Guid.NewGuid():N}";
        }

        if (value.Length > DeviceIdMaxLength)
        {
            throw ApiException.ValidationFailed($"deviceId 过长（最多 {DeviceIdMaxLength} 个字符）");
        }

        return value;
    }

    private static ApiException BuildAccountLockedException(long remainingLockMilliseconds)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(remainingLockMilliseconds / 60_000d));
        return new ApiException(
            ErrorCodes.AccountLocked,
            $"登录失败次数过多，账号已锁定 {minutes} 分钟，请稍后再试",
            StatusCodes.Status429TooManyRequests);
    }
}
