using Microsoft.AspNetCore.Identity;
using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// I messaggi di Identity arrivano così come sono all'utente (cambio password, registrazione): nella lingua dell'installazione.
public sealed class LocalizedIdentityErrors : IdentityErrorDescriber
{
    static IdentityError E(string code, params object?[] args) => new() { Code = code, Description = T("identity." + code, args) };

    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch));
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), length);
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit));
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower));
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper));
    public override IdentityError PasswordRequiresNonAlphanumeric() => E(nameof(PasswordRequiresNonAlphanumeric));
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => E(nameof(PasswordRequiresUniqueChars), uniqueChars);
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), userName);
    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), email);
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), email);
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), userName);
    public override IdentityError InvalidToken() => E(nameof(InvalidToken));
}
