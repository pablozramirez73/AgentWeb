using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace AgentWeb.Web;

// Manutenzione da terminale quando non si riesce più a entrare:
//   dotnet run --project AgentWeb.Web -- reset-password <email> [file]
// Genera una password nuova e robusta, sblocca l'account, chiude sessioni e token e scrive
// la password in un file leggibile solo dal proprietario (default ~/AgentWeb-credenziali.txt). Non la stampa a schermo.
public static class AccountMaintenance
{
    public static async Task<int> ResetPasswordAsync(IServiceProvider services, string email, string? file)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByNameAsync(email.Trim());
        if (user is null) { Console.Error.WriteLine($"Account \"{email}\" inesistente."); return 1; }

        var password = NewPassword();
        var r = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!r.Succeeded) { Console.Error.WriteLine(string.Join(" ", r.Errors.Select(e => e.Description))); return 1; }
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        var hadTwoFactor = await users.GetTwoFactorEnabledAsync(user); // telefono perso: si riattiva da Dispositivi
        if (hadTwoFactor) { await users.SetTwoFactorEnabledAsync(user, false); await users.ResetAuthenticatorKeyAsync(user); }
        var closed = await scope.ServiceProvider.GetRequiredService<SessionStore>().RevokeAllAsync(user.UserName!);

        file ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AgentWeb-credenziali.txt");
        await File.WriteAllTextAsync(file, $"AgentWeb — account {user.UserName}\nPassword: {password}\n(generata {DateTimeOffset.Now:yyyy-MM-dd HH:mm}; cambiala da Dispositivi → Cambia password, poi elimina questo file)\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine($"Password di {user.UserName} reimpostata, {closed} sessioni e token chiusi{(hadTwoFactor ? ", verifica in due passaggi disattivata" : "")}. La password è in {file} (leggibile solo da te).");
        return 0;
    }

    // 20 caratteri da un alfabeto senza caratteri ambigui, con almeno una maiuscola, una minuscola, un numero e un simbolo.
    static string NewPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789", symbols = "!#%+-=?@";
        var all = upper + lower + digits + symbols;
        var chars = new[] { Pick(upper), Pick(lower), Pick(digits), Pick(symbols) }.Concat(Enumerable.Range(0, 16).Select(_ => Pick(all))).ToArray();
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}
