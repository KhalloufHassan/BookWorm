using System.ComponentModel.DataAnnotations;

namespace BookWorm.Contracts;

/// <summary>A rating from 0 to 5 with at most one decimal, e.g. 4.8.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RatingAttribute() : ValidationAttribute("{0} must be between 0 and 5 with at most one decimal, e.g. 4.8.")
{
    public const decimal Min = 0m;
    public const decimal Max = 5m;

    public override bool IsValid(object value) => value switch
    {
        null => true,
        decimal rating => rating is >= Min and <= Max && decimal.Round(rating, 1) == rating,
        _ => false,
    };
}
