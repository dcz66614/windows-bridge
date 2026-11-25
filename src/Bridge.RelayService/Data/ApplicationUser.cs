using Microsoft.AspNetCore.Identity;

namespace Bridge.RelayService.Data;

/// <summary>Relay account. v1 carries no extra profile fields; device
/// bindings live in Bridge.Relay's journal (T10), not on the user row.
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
}
