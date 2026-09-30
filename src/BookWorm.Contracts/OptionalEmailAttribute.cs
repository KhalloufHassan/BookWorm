using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

/// <summary>An email address that may be left empty (email is optional for accounts).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class OptionalEmailAttribute() : ValidationAttribute("{0} is not a valid email address.")
{
    private static readonly EmailAddressAttribute Email = new();

    public override bool IsValid(object value) =>
        value is null || (value is string text && (string.IsNullOrWhiteSpace(text) || Email.IsValid(text.Trim())));
}
