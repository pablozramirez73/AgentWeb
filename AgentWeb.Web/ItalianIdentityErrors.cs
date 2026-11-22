using Microsoft.AspNetCore.Identity;

namespace AgentWeb.Web;

// I messaggi di Identity arrivano così come sono all'utente (cambio password, registrazione): in italiano.
public sealed class ItalianIdentityErrors : IdentityErrorDescriber
{
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = "La password attuale non è corretta." };
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = $"La password deve avere almeno {length} caratteri." };
    public override IdentityError PasswordRequiresDigit() => new() { Code = nameof(PasswordRequiresDigit), Description = "La password deve contenere almeno un numero." };
    public override IdentityError PasswordRequiresLower() => new() { Code = nameof(PasswordRequiresLower), Description = "La password deve contenere almeno una lettera minuscola." };
    public override IdentityError PasswordRequiresUpper() => new() { Code = nameof(PasswordRequiresUpper), Description = "La password deve contenere almeno una lettera maiuscola." };
    public override IdentityError PasswordRequiresNonAlphanumeric() => new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "La password deve contenere almeno un simbolo (es. ! ? @ #)." };
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new() { Code = nameof(PasswordRequiresUniqueChars), Description = $"La password deve contenere almeno {uniqueChars} caratteri diversi." };
    public override IdentityError DuplicateUserName(string userName) => new() { Code = nameof(DuplicateUserName), Description = $"Esiste già un account {userName}." };
    public override IdentityError DuplicateEmail(string email) => new() { Code = nameof(DuplicateEmail), Description = $"Esiste già un account con l'email {email}." };
    public override IdentityError InvalidEmail(string? email) => new() { Code = nameof(InvalidEmail), Description = $"L'email {email} non è valida." };
    public override IdentityError InvalidUserName(string? userName) => new() { Code = nameof(InvalidUserName), Description = $"Il nome utente {userName} non è valido." };
    public override IdentityError InvalidToken() => new() { Code = nameof(InvalidToken), Description = "Il codice non è valido o è scaduto." };
}
