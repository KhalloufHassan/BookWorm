using Microsoft.AspNetCore.Identity;

namespace BookWorm.Server.Data;

public sealed class AppUser : IdentityUser<Guid>, IAuditable
{
    public AppUser()
    {
        Id = Guid.CreateVersion7();
    }

    public AppUser(string userName) : this()
    {
        UserName = userName;
    }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
