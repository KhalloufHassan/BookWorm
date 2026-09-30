using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

/// <summary>The signed-in user.</summary>
public sealed record CurrentUser(Guid Id, string UserName, string Email, bool IsAdmin);

/// <summary>A user account as seen by an administrator.</summary>
public sealed record AdminUser(
    Guid Id,
    string UserName,
    string Email,
    bool IsAdmin,
    bool TwoFactorEnabled,
    bool IsLockedOut,
    DateTimeOffset CreatedAt);

public sealed class CreateUserRequest
{
    [Required]
    [StringLength(ApiLimits.UserNameMaxLength)]
    public string UserName { get; set; } = "";

    /// <summary>Optional; no mail server is needed.</summary>
    [OptionalEmail]
    [StringLength(ApiLimits.EmailMaxLength)]
    public string Email { get; set; }

    [Required]
    [StringLength(ApiLimits.PasswordMaxLength, MinimumLength = ApiLimits.PasswordMinLength)]
    public string Password { get; set; } = "";

    public bool IsAdmin { get; set; }
}

public sealed class UpdateUserRequest
{
    [OptionalEmail]
    [StringLength(ApiLimits.EmailMaxLength)]
    public string Email { get; set; }

    public bool IsAdmin { get; set; }
}

public sealed class ResetPasswordRequest
{
    [Required]
    [StringLength(ApiLimits.PasswordMaxLength, MinimumLength = ApiLimits.PasswordMinLength)]
    public string NewPassword { get; set; } = "";
}
